using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>A workflow step's durable outcome. Retryable failures remain pending; permanent failures require explicit host action.</summary>
public enum WorkflowStepOutcome
{
    /// <summary>The idempotent effect is complete.</summary>
    Complete,
    /// <summary>The dependency is not currently available; stop this drain without spinning.</summary>
    RetryLater,
    /// <summary>A domain decision or repair is needed.</summary>
    Failed,
}

/// <summary>One registered step. Implementations must deduplicate using the supplied stable operation identity.</summary>
/// <param name="Name">Stable step name, unchanged across retries.</param>
/// <param name="ExecuteAsync">Effect outside the metadata transaction; may be repeated after a crash or lost reply.</param>
public sealed record WorkflowStep(string Name, Func<string, CancellationToken, Task<WorkflowStepOutcome>> ExecuteAsync);

/// <summary>Versioned durable progress. It is not a second document outbox or a distributed rollback mechanism.</summary>
/// <param name="SchemaVersion">Persistence schema.</param>
/// <param name="DefinitionVersion">Host workflow definition version.</param>
/// <param name="Steps">Ordered stable step names.</param>
/// <param name="NextStep">First unconfirmed step.</param>
/// <param name="State">pending, running, failed or complete.</param>
public sealed record WorkflowProgress(int SchemaVersion, string DefinitionVersion, string[] Steps, int NextStep, string State);

/// <summary>
/// A persisted, replayable sequence for work spanning collections/blobs. Persist first, then perform one idempotent
/// effect, then record completion with an execution fence. Effects and progress are not atomically committed together.
/// A crash in that gap repeats the effect with the same identity. Use an aggregate document for one atomic editable unit.
/// </summary>
public sealed class RecoverableWorkflow
{
    private readonly IOrchestrationStore _store;
    private readonly TimeProvider _time;
    private readonly AccountScope _account;
    /// <summary>Uses one account's durable orchestration store.</summary>
    public RecoverableWorkflow(IOrchestrationStore store, AccountScope account, TimeProvider? timeProvider = null)
    { _store = store ?? throw new ArgumentNullException(nameof(store)); _account = account ?? throw new ArgumentNullException(nameof(account)); _time = timeProvider ?? TimeProvider.System; }
    private static string Identity(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (id.Length > 256) throw new ArgumentException("Workflow identity exceeds 256 characters.", nameof(id));
        return Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(id)));
    }
    private static string Encode(WorkflowProgress progress) => JsonSerializer.Serialize(progress, WorkflowJson.Default.WorkflowProgress);
    private static WorkflowProgress Decode(OrchestrationRecord record)
    {
        var progress = JsonSerializer.Deserialize(record.Payload, WorkflowJson.Default.WorkflowProgress)!;
        if (progress.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported workflow schema; progress was retained.");
        if (progress.NextStep < 0 || progress.NextStep > progress.Steps.Length) throw new InvalidDataException("Invalid workflow progress.");
        return progress;
    }
    /// <summary>Reads progress without executing effects.</summary>
    public async Task<WorkflowProgress?> GetAsync(string workflowId, CancellationToken cancellationToken = default) =>
        await _store.ReadAsync("workflow/" + Identity(workflowId), cancellationToken).ConfigureAwait(false) is { } record ? Decode(record) : null;

    /// <summary>
    /// Creates/resumes one workflow within a cooperative deadline. Returns null if another executor owns it. Exceptions
    /// leave the current step recoverable. A failed step remains failed until RetryFailedAsync. Changing definitions fails.
    /// </summary>
    public async Task<WorkflowProgress?> RunAsync(string workflowId, string definitionVersion, IReadOnlyList<WorkflowStep> steps,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionVersion);
        ArgumentNullException.ThrowIfNull(steps);
        if (definitionVersion.Length > 128 || steps.Count is < 1 or > 128 || steps.Any(s => s is null || string.IsNullOrWhiteSpace(s.Name) || s.Name.Length > 128 || s.ExecuteAsync is null)
            || steps.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != steps.Count) throw new ArgumentException("Invalid workflow definition.", nameof(steps));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        var registered = steps.ToArray(); var names = registered.Select(s => s.Name).ToArray(); var id = Identity(workflowId); var key = "workflow/" + id;
        await using var lease = await ExecutionLease.TryAcquireAsync(_store, "workflow-owner/" + id, TimeSpan.FromSeconds(30), _time, cancellationToken).ConfigureAwait(false);
        if (lease is null) return null;
        using var deadline = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, lease.Lost);
        var record = await _store.ReadAsync(key, linked.Token).ConfigureAwait(false);
        if (record is null)
        {
            var created = new WorkflowProgress(1, definitionVersion, names, 0, "pending");
            if (!await _store.TryWriteAsync([new(key, null, Encode(created))], lease.Fence, linked.Token).ConfigureAwait(false)) return null;
            record = (await _store.ReadAsync(key, linked.Token).ConfigureAwait(false))!;
        }
        var progress = Decode(record);
        if (progress.DefinitionVersion != definitionVersion || !progress.Steps.SequenceEqual(names))
            throw new InvalidOperationException("Workflow definition changed; retain the registered version until existing work completes.");
        if (progress.State is "complete" or "failed") return progress;
        try
        {
            while (progress.NextStep < registered.Length)
            {
                linked.Token.ThrowIfCancellationRequested();
                progress = progress with { State = "running" };
                if (!await _store.TryWriteAsync([new(key, record.Revision, Encode(progress))], lease.Fence, linked.Token).ConfigureAwait(false)) return await GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
                record = (await _store.ReadAsync(key, linked.Token).ConfigureAwait(false))!;
                // This identity never changes on retry, lease takeover or process restart.
                var operation = _account.StorageKey + "/" + id + "/" + progress.NextStep.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var outcome = await registered[progress.NextStep].ExecuteAsync(operation, linked.Token).ConfigureAwait(false);
                if (!Enum.IsDefined(outcome)) throw new InvalidOperationException("Unknown workflow step outcome.");
                progress = outcome switch
                {
                    WorkflowStepOutcome.Complete => progress with { NextStep = progress.NextStep + 1, State = progress.NextStep + 1 == registered.Length ? "complete" : "pending" },
                    WorkflowStepOutcome.RetryLater => progress with { State = "pending" },
                    _ => progress with { State = "failed" },
                };
                if (!await _store.TryWriteAsync([new(key, record.Revision, Encode(progress))], lease.Fence, linked.Token).ConfigureAwait(false)) return await GetAsync(workflowId, cancellationToken).ConfigureAwait(false);
                if (outcome != WorkflowStepOutcome.Complete) return progress;
                record = (await _store.ReadAsync(key, linked.Token).ConfigureAwait(false))!;
            }
            return progress;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return await GetAsync(workflowId, cancellationToken).ConfigureAwait(false); }
    }
    /// <summary>Re-enables a failed step after explicit host repair/decision. Does not undo completed effects.</summary>
    public async Task<bool> RetryFailedAsync(string workflowId, CancellationToken cancellationToken = default)
    {
        var key = "workflow/" + Identity(workflowId);
        var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null) return false;
        var progress = Decode(record);
        return progress.State == "failed" && await _store.TryWriteAsync([new(key, record.Revision, Encode(progress with { State = "pending" }))], cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

[JsonSerializable(typeof(WorkflowProgress))]
internal partial class WorkflowJson : JsonSerializerContext;
