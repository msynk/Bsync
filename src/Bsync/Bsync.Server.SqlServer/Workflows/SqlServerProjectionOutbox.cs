using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer.Workflows;

/// <summary>Durable coalesced projection work. Application callbacks own canonical reads, membership and feed lock order.</summary>
public sealed class SqlServerProjectionOutbox
{
    private readonly string _connectionString;
    private readonly string _projection;
    /// <summary>Explicit deployment migration. Requests and workers never create tables.</summary>
    public static string MigrationSql { get; } = WorkflowSchema.Migration("projection-outbox", """
        IF OBJECT_ID(N'dbo.BsyncProjectionOutbox', N'U') IS NULL
        CREATE TABLE dbo.BsyncProjectionOutbox (
            WorkKey char(64) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
            Projection nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Scope nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            DocumentId nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
            Generation uniqueidentifier NOT NULL, RequestedAt datetimeoffset NOT NULL,
            Attempts int NOT NULL DEFAULT 0, RetryAt datetimeoffset NULL, Poisoned bit NOT NULL DEFAULT 0
        );
        """);
    /// <summary>Creates a worker for one configured projection schema/route. It owns no application transaction.</summary>
    public SqlServerProjectionOutbox(string connectionString, string projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        if (!SyncIds.IsValid(projection)) throw new ArgumentException("Invalid projection identity.", nameof(projection));
        _connectionString = connectionString; _projection = projection;
    }
    private string Key(string scope, string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"{_projection.Length}:{_projection}{scope.Length}:{scope}{id.Length}:{id}"))));
    /// <summary>Enqueues or coalesces within the caller's domain transaction, after its publication locks.</summary>
    public async Task RequestAsync(SqlConnection connection, SqlTransaction transaction, string scope, string documentId, CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(transaction.Connection, connection)) throw new ArgumentException("Use the caller's transaction connection.");
        await WorkflowSchema.Validate(connection, transaction, "projection-outbox", cancellationToken).ConfigureAwait(false);
        if (!SyncIds.IsValid(scope) || !SyncIds.IsValid(documentId)) throw new ArgumentException("Invalid projection identity.");
        await using var command = new SqlCommand("""
            UPDATE dbo.BsyncProjectionOutbox WITH (UPDLOCK,HOLDLOCK)
            SET Generation=NEWID(),Attempts=0,RetryAt=NULL,Poisoned=0 WHERE WorkKey=@key;
            IF @@ROWCOUNT=0 INSERT dbo.BsyncProjectionOutbox(WorkKey,Projection,Scope,DocumentId,Generation,RequestedAt)
                VALUES(@key,@projection,@scope,@id,NEWID(),SYSUTCDATETIME());
            """, connection, transaction);
        command.Parameters.AddWithValue("@key", Key(scope, documentId)); command.Parameters.AddWithValue("@projection", _projection);
        command.Parameters.AddWithValue("@scope", scope); command.Parameters.AddWithValue("@id", documentId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Claims by generation under SQL transaction locks, rereads canonical state through the callback and commits
    /// publication plus acknowledgement together. Both callbacks must use this exact transaction, perform no external
    /// I/O, and acquire authority feeds in deterministic order before domain rows. Five failures poison only that generation.
    /// A lost commit reply is safe: the consumed generation is absent. Returns committed publication count.
    /// </summary>
    public async Task<int> DrainAsync(
        Func<SqlConnection, SqlTransaction, string, CancellationToken, Task> acquirePublicationLocks,
        Func<SqlConnection, SqlTransaction, string, string, CancellationToken, Task> publishCanonical,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(acquirePublicationLocks); ArgumentNullException.ThrowIfNull(publishCanonical);
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = new List<(string Key, string Scope, string Id, Guid Generation)>();
        await using (var connection = new SqlConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await WorkflowSchema.Validate(connection, null, "projection-outbox", cancellationToken).ConfigureAwait(false);
            await using var command = new SqlCommand("""
                SELECT TOP (@limit) WorkKey,Scope,DocumentId,Generation FROM dbo.BsyncProjectionOutbox
                WHERE Projection=@projection AND Poisoned=0 AND (RetryAt IS NULL OR RetryAt<=SYSUTCDATETIME())
                ORDER BY RequestedAt,WorkKey
                """, connection);
            command.Parameters.AddWithValue("@limit", limit); command.Parameters.AddWithValue("@projection", _projection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3)));
        }
        var count = 0;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var transaction = connection.BeginTransaction();
                await acquirePublicationLocks(connection, transaction, item.Scope, cancellationToken).ConfigureAwait(false);
                await using var claim = new SqlCommand("DELETE dbo.BsyncProjectionOutbox WHERE WorkKey=@key AND Generation=@generation", connection, transaction);
                claim.Parameters.AddWithValue("@key", item.Key); claim.Parameters.AddWithValue("@generation", item.Generation);
                if (await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) continue;
                await publishCanonical(connection, transaction, item.Scope, item.Id, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                WorkflowSchema.Record("projection", "published");
                count++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                await using var connection = new SqlConnection(_connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var failure = new SqlCommand("""
                    UPDATE dbo.BsyncProjectionOutbox SET Attempts=Attempts+1,Poisoned=CASE WHEN Attempts>=4 THEN 1 ELSE 0 END,
                    RetryAt=DATEADD(second,30,SYSUTCDATETIME()) WHERE WorkKey=@key AND Generation=@generation
                    """, connection);
                failure.Parameters.AddWithValue("@key", item.Key); failure.Parameters.AddWithValue("@generation", item.Generation);
                await failure.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return count;
    }
}
