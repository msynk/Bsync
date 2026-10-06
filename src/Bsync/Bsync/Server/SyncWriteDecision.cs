namespace Bsync.Server;

/// <summary>What an <see cref="ISyncWriteHandler{TDocument}"/> decided for one write (ADR-014).</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SyncWriteDecision<TDocument>
    where TDocument : class, ISyncEntity
{
    private SyncWriteDecision(SyncWriteDecisionKind kind, TDocument? document, string? errorCode, string? message)
    {
        Kind = kind;
        Document = document;
        ErrorCode = errorCode;
        Message = message;
    }

    /// <summary>The decision.</summary>
    public SyncWriteDecisionKind Kind { get; }

    /// <summary>For <see cref="SyncWriteDecisionKind.Accept"/>: the canonical document to store and return.</summary>
    public TDocument? Document { get; }

    /// <summary>For a rejection or retry: the stable machine-readable reason.</summary>
    public string? ErrorCode { get; }

    /// <summary>For a rejection or retry: an explanation for logs and developers (not localized).</summary>
    public string? Message { get; }

    /// <summary>Values that complete <see cref="ErrorCode"/> for the client (task C5), or <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string>? Arguments { get; private init; }

    /// <summary>
    /// Accept, storing <paramref name="canonical"/> (it may differ from the submission, for example a recomputed field,
    /// but must keep its id). The handler's writes commit with it.
    /// </summary>
    public static SyncWriteDecision<TDocument> Accept(TDocument canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return new(SyncWriteDecisionKind.Accept, canonical, null, null);
    }

    /// <summary>Answer with a conflict carrying the current stored document; the handler's writes are undone.</summary>
    public static SyncWriteDecision<TDocument> Conflict() => new(SyncWriteDecisionKind.Conflict, null, null, null);

    /// <summary>Reject permanently; the handler's writes are undone and the rejection is stored as the receipt.</summary>
    public static SyncWriteDecision<TDocument> Reject(string errorCode, string? message = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return new(SyncWriteDecisionKind.Reject, null, errorCode, message);
    }

    /// <summary>
    /// Rejects the write with a stable code and <paramref name="arguments"/> that complete it for the UI, for example
    /// <c>Reject("title-too-long", new Dictionary&lt;string, string&gt; { ["max"] = "200" })</c> (task C5). The replica
    /// keeps both on the parked record.
    /// </summary>
    public static SyncWriteDecision<TDocument> Reject(string errorCode, IReadOnlyDictionary<string, string> arguments, string? message = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        ArgumentNullException.ThrowIfNull(arguments);
        return new(SyncWriteDecisionKind.Reject, null, errorCode, message) { Arguments = new Dictionary<string, string>(arguments, StringComparer.Ordinal) };
    }

    /// <summary>Not decided now; the handler's writes are undone, no receipt is stored, and the replica resends later.</summary>
    public static SyncWriteDecision<TDocument> RetryLater(string errorCode, string? message = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorCode);
        return new(SyncWriteDecisionKind.RetryLater, null, errorCode, message);
    }
}

/// <summary>The kinds of <see cref="SyncWriteDecision{TDocument}"/>.</summary>
public enum SyncWriteDecisionKind
{
    /// <summary>Store the canonical document.</summary>
    Accept,

    /// <summary>Answer with a conflict.</summary>
    Conflict,

    /// <summary>Reject permanently.</summary>
    Reject,

    /// <summary>Ask the replica to resend later.</summary>
    RetryLater,
}
