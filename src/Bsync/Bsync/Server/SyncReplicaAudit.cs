namespace Bsync.Server;

/// <summary>
/// That a replica had caught up with a feed at a point in time (task H): after a pull that left nothing more to fetch,
/// the replica held everything up to <see cref="Checkpoint"/>.
/// </summary>
/// <param name="Replica">The replica's clock node id (the node in the timestamps of its writes), as sent in the pull request.</param>
/// <param name="Account">The authenticated caller's name, if any.</param>
/// <param name="Collection">The collection.</param>
/// <param name="Scope">The feed (tenant) within the collection.</param>
/// <param name="Checkpoint">The checkpoint the replica reached (opaque; it includes the version position).</param>
/// <param name="At">When the server answered the pull.</param>
public sealed record ReplicaAcknowledgement(string Replica, string? Account, string Collection, string Scope, string Checkpoint, DateTimeOffset At);

/// <summary>
/// Records which content each replica held, and when (task H, optional): for environments that must prove which
/// revision a device had. The HTTP endpoints record an acknowledgement when <c>SyncEndpointOptions.ReplicaAudit</c> is
/// set and a replica that identifies itself completes a pull.
/// </summary>
public interface ISyncReplicaAudit
{
    /// <summary>Records an acknowledgement. Implementations may skip one whose checkpoint equals the replica's previous one.</summary>
    Task RecordAsync(ReplicaAcknowledgement acknowledgement, CancellationToken cancellationToken = default);

    /// <summary>The acknowledgements of <paramref name="replica"/>, oldest first.</summary>
    Task<IReadOnlyList<ReplicaAcknowledgement>> GetAsync(string replica, CancellationToken cancellationToken = default);
}

/// <summary>An <see cref="ISyncReplicaAudit"/> in memory (tests, development). Repeated checkpoints are recorded once.</summary>
public sealed class InMemorySyncReplicaAudit : ISyncReplicaAudit
{
    private readonly Lock _gate = new();
    private readonly List<ReplicaAcknowledgement> _acknowledgements = [];

    /// <inheritdoc />
    public Task RecordAsync(ReplicaAcknowledgement acknowledgement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        lock (_gate)
        {
            var previous = _acknowledgements.FindLast(a => a.Replica == acknowledgement.Replica && a.Collection == acknowledgement.Collection && a.Scope == acknowledgement.Scope);
            if (previous?.Checkpoint != acknowledgement.Checkpoint)
            {
                _acknowledgements.Add(acknowledgement);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReplicaAcknowledgement>> GetAsync(string replica, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ReplicaAcknowledgement>>([.. _acknowledgements.Where(a => a.Replica == replica)]);
        }
    }
}
