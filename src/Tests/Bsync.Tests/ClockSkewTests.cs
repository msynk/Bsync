using Bsync.Storage;
using Bsync.Protocol;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task D2: the server advertises its time; a replica whose clock is ahead corrects it before stamping writes.</summary>
public sealed class ClockSkewTests
{
    private const long ServerNow = 1_790_000_000_000;

    [Fact(DisplayName = "D2 I12: a device clock two hours ahead stamps accepted writes after its first pull, over the wire encoding")]
    public async Task AheadClockConverges()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));
        var device = new ManualClock(ServerNow + (long)TimeSpan.FromHours(2).TotalMilliseconds);
        var client = new TestReplica(server, "fast", physicalClock: device, transport: inner => new JsonWireTransport<Note>(inner, NoteJsonContext.Default));

        await client.Engine.SyncAsync(); // learns the server's time
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "written with a fast clock" });
        var result = await client.Engine.SyncAsync();
        var stored = server.Get("n1");

        Assert.Equal(1, result.Pushed);
        Assert.Equal(0, result.Rejected);
        Assert.InRange(stored.UpdatedAt.WallTime, ServerNow - 5_000, ServerNow + 5_000);
        Assert.InRange(client.Clock.PhysicalOffset, TimeSpan.FromHours(-2.01), TimeSpan.FromHours(-1.99));
    }

    [Fact(DisplayName = "D2 I12: the correction never makes timestamps go backwards, and is dropped when the device clock is fixed")]
    public async Task CorrectionIsMonotonicAndRecovers()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));
        var device = new ManualClock(ServerNow + (long)TimeSpan.FromHours(2).TotalMilliseconds);
        var client = new TestReplica(server, "fast", physicalClock: device);
        var before = client.Clock.Now(); // issued with the uncorrected clock

        await client.Engine.SyncAsync();
        var after = client.Clock.Now();
        device.Set(ServerNow);
        await client.Engine.SyncAsync();

        Assert.True(after > before); // HLC stays strictly monotonic even though the offset is negative
        Assert.Equal(TimeSpan.Zero, client.Clock.PhysicalOffset);
    }

    [Fact(DisplayName = "ADR-017 I12 I19: writes stamped offline with a clock two hours ahead are re-stamped in order and accepted in the same sync; a restart stays corrected")]
    public async Task OfflineWritesAreRestamped()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));
        var device = new ManualClock(ServerNow + (long)TimeSpan.FromHours(2).TotalMilliseconds);
        var store = new InMemoryLocalStore<Note>(NoteJson.Clone);
        var client = new TestReplica(server, "fast", physicalClock: device, store: store);
        await client.Engine.WriteAsync(new Note { Id = "first", Title = "1" }); // before any pull: stamped two hours ahead
        await client.Engine.WriteGroupAsync([new Note { Id = "order" }, new Note { Id = "line" }]);
        await client.Engine.WriteAsync(new Note { Id = "first", Title = "2" });
        var fast = (await client.Engine.GetAsync("first"))!.Current.UpdatedAt;

        var result = await client.Engine.SyncAsync();

        Assert.Equal((3, 0, true), (result.Pushed, result.Rejected, result.IsComplete));
        Assert.Equal("2", server.Get("first").Title);
        Assert.All(new[] { "first", "order", "line" }, id => Assert.InRange(server.Get(id).UpdatedAt.WallTime, ServerNow - 5_000, ServerNow + 5_000));
        Assert.True(server.Get("order").UpdatedAt < server.Get("line").UpdatedAt && server.Get("line").UpdatedAt < server.Get("first").UpdatedAt); // original order kept (the second edit of "first" came last)
        Assert.True(await store.GetClockHighWaterAsync() < fast); // lowered, so a restart does not jump ahead again

        var restarted = new TestReplica(server, "fast", physicalClock: device, store: store);
        await restarted.Engine.SyncAsync();
        await restarted.Engine.WriteAsync(new Note { Id = "later" });
        var after = await restarted.Engine.SyncAsync();
        Assert.Equal((1, 0), (after.Pushed, after.Rejected));
    }

    [Fact(DisplayName = "ADR-017: with re-stamping turned off, a write stamped before the first pull stays parked with clock-skew")]
    public async Task RestampingCanBeTurnedOff()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));
        var device = new ManualClock(ServerNow + (long)TimeSpan.FromHours(2).TotalMilliseconds);
        var client = new TestReplica(server, "fast", physicalClock: device, options: new SyncOptions<Note> { RestampSkewedWrites = false });
        await client.Engine.WriteAsync(new Note { Id = "n1" });

        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Rejected);
        Assert.Equal(PushErrorCodes.ClockSkew, (await client.Engine.GetAsync("n1"))!.Rejection!.ErrorCode);
    }

    [Fact(DisplayName = "D2: pull responses carry the server time and feature")]
    public void ServerAdvertisesTime()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));

        var page = server.Server.Pull(new PullRequest(Checkpoint.Start, 1));

        Assert.Contains(SyncFeatures.ServerTime, page.Features!);
        Assert.Equal(ServerNow, page.ServerTime);
    }
}
