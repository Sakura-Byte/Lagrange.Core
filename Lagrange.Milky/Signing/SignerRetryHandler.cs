using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Lagrange.Milky.Signing;

internal sealed class SignerRetryHandler(HttpMessageHandler innerHandler, ILogger logger, TimeSpan? attemptTimeout = null) : DelegatingHandler(innerHandler)
{
    private const int MaxAttempts = 3;
    private const long MaxResponseBytes = 1024 * 1024;
    private readonly TimeSpan _attemptTimeout = attemptTimeout ?? TimeSpan.FromSeconds(6);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Signing precedes the QQ packet send. Never replay the actual messaging API.
        if (request.Method != HttpMethod.Post || request.RequestUri == null ||
            !request.RequestUri.AbsolutePath.EndsWith("/sign/sec-sign", StringComparison.Ordinal))
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var payload = request.Content == null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        for (var attempt = 1; ; attempt++)
        {
            using var outgoing = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var header in request.Headers) outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (payload != null)
            {
                outgoing.Content = new ByteArrayContent(payload);
                foreach (var header in request.Content!.Headers) outgoing.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_attemptTimeout);
            HttpResponseMessage? response = null;
            var delay = TimeSpan.FromMilliseconds((250 * (1 << (attempt - 1))) + Random.Shared.Next(0, 101));
            string reason;
            try
            {
                response = await base.SendAsync(outgoing, timeout.Token);
                if (!IsTransientStatus(response.StatusCode))
                {
                    // Buffer the small signing response here so interrupted reads can also recover.
                    if (response.IsSuccessStatusCode) await response.Content.LoadIntoBufferAsync(MaxResponseBytes, timeout.Token);
                    return response;
                }

                if (attempt == MaxAttempts)
                {
                    logger.LogWarning("Signer request exhausted {Attempts} attempts with HTTP {StatusCode}", attempt, (int)response.StatusCode);
                    return response;
                }

                var retryAfter = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is { } retryDate) retryAfter = retryDate - DateTimeOffset.UtcNow;
                // Do not retry earlier than a long server-requested cooldown.
                if (retryAfter > TimeSpan.FromSeconds(2)) return response;
                if (retryAfter > delay) delay = retryAfter.Value;
                reason = $"HTTP {(int)response.StatusCode}";
                response.Dispose();
                response = null;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && IsTransientError(error))
            {
                response?.Dispose();
                if (attempt == MaxAttempts)
                {
                    logger.LogWarning("Signer request exhausted {Attempts} attempts ({ErrorType})", attempt, error.GetType().Name);
                    throw;
                }
                reason = error.GetType().Name;
            }
            catch
            {
                response?.Dispose();
                throw;
            }

            // Log only the failure category, never the bearer token or packet body.
            logger.LogWarning("Transient signer failure ({Reason}); retry {Attempt}/{MaxAttempts} after {DelayMs} ms",
                reason, attempt + 1, MaxAttempts, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status is >= 500 and <= 599;

    private static bool IsTransientError(Exception error)
    {
        var hasIoFailure = false;
        for (Exception? current = error; current != null; current = current.InnerException)
        {
            // Invalid certificates and unsupported TLS settings require an operator fix.
            if (current is AuthenticationException) return false;
            hasIoFailure |= current is IOException;
        }

        if (error is OperationCanceledException) return true;
        if (error is HttpRequestException http)
        {
            return http.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError or HttpRequestError.ResponseEnded ||
                (hasIoFailure && http.HttpRequestError is HttpRequestError.SecureConnectionError or HttpRequestError.Unknown);
        }
        return error is IOException;
    }
}
