using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Bsync.Protocol;

namespace Bsync.Server.SqlServer;

/// <summary>Configuration for <see cref="SqlServerSyncAuthority{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SqlServerSyncAuthorityOptions<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>
    /// Connection string of the application's database. The authority keeps its tables in <see cref="Schema"/> there,
    /// next to the application's own tables (ADR-014).
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>Source-generated JSON metadata for the document type. Documents are stored as their exact JSON text.</summary>
    public required JsonTypeInfo<TDocument> DocumentType { get; init; }

    /// <summary>The database schema holding the protocol tables. Default <c>bsync</c>; letters, digits and underscores.</summary>
    public string Schema { get; init; } = "bsync";

    /// <summary>The collection name; several collections share the tables. Default <c>"default"</c>.</summary>
    public string Collection { get; init; } = "default";

    /// <summary>The physical clock used to validate origin timestamps. Defaults to the system clock.</summary>
    public IPhysicalClock? PhysicalClock { get; init; }

    /// <summary>How far an origin timestamp may be ahead of server time. Default five minutes.</summary>
    public TimeSpan MaxClockSkew { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Maximum operations accepted in one push request. Default 1000.</summary>
    public int MaxOperationsPerPush { get; init; } = 1000;

    /// <summary>Maximum changes returned by one pull. Default 1000.</summary>
    public int MaxPageSize { get; init; } = 1000;

    /// <summary>Optional fingerprint of what the caller may see; bound into checkpoints (reset reason <c>scope-changed</c>).</summary>
    public Func<SyncCallContext, string>? ScopeFingerprint { get; init; }

    /// <summary>Optional read authorization (pages skip unreadable documents; outcomes never reveal them).</summary>
    public Func<SyncCallContext, TDocument, bool>? CanRead { get; init; }

    /// <summary>Optional write authorization, checked before validation.</summary>
    public Func<SyncCallContext, PushOperation<TDocument>, TDocument?, bool>? CanWrite { get; init; }

    /// <summary>Optional application validation: return an error code to reject the operation permanently.</summary>
    public Func<SyncCallContext, PushOperation<TDocument>, TDocument?, string?>? Validator { get; init; }

    /// <summary>
    /// Optional read membership (ADR-015): the principal keys that may read a document, computed at every write (replicated,
    /// handler or publisher). With it, each caller's pull reads only its own access rows (full pages), and documents that
    /// leave its view are listed as removals. A change of readers is a new version: republish a document after a membership
    /// change elsewhere. Set together with <see cref="PrincipalKey"/>.
    /// </summary>
    public Func<TDocument, IEnumerable<string>>? Readers { get; init; }

    /// <summary>The caller's principal key for <see cref="Readers"/> (for example a user id claim); <see langword="null"/> reads nothing.</summary>
    public Func<SyncCallContext, string?>? PrincipalKey { get; init; }

    /// <summary>
    /// Optional application logic that runs in the authority's transaction for every write about to be accepted
    /// (ADR-014). Without one, the submitted document is stored unchanged.
    /// </summary>
    public ISyncWriteHandler<TDocument>? WriteHandler { get; init; }

    /// <summary>
    /// How often the authority reads the collection's feed heads while it has <c>Committed</c> subscribers, to announce
    /// commits made by other processes (hints only, I13). Default 5 seconds; <see cref="TimeSpan.Zero"/> disables polling,
    /// for example when the host forwards commits from its own message bus with <c>NotifyCommitted</c>.
    /// </summary>
    public TimeSpan CommitPollInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Command timeout for the authority's statements. Default 30 seconds.</summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
