namespace Bsync.Client;

/// <summary>What a person must decide about (task C5).</summary>
public enum SyncIssueKind
{
    /// <summary>A local change kept after a conflict: resolve or discard it.</summary>
    Conflict,

    /// <summary>A local change the server rejected: fix and retry it, or revert it.</summary>
    Rejected,

    /// <summary>A member of a dependency group parked because another member failed.</summary>
    Blocked,
}

/// <summary>One document that needs a decision.</summary>
/// <param name="Id">The document id.</param>
/// <param name="Kind">What kind of decision.</param>
/// <param name="ErrorCode">For a rejection, the server's stable code (match on it, never on <paramref name="Message"/>).</param>
/// <param name="Message">For a rejection, the server's explanation, for logs and developers.</param>
public sealed record SyncIssue(string Id, SyncIssueKind Kind, string? ErrorCode = null, string? Message = null);

/// <summary>A page of <see cref="SyncIssue"/>s, conflicts first, each kind in id order.</summary>
/// <param name="Items">The issues on this page.</param>
/// <param name="Total">All issues, on every page.</param>
public sealed record SyncIssuePage(IReadOnlyList<SyncIssue> Items, int Total);
