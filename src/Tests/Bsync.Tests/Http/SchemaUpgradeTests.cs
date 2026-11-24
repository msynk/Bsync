using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>
/// Phase 9, I17: a rolling domain-schema upgrade (docs/guide.md#disaster-recovery-and-stuck-replicas, "Schema upgrades"). The
/// server first accepts both schema ids, then drops the old one; an old client stops with upgrade-required and
/// keeps its work, and the upgraded app uploads that work from the same replica without duplicating it.
/// </summary>
public sealed class SchemaUpgradeTests
{
    [Fact(DisplayName = "I01 I04 I17: an app left behind by a schema upgrade keeps its pending work and uploads it once after upgrading")]
    public async Task RollingUpgrade()
    {
        var server = InMemorySyncServerRef.Create(Clocks.SystemPhysicalClock.Instance);
        var authority = new RefAuthority(server);
        var store = new Storage.InMemoryLocalStore<Note>(NoteJson.Clone);

        // Window: the server speaks both versions.
        await using (var both = await SyncTestHost.StartAsync(authority, supportedSchemas: ["notes-v1", "notes-v2"]))
        {
            var oldApp = new SyncEngine<Note>(store, both.Transport(schemaId: "notes-v1"), new Clocks.HybridLogicalClock("device"), NoteJson.Clone);
            await oldApp.WriteAsync(new Note { Id = "n1", Title = "synced by v1" });
            Assert.True((await oldApp.SyncAsync()).IsComplete);
        }

        // The server drops v1; the old app, not yet upgraded, keeps working offline.
        await using var v2Only = await SyncTestHost.StartAsync(authority, supportedSchemas: ["notes-v2"]);
        var stranded = new SyncEngine<Note>(store, v2Only.Transport(schemaId: "notes-v1"), new Clocks.HybridLogicalClock("device"), NoteJson.Clone);
        await stranded.WriteAsync(new Note { Id = "n2", Title = "written by v1 after the cut-over" });
        var error = await Assert.ThrowsAsync<SyncTransportException>(() => stranded.SyncAsync());
        Assert.Equal(SyncErrorCodes.UpgradeRequired, error.ErrorCode);
        Assert.False(error.IsTransient);
        Assert.Equal(1, await stranded.CountDirtyAsync());

        // The upgraded app opens the same replica.
        var upgraded = new SyncEngine<Note>(store, v2Only.Transport(schemaId: "notes-v2"), new Clocks.HybridLogicalClock("device"), NoteJson.Clone);
        var result = await upgraded.SyncAsync();

        Assert.True(result.IsComplete);
        Assert.Equal("written by v1 after the cut-over", server.Get("n2").Title);
        Assert.Equal(2, server.Server.ReceiptCount);
        Assert.Equal(0, await upgraded.CountDirtyAsync());
    }
}
