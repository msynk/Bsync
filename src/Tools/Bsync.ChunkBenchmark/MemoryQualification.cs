using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage.Sqlite;

// Separate from latency benchmarks: the GC cap deliberately changes collection frequency.
internal static class MemoryQualification
{
    private const int Chunk = 1048576;

    public static async Task<int> RunAsync(string[] args)
    {
        var heapMib = 31;
        if (args.Length is < 3 or > 4 || !int.TryParse(args[1], out var size) || size is < 1 or > 1024 ||
            !int.TryParse(args[2], out var sample) || sample < 1 ||
            (args.Length == 4 && (!int.TryParse(args[3], out heapMib) || heapMib is < 1 or > 1024)))
        {
            Console.Error.WriteLine("Usage: --memory <new-output.json> <MiB> <sample-number> [heap-limit-MiB=31]; requires a matching GC heap hard limit.");
            return 2;
        }
        var heapLimit = heapMib * (long)Chunk;
        var target = Path.GetFullPath(args[0]);
        if (File.Exists(target)) throw new IOException("Refusing to replace existing qualification evidence.");
        var configuration = GC.GetConfigurationVariables();
        if (!Environment.Is64BitProcess || GC.GetGCMemoryInfo().TotalAvailableMemoryBytes != heapLimit ||
            !configuration.TryGetValue("GCHeapHardLimit", out var limit) || Convert.ToInt64(limit) != heapLimit)
            throw new InvalidOperationException($"Start a fresh 64-bit process with DOTNET_GCHeapHardLimit={heapLimit:X} ({heapMib} MiB), without per-heap overrides.");

        SQLitePCL.Batteries_V2.Init();
        var directory = Directory.CreateTempSubdirectory("bsync-memory-").FullName;
        var database = Path.Combine(directory, "metadata.db");
        var stages = new List<MemoryStage>();
        var phase = "setup";
        string? failure = null;
        FailureMemoryReport? failureMemory = null;
        var appendedChunks = 0;
        try
        {
            var account = new AccountScope("benchmark", "memory", "local");
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(database, account);
            IBlobChunkStore storage = new BlobCacheChunkStore(new FileBlobCache(Path.Combine(directory, "chunks")));
            if (int.TryParse(Environment.GetEnvironmentVariable("BSYNC_MEMORY_PAUSE_WRITE"), out var pauseWrite) && pauseWrite > 0)
                storage = new DiagnosticChunks(storage);
            var chunks = new MeasuredChunks(storage);
            var operations = new FileBlobCacheLock(Path.Combine(directory, "operations"));
            var lifetimes = new FileAccountResourceLock(Path.Combine(directory, "lifetimes"));
            var keys = new MeasurementKeys();
            var firstKey = new LocalKeyReference(account, "measurement-first", 1);
            var secondKey = new LocalKeyReference(account, "measurement-second", 1);
            ChunkedEncryptedBlobCache Create(LocalKeyReference key) => new(chunks, metadata, operations, lifetimes, keys, key, new PlatformBlobEncryption());
            var cache = Create(firstKey);
            var caller = new byte[Chunk];
            for (var i = 0; i < caller.Length; i++) caller[i] = (byte)(i % 251);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var i = 0; i < size; i++) digest.AppendData(caller);
            var hash = Convert.ToHexStringLower(digest.GetHashAndReset());
            var expectedPayload = size * (Chunk + 28L);

            await Measure("append", async () =>
            {
                for (var i = 0; i < size; i++)
                {
                    await cache.AppendPartialAsync(hash, caller); appendedChunks++;
                }
                Require(chunks.Written == expectedPayload, "Append payload amplification.");
            });
            await Measure("complete", async () => Require(await cache.CompletePartialAsync(hash), "Publication hash mismatch."));
            await Measure("sequential-read", () => VerifyHash(cache, hash));
            await Measure("seeded-ranges", () => VerifyRanges(cache, hash, size * (long)Chunk));
            await Measure("bounded-scrub", async () =>
            {
                ChunkScrubResult result;
                do { result = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 8L * Chunk, 8); }
                while (result.Status == ChunkScrubStatus.Deferred);
                Require(result.Status == ChunkScrubStatus.Complete && result.VerifiedBytes == size * (long)Chunk, "Scrub incomplete.");
            });

