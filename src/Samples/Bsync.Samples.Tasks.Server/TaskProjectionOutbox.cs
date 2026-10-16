using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Optional coalesced current-state publication recipe. The domain transaction requests a refresh; dispatch rebuilds
/// the latest projection instead of replaying stale event payloads. This is eventual publication, not atomic domain/feed
/// publication. Every domain writer uses task-feed-before-domain lock ordering. No external calls occur in transactions.
/// </summary>
public sealed class TaskProjectionOutbox(string connectionString, SqlServerSyncAuthority<TaskDocument> authority)
{
    private static readonly System.Diagnostics.Metrics.Meter Meter = new("Bsync.Samples.Tasks");
    private static readonly System.Diagnostics.Metrics.Histogram<double> Lag = Meter.CreateHistogram<double>(
        "bsync.sample.publication.lag", "s", "Delay from the oldest coalesced request to committed publication; excludes unregistered domain writes.");
    /// <summary>Enqueues in the caller's domain transaction. Call after acquiring the task feed and before committing.</summary>
    public static async Task RequestAsync(SqlConnection connection, SqlTransaction transaction, string tenant, string taskId, CancellationToken cancellationToken = default)
    {
        await using var command = new SqlCommand("""
            UPDATE dbo.TaskProjectionOutbox WITH (UPDLOCK,HOLDLOCK)
            SET Generation=NEWID(),Attempts=0,RetryAt=NULL,Poisoned=0
            WHERE Tenant=@tenant AND TaskId=@id;
            IF @@ROWCOUNT=0 INSERT dbo.TaskProjectionOutbox(Tenant,TaskId,Generation,RequestedAt)
                VALUES(@tenant,@id,NEWID(),SYSUTCDATETIME());
            """, connection, transaction);
        command.Parameters.AddWithValue("@tenant", tenant); command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Dispatches at most 1000 refreshes. One poison item does not block unrelated scopes. Returns committed publications.</summary>
    public async Task<int> DrainAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = new List<(string Tenant, string Id, Guid Generation, DateTimeOffset RequestedAt)>();
        await using (var connection = new SqlConnection(connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var read = new SqlCommand("""
                SELECT TOP (@limit) Tenant,TaskId,Generation,RequestedAt FROM dbo.TaskProjectionOutbox
                WHERE Poisoned=0 AND (RetryAt IS NULL OR RetryAt<=SYSUTCDATETIME())
                ORDER BY RequestedAt,Tenant,TaskId
                """, connection);
            read.Parameters.AddWithValue("@limit", limit);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.GetDateTimeOffset(3)));
        }
        var completed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = connection.BeginTransaction();
                await authority.AcquirePublicationLockAsync(candidate.Tenant, transaction, cancellationToken);
                // A request updated since enumeration is left for a later drain. Its current-state projection is never
                // overwritten by a stale event payload, and deleting this generation is atomic with feed publication.
                await using var take = new SqlCommand("DELETE dbo.TaskProjectionOutbox WHERE Tenant=@tenant AND TaskId=@id AND Generation=@generation", connection, transaction);
                Parameters(take, (candidate.Tenant, candidate.Id, candidate.Generation));
                if (await take.ExecuteNonQueryAsync(cancellationToken) == 0) continue;
                await using var db = TasksDb.Enlist(connection, transaction);
                var row = await db.Tasks.SingleOrDefaultAsync(t => t.Tenant == candidate.Tenant && t.Id == candidate.Id, cancellationToken);
                if (row is null || row.Deleted) await authority.DeleteAsync(candidate.Tenant, candidate.Id, transaction, cancellationToken);
                else
                {
                    var attachments = await db.TaskAttachments.Where(a => a.Tenant == candidate.Tenant && a.TaskId == candidate.Id).ToListAsync(cancellationToken);
                    await authority.UpsertAsync(candidate.Tenant, row.ToDocument(attachments), transaction, cancellationToken);
                }
                await transaction.CommitAsync(cancellationToken);
                completed++;
                Lag.Record(Math.Max(0, (DateTimeOffset.UtcNow - candidate.RequestedAt).TotalSeconds));
                authority.NotifyCommitted(new AuthorityCommit(candidate.Tenant, [candidate.Id]));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // If commit succeeded but acknowledgement/notification failed, the deleted generation is absent and
                // this conditional update does nothing. No exception text (possibly containing domain data) is stored.
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var failed = new SqlCommand("""
                    UPDATE dbo.TaskProjectionOutbox SET Attempts=Attempts+1,
                        Poisoned=CASE WHEN Attempts>=4 THEN 1 ELSE 0 END,
                        RetryAt=DATEADD(second,30,SYSUTCDATETIME())
                    WHERE Tenant=@tenant AND TaskId=@id AND Generation=@generation
                    """, connection);
                Parameters(failed, (candidate.Tenant, candidate.Id, candidate.Generation));
                await failed.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        return completed;
    }
    private static void Parameters(SqlCommand command, (string Tenant, string Id, Guid Generation) item)
    {
        command.Parameters.AddWithValue("@tenant", item.Tenant); command.Parameters.AddWithValue("@id", item.Id);
        command.Parameters.AddWithValue("@generation", item.Generation);
    }
}
