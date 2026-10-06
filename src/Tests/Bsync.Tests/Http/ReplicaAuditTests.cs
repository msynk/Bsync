using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>Task H: the server can record which content each replica reached.</summary>
public sealed class ReplicaAuditTests
{
    [Fact(DisplayName = "H: a completed pull records the replica's checkpoint once per new checkpoint; replicas that do not identify themselves are not recorded")]
    public async Task AcknowledgementsAreRecorded()
    {
        var server = InMemorySyncServerRef.Create();
        var audit = new InMemorySyncReplicaAudit();
        await using var host = await SyncTestHost.StartAsync(new RefAuthority(server), replicaAudit: audit);
        var writer = new TestReplica(server, "writer", transport: _ => host.Transport());
        var reader = new TestReplica(server, "reader", transport: _ => host.Transport());

        await writer.Engine.WriteAsync(new Note { Id = "n1" });
        await writer.Engine.SyncAsync();
        await reader.Engine.SyncAsync();
        await reader.Engine.SyncAsync(); // nothing new: not recorded again
        var first = (await reader.Store.GetCursorAsync()).Checkpoint.Value;
        await writer.Engine.WriteAsync(new Note { Id = "n2" });
        await writer.Engine.SyncAsync();
        await reader.Engine.SyncAsync();
        var second = (await reader.Store.GetCursorAsync()).Checkpoint.Value;
        await host.Transport().PullAsync(new PullRequest(Checkpoint.Start, 10)); // an older client: no replica id

        var acknowledgements = await audit.GetAsync("reader"); // the clock node id of the reader

        Assert.Equal([first, second], acknowledgements.Select(a => a.Checkpoint));
        Assert.All(acknowledgements, a => Assert.Equal(("notes", "default"), (a.Collection, a.Scope)));
        Assert.NotEqual(first, second);
        Assert.Empty(await audit.GetAsync(string.Empty));
    }
}
