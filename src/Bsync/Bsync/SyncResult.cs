namespace Bsync;

/// <summary>Summary of one sync, pull or push run.</summary>
/// <remarks>
/// A run that returns normally is not proof that the replica is current: check
/// <see cref="IsComplete"/>. Even a complete run only means that, when it finished, the local queue
/// had been drained and the pull had reached the checkpoint the server reported as current.
/// </remarks>
/// <param name="Pulled">Number of remote changes applied to the local store.</param>
/// <param name="Pushed">Number of local operations the server accepted.</param>
/// <param name="Conflicts">Number of conflicts encountered and handed to the conflict handler.</param>
public readonly record struct SyncResult(int Pulled, int Pushed, int Conflicts)
{
    /// <summary>Number of operations the server permanently rejected (see <c>SyncRecord.Rejection</c>).</summary>
    public int Rejected { get; init; }

    /// <summary>
    /// Number of operations left pending in this run: a retryable server outcome, an outcome missing
    /// from the response, or a document that exhausted its conflict budget.
    /// </summary>
    public int Deferred { get; init; }

    /// <summary>
    /// <see langword="true"/> when work was known to remain when the run finished: pushable local
    /// changes, or pull pages beyond <see cref="SyncOptions{TDocument}.MaxPullPages"/>.
    /// </summary>
    public bool HasRemainingWork { get; init; }

    /// <summary>
    /// <see langword="true"/> when the server required a reset and the replica started a new generation
    /// with a full snapshot (see <c>SyncEngine.PullAsync</c>).
    /// </summary>
    public bool ResetPerformed { get; init; }

    /// <summary>
    /// Number of clean local records a completed post-reset snapshot did not contain; they are now marked
    /// <c>SyncRecord.MissingAfterReset</c>.
    /// </summary>
    public int MissingAfterReset { get; init; }

    /// <summary>
    /// Number of clean local records removed after a reset for a change of access or retention: the caller may no
    /// longer see them, or they were deleted and purged on the server.
    /// </summary>
    public int PurgedAfterReset { get; init; }

    /// <summary>
    /// Documents that left this replica's view without a reset (feature <c>removals</c>, ADR-015): clean copies removed from
    /// the device, plus copies with local changes that were hidden.
    /// </summary>
    public int Removed { get; init; }

    /// <summary>Clean local tombstones dropped because the server purged them (task D4).</summary>
    public int Compacted { get; init; }

    /// <summary>Whether the run drained everything it could see without deferring or leaving work.</summary>
    public bool IsComplete => !HasRemainingWork && Deferred == 0;

    /// <summary>Adds two results together (used to aggregate pull and push phases).</summary>
    public static SyncResult operator +(SyncResult a, SyncResult b) =>
        new(a.Pulled + b.Pulled, a.Pushed + b.Pushed, a.Conflicts + b.Conflicts)
        {
            Rejected = a.Rejected + b.Rejected,
            Deferred = a.Deferred + b.Deferred,
            HasRemainingWork = a.HasRemainingWork || b.HasRemainingWork,
            ResetPerformed = a.ResetPerformed || b.ResetPerformed,
            MissingAfterReset = a.MissingAfterReset + b.MissingAfterReset,
            PurgedAfterReset = a.PurgedAfterReset + b.PurgedAfterReset,
            Removed = a.Removed + b.Removed,
            Compacted = a.Compacted + b.Compacted,
        };
}
