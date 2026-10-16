using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>Host execution capability, not a guarantee of delivery or frequency.</summary>
/// <param name="Headless">Can run without a live UI.</param>
/// <param name="NetworkConstraint">Can request network availability.</param>
/// <param name="ChargingConstraint">Can request charging.</param>
public sealed record BackgroundCapabilities(bool Headless, bool NetworkConstraint, bool ChargingConstraint);

/// <summary>Constraints requested from the host; unsupported constraints must be reported, never silently promised.</summary>
/// <param name="RequiresNetwork">Wait for network when the host supports it.</param>
/// <param name="RequiresCharging">Wait for charging when the host supports it.</param>
public sealed record BackgroundConstraints(bool RequiresNetwork = true, bool RequiresCharging = false);

/// <summary>Outcome of scheduling or executing bounded account work.</summary>
public enum BackgroundWorkOutcome
{
    /// <summary>The host accepted scheduling; this is not execution evidence.</summary>
    Scheduled,
    /// <summary>All requested work was completed.</summary>
    Completed,
    /// <summary>More work remains, including a competing execution owner.</summary>
    RemainingWork,
    /// <summary>The host declined/delayed execution.</summary>
    DeferredByHost,
    /// <summary>Valid network credentials are not available.</summary>
    CredentialsUnavailable,
    /// <summary>Local keys are locked/unavailable.</summary>
    KeyUnavailable,
    /// <summary>The host cannot satisfy the requested capability.</summary>
    Unsupported,
    /// <summary>Execution was canceled; durable work is preserved.</summary>
    Canceled,
    /// <summary>Execution failed; durable work is preserved.</summary>
    Failed,
}

/// <summary>Stable, idempotent account request. Hosts use Account.StorageKey as their unique work name.</summary>
/// <param name="Account">Local account boundary; never server authorization.</param>
/// <param name="Priority">Host priority hint.</param>
/// <param name="Constraints">Host constraints.</param>
public sealed record BackgroundWorkRequest(AccountScope Account, int Priority, BackgroundConstraints Constraints);

