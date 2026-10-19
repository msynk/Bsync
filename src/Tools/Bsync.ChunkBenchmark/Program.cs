using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage.Sqlite;

// Explicit local measurement, never a substitute for the release/device qualification matrix.
if (args.FirstOrDefault() == "--memory") return await MemoryQualification.RunAsync(args[1..]);
if (args.Length != 3 || !int.TryParse(args[2], out var samples) || samples is < 1 or > 100)
{
    Console.Error.WriteLine("Usage: Bsync.ChunkBenchmark <output.json> <MiB,MiB,...> <samples-per-size>"); return 2;
}
var sizes = args[1].Split(',').Select(int.Parse).ToArray();
if (sizes.Any(s => s is < 1 or > 1024)) throw new ArgumentOutOfRangeException(nameof(sizes));
SQLitePCL.Batteries_V2.Init();
var rows = new List<Measurement>();
foreach (var size in sizes)
for (var run = 0; run < samples; run++)
{
    var directory = Directory.CreateTempSubdirectory("bsync-measure-").FullName;
    var path = Path.Combine(directory, "metadata.db");
    try
    {
        var account = new AccountScope("benchmark", "native", "local");
        await using var metadata = await SqliteOrchestrationStore.OpenAsync(path, account);
        var chunks = new MeasuredChunks(new BlobCacheChunkStore(new FileBlobCache(Path.Combine(directory, "chunks"))));
        var cache = new ChunkedEncryptedBlobCache(chunks, metadata, new FileBlobCacheLock(Path.Combine(directory, "operations")),
            new FileAccountResourceLock(Path.Combine(directory, "lifetimes")), new Keys(), new(account, "measurement", 1), new PlatformBlobEncryption());
        var buffer = new byte[1048576]; for (var i = 0; i < buffer.Length; i++) buffer[i] = (byte)(i % 251);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var i = 0; i < size; i++) digest.AppendData(buffer);
        var hash = Convert.ToHexStringLower(digest.GetHashAndReset());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var baseline = GC.GetTotalMemory(false); var peak = baseline;
        using var monitoring = new CancellationTokenSource();
        var monitor = Task.Run(async () =>
        {
            try { while (true) { peak = Math.Max(peak, GC.GetTotalMemory(false)); await Task.Delay(5, monitoring.Token); } }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
        });
        var allocated = GC.GetTotalAllocatedBytes(true); var before = Io.Read();
        var clock = Stopwatch.StartNew(); var appends = new List<double>();
        for (var i = 0; i < size; i++)
        {
            var started = Stopwatch.GetTimestamp(); await cache.AppendPartialAsync(hash, buffer);
            appends.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var appendMilliseconds = clock.Elapsed.TotalMilliseconds; var afterAppend = Io.Read();
        var appendedBytes = chunks.Written;
        if (!await cache.CompletePartialAsync(hash)) throw new InvalidDataException("Measured content did not verify.");
        var totalMilliseconds = clock.Elapsed.TotalMilliseconds;
        var allocationBytes = GC.GetTotalAllocatedBytes(true) - allocated;
        monitoring.Cancel(); await monitor;
        var scrubStart = Stopwatch.GetTimestamp();
        ChunkScrubResult result;
        do { result = await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), 8L * 1048576, 8); }
        while (result.Status != ChunkScrubStatus.Complete);
        var scrubMilliseconds = Stopwatch.GetElapsedTime(scrubStart).TotalMilliseconds;
        long canceledAt = 0; using var stop = new CancellationTokenSource();
        using var canceled = stop.Token.Register(() => Interlocked.Exchange(ref canceledAt, Stopwatch.GetTimestamp()));
        stop.CancelAfter(TimeSpan.FromMilliseconds(1));
        double? cancellationMilliseconds = null;
        try { await cache.ScrubAsync(hash, TimeSpan.FromSeconds(30), long.MaxValue, restart: true, cancellationToken: stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { var stamp = Interlocked.Read(ref canceledAt); if (stamp != 0) cancellationMilliseconds = Stopwatch.GetElapsedTime(stamp).TotalMilliseconds; }
        var stored = await cache.GetStoredBytesAsync();
        var logicalWrites = before is { } a && afterAppend is { } b ? b - a : (ulong?)null;
        var row = new Measurement(size, run + 1, appendMilliseconds, totalMilliseconds, Percentile(appends, .5),
            Percentile(appends, .95), Percentile(appends, .99), allocationBytes, baseline, Math.Max(peak, baseline) - baseline,
            appendedBytes, stored, logicalWrites, scrubMilliseconds, cancellationMilliseconds);
        if (appendedBytes != size * (1048576L + 28) || stored != appendedBytes) throw new InvalidDataException("Unexpected append amplification.");
        rows.Add(row);
        Console.WriteLine($"{size} MiB sample {run + 1}: append {appendMilliseconds:F1} ms, payload ratio {(double)appendedBytes / (size * 1048576L):F6}");
    }
    finally { await SqliteStorePool.DeleteDatabaseAsync(path); Directory.Delete(directory, recursive: true); }
}
var target = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
await File.WriteAllTextAsync(target, JsonSerializer.Serialize(new BenchmarkReport
{
    Schema = 1, Timestamp = DateTimeOffset.UtcNow, Environment = new BenchmarkEnvironment { OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription, ProcessorCount = System.Environment.ProcessorCount },
    LibraryAssemblySha256 = BenchmarkIdentity.LibraryAssemblySha256, ExecutableSha256 = BenchmarkIdentity.ExecutableSha256,
    DeclaredTargets = new(1048576, 1.001, 32 * 1048576),
    Limits = new[] {
        "Native FileBlobCache + SQLite only. Fixed 1 MiB appends; caller buffer allocated before measurement.",
        "Payload writes count actual immutable chunk-store writes. OS logical write bytes include SQLite and filesystem I/O, not physical media amplification.",
        "Five-millisecond managed-heap sampling includes runtime/garbage and may miss peaks; it cannot certify cache-only working buffers below 32 MiB.",
        "Allocations cover the whole process during append/completion. Percentiles describe per-chunk appends in each run, not a population SLO.",
        "Cancellation measures a timed scrub request when cancellation is observed; null means no observation. No mobile/browser duplication or flash telemetry measured." },
    Measurements = rows
}, BenchmarkJson.Default.BenchmarkReport));
return 0;