            // Keep an authenticated old buffer and generation alive through actual key rotation.
            await using (var old = await cache.OpenReadAsync(hash) ?? throw new InvalidDataException("Missing old reader."))
            {
                await old.ReadExactlyAsync(caller.AsMemory(0, 257));
                var rotated = Create(secondKey);
                await Measure("rotate-with-old-reader", () => rotated.RotateAsync(hash));
                Require(chunks.Written == 2 * expectedPayload, "Rotation payload amplification.");
                await Measure("old-reader-and-retention", async () =>
                {
                    await Collect(rotated);
                    Require(await rotated.GetStoredBytesAsync() == 2 * expectedPayload, "Live generation was collected.");
                    var retention = await rotated.GetKeyRetentionPageAsync();
                    Require(retention.Generations.Count == 2 && retention.Generations.Any(g => g.Key == firstKey) &&
                        retention.Generations.Any(g => g.Key == secondKey), "Missing retained key obligation.");
                    old.Position = 0;
                    Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(old)) == hash, "Old reader hash mismatch after rotation.");
                });
                cache = rotated;
            }
            await Measure("retired-collection", async () =>
            {
                await Collect(cache);
                Require(await cache.GetStoredBytesAsync() == expectedPayload, "Retired ciphertext was not reclaimed.");
                var retention = await cache.GetKeyRetentionPageAsync();
                Require(retention.Generations.Count == 1 && retention.Generations[0].Key == secondKey, "Retired key obligation was not reclaimed.");
            });
            await Measure("rotated-sequential-read", () => VerifyHash(cache, hash));
            await Measure("rotated-seeded-ranges", () => VerifyRanges(cache, hash, size * (long)Chunk));
        }
        catch (Exception error)
        {
            var before = MemorySnapshot();
            // Diagnostic only after failure; never retry or turn an OOM into a passing qualification.
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            failureMemory = new(before, MemorySnapshot());
            failure = error.ToString();
            if (Environment.GetEnvironmentVariable("BSYNC_MEMORY_DIAGNOSTIC_PAUSE") == "1")
            {
                Console.WriteLine($"Failed process {Environment.ProcessId}: allowing 30 seconds for a local diagnostic dump.");
                await Task.Delay(TimeSpan.FromSeconds(30));
            }
        }
        finally
        {
            await SqliteStorePool.DeleteDatabaseAsync(database);
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolved = Path.GetFullPath(directory);
            if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("bsync-memory-", StringComparison.Ordinal))
                throw new IOException("Unexpected temporary directory; refusing recursive cleanup.");
            Directory.Delete(resolved, recursive: true);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(output, new MemoryReport
        {
            Schema = 1, Timestamp = DateTimeOffset.UtcNow, Passed = failure is null, FailedStage = failure is null ? null : phase,
            Failure = failure, FailureMemory = failureMemory, AppendedChunks = appendedChunks,
            Mebibytes = size, Sample = sample, ChunkBytes = Chunk, RangeSeed = 90418,
            Environment = new BenchmarkEnvironment { OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription, ProcessorCount = Environment.ProcessorCount, GCSettings = configuration },
            EffectiveAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            RequestedHeapLimitBytes = heapLimit, PlanCacheWorkingBufferTargetBytes = 32L * Chunk,
            LibraryAssemblySha256 = BenchmarkIdentity.LibraryAssemblySha256, ExecutableSha256 = BenchmarkIdentity.ExecutableSha256,
            Limits = new[] {
                "The configured hard limit covers the whole managed heap and GC bookkeeping, including caller/test buffers; not native SQLite, OS caches, crypto handles, JIT or total process memory.",
                "A successful run with a cap at or above 32 MiB does not establish the plan's less-than-32-MiB incremental cache working-buffer gate.",
                "Five-millisecond samples and stage boundaries can miss peaks; the configured GC hard limit is separate evidence from sampled maxima.",
                "One object, 1 MiB chunks and one active read operation, plus one retained reader during rotation. No concurrent transfer workload or browser/mobile inference.",
                "Latency under constrained GC is diagnostic, not comparable to unconstrained benchmark percentiles. No forced GC inside measured stages." },
            Stages = stages
        }, BenchmarkJson.Default.MemoryReport);
        Console.WriteLine($"{size} MiB sample {sample}: {(failure is null ? "PASS" : "FAIL")} with {heapLimit} byte GC cap, {stages.Count} stages.");
        return failure is null ? 0 : 1;

        async Task Measure(string name, Func<Task> action)
        {
            phase = name;
            using var sampler = new CancellationTokenSource();
            long peak = 0, committed = 0;
            void Observe()
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                committed = Math.Max(committed, GC.GetGCMemoryInfo().TotalCommittedBytes);
            }
            Observe();
            var monitoring = Task.Run(async () =>
            {
                try { while (true) { Observe(); await Task.Delay(5, sampler.Token); } }
                catch (OperationCanceledException) when (sampler.IsCancellationRequested) { }
            });
            var allocation = GC.GetTotalAllocatedBytes(true);
            var started = Stopwatch.GetTimestamp();
            var passed = false;
            try { await action(); passed = true; }
            finally
            {
                sampler.Cancel(); await monitoring; Observe();
                stages.Add(new(name, passed, Stopwatch.GetElapsedTime(started).TotalMilliseconds, peak, committed,
                    GC.GetTotalAllocatedBytes(true) - allocation));
                Console.WriteLine($"  {name}: {(passed ? "pass" : "fail")}, sampled heap {peak / (double)Chunk:F2} MiB");
            }
        }
    }

    private static MemorySnapshotReport MemorySnapshot()
    {
        var info = GC.GetGCMemoryInfo();
        return new(GC.GetTotalMemory(false), info.HeapSizeBytes, info.FragmentedBytes, info.TotalCommittedBytes);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private static async Task VerifyHash(ChunkedEncryptedBlobCache cache, string hash)
    {
        await using var reader = await cache.OpenReadAsync(hash) ?? throw new InvalidDataException("Missing published object.");
        Require(Convert.ToHexStringLower(await SHA256.HashDataAsync(reader)) == hash, "Reader content hash mismatch.");
    }
    private static async Task VerifyRanges(ChunkedEncryptedBlobCache cache, string hash, long length)
    {
        await using var reader = await cache.OpenReadAsync(hash) ?? throw new InvalidDataException("Missing published object.");
        var buffer = new byte[2 * Chunk + 17];
        var random = new Random(90418);
        var starts = new List<long> { 0, Chunk - 1, Math.Max(0, length - 257), length };
        for (var i = 0; i < 64; i++) starts.Add(random.NextInt64(length));
        foreach (var start in starts)
        {
            reader.Position = start;
            var count = (int)Math.Min(buffer.Length, length - start);
            await reader.ReadExactlyAsync(buffer.AsMemory(0, count));
            for (var i = 0; i < count; i++)
                Require(buffer[i] == (byte)(((start + i) % Chunk) % 251), "Range content mismatch.");
            Require(reader.Position == start + count, "Range position mismatch.");
        }
        reader.Position = length;
        Require(await reader.ReadAsync(buffer.AsMemory(0, 1)) == 0, "Reader did not reach EOF.");
    }
    private static async Task Collect(ChunkedEncryptedBlobCache cache)
    {
        string? continuation = null;
        // Collect may return null after a full chunk page, so a complete inventory scan alone is not a completion signal.
        // This workload has exactly two generations; each fresh pass can reclaim up to 128 chunks.
        for (var pass = 0; pass < 10; pass++)
            do { continuation = await cache.CollectAsync(continuation); } while (continuation is not null);
    }
    private sealed class DiagnosticChunks(IBlobChunkStore inner) : IBlobChunkStore
    {
        private readonly int _pauseWrite = int.TryParse(Environment.GetEnvironmentVariable("BSYNC_MEMORY_PAUSE_WRITE"), out var value) ? value : 0;
        private int _writes;
        public async Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(id, bytes, cancellationToken);
            if (++_writes == _pauseWrite)
            {
                Console.WriteLine($"Diagnostic process {Environment.ProcessId}: pausing 30 seconds at write {_writes} for a live heap dump.");
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
        }
        public Task<byte[]> ReadAsync(string id, int expectedLength, CancellationToken cancellationToken = default) => inner.ReadAsync(id, expectedLength, cancellationToken);
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
        public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => inner.GetStoredBytesAsync(cancellationToken);
    }
    private sealed class MeasurementKeys : ILocalKeyProvider
    {
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(LocalKeyAccess.Available(reference, SHA256.HashData(Encoding.UTF8.GetBytes("measurement-only/" + reference.KeyId))));
        }
    }
}
