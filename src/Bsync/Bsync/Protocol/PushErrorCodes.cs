namespace Bsync.Protocol;

/// <summary>Well-known <see cref="PushOutcome{TDocument}.ErrorCode"/> values.</summary>
public static class PushErrorCodes
{
    /// <summary>The operation or document was malformed.</summary>
    public const string Invalid = "invalid";

    /// <summary>The operation id was already used for a different request.</summary>
    public const string OperationIdReused = "operation-id-reused";

    /// <summary>The document's origin timestamp is further in the future than the server allows.</summary>
    public const string ClockSkew = "clock-skew";

    /// <summary>The caller may not perform this write.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The server is temporarily unable to decide.</summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// The operation is based on a version older than the server's retention horizon and the document no longer
    /// exists (it may have been deleted and purged). Writing it again as a new document is an explicit choice.
    /// </summary>
    public const string BaseExpired = "base-expired";

    /// <summary>
    /// With <see cref="PushOutcomeKind.RetryLater"/>: nothing was applied because another operation of the same
    /// dependency group was not accepted. Resend the group once that operation is settled.
    /// </summary>
    public const string GroupAborted = "group-aborted";

    /// <summary>
    /// Replica-side rejection: another change of the dependency group was rejected or kept as a conflict, so this change
    /// is parked until that one is resolved or retried.
    /// </summary>
    public const string GroupFailed = "group-failed";

    /// <summary>Replica-side rejection: the server does not support dependency groups, so the group cannot be sent atomically.</summary>
    public const string GroupsUnsupported = "groups-unsupported";

    /// <summary>
    /// The write refers to something the server does not have yet (for example a parent document another replica has not
    /// uploaded). Sent with <see cref="PushOutcomeKind.RetryLater"/>: the replica resends the same operation with backoff.
    /// </summary>
    public const string DependencyMissing = "dependency-missing";

    /// <summary>
    /// Replica-only: the operation was too large for the server even on its own (HTTP 413), so it was parked locally and
    /// never applied. Make the document smaller, then retry it.
    /// </summary>
    public const string PayloadTooLarge = "payload-too-large";
}
