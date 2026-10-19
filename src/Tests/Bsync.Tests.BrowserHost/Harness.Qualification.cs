using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    // A bounded caller buffer, unlike the functional fixtures which intentionally hold whole test objects.
    [JSInvokable(nameof(QualifyChunkBuffers))]
    public static async Task<string> QualifyChunkBuffers(string name, int sizeMiB, string hash)
    {
        if (sizeMiB is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(sizeMiB));
        const int chunk = 1048576;
        var account = new AccountScope("qualification", "browser", name);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var operations = await BrowserBlobCacheLock.OpenAsync(_js, account);
        await using var lifetimes = await BrowserAccountResourceLock.OpenAsync(_js);
        await using var crypto = await BrowserBlobEncryption.OpenAsync(_js);
        var backing = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var measured = new QualificationChunks(new BlobCacheChunkStore(backing));
        var cache = new ChunkedEncryptedBlobCache(measured, metadata, operations, lifetimes,
            new BlobTestKeys(), new(account, "qualification", 1), crypto);
        var buffer = new byte[chunk];
        for (var i = 0; i < chunk; i++) buffer[i] = (byte)(i % 251);
        if (hash.Length != 64 || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) throw new ArgumentException("Invalid reference hash.", nameof(hash));
        var beforeProbe = GC.GetAllocatedBytesForCurrentThread();
        GC.KeepAlive(new byte[4096]);
        var allocationCounterAvailable = GC.GetAllocatedBytesForCurrentThread() > beforeProbe;
        GC.Collect(); // One declared baseline collection; no forced collection during measured operations.
        var baseline = GC.GetTotalMemory(false);
        var stages = new List<BrowserQualificationStage>();
        try
        {
            await Measure("append", sizeMiB, async () => await cache.AppendPartialAsync(hash, buffer));
            if (measured.Written != sizeMiB * (chunk + 28L)) throw new InvalidDataException("Nonlinear ciphertext payload writes.");
            if (measured.InventoryReads != 0) throw new InvalidDataException("Unlimited appends rescanned physical inventory.");
            await Measure("complete", 1, async () =>
            { if (!await cache.CompletePartialAsync(hash)) throw new InvalidDataException("Publication hash mismatch."); });
            await using (var reader = await cache.OpenReadAsync(hash) ?? throw new InvalidDataException("Missing object."))
            {
                long read = 0;
                await Measure("sequential-read", sizeMiB, async () =>
                {
                    await reader.ReadExactlyAsync(buffer); read += buffer.Length;
                });
                if (read != sizeMiB * (long)chunk || await reader.ReadAsync(buffer.AsMemory(0, 1)) != 0) throw new InvalidDataException("Read length mismatch.");
            }
            await Measure("full-hash", 1, async () =>
            {
                if (await BlobVerification.VerifyAsync(cache, hash, sizeMiB * (long)chunk) != BlobVerificationResult.Verified)
                    throw new InvalidDataException("Read hash mismatch.");
            });
            var random = new Random(4617);
            await Measure("random-range", 100, async () =>
            {
                await using var reader = await cache.OpenReadAsync(hash) ?? throw new InvalidDataException("Missing object.");
                var start = random.NextInt64(sizeMiB * (long)chunk - 257);
                reader.Position = start;
                var reads = measured.Reads;
                await reader.ReadExactlyAsync(buffer.AsMemory(0, 257));
                if (measured.Reads - reads > 2) throw new InvalidDataException("Small range read unrelated chunks.");
                for (var i = 0; i < 257; i++)
                    if (buffer[i] != (byte)(((start + i) % chunk) % 251)) throw new InvalidDataException("Range mismatch.");
            });
            await Measure("pre-cancelled-read", 10, async () =>
            {
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                var reads = measured.Reads;
                try { await cache.OpenReadAsync(hash, cancellation.Token); throw new InvalidDataException("Cancellation ignored."); }
                catch (OperationCanceledException) { }
                if (reads != measured.Reads) throw new InvalidDataException("Cancelled read reached chunk storage.");
            });
            return JsonSerializer.Serialize(new BrowserQualificationResult(sizeMiB, baseline, measured.Peak,
                measured.Written, measured.ReadBytes, measured.InventoryReads, allocationCounterAvailable, stages), BrowserQualificationJson.Default.BrowserQualificationResult);
        }
        finally
        {
            await metadata.DeleteAllAsync();
            await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        }

        async Task Measure(string stage, int count, Func<Task> operation)
        {
            measured.Stage = stage;
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var timings = new List<double>();
            for (var i = 0; i < count; i++)
            {
                var start = Stopwatch.GetTimestamp(); await operation();
                timings.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                await measured.Sample();
            }
            timings.Sort();
            double Percentile(double p) => timings[Math.Clamp((int)Math.Ceiling(p * timings.Count) - 1, 0, timings.Count - 1)];
            stages.Add(new(stage, count, timings.Sum(), Percentile(.5), Percentile(.95), Percentile(.99),
                allocationCounterAvailable ? GC.GetAllocatedBytesForCurrentThread() - allocated : null));
        }
    }

    private sealed class QualificationChunks(IBlobChunkStore inner) : IBlobChunkStore
    {
        public string Stage = "baseline";
        public long Written, Reads, ReadBytes, Peak, InventoryReads;
        public async Task Sample()
        {
            var memory = GC.GetTotalMemory(false); Peak = Math.Max(Peak, memory);
            await _js.InvokeVoidAsync("bsyncQualificationSample", Stage, memory);
        }
        public async Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        { await inner.WriteAsync(id, bytes, cancellationToken); Written += bytes.Length; await Sample(); }
        public async Task<byte[]> ReadAsync(string id, int expectedLength, CancellationToken cancellationToken = default)
        { var bytes = await inner.ReadAsync(id, expectedLength, cancellationToken); Reads++; ReadBytes += bytes.Length; await Sample(); return bytes; }
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
        public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default)
        { InventoryReads++; return inner.GetStoredBytesAsync(cancellationToken); }
    }
}

internal sealed record BrowserQualificationStage(string Name, int Samples, double TotalMilliseconds,
    double P50Milliseconds, double P95Milliseconds, double P99Milliseconds, long? AllocatedBytes);
internal sealed record BrowserQualificationResult(int SizeMiB, long BaselineManagedBytes, long SampledManagedPeakBytes,
    long CiphertextWrittenBytes, long CiphertextReadBytes, long InventoryRequests,
    bool CurrentThreadAllocationCounterAvailable, List<BrowserQualificationStage> Stages);
[JsonSerializable(typeof(BrowserQualificationResult))]
internal partial class BrowserQualificationJson : JsonSerializerContext;
