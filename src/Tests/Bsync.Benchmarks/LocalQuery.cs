using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Bsync;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Server;
using Bsync.Storage;

/// <summary>
/// Task E1: the views an app shows, through the public collection API: a 50-item page ordered by a
/// <see cref="DateTimeOffset"/> field, and a 50-item page of a selective filter (1% of the documents match).
/// Bar: p95 under 50 ms on a named device.
/// </summary>
[MemoryDiagnoser]
[Config(typeof(P95Config))]
public class LocalQuery
{
    private string _directory = string.Empty;
    private SyncSession<BenchDocument> _session = null!;
    private LocalSyncCollection<BenchDocument> _collection = null!;

    private static readonly SyncQuery<BenchDocument> Ordered = new() { Order = (a, b) => b.Due.CompareTo(a.Due), Limit = 50 };

    private static readonly SyncQuery<BenchDocument> Selective = new() { Where = d => d.Category == 42, Limit = 50 };

    private static readonly SyncQuery<BenchDocument> Indexed = new() { Index = Workload.Due.All().Descending(), Limit = 50 };

    private static readonly SyncQuery<BenchDocument> IndexedRange = new() { Index = Workload.Due.Between(Workload.Start.AddDays(100), Workload.Start.AddDays(200)), Skip = 50, Limit = 50 };

    [Params(10_000, 50_000)]
    public int Count { get; set; }

    [Params("memory", "sqlite-full")]
    public string Store { get; set; } = "memory";

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Workload.TempDirectory();
        var store = await Workload.StoreAsync(Store, _directory, [Workload.Due]);
        await Workload.SeedSyncedAsync(store, Count, "seed");
        var server = Workload.Server();
        _session = new SyncSession<BenchDocument>(new SyncSessionOptions<BenchDocument>
        {
            Cloner = Workload.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<BenchDocument>(store, "reader")),
            CreateTransport = _ => new InProcessTransport<BenchDocument>(server),
            Interval = TimeSpan.FromHours(1),
        });
        _collection = new LocalSyncCollection<BenchDocument>(_session, _ => Task.FromResult("bench"));
        await _collection.QueryAsync(); // opens the replica
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _session.DisposeAsync();
        Workload.Delete(_directory);
    }

    [Benchmark]
    public Task<IReadOnlyList<BenchDocument>> OrderedPage() => _collection.QueryAsync(Ordered);

    [Benchmark]
    public Task<IReadOnlyList<BenchDocument>> SelectivePage() => _collection.QueryAsync(Selective);

    /// <summary>E2: the same ordered page as <see cref="OrderedPage"/>, read through a declared index.</summary>
    [Benchmark]
    public Task<IReadOnlyList<BenchDocument>> IndexedPage() => _collection.QueryAsync(Indexed);

    /// <summary>E2: the second page of a date range, through the index.</summary>
    [Benchmark]
    public Task<IReadOnlyList<BenchDocument>> IndexedRangePage() => _collection.QueryAsync(IndexedRange);

    /// <summary>E2: counting a date range through the index.</summary>
    [Benchmark]
    public Task<int> IndexedRangeCount() => _collection.CountAsync(IndexedRange);

    private sealed class P95Config : ManualConfig
    {
        public P95Config() => AddColumn(StatisticColumn.P95);
    }
}
