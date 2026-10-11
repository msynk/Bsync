using Bsync.Client;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.Sqlite;

/// <summary>Task G3: deleting an account's replica from the device (sign-out on a shared device), and storage persistence.</summary>
public sealed class ReplicaWipeTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("bsync-wipe-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private SyncSession<Note> Session(InMemorySyncServerRef server, string collection, SyncCoordinator? coordinator = null, Func<string, CancellationToken, Task>? delete = null) =>
        new(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = async (account, cancellationToken) =>
            {
                var store = await SqliteLocalStore<Note>.OpenAsync(
                    new SqliteLocalStoreOptions { DataSource = DatabasePath(account), Collection = collection },
                    NoteJsonContext.Default.Note,
                    cancellationToken);
                return new LocalReplica<Note>(store, $"{account}-{collection}");
            },
            CreateTransport = _ => new ServerRefTransport(server),
            DeleteReplica = delete ?? ((account, cancellationToken) => SqliteStorePool.DeleteDatabaseAsync(DatabasePath(account), cancellationToken)),
            Coordination = coordinator is null ? null : new SyncCoordination(coordinator, collection),
            Interval = TimeSpan.FromMinutes(10),
        });

    private string DatabasePath(string account) => Path.Combine(_directory, $"{account}.db");

    [Fact(DisplayName = "G3 I07: two collections in one database are closed together and the account's files are removed; other accounts and the server are untouched")]
    public async Task CoordinatedWipeRemovesFiles()
    {
        var server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
        await using var coordinator = new SyncCoordinator();
        await using var notes = Session(server, "notes", coordinator);
        await using var drafts = Session(server, "drafts", coordinator);
        var notesEngine = await notes.GetEngineAsync("alice");
        await notesEngine.WriteAsync(new Note { Id = "n1" });
        await (await drafts.GetEngineAsync("alice")).WriteAsync(new Note { Id = "d1" });
        await notesEngine.SyncAsync();
        await using (var bobs = Session(server, "notes"))
        {
            await (await bobs.GetEngineAsync("bob")).WriteAsync(new Note { Id = "b1" });
        }

        await coordinator.DeleteReplicasAsync("alice");

        Assert.Empty(Directory.EnumerateFiles(_directory, "alice.db*"));
        Assert.True(File.Exists(DatabasePath("bob")));
        Assert.Equal(SyncState.Stopped, notes.Status.State);
        var reopened = await SqliteLocalStore<Note>.OpenAsync(new SqliteLocalStoreOptions { DataSource = DatabasePath("alice"), Collection = "drafts" }, NoteJsonContext.Default.Note);
        Assert.Empty(await reopened.QueryAsync(includeDeleted: true)); // a new, empty database
        Assert.NotNull(server.Server.GetVersion("n1")); // never deleted from the server
    }

    [Fact(DisplayName = "G3: deleting the replica of the open account stops replication first; deleting another account's leaves the open one running")]
    public async Task WipeWhileRunning()
    {
        var server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
        var deleted = new List<string>();
        await using var session = Session(server, "notes", delete: (account, cancellationToken) =>
        {
            deleted.Add(account);
            return SqliteStorePool.DeleteDatabaseAsync(DatabasePath(account), cancellationToken);
        });
        await (await session.GetEngineAsync("carol")).WriteAsync(new Note { Id = "c1" });

        await session.DeleteReplicaAsync("dave");
        Assert.Equal("carol", session.Account);

        await session.DeleteReplicaAsync("carol");

        Assert.Equal(["dave", "carol"], deleted);
        Assert.Null(session.Account);
        Assert.False(File.Exists(DatabasePath("carol")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SyncSession<Note>(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone), "n")),
            CreateTransport = _ => new ServerRefTransport(server),
        }).DeleteReplicaAsync("carol"));
    }

    [Fact(DisplayName = "G3: the status reports whether the platform granted persistent storage")]
    public async Task PersistenceIsReported()
    {
        var server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
        await using var session = new SyncSession<Note>(new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (account, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone), account) { PersistentStorage = account == "granted" }),
            CreateTransport = _ => new ServerRefTransport(server),
            Interval = TimeSpan.FromMinutes(10),
        });

        Assert.Null(session.Status.PersistentStorage);
        await session.GetEngineAsync("refused");
        Assert.False(session.Status.PersistentStorage);
        await session.GetEngineAsync("granted");
        Assert.True(session.Status.PersistentStorage);
        await session.StopAsync();
        Assert.Null(session.Status.PersistentStorage);
    }
}
