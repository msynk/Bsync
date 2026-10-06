using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task F1: a pending document can be held back until something it names (an attachment's bytes) is on the server.</summary>
public sealed class ReadyToPushTests
{
    [Fact(DisplayName = "F1 I01 I08: a held document stays pending and counts as deferred; the others upload; it uploads once ready")]
    public async Task HeldDocumentWaits()
    {
        var server = InMemorySyncServerRef.Create();
        var ready = new HashSet<string>(StringComparer.Ordinal) { "plain" };
        var replica = new TestReplica(server, "a", options: new SyncOptions<Note> { ReadyToPush = (note, _) => ValueTask.FromResult(ready.Contains(note.Id)) });
        await replica.Engine.WriteAsync(new Note { Id = "plain" });
        await replica.Engine.WriteAsync(new Note { Id = "with-attachment" });

        var first = await replica.Engine.SyncAsync();

        Assert.Equal((1, 1, false), (first.Pushed, first.Deferred, first.IsComplete));
        Assert.Null(server.Server.GetVersion("with-attachment"));
        Assert.True((await replica.RecordAsync("with-attachment")).IsDirty);

        ready.Add("with-attachment");
        var second = await replica.Engine.SyncAsync();

        Assert.Equal((1, 0, true), (second.Pushed, second.Deferred, second.IsComplete));
        Assert.NotNull(server.Server.GetVersion("with-attachment"));
    }

    [Fact(DisplayName = "F1 I19: a held member holds its whole dependency group")]
    public async Task HeldMemberHoldsGroup()
    {
        var server = InMemorySyncServerRef.Create();
        var held = true;
        var replica = new TestReplica(server, "a", options: new SyncOptions<Note> { ReadyToPush = (note, _) => ValueTask.FromResult(!(held && note.Id == "line")) });
        await replica.Engine.SyncAsync(); // learns the server's features (groups)
        await replica.Engine.WriteGroupAsync([new Note { Id = "order" }, new Note { Id = "line" }]);
        await replica.Engine.WriteAsync(new Note { Id = "other" });

        var first = await replica.Engine.SyncAsync();

        Assert.Null(server.Server.GetVersion("order"));
        Assert.Null(server.Server.GetVersion("line"));
        Assert.NotNull(server.Server.GetVersion("other"));
        Assert.Equal(2, first.Deferred);

        held = false;
        await replica.Engine.SyncAsync();

        Assert.NotNull(server.Server.GetVersion("order"));
        Assert.NotNull(server.Server.GetVersion("line"));
    }
}
