using System.Diagnostics.Tracing;
using Bsync;
using Bsync.Clocks;
using Bsync.Server;

/// <summary>
/// Task D9: "push-alloc &lt;store&gt; [top]" pushes 10,000 queued writes (the <see cref="Reconnect"/> workload) after a warm-up
/// run, and prints the bytes allocated per pushed document, plus the most-allocated types sampled by the runtime's
/// allocation ticks (about one sample per 100 KB).
/// </summary>
public static class PushAllocations
{
    public static async Task RunAsync(string store, int top)
    {
        await PushOnceAsync(store, null); // warm-up: JIT, caches, pools
        using var ticks = new AllocationTicks();
        var allocated = new long[1];
        var pushed = await PushOnceAsync(store, (ticks, allocated));
        Console.WriteLine($"store={store} pushed={pushed} allocatedPerDocument={allocated[0] / (double)pushed / 1024:0.0}KB");
        foreach (var (type, bytes) in ticks.Top(top))
        {
            Console.WriteLine($"  {bytes / (double)pushed / 1024,8:0.00} KB/doc  {type}");
        }
    }

    private static async Task<int> PushOnceAsync(string kind, (AllocationTicks Ticks, long[] Allocated)? measure)
    {
        var directory = Workload.TempDirectory();
        try
        {
            var counts = new int[4];
            Func<BenchDocument, BenchDocument> Counted(int slot) => d => { Interlocked.Increment(ref counts[slot]); return Workload.Clone(d); };
            var fingerprint = Bsync.Documents.DocumentCloner.JsonFingerprint(BenchJson.Default.BenchDocument);
            var store = kind == "memory" ? new Bsync.Storage.InMemoryLocalStore<BenchDocument>(Counted(0)) : await Workload.StoreAsync(kind, directory);
            await Workload.SeedPendingAsync(store, 10_000, "device");
            Array.Clear(counts);
            var server = new InMemorySyncServer<BenchDocument>(new InMemorySyncServerOptions<BenchDocument>
            {
                Cloner = Counted(2),
                Fingerprint = fingerprint, // not wrapped: the server hashes JsonFingerprint's bytes directly
                MaxOperationsPerPush = 1000,
                MaxPageSize = 1000,
            });
            var engine = new SyncEngine<BenchDocument>(store, new InProcessTransport<BenchDocument>(server), new HybridLogicalClock("device"), Counted(1),
                options: new SyncOptions<BenchDocument> { PushBatchSize = 500, MaxPushBatches = 100 });
            measure?.Ticks.Start();
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var result = await engine.SyncAsync();
            if (measure is { } m)
            {
                m.Allocated[0] = GC.GetTotalAllocatedBytes(precise: true) - before;
                m.Ticks.Stop();
                Console.WriteLine($"clones per document: store={counts[0] / 10_000.0} engine={counts[1] / 10_000.0} server={counts[2] / 10_000.0}");
            }

            if (store is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync();
            }

            return result.Pushed;
        }
        finally
        {
            Workload.Delete(directory);
        }
    }

    /// <summary>Collects GCAllocationTick events (keyword GC, verbose) by type name.</summary>
    private sealed class AllocationTicks : EventListener
    {
        private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
        private volatile bool _on;

        public void Start() => _on = true;

        public void Stop() => _on = false;

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs data)
        {
            if (!_on || data.EventName is not "GCAllocationTick_V4" and not "GCAllocationTick_V3" || data.Payload is null)
            {
                return;
            }

            var type = data.Payload[data.PayloadNames!.IndexOf("TypeName")] as string ?? "?";
            var amount = Convert.ToInt64(data.Payload[data.PayloadNames.IndexOf("AllocationAmount64")], System.Globalization.CultureInfo.InvariantCulture);
            lock (_bytes)
            {
                _bytes[type] = _bytes.GetValueOrDefault(type) + amount;
            }
        }

        public IEnumerable<(string Type, long Bytes)> Top(int count)
        {
            lock (_bytes)
            {
                return _bytes.OrderByDescending(p => p.Value).Take(count).Select(p => (p.Key, p.Value)).ToList();
            }
        }
    }
}
