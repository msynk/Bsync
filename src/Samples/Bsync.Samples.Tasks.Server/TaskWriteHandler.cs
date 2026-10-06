using Bsync.Protocol;
using Bsync.Server;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Runs in the sync authority's transaction for every replicated write that would be accepted (ADR-014): enforces
/// <see cref="TaskRules"/>, recomputes <see cref="TaskDocument.Slug"/>, and saves the row through EF Core, so the
/// application's table and the replication feed commit together or not at all.
/// </summary>
public sealed class TaskWriteHandler : ISyncWriteHandler<TaskDocument>
{
    public async ValueTask<SyncWriteDecision<TaskDocument>> HandleAsync(SyncWriteContext<TaskDocument> write, CancellationToken cancellationToken)
    {
        var task = write.Submitted;
        if (!task.Deleted && string.IsNullOrWhiteSpace(task.Title))
        {
            return SyncWriteDecision<TaskDocument>.Reject(TaskRules.TitleRequired, "A task needs a title.");
        }

        task.Slug = TaskRules.Slug(task.Title);

        await using var db = TasksDb.Enlist(write.Connection!, write.Transaction!);
        var tenant = write.Caller.Scope;

        // Attachments (task F1): the bytes must be on the server, verified, uploaded by this tenant. A client uploads them
        // before it sends the task; if one is missing anyway (another device's upload still running), try again later.
        var hashes = task.Deleted ? [] : task.Attachments.Select(a => a.Sha256).Distinct(StringComparer.Ordinal).ToList();
        var held = await db.TenantBlobs.Where(b => b.Tenant == tenant && hashes.Contains(b.Sha256)).ToDictionaryAsync(b => b.Sha256, b => b.Size, cancellationToken);
        if (task.Attachments.Any(a => !held.TryGetValue(a.Sha256, out var size) || size != a.Size) && !task.Deleted)
        {
            return SyncWriteDecision<TaskDocument>.RetryLater(PushErrorCodes.DependencyMissing, "An attachment has not been uploaded yet.");
        }
        var row = await db.Tasks.SingleOrDefaultAsync(t => t.Tenant == tenant && t.Id == task.Id, cancellationToken);
        if (row is null)
        {
            row = new TaskEntity { Tenant = tenant, Id = task.Id };
            db.Tasks.Add(row);
        }

        row.Title = task.Title;
        row.Slug = task.Slug;
        row.Done = task.Done;
        row.Deleted = task.Deleted;
        task.Revision = ++row.Revision;
        row.ChangedBy = write.Caller.Principal.FindFirst("sub")?.Value;
        db.TaskAttachments.RemoveRange(await db.TaskAttachments.Where(a => a.Tenant == tenant && a.TaskId == task.Id).ToListAsync(cancellationToken));
        if (!task.Deleted)
        {
            db.TaskAttachments.AddRange(task.Attachments.DistinctBy(a => a.Id, StringComparer.Ordinal).Select(a => new TaskAttachment
            {
                Tenant = tenant, TaskId = task.Id, AttachmentId = a.Id, Sha256 = a.Sha256, Size = a.Size, ContentType = a.ContentType, FileName = a.FileName,
            }));
        }

        await db.SaveChangesAsync(cancellationToken);

        return SyncWriteDecision<TaskDocument>.Accept(task);
    }
}
