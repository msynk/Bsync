using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Executes intents (task F3) in the sync authority's transaction: the intent's acceptance, the change to
/// <c>dbo.Tasks</c> and the published task document commit together or not at all. That is what makes execution happen
/// exactly once: a replica whose response was lost resends the same operation, and the authority answers it from the
/// receipt of the first one without calling this handler again.
/// </summary>
public sealed class IntentWriteHandler(SqlServerSyncAuthority<TaskDocument> tasks) : ISyncWriteHandler<TaskIntent>
{
    public async ValueTask<SyncWriteDecision<TaskIntent>> HandleAsync(SyncWriteContext<TaskIntent> write, CancellationToken cancellationToken)
    {
        var intent = write.Submitted;

        // Intents are immutable: a client may create one, never change or delete it.
        if (write.Current is not null)
        {
            return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.Immutable, "Intents cannot be changed; create a new one.");
        }

        // Execution state is the server's. Whatever the client sent is replaced, and identities come from the caller.
        intent.State = IntentStates.Pending;
        intent.Code = null;
        intent.ExecutedBy = null;

        await using var db = TasksDb.Enlist(write.Connection!, write.Transaction!);
        var tenant = write.Caller.Scope;
        var row = await db.Tasks.SingleOrDefaultAsync(t => t.Tenant == tenant && t.Id == intent.TaskId, cancellationToken);
        if (row is null)
        {
            // The task may still be on its way from the same device: not an error yet. No receipt is stored, so the
            // replica sends the same intent again later and it is decided then (task D3).
            return SyncWriteDecision<TaskIntent>.RetryLater(PushErrorCodes.DependencyMissing, "The task has not reached the server yet.");
        }

        var rejection =
            row.Deleted ? IntentCodes.TargetDeleted
            : intent.TaskRevision is { } seen && seen != row.Revision ? IntentCodes.TargetChanged
            : intent.Kind switch
            {
                IntentKinds.Complete => null,
                IntentKinds.Rename when string.IsNullOrWhiteSpace(intent.Title) => TaskRules.TitleRequired,
                IntentKinds.Rename => null,
                _ => IntentCodes.UnknownKind,
            };
        if (rejection is not null)
        {
            // Accepted (it is kept and replicated) but not executed: the code stays on the intent for the UI.
            intent.State = IntentStates.Rejected;
            intent.Code = rejection;
            return SyncWriteDecision<TaskIntent>.Accept(intent);
        }

        if (intent.Kind == IntentKinds.Complete)
        {
            row.Done = true;
        }
        else
        {
            row.Title = intent.Title!;
            row.Slug = TaskRules.Slug(intent.Title!);
        }

        row.Revision++;
        row.ChangedBy = write.Caller.Principal.FindFirst("sub")?.Value;
        await db.SaveChangesAsync(cancellationToken);
        var attachments = await db.TaskAttachments.Where(a => a.Tenant == tenant && a.TaskId == row.Id).ToListAsync(cancellationToken);
        await tasks.UpsertAsync(tenant, row.ToDocument(attachments), write.Transaction, cancellationToken);

        intent.State = IntentStates.Executed;
        intent.ExecutedBy = row.ChangedBy;
        return SyncWriteDecision<TaskIntent>.Accept(intent);
    }
}
