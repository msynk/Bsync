using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>Task D1: a request the server refuses as too large is split; a document too large on its own is parked locally.</summary>
public sealed class OversizedPushTests
{
    [Fact(DisplayName = "D1 I08 I19: one 5 MiB document among 99 small ones is parked as payload-too-large; the 99 are uploaded")]
    public async Task OversizedDocumentIsParked()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions(SystemPhysicalClock.Instance));
        await using var host = await SyncTestHost.StartAsync(server); // 4 MiB request body limit
        var client = new TestReplica(InMemorySyncServerRef.Create(), "c", physicalClock: SystemPhysicalClock.Instance, transport: _ => host.Transport());
        for (var i = 0; i < 99; i++)
        {
            await client.Engine.WriteAsync(new Note { Id = $"n{i:00}", Title = "small" });
        }

        await client.Engine.WriteAsync(new Note { Id = "huge", Title = new string('x', 5 * 1024 * 1024) });

        var result = await client.Engine.SyncAsync();
        var huge = await client.RecordAsync("huge");

        Assert.Equal(99, result.Pushed);
        Assert.Equal(1, result.Rejected);
        Assert.Equal(PushErrorCodes.PayloadTooLarge, huge.Rejection!.ErrorCode);
        Assert.Equal(99, server.Snapshot().Count);
        Assert.Null(server.GetVersion("huge"));
        Assert.Equal(1, await client.Engine.CountDirtyAsync()); // the parked edit is kept, not dropped

        // Made smaller, it is retried as a new operation and accepted.
        await client.Engine.WriteAsync(new Note { Id = "huge", Title = "smaller now" });
        var retried = await client.Engine.SyncAsync();
        Assert.Equal(1, retried.Pushed);
        Assert.Equal("smaller now", server.Snapshot().Single(n => n.Id == "huge").Title);
    }

    [Fact(DisplayName = "D1 I19: an oversized dependency group is parked whole and never split")]
    public async Task OversizedGroupIsParkedWhole()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions(SystemPhysicalClock.Instance));
        await using var host = await SyncTestHost.StartAsync(server, maxRequestBodyBytes: 64 * 1024);
        var client = new TestReplica(InMemorySyncServerRef.Create(), "c", physicalClock: SystemPhysicalClock.Instance, transport: _ => host.Transport());
        await client.Engine.SyncAsync(); // learn that the server supports groups
        await client.Engine.WriteAsync(new Note { Id = "alone", Title = "small" });
        await client.Engine.WriteGroupAsync([new Note { Id = "order", Title = new string('o', 40 * 1024) }, new Note { Id = "line", Title = new string('l', 40 * 1024) }]);

        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal(["alone"], server.Snapshot().Select(n => n.Id));
        Assert.NotNull((await client.RecordAsync("order")).Rejection);
        Assert.NotNull((await client.RecordAsync("line")).Rejection);
    }
}
