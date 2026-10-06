using Bsync.Conflicts;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task D7: a kept conflict can show the local edit instead of the server state.</summary>
public sealed class KeptConflictViewTests
{
    private static async Task<(InMemorySyncServerRef Server, TestReplica Mine, TestReplica Theirs)> ConflictAsync(KeptConflictView view)
    {
        var server = InMemorySyncServerRef.Create();
        var mine = new TestReplica(server, "mine", options: new SyncOptions<Note> { KeptConflictView = view });
        var theirs = new TestReplica(server, "theirs");
        await mine.Engine.WriteAsync(new Note { Id = "n1", Title = "base" });
        await mine.Engine.SyncAsync();
        await theirs.Engine.SyncAsync();
        await theirs.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await theirs.Engine.SyncAsync();
        await mine.Engine.WriteAsync(new Note { Id = "n1", Title = "mine" });
        Assert.Equal(1, (await mine.Engine.SyncAsync()).Conflicts);
        return (server, mine, theirs);
    }

    [Fact(DisplayName = "D7 F20 I11: with the local view, the user's edit stays visible after a deferred conflict, and a newer server change does not hide it")]
    public async Task LocalEditStaysVisible()
    {
        var (_, mine, theirs) = await ConflictAsync(KeptConflictView.Local);

        Assert.Equal("mine", (await mine.Engine.QueryAsync()).Single().Title);
        Assert.Single(await mine.Engine.GetConflictsAsync());
        Assert.Equal(0, await mine.Engine.CountDirtyAsync()); // not uploaded until decided

        await theirs.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs again" });
        await theirs.Engine.SyncAsync();
        await mine.Engine.SyncAsync();
        var record = await mine.RecordAsync("n1");

        Assert.Equal("mine", record.Current.Title);
        Assert.Equal("theirs again", record.Conflict!.Server.Title); // decided against the latest server state

        Assert.True(await mine.Engine.DiscardConflictAsync("n1"));
        Assert.Equal("theirs again", (await mine.Engine.QueryAsync()).Single().Title);
    }

    [Fact(DisplayName = "D7 I11: the default (server) view is unchanged; resolving uploads the decision")]
    public async Task ServerViewIsDefault()
    {
        var (server, mine, _) = await ConflictAsync(KeptConflictView.Server);

        Assert.Equal("theirs", (await mine.Engine.QueryAsync()).Single().Title);
        await mine.Engine.ResolveConflictAsync("n1", new Note { Id = "n1", Title = "merged" });
        await mine.Engine.SyncAsync();

        Assert.Equal("merged", server.Get("n1").Title);
    }
}
