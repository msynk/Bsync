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
}
