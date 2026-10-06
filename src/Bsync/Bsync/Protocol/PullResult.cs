using System.Text.Json.Serialization;

namespace Bsync.Protocol;

/// <summary>One page of the change feed.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
/// <param name="Changes">
/// Changes after the requested checkpoint in feed order. A page contains at most one change per
/// document and at most <see cref="PullRequest.BatchSize"/> changes.
/// </param>
/// <param name="Checkpoint">
/// The checkpoint to store once <paramref name="Changes"/> are durably applied, and to send on the next
/// request.
/// </param>
/// <param name="HasMore">
/// <see langword="true"/> when more changes are immediately available. A page with
/// <paramref name="HasMore"/> set must advance the checkpoint.
/// </param>
public sealed record PullResult<TDocument>(
    [property: JsonPropertyName("changes"), JsonRequired] IReadOnlyList<RemoteChange<TDocument>> Changes,
    [property: JsonPropertyName("checkpoint"), JsonRequired] Checkpoint Checkpoint,
    [property: JsonPropertyName("hasMore"), JsonRequired] bool HasMore)
    where TDocument : class, ISyncEntity
{
    /// <summary>Optional protocol features this server supports (see <see cref="SyncFeatures"/>); absent means none.</summary>
    [JsonPropertyName("features")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Features { get; init; }

    /// <summary>The server's limits (feature <see cref="SyncFeatures.Limits"/>); absent from older servers.</summary>
    [JsonPropertyName("limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncLimits? Limits { get; init; }

    /// <summary>The server's time in Unix milliseconds when it served the page (feature <see cref="SyncFeatures.ServerTime"/>).</summary>
    [JsonPropertyName("serverTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(WireNullableInt64JsonConverter))]
    public long? ServerTime { get; init; }

    /// <summary>
    /// The version at or below which the server purged tombstones (feature <see cref="SyncFeatures.Retention"/>); 0 when
    /// nothing was purged.
    /// </summary>
    [JsonPropertyName("retentionHorizon")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(WireNullableInt64JsonConverter))]
    public long? RetentionHorizon { get; init; }

    /// <summary>
    /// Ids of documents that left the caller's view in this page's range (feature <see cref="SyncFeatures.Removals"/>, sent
    /// only to replicas that asked for it). Replicas remove clean copies and hide ones with local changes; nothing is
    /// deleted on the server. A page never lists an id both here and in <see cref="Changes"/>.
    /// </summary>
    [JsonPropertyName("removals")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Removals { get; init; }
}
