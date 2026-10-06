namespace Bsync.Client;

/// <summary>The synchronization status of one document.</summary>
/// <param name="State">The state.</param>
/// <param name="Detail">The rejection code, when <see cref="SyncItemState.Rejected"/>.</param>
public sealed record SyncItemStatus(SyncItemState State, string? Detail = null)
{
    /// <summary>Values that complete the rejection code in <see cref="Detail"/> (task C5), when the server sent any.</summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; init; }
}
