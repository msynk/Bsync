using System.Text.Json.Serialization;

namespace Bsync.Protocol;

/// <summary>
/// A request for the next page of the server's change feed, starting strictly after
/// <see cref="Since"/>.
/// </summary>
/// <param name="Since">The client's stored checkpoint. <see cref="Checkpoint.Start"/> for a full sync.</param>
/// <param name="BatchSize">The maximum number of changes the server may return (at least 1).</param>
public readonly record struct PullRequest(
    [property: JsonPropertyName("checkpoint"), JsonRequired] Checkpoint Since,
    [property: JsonPropertyName("limit"), JsonRequired] int BatchSize)
{
    /// <summary>
    /// Optional protocol features the replica understands in the response (for example <see cref="SyncFeatures.Removals"/>);
    /// absent means none. Older servers ignore it.
    /// </summary>
    [JsonPropertyName("features")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Features { get; init; }

    /// <summary>
    /// Identifies the replica (its clock node id), so a server can record which content each replica reached
    /// (optional, task H). Servers that do not audit ignore it.
    /// </summary>
    [JsonPropertyName("replica")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Replica { get; init; }
}
