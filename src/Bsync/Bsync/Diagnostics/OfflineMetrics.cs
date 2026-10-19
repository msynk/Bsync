using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Bsync.Storage;

namespace Bsync.Diagnostics;

/// <summary>
/// Opt-in cached aggregate gauges over bounded, redacted orchestration snapshots. Create one per distinct account store
/// in a process; refreshing duplicate views double-counts them. Refresh at a host-controlled cadence, never on a metric
/// callback. Dispose on account close. Metrics carry no account, content, endpoint or application-provided tags.
/// </summary>
public sealed class OfflineMetrics : IDisposable
{
    private sealed class Snapshot { internal OfflineDiagnosticReport? Report; }
    private static readonly ConditionalWeakTable<OfflineMetrics, Snapshot> Snapshots = new();
    private readonly Snapshot _snapshot = new();
    private readonly IOrchestrationStore _store;
    private int _disposed;

    private static readonly ObservableGauge<long>[] Gauges =
    [
        Gauge("bsync.transfer.backlog", "transfer.pending", "{job}", "Nonterminal jobs in the last bounded snapshots, including paused/failed work."),
        Gauge("bsync.transfer.pending_expected_bytes", "transfer.pending_expected_bytes", "By", "Full expected sizes of pending jobs; not remaining bytes or deduplicated disk usage."),
        Gauge("bsync.transfer.unknown_age", "transfer.unknown_age", "{job}", "Pending jobs without an original enqueue timestamp."),
        Gauge("bsync.cache.reserved_bytes", "cache.reserved_bytes", "By", "Conservative metadata reservations in the last bounded snapshots."),
        Gauge("bsync.cache.pinned_reserved_bytes", "cache.pinned_reserved_bytes", "By", "Conservative reservations protected by durable owners."),
        Gauge("bsync.blob.repair_queued", "repair.Needed", "{repair}", "Repairs awaiting execution in the last bounded snapshots; excludes deferred and running repairs."),
        Gauge("bsync.blob.chunk_reserved_bytes", "blob.chunk_reserved_bytes", "By", "Journaled ciphertext reservations, including writes interrupted before physical storage."),
        Gauge("bsync.blob.quarantined_reserved_bytes", "blob.quarantined_reserved_bytes", "By", "Subset of journaled ciphertext reservations protected by explicit recovery quarantine."),
        Gauge("bsync.background.pending", "background.pending", "{request}", "Pending background requests in the last bounded snapshots."),
        SyncDiagnostics.Meter.CreateObservableGauge("bsync.transfer.oldest_known_age", OldestTransferAge, "s", "Age of the oldest timestamped pending job; see unknown_age and snapshot age/partial gauges."),
        SyncDiagnostics.Meter.CreateObservableGauge("bsync.offline.snapshot.oldest_age", () => Reports().Select(r => Elapsed(r)).DefaultIfEmpty().Max(), "s", "Age of the oldest registered snapshot; no storage work occurs in collection callbacks."),
        SyncDiagnostics.Meter.CreateObservableGauge("bsync.offline.snapshot.partial", () => (long)Reports().Count(r => r.Truncated || r.UnsupportedRecords > 0), "{snapshot}", "Snapshots with truncation or unsupported records; totals are partial."),
    ];
    private static readonly ObservableGauge<long> PackageStates = SyncDiagnostics.Meter.CreateObservableGauge(
        "bsync.package.last_verified", () => Enum.GetValues<Blobs.OfflinePackageReadiness>().Select(state =>
            new Measurement<long>(Sum("package.last_verified." + state), new KeyValuePair<string, object?>("bsync.readiness", state.ToString()))),
        "{package}", "Persisted verification observations by readiness; does not prove current bytes or unchanged active pointers.");
    private static readonly ObservableGauge<long> BackgroundStates = SyncDiagnostics.Meter.CreateObservableGauge(
        "bsync.background.last_outcome", () => Enum.GetValues<Client.BackgroundWorkOutcome>().Select(state =>
            new Measurement<long>(Sum("background." + state), new KeyValuePair<string, object?>("bsync.outcome", state.ToString()))),
        "{request}", "Last observed outcome of durable requests, not an execution counter.");

    /// <summary>Registers an initially unmeasured account store. This object does not own/dispose the store.</summary>
    public OfflineMetrics(IOrchestrationStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store; Snapshots.Add(this, _snapshot);
        _ = Gauges; _ = PackageStates; _ = BackgroundStates;
    }

    /// <summary>Refreshes at most 10000 metadata records and returns the redacted report, with truncation explicitly marked.</summary>
    public async Task<OfflineDiagnosticReport> RefreshAsync(int recordLimit = 1000, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var report = await OfflineDiagnosticExport.CaptureAsync(_store, recordLimit, cancellationToken).ConfigureAwait(false);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _snapshot.Report, report with { Counts = new Dictionary<string, long>(report.Counts, StringComparer.Ordinal) });
        return report;
    }

    private static ObservableGauge<long> Gauge(string name, string key, string unit, string description) =>
        SyncDiagnostics.Meter.CreateObservableGauge(name, () => Sum(key), unit, description);
    private static IEnumerable<OfflineDiagnosticReport> Reports()
    {
        foreach (var pair in Snapshots)
            if (Volatile.Read(ref pair.Value.Report) is { } report) yield return report;
    }
    private static long Sum(string key)
    {
        long total = 0;
        foreach (var report in Reports())
        {
            var value = report.Counts.GetValueOrDefault(key);
            total = value > long.MaxValue - total ? long.MaxValue : total + value;
        }
        return total;
    }
    private static long Elapsed(OfflineDiagnosticReport report) => Math.Max(0, (long)(DateTimeOffset.UtcNow - report.CapturedAt).TotalSeconds);
    private static long OldestTransferAge() => Reports().Where(r => r.Counts.ContainsKey("transfer.oldest_known_age_seconds"))
        .Select(r => r.Counts["transfer.oldest_known_age_seconds"] + Elapsed(r)).DefaultIfEmpty().Max();

    /// <summary>Unregisters this account snapshot. Idempotent; no account data is deleted.</summary>
    public void Dispose()
    { Interlocked.Exchange(ref _disposed, 1); Snapshots.Remove(this); }
}
