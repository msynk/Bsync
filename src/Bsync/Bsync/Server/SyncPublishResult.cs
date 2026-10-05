namespace Bsync.Server;

/// <summary>What an <see cref="ISyncPublisher{TDocument}"/> call changed.</summary>
/// <param name="Written">Documents created or changed (each got a new version).</param>
/// <param name="Unchanged">Documents whose content equalled the stored one (no new version).</param>
/// <param name="Deleted">Documents replaced by tombstones.</param>
public sealed record SyncPublishResult(int Written, int Unchanged, int Deleted)
{
    /// <summary>Nothing changed.</summary>
    public static SyncPublishResult None { get; } = new(0, 0, 0);

    /// <summary>Adds two results.</summary>
    public SyncPublishResult Add(SyncPublishResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new(Written + other.Written, Unchanged + other.Unchanged, Deleted + other.Deleted);
    }
}
