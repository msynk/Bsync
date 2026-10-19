using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(BenchmarkReport))]
[JsonSerializable(typeof(MemoryReport))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(string))]
internal partial class BenchmarkJson : JsonSerializerContext;

internal sealed class BenchmarkEnvironment
{
    public required string OS { get; init; }
    public required string Architecture { get; init; }
    public required string Runtime { get; init; }
    public int ProcessorCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object>? GCSettings { get; init; }
}
internal sealed record BenchmarkTargets(int ChunkBytes, double MaximumPayloadWriteRatio, long DesiredCacheWorkingBuffersBytes);
internal sealed class BenchmarkReport
{
    public int Schema { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public required BenchmarkEnvironment Environment { get; init; }
    public string? LibraryAssemblySha256 { get; init; }
    public string? ExecutableSha256 { get; init; }
    public required BenchmarkTargets DeclaredTargets { get; init; }
    public required string[] Limits { get; init; }
    public required List<Measurement> Measurements { get; init; }
}
internal sealed record MemorySnapshotReport(long ManagedBytes, long HeapSizeBytes, long FragmentedBytes, long TotalCommittedBytes);
internal sealed record FailureMemoryReport(MemorySnapshotReport BeforeCompaction, MemorySnapshotReport AfterCompaction);
internal sealed record MemoryStage(string Name, bool Passed, double Milliseconds, long SampledManagedBytes, long ObservedGcCommittedBytes, long AllocatedBytes);
internal sealed class MemoryReport
{
    public int Schema { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public bool Passed { get; init; }
    public string? FailedStage { get; init; }
    public string? Failure { get; init; }
    public FailureMemoryReport? FailureMemory { get; init; }
    public int AppendedChunks { get; init; }
    public int Mebibytes { get; init; }
    public int Sample { get; init; }
    public int ChunkBytes { get; init; }
    public int RangeSeed { get; init; }
    public required BenchmarkEnvironment Environment { get; init; }
    public long EffectiveAvailableMemoryBytes { get; init; }
    public long RequestedHeapLimitBytes { get; init; }
    public long PlanCacheWorkingBufferTargetBytes { get; init; }
    public string? LibraryAssemblySha256 { get; init; }
    public string? ExecutableSha256 { get; init; }
    public required string[] Limits { get; init; }
    public required List<MemoryStage> Stages { get; init; }
}
