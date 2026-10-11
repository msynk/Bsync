using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Clocks;
using Bsync.Protocol;

namespace Bsync.Server;

/// <summary>Configuration for <see cref="InMemorySyncServer{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class InMemorySyncServerOptions<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>Deep-clone function (for example <c>DocumentCloner.Json(context.MyDocument)</c>).</summary>
    public required Func<TDocument, TDocument> Cloner { get; init; }

    /// <summary>
    /// Produces a canonical string for a document, used to detect an operation id reused with a
    /// different payload (for example <c>DocumentCloner.JsonFingerprint(context.MyDocument)</c>).
    /// </summary>
    public required Func<TDocument, string> Fingerprint { get; init; }

    /// <summary>The physical clock used to validate origin timestamps. Defaults to the system clock.</summary>
    public IPhysicalClock? PhysicalClock { get; init; }

    /// <summary>
    /// How far a document's origin <see cref="ISyncEntity.UpdatedAt"/> may be ahead of server time before
    /// the write is rejected with <see cref="PushErrorCodes.ClockSkew"/>. Default five minutes.
    /// </summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum operations accepted in one push request. Default 1000.</summary>
    public int MaxOperationsPerPush { get; init; } = 1000;

    /// <summary>Maximum changes returned by one pull, regardless of the requested batch size. Default 1000.</summary>
    public int MaxPageSize { get; init; } = 1000;

    /// <summary>
    /// A backup to start from (see <see cref="InMemorySyncServer{TDocument}.CreateBackup"/>). The restored
    /// server gets a new epoch, so replicas holding checkpoints from the original must reset.
    /// </summary>
    public InMemorySyncServerBackup<TDocument>? RestoreFrom { get; init; }

    /// <summary>
    /// The lowest version the server may issue next is <c>VersionFloor + 1</c>. After a restore, set it at or
    /// above every version the lost history may have issued, so no version is ever reused for a different
    /// state (docs/protocol/v1.md, section 6).
    /// </summary>
    public long VersionFloor { get; init; }

    /// <summary>
    /// Optional fingerprint of what the caller may see (for example a hash of roles, grants or a filter). It is
    /// bound into every checkpoint; when it changes, the caller's old checkpoint yields
    /// <see cref="ResetReasons.ScopeChanged"/> and the replica resnapshots, which removes documents it may no longer
    /// read and brings back regranted ones even if they did not change.
    /// </summary>
    public Func<SyncCallContext, string>? ScopeFingerprint { get; init; }

    /// <summary>
    /// Optional read authorization. Documents the caller may not read are left out of its pull pages, and
    /// conflict or replayed outcomes that would reveal such a document are returned as
    /// <see cref="PushErrorCodes.Forbidden"/> rejections without the document.
    /// </summary>
    public Func<SyncCallContext, TDocument, bool>? CanRead { get; init; }

    /// <summary>
    /// Optional write authorization, checked before validation. Receives the operation and the current
    /// state (if any). A refused write is rejected with <see cref="PushErrorCodes.Forbidden"/>.
    /// </summary>
    public Func<SyncCallContext, PushOperation<TDocument>, TDocument?, bool>? CanWrite { get; init; }

    /// <summary>
    /// Optional application validation. Return <see langword="null"/> to allow the operation, or an error
    /// code to reject it permanently. Receives the caller, the operation and the current server state, if any.
    /// </summary>
    public Func<SyncCallContext, PushOperation<TDocument>, TDocument?, string?>? Validator { get; init; }

    /// <summary>
    /// Optional read membership (ADR-015): the principal keys that may read a document, computed at every write. With it,
    /// each caller's pull returns only documents it may read, in full pages, and documents that leave its view are listed
    /// as removals (or, for replicas without that feature, answered with <c>scope-changed</c>). Set together with
    /// <see cref="PrincipalKey"/>.
    /// </summary>
    public Func<TDocument, IEnumerable<string>>? Readers { get; init; }

    /// <summary>The caller's principal key for <see cref="Readers"/> (for example a user id claim); <see langword="null"/> reads nothing.</summary>
    public Func<SyncCallContext, string?>? PrincipalKey { get; init; }

    /// <summary>
    /// Optional application logic for every write about to be accepted (ADR-014): return a canonical document, a conflict,
    /// a permanent rejection, or <see cref="SyncWriteDecision{TDocument}.RetryLater"/> (no receipt is stored, so the replica
    /// resends the same operation later). This server has no database: the handler gets no connection or transaction,
    /// must not have side effects, and runs under the server's lock, so it must complete synchronously.
    /// </summary>
    public ISyncWriteHandler<TDocument>? WriteHandler { get; init; }
}