static double Percentile(List<double> values, double p) => values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(p * values.Count) - 1, 0, values.Count - 1));
sealed record Measurement(int Mebibytes, int Sample, double AppendMilliseconds, double AppendAndCompleteMilliseconds,
    double AppendP50Milliseconds, double AppendP95Milliseconds, double AppendP99Milliseconds, long AllocatedBytes,
    long ManagedBaselineBytes, long SampledManagedIncreaseBytes, long ChunkPayloadWrittenBytes, long StoredCiphertextBytes,
    ulong? OsLogicalAppendWriteBytes, double ResumableScrubMilliseconds, double? ScrubCancellationMilliseconds);
sealed class Keys : ILocalKeyProvider
{
    public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)11, 32).ToArray()));
}
sealed class MeasuredChunks(IBlobChunkStore inner) : IBlobChunkStore
{
    public long Written;
    public async Task WriteAsync(string id, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    { await inner.WriteAsync(id, bytes, cancellationToken); Written += bytes.Length; }
    public Task<byte[]> ReadAsync(string id, int expectedLength, CancellationToken cancellationToken = default) => inner.ReadAsync(id, expectedLength, cancellationToken);
    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => inner.DeleteAsync(id, cancellationToken);
    public Task<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) => inner.GetStoredBytesAsync(cancellationToken);
}
static class Io
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out Counters counters);
    internal static ulong? Read()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var process = Process.GetCurrentProcess();
        return GetProcessIoCounters(process.Handle, out var counters) ? counters.WriteBytes : null;
    }
}
