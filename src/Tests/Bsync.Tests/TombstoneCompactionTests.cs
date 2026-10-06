using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task D4: a replica drops clean tombstones the server has purged, so their number stays bounded.</summary>
public sealed class TombstoneCompactionTests
{
    [Fact(DisplayName = "D4 F10 I10: over a 30-day soak with a 7-day server retention, local tombstones stay bounded")]
    public async Task TombstonesStayBounded()
    {
        var server = InMemorySyncServerRef.Create();
        var writer = new TestReplica(server, "writer");
        var reader = new TestReplica(server, "reader");
        var dayEnds = new List<long>();
        var counts = new List<int>();
        var compacted = 0;
        for (var day = 0; day < 30; day++)
        {
            // Each day twenty notes are created and the twenty created the day before are deleted.
            for (var i = 0; i < 20; i++)
            {
                await writer.Engine.WriteAsync(new Note { Id = $"d{day:00}-{i:00}" });
                if (day > 0)
                {
                    await writer.Engine.DeleteAsync($"d{day - 1:00}-{i:00}");
                }
            }

            await writer.Engine.SyncAsync();
            dayEnds.Add(server.Server.HighestVersion);
            if (day >= 7)
            {
                server.Server.PurgeTombstones(dayEnds[day - 7]); // retention: seven days
            }

            var result = await reader.Engine.SyncAsync();
            Assert.False(result.ResetPerformed); // a replica that syncs daily never falls behind the horizon
            compacted += result.Compacted;
            counts.Add((await reader.Store.QueryAsync(includeDeleted: true)).Count(n => n.Deleted));
        }

        Assert.True(compacted > 0);
        Assert.True(counts.Max() <= 8 * 20, $"tombstones peaked at {counts.Max()}");
        Assert.True(counts[^1] <= 8 * 20, $"{counts[^1]} tombstones after 30 days");
        Assert.Equal(20, (await reader.Engine.QueryAsync()).Count); // live notes untouched
    }
}
