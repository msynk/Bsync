namespace Bsync.Client;

/// <summary>What a collection's synchronization is doing, for the UI.</summary>
/// <param name="State">The overall state. Never <see cref="SyncState.Synced"/> while conflicts or rejections wait for a decision.</param>
/// <param name="Pending">Local changes not yet accepted by the server (including parked ones).</param>
/// <param name="Detail">A short explanation for the UI, or <see langword="null"/>.</param>
/// <param name="LastSynced">When a sync last completed without remaining work, if ever.</param>
public sealed record SyncStatus(SyncState State, int Pending, string? Detail, DateTimeOffset? LastSynced)
{
    /// <summary>Before the first sync of the session.</summary>
    public static SyncStatus Starting { get; } = new(SyncState.Starting, 0, null, null);

    /// <summary>Local changes kept after a conflict, waiting for a decision.</summary>
    public int Conflicts { get; init; }

    /// <summary>Local changes the server rejected, parked until retried or reverted.</summary>
    public int Rejected { get; init; }

    /// <summary>Of <see cref="Rejected"/>, members of a dependency group parked because another member failed.</summary>
    public int Blocked { get; init; }

    /// <summary>When server changes were last pulled successfully in this session.</summary>
    public DateTimeOffset? LastPulled { get; init; }

    /// <summary>When local changes were last pushed successfully (or found nothing to push) in this session.</summary>
    public DateTimeOffset? LastPushed { get; init; }

    /// <summary>
    /// Whether the replica's storage is protected from eviction by the platform: <see langword="true"/> when a browser
    /// granted persistent storage, <see langword="false"/> when it refused (the browser may delete the replica under
    /// storage pressure, so unsynced changes could be lost), <see langword="null"/> when unknown or not applicable
    /// (native files). Taken from <see cref="LocalReplica{TDocument}.PersistentStorage"/>.
    /// </summary>
    public bool? PersistentStorage { get; init; }

    /// <summary>How old the replica's view of the server is at <paramref name="now"/>; <see langword="null"/> if it never pulled.</summary>
    public TimeSpan? Staleness(DateTimeOffset now) => LastPulled is { } pulled ? now - pulled : null;

    /// <summary>Whether the replica has not pulled within <paramref name="threshold"/> of <paramref name="now"/> (or never).</summary>
    public bool IsStale(TimeSpan threshold, DateTimeOffset now) => Staleness(now) is not { } age || age > threshold;
}
