namespace Bsync.Conflicts;

/// <summary>What a record shows while a conflict is kept for a decision (<see cref="SyncOptions{TDocument}.KeptConflictView"/>, task D7).</summary>
public enum KeptConflictView
{
    /// <summary>
    /// The server's state (the default). Choose it when the app shows kept conflicts to the user (a "keep mine / keep
    /// theirs" UI): everyone sees the same data until someone decides.
    /// </summary>
    Server,

    /// <summary>
    /// The local edit, marked conflicted. Choose it when the app has no conflict UI yet, so a user never sees their own
    /// edit apparently disappear; the edit is still not uploaded until it is resolved, and discarding it shows the server
    /// state.
    /// </summary>
    Local,
}
