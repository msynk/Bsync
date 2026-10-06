using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;

namespace Bsync.Diagnostics;

/// <summary>
/// Names of the traces and metrics Bsync emits (docs/operations/observability.md). Subscribe with
/// OpenTelemetry (<c>AddSource(SyncDiagnostics.SourceName)</c>, <c>AddMeter(SyncDiagnostics.SourceName)</c>) or any
/// <see cref="ActivityListener"/>/<see cref="MeterListener"/>. Nothing is emitted, and nothing allocated, without a
/// listener. Document contents and ids are never recorded; tags carry counts, outcome kinds, error codes and the
/// engine's diagnostics name.
/// </summary>
public static class SyncDiagnostics
{
    /// <summary>The name of the <see cref="System.Diagnostics.ActivitySource"/> and <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
    public const string SourceName = "Bsync";

    /// <summary>Tag: the engine's <see cref="SyncOptions{TDocument}.DiagnosticsName"/>.</summary>
    public const string NameTag = "bsync.name";

    internal static readonly ActivitySource ActivitySource = new(SourceName, typeof(SyncDiagnostics).Assembly.GetName().Version?.ToString());

    internal static readonly Meter Meter = new(SourceName, typeof(SyncDiagnostics).Assembly.GetName().Version?.ToString());

    /// <summary>Operations sent, by outcome (<c>bsync.outcome</c>) and, for rejections, <c>error.type</c>.</summary>
    internal static readonly Counter<long> Operations = Meter.CreateCounter<long>(
        "bsync.push.operations", "{operation}", "Push operations sent, by outcome.");

    internal static readonly Counter<long> PulledChanges = Meter.CreateCounter<long>(
        "bsync.pull.changes", "{change}", "Server changes applied to the local store.");

    internal static readonly Counter<long> ConflictDecisions = Meter.CreateCounter<long>(
        "bsync.conflicts", "{conflict}", "Conflicts decided by the conflict handler, by decision.");

    internal static readonly Counter<long> Resets = Meter.CreateCounter<long>(
        "bsync.resets", "{reset}", "Replica resets, by reason.");

    internal static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>(
        "bsync.run.duration", "s", "Duration of sync, pull and push runs, by operation and result.");

    private static readonly ConditionalWeakTable<object, QueueState> Queues = new();

    internal static readonly ObservableGauge<double> QueueDepth = Meter.CreateObservableGauge(
        "bsync.queue.depth",
        () => Observe(static q => q.Depth),
        "{document}",
        "Documents with local changes not yet confirmed by the server, as of the engine's last run.");

    internal static readonly ObservableGauge<double> QueueOldestAge = Meter.CreateObservableGauge(
        "bsync.queue.oldest_age",
        () => Observe(static q => q.OldestAgeSeconds()),
        "s",
        "Age of the oldest change waiting to be uploaded (by its authoring time), as of the engine's last run.");

    internal static readonly ObservableGauge<long> Issues = Meter.CreateObservableGauge(
        "bsync.issues",
        ObserveIssues,
        "{document}",
        "Documents waiting for a person's decision, by kind (conflict, rejected, blocked), as of the engine's last run.");

    internal static QueueState Track(object engine, string name)
    {
        var state = new QueueState(name);
        Queues.AddOrUpdate(engine, state);
        return state;
    }

    private static IEnumerable<Measurement<double>> Observe(Func<QueueState, double> value)
    {
        foreach (var (_, state) in (IEnumerable<KeyValuePair<object, QueueState>>)Queues)
        {
            if (state.Measured)
            {
                yield return new Measurement<double>(value(state), new KeyValuePair<string, object?>(NameTag, state.Name));
            }
        }
    }

    private static IEnumerable<Measurement<long>> ObserveIssues()
    {
        foreach (var (_, state) in (IEnumerable<KeyValuePair<object, QueueState>>)Queues)
        {
            if (state.Issues is { } issues)
            {
                var name = new KeyValuePair<string, object?>(NameTag, state.Name);
                yield return new Measurement<long>(issues.Conflicts, name, new KeyValuePair<string, object?>("bsync.issue", "conflict"));
                yield return new Measurement<long>(issues.Rejected - issues.Blocked, name, new KeyValuePair<string, object?>("bsync.issue", "rejected"));
                yield return new Measurement<long>(issues.Blocked, name, new KeyValuePair<string, object?>("bsync.issue", "blocked"));
            }
        }
    }

    /// <summary>The last measured queue of one engine; read by the observable gauges.</summary>
    internal sealed class QueueState(string name)
    {
        private long _depth;
        private long _oldestWallTime = -1;
        private int _measured;

        public string Name { get; } = name;

        public bool Measured => Volatile.Read(ref _measured) == 1;

        public double Depth => Volatile.Read(ref _depth);

        /// <summary>The last measured issue counts, or <see langword="null"/> before the first measurement.</summary>
        public Storage.SyncIssueCounts? Issues { get; set; }

        public void Update(long depth, long? oldestWallTimeMs)
        {
            Volatile.Write(ref _depth, depth);
            Volatile.Write(ref _oldestWallTime, oldestWallTimeMs ?? -1);
            Volatile.Write(ref _measured, 1);
        }

        public double OldestAgeSeconds()
        {
            var oldest = Volatile.Read(ref _oldestWallTime);
            return oldest < 0 ? 0 : Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - oldest) / 1000.0);
        }
    }
}
