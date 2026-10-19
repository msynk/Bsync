using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>One cooperative maintenance page. Providers must enforce limits before destructive work.</summary>
/// <param name="Continuation">Provider-specific durable cursor, or null to start.</param>
/// <param name="MaximumItems">Maximum units of work; each step documents its unit.</param>
/// <param name="MaximumBytes">Maximum accounted bytes examined/affected, as documented by the step; excludes runtime overhead.</param>
/// <param name="DryRun">Observe only: no data changes or durable checkpoint updates.</param>
public sealed record MaintenancePageRequest(string? Continuation, int MaximumItems, long MaximumBytes, bool DryRun);

/// <summary>A replayable page result. An interrupted page must be safe to repeat from its previous cursor.</summary>
/// <param name="Items">Units examined, including protected units.</param>
/// <param name="Bytes">Accounted bytes charged against the request.</param>
/// <param name="Continuation">Next cursor; null completes this step's current scan.</param>
/// <param name="RequiresLargerBudget">The next indivisible unit exceeds the remaining item/byte allowance; no work was done on that unit.</param>
public sealed record MaintenancePageResult(int Items, long Bytes, string? Continuation, bool RequiresLargerBudget = false);

/// <summary>Named, versioned maintenance operation. Change Version when cursor meaning or behavior becomes incompatible.</summary>
/// <param name="Name">Stable portable name, unique within the plan.</param>
/// <param name="Version">Stable portable implementation version.</param>
/// <param name="ExecuteAsync">Uses only the scope's account; honors page limits, dry run and cancellation, and joins work before returning.</param>
public sealed record AccountMaintenanceStep(string Name, string Version,
    Func<AccountWorkScope, MaintenancePageRequest, CancellationToken, Task<MaintenancePageResult>> ExecuteAsync);

/// <summary>Reason a bounded maintenance invocation stopped.</summary>
public enum MaintenanceOutcome
{
    /// <summary>Every step completed a scan. The next invocation starts a fresh cycle.</summary>
    Completed,
    /// <summary>Item/byte/page limits were reached; durable progress is available.</summary>
    RemainingWork,
    /// <summary>Another account reader, worker, maintainer or deletion owner is active.</summary>
    Busy,
    /// <summary>Host-reported urgent work takes priority.</summary>
    UrgentWork,
    /// <summary>The cooperative deadline expired; started work and resource disposal were joined.</summary>
    BudgetExpired,
    /// <summary>An indivisible unit needs a larger byte/item allowance.</summary>
    RequiresLargerBudget,
}

/// <summary>Content-free summary of a bounded invocation; counts describe completed callbacks, not an atomic store snapshot.</summary>
/// <param name="Outcome">Stop reason.</param>
/// <param name="Items">Units reported by completed callbacks.</param>
/// <param name="Bytes">Accounted bytes reported by completed callbacks.</param>
/// <param name="Pages">Completed callbacks.</param>
public sealed record AccountMaintenanceResult(MaintenanceOutcome Outcome, int Items, long Bytes, int Pages);

/// <summary>
/// Opt-in maintenance proposal implemented as a bounded runner. Acquires exclusive physical account ownership, opens
/// fresh resources and persists one checkpoint after each idempotent page. It never preempts a live account user.
/// Catalog and account locks must outlive deletable resources. Callbacks define byte/item units and must obey limits;
/// validation after return detects violations but cannot undo arbitrary host side effects. No network work belongs here.
/// </summary>
public sealed class AccountMaintenance
{
    private readonly AccountComposition _accounts;
    private readonly IOrchestrationStore _catalog;
    private readonly TimeProvider _time;
    private static readonly System.Diagnostics.Metrics.Counter<long> Outcomes = Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.maintenance.runs", "{run}", "Maintenance stop reasons; caller cancellation is distinct from budget expiry. No account or plan identifiers.");

