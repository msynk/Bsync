using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Storage.Sqlite;
using Bsync.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Bsync.Tests.Sqlite;

/// <summary>Task C2: pull-only collections store one copy per document and refuse local writes.</summary>
public sealed class PullOnlyTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bsync-pullonly-").FullName;
    private readonly List<string> _paths = [];

    public void Dispose()
    {
        foreach (var path in _paths)
        {
            SqliteStorePool.Release(path);
        }

        Directory.Delete(_directory, recursive: true);
    }

    private static InMemorySyncServerRef ServerWith(int count)
    {
        var server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
        var body = new string('x', 900); // about 1 KB per document
        var stamp = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "server");
        for (var start = 0; start < count; start += 1000)
        {
            server.Server.Push(new PushRequest<Note>([.. Enumerable.Range(start, Math.Min(1000, count - start))
                .Select(i => new PushOperation<Note>($"o{i}", $"n{i:00000}", null, new Note { Id = $"n{i:00000}", Title = $"note {i}", Body = body, UpdatedAt = stamp }))]));
        }

        return server;
    }

    private async Task<(SyncEngine<Note> Engine, string Path)> ReplicaAsync(InMemorySyncServerRef server, string name, SyncMode mode)
    {
        var path = Path.Combine(_directory, $"{name}.db");
        _paths.Add(path);
        var store = await SqliteLocalStore<Note>.OpenAsync(new SqliteLocalStoreOptions { DataSource = path, Collection = "notes" }, NoteJsonContext.Default.Note);
        var engine = new SyncEngine<Note>(store, new ServerRefTransport(server), new HybridLogicalClock(name), NoteJson.Clone, options: new SyncOptions<Note> { Mode = mode, PullBatchSize = 1000 });
        return (engine, path);
    }

    private static long CompactedBytes(string path)
    {
        SqliteStorePool.Release(path);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
            command.ExecuteNonQuery();
        }

        return new FileInfo(path).Length;
    }

    // Before D8 a two-way replica stored the base copy of every clean record too: 41,811,968 bytes against 14,450,688 for
    // pull-only (34.6%, docs/benchmarks.md). Since SQLite schema 4 stores that copy once, clean records cost the same in both
    // modes; pull-only still never stores base, pending or conflict copies.
    [Fact(DisplayName = "C2 D8 F11: 10,000 clean SQLite records cost no more than one copy each, in pull-only and in two-way mode")]
    public async Task PullOnlyHalvesStorage()
    {
        var server = ServerWith(10_000);
        var (twoWay, twoWayPath) = await ReplicaAsync(server, "two-way", SyncMode.TwoWay);
        var (pullOnly, pullOnlyPath) = await ReplicaAsync(server, "pull-only", SyncMode.PullOnly);

        Assert.Equal(10_000, (await twoWay.SyncAsync()).Pulled);
        Assert.Equal(10_000, (await pullOnly.SyncAsync()).Pulled);
        Assert.Equal(10_000, (await pullOnly.QueryAsync()).Count);

        var full = CompactedBytes(twoWayPath);
        var slim = CompactedBytes(pullOnlyPath);
        output.WriteLine($"two-way {full:N0} bytes, pull-only {slim:N0} bytes ({100.0 * slim / full:F1}%)");
        Assert.True(slim <= full, $"pull-only uses {100.0 * slim / full:F1}% of two-way");
        Assert.True(full <= slim * 1.05, $"a clean two-way record stores its base again ({100.0 * full / slim:F1}% of pull-only)");
        Assert.True(full / 10_000 < 2_000, $"{full / 10_000} bytes per 1 KB record"); // one JSON copy plus row overhead
    }

    [Fact(DisplayName = "C2: local writes on a pull-only replica throw SyncReadOnlyException and queue nothing; updates still arrive")]
    public async Task PullOnlyRefusesWrites()
    {
        var server = ServerWith(3);
        var (engine, _) = await ReplicaAsync(server, "reader", SyncMode.PullOnly);
        await engine.SyncAsync();

        await Assert.ThrowsAsync<SyncReadOnlyException>(() => engine.WriteAsync(new Note { Id = "n00000", Title = "local" }));
        await Assert.ThrowsAsync<SyncReadOnlyException>(() => engine.DeleteAsync("n00001"));
        await Assert.ThrowsAsync<SyncReadOnlyException>(() => engine.WriteGroupAsync([new Note { Id = "a" }]));
        server.Server.Push(new PushRequest<Note>([new PushOperation<Note>("u", "n00002", server.Server.GetVersion("n00002"), new Note { Id = "n00002", Title = "updated", UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 1, "server") })]));
        await engine.SyncAsync();

        Assert.Equal(0, await engine.CountDirtyAsync());
        Assert.Equal("updated", (await engine.GetAsync("n00002"))!.Current.Title);
        Assert.Null((await engine.GetAsync("n00002"))!.Base);
    }
}
