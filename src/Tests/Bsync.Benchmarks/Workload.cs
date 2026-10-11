using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Running;
using Bsync;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Storage.Sqlite;

/// <summary>Shared setup: stores, servers and documents.</summary>
public static class Workload
{
    public static readonly string Body = new('x', 960);

    public static readonly Func<BenchDocument, BenchDocument> Clone = DocumentCloner.Json(BenchJson.Default.BenchDocument);

    public static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A document; <c>Due</c> is spread pseudo-randomly so that its order differs from the id order.</summary>
    public static BenchDocument Document(int i) => new()
    {
        Id = $"doc-{i:D6}", Title = $"Document {i}", Body = Body, Priority = i % 5, Category = i % 100,
        Due = Start.AddMinutes((long)i * 7919 % 1_000_003),
    };

    /// <summary>Stores <paramref name="count"/> clean, synced documents in one store transaction.</summary>
    public static Task SeedSyncedAsync(ILocalStore<BenchDocument> store, int count, string node)
    {
        var clock = new HybridLogicalClock(node);
        return store.UpdateAsync(Enumerable.Range(0, count).Select(i =>
        {
            var document = Document(i);
            document.UpdatedAt = clock.Now();
            return new RecordUpdate<BenchDocument>(document.Id, _ => new SyncRecord<BenchDocument>(document, document, IsDirty: false) { BaseVersion = i + 1 });
        }).ToList());
    }

    public static InMemorySyncServer<BenchDocument> Server() => new(new InMemorySyncServerOptions<BenchDocument>
    {
        Cloner = Clone,
        Fingerprint = DocumentCloner.JsonFingerprint(BenchJson.Default.BenchDocument),
        MaxOperationsPerPush = 1000,
        MaxPageSize = 1000,
    });

    /// <summary>An index on <see cref="BenchDocument.Due"/> (task E2).</summary>
    public static readonly SyncIndex<BenchDocument, DateTimeOffset> Due = SyncIndex<BenchDocument>.Create("due", d => d.Due);

    public static async Task<ILocalStore<BenchDocument>> StoreAsync(string kind, string directory, IReadOnlyList<SyncIndex<BenchDocument>>? indexes = null) => kind switch
    {
        "memory" => new InMemoryLocalStore<BenchDocument>(Clone, indexes),
        "sqlite-full" => await SqliteLocalStore<BenchDocument>.OpenAsync(new SqliteLocalStoreOptions { DataSource = Path.Combine(directory, $"{Guid.NewGuid():N}.db"), Durability = SqliteDurability.Full }, BenchJson.Default.BenchDocument, indexes ?? []),
        "sqlite-normal" => await SqliteLocalStore<BenchDocument>.OpenAsync(new SqliteLocalStoreOptions { DataSource = Path.Combine(directory, $"{Guid.NewGuid():N}.db"), Durability = SqliteDurability.Normal }, BenchJson.Default.BenchDocument, indexes ?? []),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Queues <paramref name="count"/> local writes in one store transaction (the setup is not what is measured).</summary>
    public static Task SeedPendingAsync(ILocalStore<BenchDocument> store, int count, string node)
    {
        var clock = new HybridLogicalClock(node);
        return store.UpdateAsync(Enumerable.Range(0, count).Select(i =>
        {
            var document = Document(i);
            document.UpdatedAt = clock.Now();
            return new RecordUpdate<BenchDocument>(document.Id, _ => new SyncRecord<BenchDocument>(document, null, IsDirty: true) { LocalRevision = 1 });
        }).ToList());
    }

    public static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bsync-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    public static void Delete(string directory)
    {
        SqliteConnectionPools.ReleaseAll(directory);
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
