using System.Text.Json.Serialization;

namespace Bsync.Protocol;

/// <summary>
/// The limits a server enforces, advertised in <see cref="PullResult{TDocument}.Limits"/> with feature
/// <see cref="SyncFeatures.Limits"/> (protocol §8). Replicas clamp their batch sizes to them.
/// </summary>
/// <param name="MaxOperationsPerPush">Maximum operations the server accepts in one push request.</param>
/// <param name="MaxPageSize">Maximum changes the server returns in one pull page.</param>
public sealed record SyncLimits(
    [property: JsonPropertyName("maxOperationsPerPush")] int MaxOperationsPerPush,
    [property: JsonPropertyName("maxPageSize")] int MaxPageSize);
