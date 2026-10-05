using Bsync.Protocol;
using Bsync.Storage.Sqlite;
using Bsync.Tests.Sqlite;
using Bsync.Tests.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace Bsync.Tests;

/// <summary>
/// Phase 9: stuck-queue tooling (rejections, revert), moving local work between stores, and rebuilding a damaged
/// SQLite replica (docs/operations/disaster-recovery.md).
/// </summary>
public sealed class RecoveryTests(ITestOutputHelper output) : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private static InMemorySyncServerRef Rejecting(Func<bool> reject) =>
        new(new Server.InMemorySyncServer<Note>(NoteJson.ServerOptions(validator: (_, op, _) => reject() && op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null)));

    [Fact(DisplayName = "I19: rejected changes are listed and can be retried as a new operation once the cause is fixed")]
    public async Task RetryRejected()
    {
        var strict = true;
        var server = Rejecting(() => strict);
        var client = new TestReplica(server, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "bad" });
        await client.Engine.WriteAsync(new Note { Id = "n2", Title = "fine" });
        Assert.Equal(1, (await client.Engine.SyncAsync()).Rejected);
        var rejected = Assert.Single(await client.Engine.GetRejectedAsync());
        Assert.Equal(("n1", PushErrorCodes.Forbidden), (rejected.Current.Id, rejected.Rejection!.ErrorCode));
        var firstOperation = client.Transport.PushLog.SelectMany(p => p.Operations).Single(o => o.DocumentId == "n1").OperationId;

        Assert.Equal(0, (await client.Engine.SyncAsync()).Pushed); // parked: never resent on its own
        strict = false;
        Assert.NotNull(await client.Engine.RetryRejectedAsync("n1"));
        Assert.Null(await client.Engine.RetryRejectedAsync("n2")); // not rejected
        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal("bad", server.Get("n1").Title);
        Assert.Empty(await client.Engine.GetRejectedAsync());
        Assert.NotEqual(firstOperation, client.Transport.PushLog[^1].Operations.Single().OperationId); // a new operation, not a replay
    }

    [Fact(DisplayName = "I02 I10: reverting returns a document to the newest known server state; never-confirmed documents are hidden")]
    public async Task Revert()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "c");
        var other = new TestReplica(server, "o");
        await client.Engine.WriteAsync(new Note { Id = "synced", Title = "v1" });
        await client.Engine.SyncAsync();

        await client.Engine.WriteAsync(new Note { Id = "synced", Title = "local edit" });
        await client.Engine.WriteAsync(new Note { Id = "created", Title = "never sent" });
        Assert.True(await client.Engine.RevertAsync("synced"));
        Assert.True(await client.Engine.RevertAsync("created"));
        Assert.False(await client.Engine.RevertAsync("created"));
        Assert.False(await client.Engine.RevertAsync("unknown"));

        Assert.Equal("v1", (await client.RecordAsync("synced")).Current.Title);
        Assert.True((await client.RecordAsync("created")).MissingAfterReset);
        Assert.Equal(["synced"], (await client.Engine.QueryAsync()).Select(n => n.Id));
        Assert.Equal(0, await client.Engine.CountDirtyAsync());
        Assert.Equal(0, (await client.Engine.SyncAsync()).Pushed);
        Assert.Single(server.Server.Snapshot());

        // A newer server state seen while dirty is the one reverted to.
        await other.Engine.SyncAsync();
        await other.Engine.WriteAsync(new Note { Id = "synced", Title = "v2 from other" });
        await other.Engine.SyncAsync();
        await client.Engine.WriteAsync(new Note { Id = "synced", Title = "edit over v1" });
        await client.Engine.PullAsync();
        Assert.True(await client.Engine.RevertAsync("synced"));
        Assert.Equal("v2 from other", (await client.RecordAsync("synced")).Current.Title);
        Assert.Equal(0, (await client.Engine.SyncAsync()).Conflicts);
    }

    [Fact(DisplayName = "I04 I09: reverting a change whose upload outcome is unknown shows the server's decision after the next pull")]
    public async Task RevertAfterLostResponse()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "sent" });
        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());

        Assert.True(await client.Engine.RevertAsync("n1"));
        Assert.Null(await client.Engine.GetAsync("n1") is { MissingAfterReset: false } ? "visible" : null);
        await client.Engine.SyncAsync();

        Assert.Equal("sent", (await client.RecordAsync("n1")).Current.Title);
        Assert.False((await client.RecordAsync("n1")).MissingAfterReset);
        Assert.Equal(1, server.Server.ReceiptCount);
    }

    [Fact(DisplayName = "I01 I04 I11: exported local work imports into a new store and is uploaded exactly once")]
    public async Task ExportImport()
    {
        var strict = true;
        var server = Rejecting(() => strict);
        var other = new TestReplica(server, "o");
        await other.Engine.WriteAsync(new Note { Id = "contested", Title = "base" });
        await other.Engine.SyncAsync();
        var old = new TestReplica(server, "old");
        await old.Engine.SyncAsync();
        await other.Engine.WriteAsync(new Note { Id = "contested", Title = "theirs" });
        await other.Engine.SyncAsync();

        await old.Engine.WriteAsync(new Note { Id = "contested", Title = "mine" });
        await old.Engine.WriteAsync(new Note { Id = "rejected", Title = "bad" });
        await old.Engine.SyncAsync(); // conflict kept, rejection parked
        await old.Engine.WriteAsync(new Note { Id = "in-flight", Title = "sent, response lost" });
        old.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => old.Engine.PushAsync());
        await old.Engine.WriteAsync(new Note { Id = "queued", Title = "never sent" });

        var export = await old.Engine.ExportLocalChangesAsync();
        Assert.Equal(["contested", "in-flight", "queued", "rejected"], export.Select(r => r.Current.Id));

        var fresh = new TestReplica(server, "new");
        await fresh.Engine.WriteAsync(new Note { Id = "queued", Title = "the new store's own edit" });
        var skipped = await fresh.Engine.ImportLocalChangesAsync(export);
        Assert.Equal(["queued"], skipped);
        Assert.Equal(export.Single(r => r.Current.Id == "in-flight").Pending!.OperationId, (await fresh.RecordAsync("in-flight")).Pending!.OperationId);

        strict = false;
        await fresh.Engine.SyncAsync();
        Assert.Equal("sent, response lost", server.Get("in-flight").Title);
        Assert.Equal("the new store's own edit", server.Get("queued").Title);
        Assert.Equal(PushErrorCodes.Forbidden, (await fresh.RecordAsync("rejected")).Rejection!.ErrorCode); // still parked
        Assert.Equal("mine", Assert.Single(await fresh.Engine.GetConflictsAsync()).Conflict!.Local.Title);
        Assert.Contains(fresh.Transport.PushLog.SelectMany(p => p.Operations), o => o.DocumentId == "in-flight");
        Assert.Equal(1, server.Server.Snapshot().Count(n => n.Id == "in-flight"));
        Assert.Equal("theirs", server.Get("contested").Title);
        Assert.True(await fresh.Store.GetClockHighWaterAsync() >= export.Where(r => !skipped.Contains(r.Current.Id)).Max(r => r.Current.UpdatedAt));
        Assert.True(fresh.Clock.Now() > export.Max(r => r.Current.UpdatedAt));
    }

    [Fact(DisplayName = "I17: a healthy SQLite replica passes the check")]
    public async Task CheckHealthy()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync([new("n1", _ => new Storage.SyncRecord<Note>(new Note { Id = "n1" }, null, true))]);
        Assert.Empty(await SqliteStoreRecovery.CheckAsync(_database.Path));
        Assert.NotEmpty(await SqliteStoreRecovery.CheckAsync(_database.Path + ".absent"));
    }

    [Fact(DisplayName = "I01 I04: rebuilding a SQLite replica keeps local work with its operation ids and moves the old file aside")]
    public async Task RebuildKeepsLocalWork()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "o");
        await other.Engine.WriteAsync(new Note { Id = "contested", Title = "base" });
        await other.Engine.WriteAsync(new Note { Id = "clean", Title = "on the server" });
        await other.Engine.SyncAsync();
        var client = new TestReplica(server, "c", store: await _database.OpenAsync());
        await client.Engine.SyncAsync();
        await other.Engine.WriteAsync(new Note { Id = "contested", Title = "theirs" });
        await other.Engine.SyncAsync();
        await client.Engine.WriteAsync(new Note { Id = "contested", Title = "mine" });
        await client.Engine.SyncAsync();
        await client.Engine.WriteAsync(new Note { Id = "in-flight", Title = "sent" });
        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());
        await client.Engine.WriteAsync(new Note { Id = "queued", Title = "not sent" });
        var operation = (await client.RecordAsync("in-flight")).Pending!.OperationId;
        var highWater = await client.Store.GetClockHighWaterAsync();
        var identity = await (await _database.OpenAsync()).GetReplicaIdentityAsync();

        var report = await SqliteStoreRecovery.RebuildAsync(_database.Path);

        Assert.True(report.Complete);
        Assert.Equal(3, report.SalvagedRecords); // in-flight, queued, and the kept conflict; "clean" comes from the server
        Assert.True(File.Exists(report.DamagedCopy));
        Assert.Empty(await SqliteStoreRecovery.CheckAsync(_database.Path));
        var rebuilt = await _database.OpenAsync();
        Assert.NotEqual(identity.ReplicaId, (await rebuilt.GetReplicaIdentityAsync()).ReplicaId);
        Assert.True(await rebuilt.GetClockHighWaterAsync() >= highWater);
        Assert.Equal(operation, (await rebuilt.GetAsync("in-flight"))!.Pending!.OperationId);
        Assert.Null(await rebuilt.GetAsync("clean"));

        var restarted = new TestReplica(server, "c2", store: rebuilt);
        await restarted.Engine.SyncAsync();
        Assert.Equal(("sent", "not sent"), (server.Get("in-flight").Title, server.Get("queued").Title));
        Assert.Equal("on the server", (await restarted.RecordAsync("clean")).Current.Title);
        Assert.Equal("mine", Assert.Single(await restarted.Engine.GetConflictsAsync()).Conflict!.Local.Title);
        Assert.Equal(0, await restarted.Engine.CountDirtyAsync());
        Assert.Equal(1, server.Server.Snapshot().Count(n => n.Id == "in-flight"));
    }

    [Fact(DisplayName = "I01: a file that is no longer a database is reported, moved aside and replaced; nothing is invented")]
    public async Task RebuildUnreadableFile()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync([new("n1", _ => new Storage.SyncRecord<Note>(new Note { Id = "n1" }, null, true))]);
        await CheckpointAsync();
        SqliteStorePool.Release(_database.Path);
        await using (var file = File.OpenWrite(_database.Path))
        {
            file.Write(Enumerable.Repeat((byte)0x5A, 100).ToArray());
        }

        Assert.NotEmpty(await SqliteStoreRecovery.CheckAsync(_database.Path));
        var report = await SqliteStoreRecovery.RebuildAsync(_database.Path);

        Assert.False(report.Complete);
        Assert.Equal(0, report.SalvagedRecords);
        Assert.True(File.Exists(report.DamagedCopy));
        var rebuilt = await _database.OpenAsync();
        Assert.Equal(0, await rebuilt.CountDirtyAsync());
        Assert.Empty(await SqliteStoreRecovery.CheckAsync(_database.Path));
    }

    [Fact(DisplayName = "I01: rebuilding a file with damaged pages salvages what it can read and yields a sound database")]
    public async Task RebuildDamagedPages()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync(Enumerable.Range(0, 400)
            .Select(i => new Storage.RecordUpdate<Note>($"n{i:D3}", _ => new Storage.SyncRecord<Note>(new Note { Id = $"n{i:D3}", Title = new string('x', 300) }, null, true) { LocalRevision = 1 }))
            .ToList());
        await CheckpointAsync();
        SqliteStorePool.Release(_database.Path);
        var length = new FileInfo(_database.Path).Length;
        await using (var file = File.OpenWrite(_database.Path))
        {
            // Damage a stretch of pages in the second half of the file (records and index pages), not the header.
            file.Position = length / 2;
            file.Write(Enumerable.Repeat((byte)0xA5, 16 * 1024).ToArray());
        }

        Assert.NotEmpty(await SqliteStoreRecovery.CheckAsync(_database.Path));
        var report = await SqliteStoreRecovery.RebuildAsync(_database.Path);

        output.WriteLine($"salvaged {report.SalvagedRecords} of 400; read error: {report.ReadError}");
        Assert.InRange(report.SalvagedRecords, 0, 399);
        Assert.Empty(await SqliteStoreRecovery.CheckAsync(_database.Path));
        var rebuilt = await _database.OpenAsync();
        Assert.Equal(report.SalvagedRecords, await rebuilt.CountDirtyAsync());
        Assert.All(await rebuilt.QueryAsync(), n => Assert.Equal(300, n.Title.Length));
    }

    [Fact(DisplayName = "I01: a readable but damaged row is skipped and reported; the rebuilt store reads every record it has")]
    public async Task RebuildSkipsDamagedRows()
    {
        var store = await _database.OpenAsync();
        await store.UpdateAsync(new[] { "a", "b", "c" }.Select(id => new Storage.RecordUpdate<Note>(id, _ => new Storage.SyncRecord<Note>(new Note { Id = id, Title = id }, null, true) { LocalRevision = 1 })).ToList());
        await using (var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE bs_records SET current = '{\"Id\":\"b\",\"Tit' WHERE id = 'b'";
            await command.ExecuteNonQueryAsync();
        }

        var report = await SqliteStoreRecovery.RebuildAsync(_database.Path);

        Assert.Equal((2, 1), (report.SalvagedRecords, report.DiscardedRecords));
        Assert.False(report.Complete);
        var rebuilt = await _database.OpenAsync();
        Assert.Equal(["a", "c"], (await rebuilt.QueryAsync()).Select(n => n.Id));
    }

    private async Task CheckpointAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_database.Path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await command.ExecuteNonQueryAsync();
    }
}
