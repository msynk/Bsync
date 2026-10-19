using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Diagnostics;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

[CollectionDefinition("Blob metrics", DisableParallelization = true)]
public sealed class BlobMetricsCollection;

[Collection("Blob metrics")]
public sealed class BlobMetricsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairAndRetentionMeasurementsCountCommittedWorkWithoutIdentityTags(bool missing)
    {
        var root = Directory.CreateTempSubdirectory("bsync-metrics-").FullName;
        var values = new ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == SyncDiagnostics.SourceName) meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((i, v, t, _) => values.Enqueue((i.Name, v, t.ToArray())));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => values.Enqueue((i.Name, v, t.ToArray())));
        listener.Start();
        try
        {
            await using var store = new InMemoryOrchestrationStore();
            var cache = new ManagedBlobCache(new FileBlobCache(root), store, new FileBlobCacheLock(root + "/locks"), 1000, 1000);
            using var http = new HttpClient(new Download()) { BaseAddress = new("https://private-authority.test/") };
            var transfers = new BlobTransferManager(store, cache, new(http));
            var packages = new OfflinePackageManager(store, cache, transfers, new FileBlobCacheLock(root + "/locks"), (_, _) => Task.FromResult(true));
            var blob = await cache.ImportAsync(new MemoryStream([1, 2, 3, 4]));
            await packages.StageAsync(new(1, "private-package", "one", null, [new("private-item", blob.Sha256, 4, true, [])]));
            await packages.PrepareAsync("private-package", TimeSpan.FromSeconds(10));
            for (var i = 0; i < 2; i++)
            {
                var path = Path.Combine(root, "objects", blob.Sha256[..2], blob.Sha256);
                if (missing) File.Delete(path);
                else await File.WriteAllBytesAsync(path, [9, 9, 9, 9]);
                await packages.PrepareAsync("private-package", TimeSpan.FromSeconds(10));
                await packages.PrepareAsync("private-package", TimeSpan.FromSeconds(10));
            }
            var repairs = values.Where(v => v.Name == "bsync.blob.repairs").ToArray();
            foreach (var state in missing ? new[] { "Needed", "Downloading", "Completed" } : new[] { "Needed", "Downloading", "Promoting", "Completed" })
                Assert.Equal(2, repairs.Where(v => v.Tags.Any(t => t.Key == "bsync.state" && Equals(t.Value, state))).Sum(v => v.Value));
            Assert.Equal(8, values.Where(v => v.Name == "bsync.blob.repaired_bytes").Sum(v => v.Value));
            Assert.Equal(2, values.Count(v => v.Name == "bsync.blob.repair_attempt_duration"));
            await cache.MaintainOwnersAsync(new UnknownOwner(), TimeSpan.FromSeconds(5));
            Assert.Equal(4, Assert.Single(values, v => v.Name == "bsync.cache.protected_page_bytes").Value);

            var generation = new string('0', 32);
            await store.TryWriteAsync([new("encrypted-chunk/v2/" + generation + "/" + new string('a', 64), null, "42"),
                new("encrypted-protected/v2/" + generation, null, blob.Sha256)]);
            var chunks = new ChunkedEncryptedBlobCache(new BlobCacheChunkStore(new FileBlobCache(root + "/chunks")), store,
                new FileBlobCacheLock(root + "/operations"), new FileAccountResourceLock(root + "/lifetimes"),
                new LockedKeys(), new(new("private", "authority", "account"), "key", 1), new PlatformBlobEncryption());
            Assert.Equal(42, (await chunks.ObserveStoragePageAsync()).QuarantinedBytes);
            Assert.Equal(42, Assert.Single(values, v => v.Name == "bsync.blob.quarantined_page_bytes").Value);
            await store.TryWriteAsync([new("repair/v1/private-job", null, """{"SchemaVersion":1,"State":0,"ErrorCode":"private-error"}""")]);
            using (var metrics = new OfflineMetrics(store))
            {
                var report = await metrics.RefreshAsync();
                Assert.False(report.Truncated); Assert.Equal(0, report.UnsupportedRecords);
                Assert.Equal(42, report.Counts["blob.quarantined_reserved_bytes"]);
                Assert.Equal(1, report.Counts["repair.Needed"]);
                listener.RecordObservableInstruments();
                Assert.Equal(42, values.Last(v => v.Name == "bsync.blob.quarantined_reserved_bytes").Value);
                Assert.Equal(1, values.Last(v => v.Name == "bsync.blob.repair_queued").Value);
            }
            listener.RecordObservableInstruments();
            Assert.Equal(0, values.Last(v => v.Name == "bsync.blob.quarantined_reserved_bytes").Value);
            Assert.All(values.Where(v => v.Name.StartsWith("bsync.blob.", StringComparison.Ordinal) || v.Name == "bsync.cache.protected_page_bytes"), v =>
                Assert.All(v.Tags, t => Assert.Contains(t.Key, new[] { "bsync.state", "bsync.error", "bsync.dry_run", "bsync.direction", "bsync.result" })));
            Assert.DoesNotContain(values.SelectMany(v => v.Tags), t => t.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true || Equals(t.Value, blob.Sha256));
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Download : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
    }
    private sealed class UnknownOwner : IBlobOwnerResolver
    {
        public ValueTask<BlobOwnerResolution> ResolveAsync(string sha256, string owner, CancellationToken cancellationToken = default) => ValueTask.FromResult(BlobOwnerResolution.Unknown);
    }
    private sealed class LockedKeys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Observations must not acquire keys.");
    }
}
