using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Microsoft.EntityFrameworkCore;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Executes intents (task F3) in the sync authority's transaction: the intent's acceptance, the change to
/// <c>dbo.Tasks</c> and the published task document commit together or not at all. That is what makes execution happen
/// at most once for a retained command identity. Transport receipts cover retries of one operation; the independent
/// command ledger also refuses re-execution after transport receipts/result documents are purged. Keep ledger identities.
/// </summary>
public sealed class IntentWriteHandler(SqlServerSyncAuthority<TaskDocument> tasks) : ISyncWriteHandler<TaskIntent>
{
    /// <summary>Optional dependency; participating hosts register the same projection on API and task writers.</summary>
    public TaskAudienceProjection? AudienceProjection { get; init; }

    public async ValueTask<SyncWriteDecision<TaskIntent>> HandleAsync(SyncWriteContext<TaskIntent> write, CancellationToken cancellationToken)
    {
        var intent = write.Submitted;
        if (intent.RequestSchema != 1) return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.UnsupportedSchema);
        if (intent.Deleted) return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.Immutable);

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
        // Global order: intent feed, task feed, optional audience feed, ledger/domain rows.
        await tasks.AcquirePublicationLockAsync(tenant, write.Transaction!, cancellationToken);
        if (AudienceProjection is not null)
            await AudienceProjection.AcquireLockAsync(tenant, write.Transaction!, cancellationToken);
        var fingerprint = IntentLedger.Fingerprint(intent, write.Caller.Principal.FindFirst("sub")?.Value);
        var prior = await IntentLedger.ReadAsync(write, cancellationToken);
        if (prior.Found)
        {
            if (prior.Hash != fingerprint) return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.Immutable);
            if (prior.Result is null) return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.ResultExpired);
            return SyncWriteDecision<TaskIntent>.Accept(System.Text.Json.JsonSerializer.Deserialize(prior.Result, TasksJson.Default.TaskIntent)!);
        }
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
            await IntentLedger.SaveAsync(write, fingerprint, cancellationToken);
            return SyncWriteDecision<TaskIntent>.Accept(intent);
        }

        // The immutable request snapshot and payload decoding use explicit generated registrations. This adapter
        // preserves the existing sample wire format and ledger fingerprint for previously accepted commands.
        Bsync.Client.IntentRequest request;
        try { request = TaskIntentDefinitions.Capture(intent); }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException)
        { return SyncWriteDecision<TaskIntent>.Reject(IntentCodes.InvalidRequest); }
        if (TaskIntentDefinitions.Complete.Matches(request))
        {
            _ = TaskIntentDefinitions.Complete.ReadPayload(request);
            row.Done = true;
        }
        else
        {
            var payload = TaskIntentDefinitions.Rename.ReadPayload(request);
            row.Title = payload.Title!;
            row.Slug = TaskRules.Slug(payload.Title!);
        }

        row.Revision++;
        row.ChangedBy = write.Caller.Principal.FindFirst("sub")?.Value;
        await db.SaveChangesAsync(cancellationToken);
        var attachments = await db.TaskAttachments.Where(a => a.Tenant == tenant && a.TaskId == row.Id).ToListAsync(cancellationToken);
        await tasks.UpsertAsync(tenant, row.ToDocument(attachments), write.Transaction, cancellationToken);
        if (AudienceProjection is not null)
            await AudienceProjection.PublishAsync(write.Connection!, write.Transaction!, tenant, row.Id, cancellationToken);

        intent.State = IntentStates.Executed;
        intent.ExecutedBy = row.ChangedBy;
        await IntentLedger.SaveAsync(write, fingerprint, cancellationToken);
        return SyncWriteDecision<TaskIntent>.Accept(intent);
    }
}
