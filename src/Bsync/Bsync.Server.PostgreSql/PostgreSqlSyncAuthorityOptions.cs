using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Bsync.Protocol;
using Npgsql;

namespace Bsync.Server.PostgreSql;

/// <summary>Configuration for <see cref="PostgreSqlSyncAuthority{TDocument}"/>.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class PostgreSqlSyncAuthorityOptions<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>The data source (connection pool). Share one per database in the process.</summary>
    public required NpgsqlDataSource DataSource { get; init; }

    /// <summary>Source-generated JSON metadata for the document type. Documents are stored as their exact JSON text.</summary>
    public required JsonTypeInfo<TDocument> DocumentType { get; init; }

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
}
