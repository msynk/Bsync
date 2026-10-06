namespace Bsync.Client;

/// <summary>
/// What a caller waits for with <see cref="ISyncCollection{TDocument}.SyncAsync"/> (task C4). Parts combine: every part set
/// must be reached.
/// </summary>
/// <param name="CaughtUp">A pull that started after the wait began has read everything the server had.</param>
/// <param name="AllPendingAccepted">No local change is waiting to be uploaded (parked rejections and kept conflicts do not count; see the result).</param>
/// <param name="Documents">Local revisions that must be accepted by the server: a revision counts as accepted once the record is clean at that revision or a later one.</param>
public sealed record SyncGoal(bool CaughtUp = false, bool AllPendingAccepted = false, IReadOnlyList<SyncDocumentRevision>? Documents = null)
{
    /// <summary>Profile: the replica has the server's latest state (a refresh before showing data).</summary>
    public static SyncGoal Background { get; } = new(CaughtUp: true);

    /// <summary>Profile: caught up, and everything written locally is uploaded (before going offline or signing out).</summary>
    public static SyncGoal Complete { get; } = new(CaughtUp: true, AllPendingAccepted: true);

    /// <summary>The server accepted local revision <paramref name="revision"/> (from <see cref="LocalWriteReceipt"/>) of <paramref name="id"/>, or a later one.</summary>
    public static SyncGoal Accepted(string id, long revision) => new(Documents: [new SyncDocumentRevision(id, revision)]);

    /// <summary>Both goals.</summary>
    public SyncGoal And(SyncGoal other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SyncGoal(
            CaughtUp || other.CaughtUp,
            AllPendingAccepted || other.AllPendingAccepted,
            (Documents, other.Documents) switch
            {
                (null, var documents) => documents,
                (var documents, null) => documents,
                var (a, b) => [.. a, .. b],
            });
    }
}

/// <summary>A document's local revision, as returned in <see cref="LocalWriteReceipt.LocalRevision"/>.</summary>
public sealed record SyncDocumentRevision(string Id, long Revision);

/// <summary>Progress of a <see cref="ISyncCollection{TDocument}.SyncAsync"/> wait, reported after each sync run.</summary>
/// <param name="Runs">Sync runs since the wait began.</param>
/// <param name="Pulled">Server changes applied since the wait began.</param>
/// <param name="Pushed">Local changes accepted since the wait began.</param>
/// <param name="Pending">Local changes still waiting to be uploaded.</param>
public sealed record SyncProgress(int Runs, int Pulled, int Pushed, int Pending);

/// <summary>How a <see cref="ISyncCollection{TDocument}.SyncAsync"/> wait ended.</summary>
/// <param name="Reached">Every part of the goal was reached.</param>
/// <param name="CaughtUp">A pull that started after the wait began read everything.</param>
/// <param name="Pending">Local changes still waiting to be uploaded.</param>
/// <param name="Unaccepted">Requested revisions not accepted (still pending, rejected or in conflict).</param>
/// <param name="Work">What the sync runs during the wait did.</param>
public sealed record SyncGoalResult(bool Reached, bool CaughtUp, int Pending, IReadOnlyList<SyncDocumentRevision> Unaccepted, SyncResult Work)
{
    /// <summary>Combines the results of several collections: reached only if each was.</summary>
    public static SyncGoalResult Combine(IEnumerable<SyncGoalResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var all = results.ToList();
        return new SyncGoalResult(
            all.All(r => r.Reached),
            all.All(r => r.CaughtUp),
            all.Sum(r => r.Pending),
            [.. all.SelectMany(r => r.Unaccepted)],
            all.Aggregate(default(SyncResult), (sum, r) => sum + r.Work));
    }
}