/// <summary>A platform adapter schedules a unique account job. It must not use the UI's live service scope.</summary>
public interface IBackgroundWorkScheduler
{
    /// <summary>Supported constraints/execution model.</summary>
    BackgroundCapabilities Capabilities { get; }
    /// <summary>Schedules/coalesces a request; Scheduled does not mean it ran.</summary>
    Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Durable scheduling state, intentionally separate from the existing document outbox.</summary>
/// <param name="SchemaVersion">Persistence format.</param>
/// <param name="Request">Latest requested account work.</param>
/// <param name="Pending">True until a matching execution reports completion.</param>
/// <param name="LastOutcome">Last host scheduling/execution outcome.</param>
/// <param name="Generation">Unique request generation; an older worker cannot acknowledge a newer request.</param>
public sealed record BackgroundWorkState(int SchemaVersion, BackgroundWorkRequest Request, bool Pending, BackgroundWorkOutcome LastOutcome, string Generation);

/// <summary>
/// Persists requests before invoking the platform scheduler and preserves them when scheduling is unsupported or
/// deferred. Foreground/resume calls RunAsync as the dependable fallback. Host callbacks open a fresh account-scoped
/// service lifetime and obey cancellation; the library cannot guarantee OS scheduling or stop a noncooperative callback.
/// </summary>
public sealed class BackgroundWorkDispatcher
{
    private readonly IOrchestrationStore _catalog;
    private readonly TimeProvider _time;
    /// <summary>Uses a durable catalog available to fresh host lifetimes, outside replicas that may be locked.</summary>
    public BackgroundWorkDispatcher(IOrchestrationStore catalog, TimeProvider? timeProvider = null)
    { _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _time = timeProvider ?? TimeProvider.System; }
    private static string Key(AccountScope account) => "background/" + account.StorageKey;
    private static string Encode(BackgroundWorkState state) => JsonSerializer.Serialize(state, BackgroundJson.Default.BackgroundWorkState);
    private static BackgroundWorkState Decode(OrchestrationRecord record)
    {
        var state = JsonSerializer.Deserialize(record.Payload, BackgroundJson.Default.BackgroundWorkState)!;
        if (state.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported background request schema; request retained.");
        return state;
    }
    /// <summary>Returns persisted state, independently of any live worker.</summary>
    public async Task<BackgroundWorkState?> GetAsync(AccountScope account, CancellationToken cancellationToken = default) =>
        await _catalog.ReadAsync(Key(account), cancellationToken).ConfigureAwait(false) is { } record ? Decode(record) : null;
    /// <summary>Persists before calling the host. Repeating requests coalesces by account and never clears pending work.</summary>
    public async Task<BackgroundWorkOutcome> RequestAsync(BackgroundWorkRequest request, IBackgroundWorkScheduler scheduler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(request.Account); ArgumentNullException.ThrowIfNull(request.Constraints);
        ArgumentNullException.ThrowIfNull(scheduler);
        var key = Key(request.Account); OrchestrationRecord? record;
        var initial = new BackgroundWorkState(1, request, true, BackgroundWorkOutcome.DeferredByHost, Guid.NewGuid().ToString("N"));
        while (true)
        {
            record = await _catalog.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (record is not null) _ = Decode(record);
            if (await _catalog.TryWriteAsync([new(key, record?.Revision, Encode(initial))], cancellationToken: cancellationToken).ConfigureAwait(false)) break;
        }
        record = await _catalog.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var outcome = BackgroundWorkOutcome.Unsupported;
        if ((!request.Constraints.RequiresNetwork || scheduler.Capabilities.NetworkConstraint)
            && (!request.Constraints.RequiresCharging || scheduler.Capabilities.ChargingConstraint))
        {
            try { outcome = await scheduler.ScheduleAsync(request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { outcome = BackgroundWorkOutcome.Canceled; }
            catch (Exception) { outcome = BackgroundWorkOutcome.Failed; }
        }
        if (outcome is not (BackgroundWorkOutcome.Scheduled or BackgroundWorkOutcome.DeferredByHost or BackgroundWorkOutcome.Unsupported or BackgroundWorkOutcome.Canceled or BackgroundWorkOutcome.Failed))
            throw new InvalidOperationException("A scheduler cannot claim an execution outcome.");
        if (record is not null && record.Payload == Encode(initial))
            await _catalog.TryWriteAsync([new(key, record.Revision, Encode(initial with { LastOutcome = outcome }))], cancellationToken: CancellationToken.None).ConfigureAwait(false);
        return outcome;
    }
    /// <summary>
    /// Runs fresh account work under execution ownership. The callback returns only execution outcomes, and must finish
    /// before returning. A request arriving during execution stays pending even if the older execution completes.
    /// </summary>
    public async Task<BackgroundWorkOutcome> RunAsync(AccountScope account, TimeSpan budget,
        Func<AccountScope, CancellationToken, Task<BackgroundWorkOutcome>> execute, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        await using var lease = await ExecutionLease.TryAcquireAsync(_catalog, "background-owner/" + account.StorageKey, TimeSpan.FromSeconds(30), _time, cancellationToken).ConfigureAwait(false);
        if (lease is null) return BackgroundWorkOutcome.RemainingWork;
        var key = Key(account); var record = await _catalog.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null || !Decode(record).Pending) return BackgroundWorkOutcome.Completed;
        var state = Decode(record);
        if (state.Request.Account != account) throw new InvalidDataException("Background request account mismatch.");
        using var deadline = new CancellationTokenSource(budget, _time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, lease.Lost);
        BackgroundWorkOutcome outcome;
        try { outcome = await execute(account, stop.Token).ConfigureAwait(false); }
        catch (ReplicaKeyUnavailableException) { outcome = BackgroundWorkOutcome.KeyUnavailable; }
        catch (OperationCanceledException) { outcome = cancellationToken.IsCancellationRequested ? BackgroundWorkOutcome.Canceled : BackgroundWorkOutcome.RemainingWork; }
        catch (Exception) { outcome = BackgroundWorkOutcome.Failed; }
        if (!Enum.IsDefined(outcome) || outcome == BackgroundWorkOutcome.Scheduled) throw new InvalidOperationException("A worker must return an execution outcome.");
        // A host callback may swallow expiration/cancellation. Do not acknowledge its request after the granted run ended.
        if (stop.IsCancellationRequested)
            outcome = cancellationToken.IsCancellationRequested ? BackgroundWorkOutcome.Canceled : BackgroundWorkOutcome.RemainingWork;
        var saved = await _catalog.TryWriteAsync([new(key, record.Revision, Encode(state with { Pending = outcome != BackgroundWorkOutcome.Completed, LastOutcome = outcome }))],
            lease.Fence, CancellationToken.None).ConfigureAwait(false);
        return !saved && outcome == BackgroundWorkOutcome.Completed ? BackgroundWorkOutcome.RemainingWork : outcome;
    }
}

[JsonSerializable(typeof(BackgroundWorkState))]
internal partial class BackgroundJson : JsonSerializerContext;