    /// <summary>Uses the same account composition and external catalog as lifecycle/deletion coordination.</summary>
    public AccountMaintenance(AccountComposition accounts, IOrchestrationStore catalog, TimeProvider? timeProvider = null)
    { _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts)); _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog)); _time = timeProvider ?? TimeProvider.System; }

    /// <summary>
    /// Runs at most 32 pages with one deadline covering opening, urgent checks, work, checkpoints and joined disposal.
    /// Checks urgent work before each page. Persisted progress is bound to the ordered name/version list; use a new plan
    /// name for an incompatible plan. Dry run reads existing progress but writes neither checkpoints nor data.
    /// Caller cancellation throws; expiry returns BudgetExpired. A page interrupted before checkpointing is replayed.
    /// </summary>
    public async Task<AccountMaintenanceResult> MaintainAsync(AccountScope account, string planName, IReadOnlyList<AccountMaintenanceStep> steps,
        TimeSpan budget, Func<AccountWorkScope, CancellationToken, Task<bool>> hasUrgentWork,
        int maximumItems = 128, long maximumBytes = 8388608, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account); ArgumentNullException.ThrowIfNull(steps); ArgumentNullException.ThrowIfNull(hasUrgentWork);
        ValidateName(planName);
        if (steps.Count is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(steps));
        var plan = steps.ToArray(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan)
        {
            ArgumentNullException.ThrowIfNull(step); ValidateName(step.Name); ValidateName(step.Version); ArgumentNullException.ThrowIfNull(step.ExecuteAsync);
            if (!names.Add(step.Name)) throw new ArgumentException("Duplicate maintenance step.", nameof(steps));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumItems, 1); ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumItems, 10000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1);
        var signature = Hash(string.Join('\n', plan.Select(s => s.Name + ":" + s.Version)));
        var key = "maintenance/v1/" + account.StorageKey + "/" + Hash(planName);
        using var deadline = new CooperativeDeadline(budget, _time, cancellationToken, "account_maintenance");
        var items = 0; long bytes = 0; var pages = 0;
        AccountMaintenanceResult Result(MaintenanceOutcome outcome) => new(outcome, items, bytes, pages);
        async Task<AccountMaintenanceResult> Run()
        {
            AccountWorkScope scope;
            try { scope = await _accounts.OpenMaintenanceAsync(account, deadline.Token).ConfigureAwait(false); }
            catch (AccountResourcesInUseException) { return Result(MaintenanceOutcome.Busy); }
            await using (scope.ConfigureAwait(false))
            {
                var record = await _catalog.ReadAsync(key, deadline.Token).ConfigureAwait(false);
                var state = record is null ? new MaintenanceCheckpoint(1, signature, 0, null)
                    : JsonSerializer.Deserialize(record.Payload, MaintenanceJson.Default.MaintenanceCheckpoint) ?? throw new InvalidDataException("Missing maintenance checkpoint.");
                if (state.Version != 1 || state.Signature != signature || state.Step < 0 || state.Step >= plan.Length || state.Continuation?.Length > 4096)
                    throw new InvalidDataException("Incompatible maintenance checkpoint; use a new plan identity.");
                while (pages < 32 && items < maximumItems && bytes < maximumBytes)
                {
                    scope.RequireMaintenance(); _ = deadline.Remaining;
                    var urgent = await hasUrgentWork(scope, deadline.Token).ConfigureAwait(false);
                    scope.RequireMaintenance();
                    if (urgent) { _ = deadline.Remaining; return Result(MaintenanceOutcome.UrgentWork); }
                    _ = deadline.Remaining;
                    var page = await plan[state.Step].ExecuteAsync(scope, new(state.Continuation, maximumItems - items, maximumBytes - bytes, dryRun), deadline.Token).ConfigureAwait(false);
                    scope.RequireMaintenance();
                    _ = deadline.Remaining;
                    if (page is null || page.Items < 0 || page.Items > maximumItems - items || page.Bytes < 0 || page.Bytes > maximumBytes - bytes || page.Continuation?.Length > 4096)
                        throw new InvalidDataException("Maintenance callback exceeded its declared bounds.");
                    if (!page.RequiresLargerBudget && page.Items == 0 && page.Continuation is not null && page.Continuation == state.Continuation)
                        throw new InvalidDataException("Maintenance callback did not advance.");
                    items += page.Items; bytes += page.Bytes; pages++;
                    var complete = !page.RequiresLargerBudget && page.Continuation is null;
                    var cycleComplete = complete && state.Step == plan.Length - 1;
                    state = state with { Step = complete ? (cycleComplete ? 0 : state.Step + 1) : state.Step, Continuation = page.Continuation };
                    if (!dryRun)
                    {
                        var payload = JsonSerializer.Serialize(state, MaintenanceJson.Default.MaintenanceCheckpoint);
                        if (!await _catalog.TryWriteAsync([new(key, record?.Revision, payload)], cancellationToken: deadline.Token).ConfigureAwait(false))
                            throw new IOException("Maintenance checkpoint changed outside account ownership.");
                        record = await _catalog.ReadAsync(key, deadline.Token).ConfigureAwait(false) ?? throw new IOException("Maintenance checkpoint disappeared.");
                    }
                    if (page.RequiresLargerBudget) return Result(MaintenanceOutcome.RequiresLargerBudget);
                    if (cycleComplete) return Result(MaintenanceOutcome.Completed);
                }
                return Result(MaintenanceOutcome.RemainingWork);
            }
        }
        try
        {
            var result = await Run().ConfigureAwait(false);
            _ = deadline.Remaining;
            Outcomes.Add(1, new KeyValuePair<string, object?>("bsync.outcome", result.Outcome.ToString()));
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.Token.IsCancellationRequested)
        { Outcomes.Add(1, new KeyValuePair<string, object?>("bsync.outcome", "BudgetExpired")); return Result(MaintenanceOutcome.BudgetExpired); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { Outcomes.Add(1, new KeyValuePair<string, object?>("bsync.outcome", "Canceled")); throw; }
        catch { Outcomes.Add(1, new KeyValuePair<string, object?>("bsync.outcome", "Failed")); throw; }
    }

    private static void ValidateName(string name) { OrchestrationValidation.Key(name); if (name.Length > 128) throw new ArgumentException("Maintenance identities are bounded to 128 characters.", nameof(name)); }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

internal sealed record MaintenanceCheckpoint(int Version, string Signature, int Step, string? Continuation);
[JsonSerializable(typeof(MaintenanceCheckpoint))]
internal partial class MaintenanceJson : JsonSerializerContext;
