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
        row.ChangedBy = write.Caller.Principal.FindFirst("sub")?.Value;
        await db.SaveChangesAsync(cancellationToken);

        return SyncWriteDecision<TaskDocument>.Accept(task);
    }
}
