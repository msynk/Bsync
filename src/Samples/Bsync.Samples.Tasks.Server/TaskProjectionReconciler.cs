using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Complete current-state reconciliation for a bounded tenant dataset. Every domain/attachment writer must acquire
/// the same task feed lock before touching domain rows. Holding that boundary through commit eliminates the snapshot/
/// catch-up gap; this intentionally trades writer availability for a simple, atomic repair. Not an online large-feed rebuild.
/// </summary>
public sealed class TaskProjectionReconciler(string connectionString, SqlServerSyncAuthority<TaskDocument> authority)
{
    /// <summary>
    /// Replaces only a complete tenant snapshot, including attachment changes and absent/deleted tasks, then consumes
    /// the tenant's covered outbox requests in the same transaction. Limits refuse before publication, never truncate.
    /// The tenant is supplied by trusted server code. Cancellation/failure rolls back; rerunning is idempotent.
    /// </summary>
    public async Task<SyncPublishResult> ReconcileAsync(string tenant, int maximumTasks = 1000, int maximumAttachments = 10000,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant);
        if (maximumTasks is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumTasks));
        if (maximumAttachments is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumAttachments));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction();
        await authority.AcquirePublicationLockAsync(tenant, transaction, cancellationToken);
        await using var db = TasksDb.Enlist(connection, transaction);
        var rows = await db.Tasks.AsNoTracking().Where(t => t.Tenant == tenant && !t.Deleted)
            .OrderBy(t => t.Id).Take(maximumTasks + 1).ToListAsync(cancellationToken);
        if (rows.Count > maximumTasks) throw new InvalidOperationException("Tenant exceeds the bounded reconciliation limit; no feed replacement was attempted.");
        // Join in SQL so the application's configured collation/relationship semantics match ordinary domain reads.
        var attachments = await (from task in db.Tasks.AsNoTracking()
                                 join attachment in db.TaskAttachments.AsNoTracking()
                                     on new { task.Tenant, TaskId = task.Id } equals new { attachment.Tenant, attachment.TaskId }
                                 where task.Tenant == tenant && !task.Deleted
                                 orderby task.Id, attachment.AttachmentId
                                 select new { Id = task.Id, Attachment = attachment })
            .Take(maximumAttachments + 1).ToListAsync(cancellationToken);
        if (attachments.Count > maximumAttachments) throw new InvalidOperationException("Tenant attachments exceed the bounded reconciliation limit; no feed replacement was attempted.");
        var byTask = attachments.ToLookup(a => a.Id, a => a.Attachment, StringComparer.Ordinal);
        var result = await authority.ReplaceScopeAsync(tenant, rows.Select(row => row.ToDocument(byTask[row.Id])), transaction, cancellationToken);
        await using var consume = new SqlCommand("DELETE dbo.TaskProjectionOutbox WHERE Tenant=@tenant", connection, transaction);
        consume.Parameters.AddWithValue("@tenant", tenant);
        await consume.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        authority.NotifyCommitted(new AuthorityCommit(tenant, []));
        return result;
    }
}
