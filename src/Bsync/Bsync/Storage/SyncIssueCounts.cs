namespace Bsync.Storage;

/// <summary>Records that need a person's decision (task C5).</summary>
/// <param name="Conflicts">Local changes kept after a conflict.</param>
/// <param name="Rejected">Local changes the server rejected, parked until retried or reverted.</param>
/// <param name="Blocked">Of <paramref name="Rejected"/>, members of a dependency group parked because another member failed.</param>
public sealed record SyncIssueCounts(int Conflicts, int Rejected, int Blocked)
{
    /// <summary>No issues.</summary>
    public static SyncIssueCounts None { get; } = new(0, 0, 0);

    /// <summary>Conflicts plus rejections.</summary>
    public int Total => Conflicts + Rejected;
}
