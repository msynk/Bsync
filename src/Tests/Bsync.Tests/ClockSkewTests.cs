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

    [Fact(DisplayName = "D2: pull responses carry the server time and feature")]
    public void ServerAdvertisesTime()
    {
        var server = InMemorySyncServerRef.Create(new ManualClock(ServerNow));

        var page = server.Server.Pull(new PullRequest(Checkpoint.Start, 1));

        Assert.Contains(SyncFeatures.ServerTime, page.Features!);
        Assert.Equal(ServerNow, page.ServerTime);
    }
}
