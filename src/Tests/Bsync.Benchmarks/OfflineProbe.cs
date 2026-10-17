using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bsync;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Server;
using Bsync.Storage.Sqlite;

/// <summary>Bounded desktop qualification probe. Individual observations, not BenchmarkDotNet aggregate iteration percentiles.</summary>
internal static class OfflineProbe
{
    public static async Task RunAsync(string output)
    {
        var directory = Workload.TempDirectory();
        var results = new List<object>();
        try
        {
            var account = new AccountScope("benchmark", "local", "probe");
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(Path.Combine(directory, "orchestration.db"), account);
            var locks = new FileBlobCacheLock(Path.Combine(directory, "locks"));
            var cache = new ManagedBlobCache(new FileBlobCache(Path.Combine(directory, "bytes")), metadata, locks, 64 * 1024 * 1024, 128 * 1024 * 1024);
            var store = await Workload.StoreAsync("sqlite-full", directory, [Workload.Due]);
            await Workload.SeedSyncedAsync(store, 10_000, "seed");
            var engine = new SyncEngine<BenchDocument>(store, new InProcessTransport<BenchDocument>(Workload.Server()), new HybridLogicalClock("probe"), Workload.Clone);

            // Concurrent managed-cache writes model transfer staging pressure, with fixed payload and bounded disk use.
            using (var stop = new CancellationTokenSource())
            {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var staging = Task.Run(async () =>
                {
                    var hash = new string('a', 64); var buffer = new byte[256 * 1024]; var chunks = 0;
                    try
                    {
                        while (true)
                        {
                            await cache.AppendPartialAsync(hash, buffer, stop.Token);
                            started.TrySetResult();
                            if (++chunks % 64 == 0) await cache.DiscardPartialAsync(hash, stop.Token);
                            await Task.Delay(1, stop.Token);
                        }
                    }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                    catch (Exception error) { started.TrySetException(error); throw; }
                });
                try
                {
                    await started.Task;
                    var sequence = 0;
                    await Measure("sqlite-full-save-during-managed-staging", 200, 50, async () => { await engine.WriteAsync(Workload.Document(sequence++)); });
                }
                finally { stop.Cancel(); await staging; }
            }

            await Measure("sqlite-indexed-page-50-of-10000", 200, 50, async () =>
            {
                var page = await store.QueryIndexAsync(Workload.Due.All().Descending(), null, 50);
                if (page.Count != 50) throw new InvalidOperationException("Incomplete index page.");
            });
            using var http = new HttpClient(new NoNetwork()) { BaseAddress = new("https://unused.invalid/") };
            var transfers = new BlobTransferManager(metadata, cache, new HttpBlobTransfer(http));
            var packages = new OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(true));
            var data = new byte[1024 * 1024]; new Random(1729).NextBytes(data);
            var blob = await cache.ImportAsync(new MemoryStream(data));
            var revision = 0;
            await Measure("package-stage-activate-one-mib", 100, 250, async () =>
            {
                var next = (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await packages.StageAsync(new(1, "library", next, null, [new("chapter", blob.Sha256, blob.Size, true, [])]));
                if (!await packages.TryActivateAsync("library", next)) throw new InvalidOperationException("Activation failed.");
            });
            var encrypted = new EncryptedBlobCache(new FileBlobCache(Path.Combine(directory, "encrypted")), metadata, locks,
                new Keys(), new(account, "probe", 1), new PlatformBlobEncryption());
            var large = new byte[16 * 1024 * 1024]; new Random(1729).NextBytes(large);
            var encryptedBlob = await encrypted.ImportAsync(new MemoryStream(large));
            await Measure("encrypted-16mib-open-authenticate-read-4k", 100, 250, async () =>
            {
                await using var stream = await encrypted.OpenReadAsync(encryptedBlob.Sha256);
                stream!.Position = 8 * 1024 * 1024;
                await stream.ReadExactlyAsync(new byte[4096]);
            });

            // Each observation uses 1,000 distinct prepared writes and a fresh authority; seeding is outside timing.
            SyncEngine<BenchDocument> reconnect = null!;
            await Measure("sqlite-reconnect-1000-inprocess", 100, 10_000, async () =>
            {
                var synced = await reconnect.SyncAsync();
                if (!synced.IsComplete) throw new InvalidOperationException("Reconnect did not complete.");
            }, async () =>
            {
                var reconnectStore = await Workload.StoreAsync("sqlite-full", directory);
                await Workload.SeedPendingAsync(reconnectStore, 1000, "offline");
                reconnect = new SyncEngine<BenchDocument>(reconnectStore, new InProcessTransport<BenchDocument>(Workload.Server()),
                    new HybridLogicalClock("reconnect"), Workload.Clone);
            });
            var process = Process.GetCurrentProcess(); process.Refresh();
            var report = new { utc = DateTimeOffset.UtcNow, os = RuntimeInformation.OSDescription, architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                runtime = RuntimeInformation.FrameworkDescription, seed = 1729, peakWorkingSetBytes = process.PeakWorkingSet64,
                peakWorkingSetBudgetBytes = 1024L * 1024 * 1024, withinMemoryBudget = process.PeakWorkingSet64 < 1024L * 1024 * 1024,
                note = "One desktop process; peak includes setup; allocations include concurrent staging. In-process reconnect has no network latency. Sample quantiles, not confidence bounds.", results };
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(Path.GetFullPath(output));
        }
        finally { Workload.Delete(directory); }

        async Task Measure(string name, int count, double p95BudgetMs, Func<Task> action, Func<Task>? prepare = null)
        {
            if (prepare is not null) await prepare();
            await action(); // warmup excluded
            var observations = new List<double>(); long allocated = 0;
            for (var i = 0; i < count; i++)
            {
                if (prepare is not null) await prepare();
                var before = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                await action(); watch.Stop();
                allocated += GC.GetTotalAllocatedBytes(true) - before;
                observations.Add(watch.Elapsed.TotalMilliseconds);
            }
            observations.Sort();
            double Quantile(double p) => observations[(int)Math.Ceiling(p * count) - 1];
            var p95 = Quantile(.95);
            results.Add(new { name, count, p50Ms = Quantile(.50), p95Ms = p95, p99Ms = Quantile(.99), p95BudgetMs,
                withinBudget = p95 < p95BudgetMs, allocatedBytesPerObservation = allocated / count, observationsMs = observations });
            Console.WriteLine($"{name}: p50={Quantile(.5):F2} p95={p95:F2} p99={Quantile(.99):F2} ms; budget={p95BudgetMs} ms");
        }
    }
    private sealed class Keys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(LocalKeyAccess.Available(reference, new byte[32])); // Reproducible benchmark key, never production.
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Probe requires no network.");
    }
}
