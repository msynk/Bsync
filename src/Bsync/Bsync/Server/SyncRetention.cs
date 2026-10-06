namespace Bsync.Server;

/// <summary>An authority whose tombstones and receipts <see cref="SyncRetention"/> can purge (task D5).</summary>
public interface ISyncRetentionTarget
{
    /// <summary>The current head version of every feed (scope) this authority serves.</summary>
    Task<IReadOnlyDictionary<string, long>> GetFeedHeadsAsync(CancellationToken cancellationToken = default);

    /// <summary>Purges tombstones of <paramref name="scope"/> at or below <paramref name="throughVersion"/>.</summary>
    Task<int> PurgeTombstonesAsync(string scope, long throughVersion, CancellationToken cancellationToken = default);

    /// <summary>Purges receipts of operations of <paramref name="scope"/> accepted at or below <paramref name="throughVersion"/>.</summary>
    Task<int> PurgeReceiptsAsync(string scope, long throughVersion, CancellationToken cancellationToken = default);
}

/// <summary>Retention policy (task D5, F18).</summary>
public sealed class SyncRetentionOptions
{
    /// <summary>
    /// The longest time a replica may stay offline and still continue incrementally. Tombstones older than this are purged
    /// (a replica offline longer resets with reason <c>expired</c>). Default 45 days.
    /// </summary>
    public TimeSpan MaxOfflineHorizon { get; set; } = TimeSpan.FromDays(45);

    /// <summary>
    /// How long receipts are kept. Must be at least <see cref="MaxOfflineHorizon"/>: a replica that resends an accepted
    /// write after its receipt is gone gets a conflict for its own write. Default: <see cref="MaxOfflineHorizon"/>.
    /// </summary>
    public TimeSpan? ReceiptHorizon { get; set; }

    /// <summary>How often feed heads are sampled and purges run. Default one hour.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>The authorities to purge (in addition to those registered as <see cref="ISyncRetentionTarget"/> services).</summary>
    public IList<ISyncRetentionTarget> Targets { get; } = [];

    /// <summary>Time source (tests).</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Returns the problems with these options, or an empty list.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (MaxOfflineHorizon <= TimeSpan.Zero)
        {
            problems.Add("MaxOfflineHorizon must be positive.");
        }

        if (ReceiptHorizon is { } receipts && receipts < MaxOfflineHorizon)
        {
            problems.Add(
                $"ReceiptHorizon ({receipts.TotalDays:0.##} days) is shorter than MaxOfflineHorizon ({MaxOfflineHorizon.TotalDays:0.##} days). " +
                "A replica offline longer than the receipt horizon would get conflicts for its own accepted writes. Keep receipts at least as long as replicas may stay offline.");
        }

        if (Interval <= TimeSpan.Zero)
        {
            problems.Add("Interval must be positive.");
        }

        return problems;
    }
}

/// <summary>
/// Purges tombstones and receipts older than a time horizon (task D5). Authorities purge by version, so this samples every
/// feed's head version over time and purges through the head that was current a horizon ago. Samples live in memory:
/// after a restart nothing is purged until one horizon has passed again, which only delays purging.
/// </summary>
public sealed class SyncRetention
{
    private readonly SyncRetentionOptions _options;
    private readonly IReadOnlyList<ISyncRetentionTarget> _targets;
    private readonly Dictionary<(ISyncRetentionTarget Target, string Scope), List<(DateTimeOffset At, long Head)>> _samples = [];

    /// <summary>Creates the policy. Throws if <paramref name="options"/> are invalid.</summary>
    public SyncRetention(SyncRetentionOptions options, IEnumerable<ISyncRetentionTarget>? targets = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is [_, ..] problems)
        {
            throw new ArgumentException(string.Join(" ", problems), nameof(options));
        }

        _options = options;
        _targets = [.. options.Targets, .. targets ?? []];
    }

    /// <summary>Samples every feed's head and purges what is older than the horizons. Returns tombstones and receipts removed.</summary>
    public async Task<(int Tombstones, int Receipts)> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var now = _options.TimeProvider.GetUtcNow();
        var tombstones = 0;
        var receipts = 0;
        foreach (var target in _targets)
        {
            foreach (var (scope, head) in await target.GetFeedHeadsAsync(cancellationToken).ConfigureAwait(false))
            {
                var samples = _samples.TryGetValue((target, scope), out var list) ? list : _samples[(target, scope)] = [];
                samples.Add((now, head));
                if (HeadAt(samples, now - _options.MaxOfflineHorizon) is { } tombstoneHorizon)
                {
                    tombstones += await target.PurgeTombstonesAsync(scope, tombstoneHorizon, cancellationToken).ConfigureAwait(false);
                }

                if (HeadAt(samples, now - (_options.ReceiptHorizon ?? _options.MaxOfflineHorizon)) is { } receiptHorizon)
                {
                    receipts += await target.PurgeReceiptsAsync(scope, receiptHorizon, cancellationToken).ConfigureAwait(false);
                }

                // Keep only what a later run can still need.
                var oldest = now - TimeSpan.FromTicks(Math.Max(_options.MaxOfflineHorizon.Ticks, (_options.ReceiptHorizon ?? TimeSpan.Zero).Ticks));
                var keepFrom = samples.FindLastIndex(s => s.At <= oldest);
                if (keepFrom > 0)
                {
                    samples.RemoveRange(0, keepFrom);
                }
            }
        }

        return (tombstones, receipts);
    }

    // The newest head sampled at or before the cutoff: everything at or below it is at least that old.
    private static long? HeadAt(List<(DateTimeOffset At, long Head)> samples, DateTimeOffset cutoff)
    {
        long? head = null;
        foreach (var sample in samples)
        {
            if (sample.At > cutoff)
            {
                break;
            }

            head = sample.Head;
        }

        return head;
    }
}
