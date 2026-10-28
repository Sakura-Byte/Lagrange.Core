using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using Lagrange.Milky.Signing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lagrange.Milky.Test;

public class SignerRetryHandlerTest
{
    [Test]
    public async Task TlsEofRecoversAndReplaysOnlyTheIdenticalSigningRequest()
    {
        var transport = new ProbeHandler((attempt, _) => attempt < 3
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException(HttpRequestError.SecureConnectionError,
                "The SSL connection could not be established", new IOException("Received an unexpected EOF or 0 bytes")))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("signed") }));
        using var client = CreateClient(transport);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only");
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("{\"seq\":42,\"body\":\"aabb\"}"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("signed"));
        Assert.That(transport.Requests, Has.Count.EqualTo(3));
        Assert.That(transport.Requests.Distinct().Count(), Is.EqualTo(3));
        Assert.That(transport.Bodies.Distinct().Single(), Is.EqualTo("{\"seq\":42,\"body\":\"aabb\"}"));
        Assert.That(transport.Authorizations, Is.All.EqualTo("Bearer test-only"));
    }

    [Test]
    public void RepeatedConnectionFailuresStopAfterThreeAttempts()
    {
        var transport = new ProbeHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset")));
        using var client = CreateClient(transport);
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload")));
        Assert.That(transport.Requests, Has.Count.EqualTo(3));
    }

    [TestCase(408)]
    [TestCase(429)]
    [TestCase(500)]
    [TestCase(502)]
    [TestCase(503)]
    [TestCase(504)]
    public async Task TransientHttpResponsesAreRetried(int code)
    {
        var transport = new ProbeHandler((attempt, _) => Task.FromResult(new HttpResponseMessage(
            attempt == 1 ? (HttpStatusCode)code : HttpStatusCode.OK)));
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(transport.Requests, Has.Count.EqualTo(2));
    }

    [TestCase(400)]
    [TestCase(401)]
    [TestCase(403)]
    [TestCase(404)]
    public async Task PermanentHttpFailuresAreNotRetried(int code)
    {
        var transport = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"));
        Assert.That((int)response.StatusCode, Is.EqualTo(code));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void CertificateFailuresAreNotRetried()
    {
        var transport = new ProbeHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "Invalid certificate",
                new AuthenticationException("Certificate validation failed"))));
        using var client = CreateClient(transport);
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload")));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ApplicationErrorsAreReturnedWithoutRetry()
    {
        const string json = "{\"code\":1,\"message\":\"not authorized\",\"value\":null}";
        var transport = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json) }));
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(json));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task OtherEndpointsIncludingQqSendAreNeverRetried()
    {
        var transport = new ProbeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)));
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("http://lagrange/api/send_private_message", new StringContent("payload"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void CancellationDuringConnectionDoesNotRetry()
    {
        var transport = new ProbeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"), cancellation.Token));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void CancellationDuringBackoffDoesNotRetry()
    {
        var transport = new ProbeHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.ConnectionError, "Connection reset")));
        using var client = CreateClient(transport);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"), cancellation.Token));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void PerAttemptTimeoutIsBoundedAndRetriesOnlyThreeTimes()
    {
        var transport = new ProbeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(new SignerRetryHandler(transport, NullLogger.Instance, TimeSpan.FromMilliseconds(20)))
            { Timeout = TimeSpan.FromSeconds(4) };
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload")));
        Assert.That(transport.Requests, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task TruncatedResponseBodyRecoversBeforeReturningTheSignature()
    {
        var transport = new ProbeHandler((attempt, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = attempt == 1 ? new BrokenContent() : new StringContent("signed") }));
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("signed"));
        Assert.That(transport.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task LongRetryAfterIsRespectedWithoutBusyRetrying()
    {
        var transport = new ProbeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return Task.FromResult(response);
        });
        using var client = CreateClient(transport);
        using var response = await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload"));
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public void CertificateErrorsAreNotRetriedEvenInsideAnIoWrapper()
    {
        var transport = new ProbeHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.SecureConnectionError, "Invalid certificate",
                new IOException("TLS failed", new AuthenticationException("Certificate validation failed")))));
        using var client = CreateClient(transport);
        Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.PostAsync("https://signer.test/sign/sec-sign", new StringContent("payload")));
        Assert.That(transport.Requests, Has.Count.EqualTo(1));
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(new IOException("The response ended prematurely"));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler transport) =>
        new(new SignerRetryHandler(transport, NullLogger.Instance)) { Timeout = TimeSpan.FromSeconds(20) };

    private sealed class ProbeHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        public List<string?> Authorizations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Authorizations.Add(request.Headers.Authorization?.ToString());
            return await respond(Requests.Count, cancellationToken);
        }
    }
}
