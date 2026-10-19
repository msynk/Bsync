using System.Data.Common;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Opt-in dependent publication on a caller-owned SQL Server transaction. The caller must already hold the Tasks
/// feed lock, acquire this lock before accessing domain rows, publish after SaveChanges, and notify after commit.
/// This helper never opens, commits, retries or disposes the caller's connection/transaction and performs no authorization.
/// </summary>
public sealed class TaskAudienceProjection(SqlServerSyncAuthority<AudienceTask> audience)
{
    public Task AcquireLockAsync(string tenant, DbTransaction transaction, CancellationToken cancellationToken = default) =>
        audience.AcquirePublicationLockAsync(tenant, transaction, cancellationToken);

    public async Task PublishAsync(DbConnection connection, DbTransaction transaction, string tenant, string taskId,
        CancellationToken cancellationToken = default)
    {
        if (connection is not SqlConnection sqlConnection || transaction is not SqlTransaction sqlTransaction ||
            !ReferenceEquals(transaction.Connection, connection))
            throw new ArgumentException("An active SQL Server transaction on the supplied connection is required.");
        // Reacquiring is safe only because participating writers acquired Tasks, then audience, before domain rows.
        await AcquireLockAsync(tenant, transaction, cancellationToken);
        await using var db = TasksDb.Enlist(connection, transaction);
        var row = await db.Tasks.AsNoTracking().SingleOrDefaultAsync(t => t.Tenant == tenant && t.Id == taskId, cancellationToken);
        if (row is null || row.Deleted)
        {
            await audience.DeleteAsync(tenant, taskId, transaction, cancellationToken);
            return;
        }
        var readers = await TaskAudiencePublication.ReadersAsync(sqlConnection, sqlTransaction, tenant, row.Id, 256, cancellationToken);
        await audience.UpsertAsync(tenant, TaskAudiencePublication.Project(row, readers.Select(r => r.Principal).ToArray()),
            transaction, cancellationToken);
    }

    /// <summary>May be subscribed to the Tasks authority's Committed event; hints never establish durable correctness.</summary>
    public void NotifyCommitted(AuthorityCommit commit) => audience.NotifyCommitted(commit);
}
