using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Bsync.Clocks;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>Optional read-only projection. Reader identities are derived from server ACL rows, never a client submission.</summary>
public sealed class AudienceTask : ISyncEntity
{
    public string Id { get; set; } = "";
    public HlcTimestamp UpdatedAt { get; set; }
    public bool Deleted { get; set; }
    public string Title { get; set; } = "";
    public bool Done { get; set; }
    public long Revision { get; set; }
    public string[] Readers { get; set; } = [];
}

[JsonSerializable(typeof(AudienceTask))]
public partial class AudienceTaskJson : JsonSerializerContext;

/// <summary>
/// Explicit related-table membership publication recipe, opt-in and separate from the tenant-wide Tasks feed.
/// Every writer acquires the Tasks feed before this audience feed, then domain/ACL rows. Repairs hold both boundaries
/// through commit, so catch-up is empty; this bounded recipe deliberately blocks writers rather than claiming online rebuilding.
/// </summary>
public sealed class TaskAudiencePublication(string connectionString, SqlServerSyncAuthority<TaskDocument> tasks, SqlServerSyncAuthority<AudienceTask> audience)
{
    public static Task<SqlServerSyncAuthority<AudienceTask>> CreateAuthorityAsync(string connectionString, CancellationToken cancellationToken = default) =>
        SqlServerSyncAuthority<AudienceTask>.CreateAsync(new()
        {
            ConnectionString = connectionString, Collection = "task-audience", DocumentType = AudienceTaskJson.Default.AudienceTask,
            Readers = document => document.Readers, PrincipalKey = caller => caller.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            CanWrite = (_, _, _) => false,
        }, cancellationToken);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            IF OBJECT_ID(N'dbo.TaskReaders',N'U') IS NULL
            CREATE TABLE dbo.TaskReaders(
                Tenant nvarchar(256) NOT NULL, TaskId nvarchar(256) NOT NULL,
                PrincipalKey varbinary(512) NOT NULL, PrincipalId nvarchar(256) NOT NULL,
                CONSTRAINT PK_TaskReaders PRIMARY KEY NONCLUSTERED(Tenant,TaskId,PrincipalKey));
            """, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Changes server-owned ACL rows and republishes grants/removals in the same transaction. Host must authorize this operation.</summary>
    public async Task SetReadersAsync(string tenant, string taskId, IReadOnlyList<string> readers, CancellationToken cancellationToken = default)
    {
        SyncIds.Validate(tenant); SyncIds.Validate(taskId); ArgumentNullException.ThrowIfNull(readers);
        var copy = readers.ToArray();
        if (copy.Length > 256) throw new ArgumentException("At most 256 readers per task.", nameof(readers));
        foreach (var reader in copy) SyncIds.Validate(reader);
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await LockAsync(tenant, transaction, cancellationToken);
        await using var delete = new SqlCommand("DELETE dbo.TaskReaders WHERE Tenant=@tenant AND TaskId=@id", connection, transaction);
        delete.Parameters.AddWithValue("@tenant", tenant); delete.Parameters.AddWithValue("@id", taskId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        foreach (var reader in copy.Distinct(StringComparer.Ordinal))
        {
            await using var insert = new SqlCommand("INSERT dbo.TaskReaders(Tenant,TaskId,PrincipalKey,PrincipalId) VALUES(@tenant,@id,@key,@principal)", connection, transaction);
            insert.Parameters.AddWithValue("@tenant", tenant); insert.Parameters.AddWithValue("@id", taskId);
            insert.Parameters.AddWithValue("@key", Encoding.BigEndianUnicode.GetBytes(reader)); insert.Parameters.AddWithValue("@principal", reader);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await PublishAsync(connection, transaction, tenant, taskId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        audience.NotifyCommitted(new AuthorityCommit(tenant, [taskId]));
    }

    /// <summary>Call after acquiring AcquirePublicationLocksAsync before domain mutations and after SaveChanges, before commit. Notify only after commit.</summary>
    public async Task PublishAsync(SqlConnection connection, SqlTransaction transaction, string tenant, string taskId, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(transaction.Connection, connection)) throw new ArgumentException("Connection and transaction must match.");
        await LockAsync(tenant, transaction, cancellationToken);
        await using var db = TasksDb.Enlist(connection, transaction);
        var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Tenant == tenant && t.Id == taskId, cancellationToken);
        if (row is null || row.Deleted) { await audience.DeleteAsync(tenant, taskId, transaction, cancellationToken); return; }
        var readers = await ReadersAsync(connection, transaction, tenant, row.Id, 256, cancellationToken);
        await audience.UpsertAsync(tenant, Project(row, readers.Select(r => r.Principal).ToArray()), transaction, cancellationToken);
    }

    /// <summary>Reconciles deletions and all related reader rows from one complete protected source snapshot; never publishes a truncated enumeration.</summary>
    public async Task ReconcileAsync(string tenant, int maximumTasks = 1000, int maximumReaders = 10000, CancellationToken cancellationToken = default)
    {
        if (maximumTasks is < 1 or > 10000 || maximumReaders is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumTasks));
        await using var connection = new SqlConnection(connectionString); await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await LockAsync(tenant, transaction, cancellationToken);
        await using var db = TasksDb.Enlist(connection, transaction);
        var rows = await db.Tasks.AsNoTracking().Where(t => t.Tenant == tenant && !t.Deleted).OrderBy(t => t.Id).Take(maximumTasks + 1).ToArrayAsync(cancellationToken);
        if (rows.Length > maximumTasks) throw new InvalidOperationException("Task reconciliation limit exceeded; prior feed retained.");
        var readers = await ReadersAsync(connection, transaction, tenant, null, maximumReaders, cancellationToken);
        var byTask = readers.ToLookup(r => r.TaskId, r => r.Principal, StringComparer.Ordinal);
        if (rows.Any(row => byTask[row.Id].Count() > 256)) throw new InvalidDataException("Task reader limit exceeded; prior feed retained.");
        await audience.ReplaceScopeAsync(tenant, rows.Select(row => Project(row, byTask[row.Id].ToArray())), transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        audience.NotifyCommitted(new AuthorityCommit(tenant, []));
    }

    /// <summary>Every registered writer acquires both feed boundaries before touching domain or ACL rows.</summary>
    public async Task AcquirePublicationLocksAsync(string tenant, SqlTransaction transaction, CancellationToken cancellationToken = default)
    {
        await tasks.AcquirePublicationLockAsync(tenant, transaction, cancellationToken);
        await audience.AcquirePublicationLockAsync(tenant, transaction, cancellationToken);
    }
    private Task LockAsync(string tenant, SqlTransaction transaction, CancellationToken ct) => AcquirePublicationLocksAsync(tenant, transaction, ct);
    private static AudienceTask Project(TaskEntity row, string[] readers) => new() { Id = row.Id, Title = row.Title, Done = row.Done, Revision = row.Revision, Readers = readers };
    private static async Task<List<(string TaskId, string Principal)>> ReadersAsync(SqlConnection connection, SqlTransaction transaction, string tenant, string? taskId, int limit, CancellationToken ct)
    {
        // Join yields the canonical domain ID under its SQL collation; principal keys themselves stay ordinal binary values.
        await using var command = new SqlCommand("""
            SELECT TOP (@limit) t.Id,r.PrincipalId FROM dbo.TaskReaders r JOIN dbo.Tasks t ON t.Tenant=r.Tenant AND t.Id=r.TaskId
            WHERE t.Tenant=@tenant AND t.Deleted=0 AND (@id IS NULL OR t.Id=@id) ORDER BY t.Id,r.PrincipalKey
            """, connection, transaction);
        command.Parameters.AddWithValue("@limit", limit + 1); command.Parameters.AddWithValue("@tenant", tenant);
        command.Parameters.Add("@id", System.Data.SqlDbType.NVarChar, 256).Value = (object?)taskId ?? DBNull.Value;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<(string, string)>();
        while (await reader.ReadAsync(ct)) result.Add((reader.GetString(0), reader.GetString(1)));
        if (result.Count > limit) throw new InvalidOperationException("Reader reconciliation limit exceeded; prior feed retained.");
        return result;
    }
}
