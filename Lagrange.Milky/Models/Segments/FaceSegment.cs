using System.Text.Json.Serialization;

namespace Lagrange.Milky.Models.Segments;

public sealed class FaceIncomingSegment : IncomingSegmentBase<FaceIncomingSegmentData>;

public sealed class FaceIncomingSegmentData
{
    [JsonPropertyName("face_id")] public required string FaceId { get; init; }
    [JsonPropertyName("is_large")] public required bool IsLarge { get; init; }
}

public sealed class FaceOutgoingSegment : OutgoingSegmentBase<FaceOutgoingSegmentData>;

public sealed class FaceOutgoingSegmentData
{
    [JsonPropertyName("face_id")] public required string FaceId { get; init; }
    [JsonPropertyName("is_large")] public bool IsLarge { get; init; }
}
