using Bsync.Clocks;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests.Sqlite;

/// <summary>SQLite-specific behaviour: reopen, multiple instances and collections, schema and identity.</summary>
public sealed class SqliteStoreTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Fact(DisplayName = "T17 T23 I01 I12: an engine restarted on a reopened file keeps pending work, cursor and clock high-water")]
    public async Task RestartKeepsState()
    {
        var server = InMemorySyncServerRef.Create();
        var frozen = new ManualClock(1_000);
        var first = new TestReplica(server, "a", physicalClock: frozen, store: await _database.OpenAsync());
        await first.Engine.WriteAsync(new Note { Id = "synced" });
        await first.Engine.SyncAsync();
        var t1 = (await first.Engine.WriteAsync(new Note { Id = "pending", Title = "offline" })).UpdatedAt;
        first.Transport.FailBeforeSend = true;
        await Assert.ThrowsAsync<InjectedFaultException>(() => first.Engine.PushAsync());
        var operationId = (await first.RecordAsync("pending")).Pending!.OperationId;
        var cursor = await first.Store.GetCursorAsync();

        SqliteStorePool.Release(_database.Path);
        var restarted = new TestReplica(server, "a", physicalClock: frozen, store: await _database.OpenAsync());
        Assert.Equal(cursor, await restarted.Store.GetCursorAsync());
        var t2 = (await restarted.Engine.WriteAsync(new Note { Id = "later" })).UpdatedAt;
        var result = await restarted.Engine.SyncAsync();

        Assert.True(t2 > t1);
        Assert.Equal(operationId, restarted.Transport.PushLog[0].Operations.Single(o => o.DocumentId == "pending").OperationId);
        Assert.True(result.IsComplete);
        Assert.Equal("offline", server.Get("pending").Title);
    }

    [Fact(DisplayName = "I02: two store instances on one file serialize concurrent read-modify-write transforms")]
    public async Task ConcurrentInstances()
    {
        var one = await _database.OpenAsync();
        var two = await _database.OpenAsync();
        await one.UpdateAsync([new("counter", _ => new SyncRecord<Note>(new Note { Id = "counter" }, null, false))]);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() =>
            (i % 2 == 0 ? one : two).UpdateAsync([new("counter", r => r! with { LocalRevision = r.LocalRevision + 1 })]))));

        Assert.Equal(40, (await one.GetAsync("counter"))!.LocalRevision);
    }

    [Fact(DisplayName = "I07: collections sharing a file are isolated")]
    public async Task CollectionsIsolated()
    {
        var notes = await _database.OpenAsync("notes");
        var tasks = await _database.OpenAsync("tasks");
        await notes.UpdateAsync([new("x", _ => new SyncRecord<Note>(new Note { Id = "x", UpdatedAt = new HlcTimestamp(5, 0, "n") }, null, true))], new ReplicaCursor(new Checkpoint("cp"), 2, false));

        Assert.Null(await tasks.GetAsync("x"));
        Assert.Empty(await tasks.GetPendingAsync(10));
        Assert.Equal(ReplicaCursor.Initial, await tasks.GetCursorAsync());
        Assert.Equal(HlcTimestamp.MinValue, await tasks.GetClockHighWaterAsync());
        Assert.Equal(1, await notes.CountDirtyAsync());
    }

    [Fact(DisplayName = "I17: a database from a newer schema is refused and left untouched")]
    public async Task NewerSchemaRefused()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync([new("x", _ => new SyncRecord<Note>(new Note { Id = "x" }, null, true))]);
        await using (var connection = new SqliteConnection($"Data Source={_database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            await command.ExecuteNonQueryAsync();
        }

        var error = await Assert.ThrowsAsync<SqliteStoreSchemaException>(() => _database.OpenAsync());
        Assert.Contains("99", error.Message);
        await using var check = new SqliteConnection($"Data Source={_database.Path}");
        await check.OpenAsync();
        await using var count = check.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM bs_records";
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }

    [Fact(DisplayName = "D8 I17: a schema 3 database upgrades in place; records keep their base, and new clean writes store none")]
    public async Task Schema3Upgrades()
    {
        var server = InMemorySyncServerRef.Create();
        var replica = new TestReplica(server, "a", store: await _database.OpenAsync());
        await replica.Engine.WriteAsync(new Note { Id = "clean", Title = "synced" });
        await replica.Engine.SyncAsync();
        await replica.Engine.WriteAsync(new Note { Id = "clean", Title = "edited" }); // pending, base differs from current
        SqliteStorePool.Release(_database.Path);

        // Turn the file into what a 0.2.0 store wrote: base stored in full, no base_same column, schema 3.
        await using (var connection = new SqliteConnection($"Data Source={_database.Path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE bs_records SET base = current WHERE base_same = 1;
                ALTER TABLE bs_records DROP COLUMN base_same;
                DROP TABLE bs_index;
                ALTER TABLE bs_records DROP COLUMN rejection_arguments;
                PRAGMA user_version = 3;
                """;
            await command.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
        var reopened = new TestReplica(server, "a", store: await _database.OpenAsync());
        var record = await reopened.RecordAsync("clean");
        await reopened.Engine.SyncAsync();
        await reopened.Engine.WriteAsync(new Note { Id = "fresh" });
        await reopened.Engine.SyncAsync();

        Assert.Equal("synced", record.Base!.Title);
        Assert.Equal("edited", record.Current.Title);
        Assert.Equal("edited", server.Get("clean").Title);
        Assert.Equal("fresh", (await reopened.RecordAsync("fresh")).Base!.Id); // base read back from base_same
        await using var check = new SqliteConnection($"Data Source={_database.Path}");
        await check.OpenAsync();
        await using var query = check.CreateCommand();
        query.CommandText = "SELECT (SELECT user_version FROM pragma_user_version), (SELECT COUNT(*) FROM bs_records WHERE base IS NULL AND base_same = 1)";
        await using var reader = await query.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(SqliteLocalStore<Note>.SchemaVersion, reader.GetInt32(0));
        Assert.Equal(2, reader.GetInt64(1)); // both clean records, after their pushes were accepted
    }

    [Fact(DisplayName = "I12: the replica id is stable across reopen; a new incarnation changes only the incarnation")]
    public async Task ReplicaIdentity()
    {
        var first = await (await _database.OpenAsync()).GetReplicaIdentityAsync();
        var reopened = await (await _database.OpenAsync("other")).GetReplicaIdentityAsync();
        var next = await (await _database.OpenAsync()).BeginNewIncarnationAsync();

        Assert.Equal(first, reopened);
        Assert.Equal(first.ReplicaId, next.ReplicaId);
        Assert.NotEqual(first.Incarnation, next.Incarnation);
        Assert.True(HlcTimestamp.IsValidNode(next.Incarnation));
    }

    [Fact(DisplayName = "I08: pending enumeration with many exclusions still returns the next records")]
    public async Task PendingWithManyExclusions()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync(Enumerable.Range(0, 300)
            .Select(i => new RecordUpdate<Note>($"n{i:D3}", _ => new SyncRecord<Note>(new Note { Id = $"n{i:D3}", UpdatedAt = new HlcTimestamp(i, 0, "n") }, null, true)))
            .ToList());
        var exclude = Enumerable.Range(0, 250).Select(i => $"n{i:D3}").ToHashSet(StringComparer.Ordinal);

        var pending = await store.GetPendingAsync(5, exclude);

        Assert.Equal(["n250", "n251", "n252", "n253", "n254"], pending.Select(r => r.Current.Id));
    }

    [Fact(DisplayName = "T60 I20: engines on SQLite stores converge through restores, lost responses and crashes")]
    public async Task EnginesOnSqliteConverge()
    {
        var world = new ManualClock(1_000_000);
        var server = InMemorySyncServerRef.Create(world);
        var replicas = new List<TestReplica>();
        for (var i = 0; i < 3; i++)
        {
            replicas.Add(new TestReplica(server, $"r{i}", options: new SyncOptions<Note> { PushBatchSize = 2, PullBatchSize = 2 }, physicalClock: world, store: await _database.OpenAsync($"replica{i}")));
        }

        var random = new Random(7);
        var backup = server.Server.CreateBackup();
        for (var step = 0; step < 150; step++)
        {
            world.Advance(1);
            var replica = replicas[random.Next(replicas.Count)];
            try
            {
                switch (random.Next(8))
                {
                    case 0 or 1:
                        await replica.Engine.WriteAsync(new Note { Id = $"d{random.Next(5)}", Title = $"s{step}" });
                        break;
                    case 2:
                        await replica.Engine.DeleteAsync($"d{random.Next(5)}");
                        break;
                    case 3:
                        replica.Transport.LoseResponses = 1;
                        await replica.Engine.PushAsync();
                        break;
                    case 4:
                        backup = server.Server.CreateBackup();
                        break;
                    case 5 when step % 3 == 0:
                        server.Restore(backup, world);
                        break;
                    default:
                        await replica.Engine.SyncAsync();
                        break;
                }
            }
            catch (InjectedFaultException)
            {
            }
            finally
            {
                replica.Transport.LoseResponses = 0;
            }
        }

        for (var round = 0; round < 10; round++)
        {
            foreach (var replica in replicas)
            {
                await replica.Engine.SyncAsync();
            }
        }

        var expected = server.Server.Snapshot().ToDictionary(n => n.Id, n => $"{n.Title}|{n.Deleted}|{n.UpdatedAt}");
        foreach (var replica in replicas)
        {
            Assert.Equal(0, await replica.Engine.CountDirtyAsync());
            Assert.Equal(expected, (await replica.Engine.QueryAsync(includeDeleted: true)).ToDictionary(n => n.Id, n => $"{n.Title}|{n.Deleted}|{n.UpdatedAt}"));
        }
    }
}
