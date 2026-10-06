using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

/// <summary>Task D5: a hosted retention service purges tombstones and receipts by age, never sooner than the offline horizon.</summary>
public sealed class RetentionServiceTests
{
    [Fact(DisplayName = "D5 F18: a receipt horizon shorter than the offline horizon fails at startup with a clear message")]
    public async Task ShortReceiptHorizonFailsAtStartup()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSyncRetention(options => options.ReceiptHorizon = TimeSpan.FromDays(7));
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("ReceiptHorizon (7 days) is shorter than MaxOfflineHorizon (45 days)", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new SyncRetention(new SyncRetentionOptions { ReceiptHorizon = TimeSpan.FromDays(7) }));
    }

    [Fact(DisplayName = "D5 F18 I10: nothing younger than the horizon is purged; older tombstones and receipts are, and a replica offline longer resets")]
    public async Task PurgesByAge()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var server = InMemorySyncServerRef.Create();
        var writer = new TestReplica(server, "writer");
        var offline = new TestReplica(server, "offline");
        await writer.Engine.WriteAsync(new Note { Id = "kept" });
        await writer.Engine.SyncAsync();
        await offline.Engine.SyncAsync(); // its checkpoint is now above zero
        var target = (ISyncRetentionTarget)server.Server;
        var retention = new SyncRetention(new SyncRetentionOptions { MaxOfflineHorizon = TimeSpan.FromDays(45), TimeProvider = time }, [target]);

        // Day 0: ten notes are created and deleted.
        for (var i = 0; i < 10; i++)
        {
            await writer.Engine.WriteAsync(new Note { Id = $"old-{i}" });
        }

        await writer.Engine.SyncAsync();
        for (var i = 0; i < 10; i++)
        {
            await writer.Engine.DeleteAsync($"old-{i}");
        }

        await writer.Engine.SyncAsync();
        await retention.RunOnceAsync();

        // Daily runs for 44 days purge nothing.
        var early = 0;
        for (var day = 1; day <= 44; day++)
        {
            time.Advance(TimeSpan.FromDays(1));
            if (day == 20)
            {
                await writer.Engine.WriteAsync(new Note { Id = "young" });
                await writer.Engine.SyncAsync();
            }

            var (tombstones, receipts) = await retention.RunOnceAsync();
            early += tombstones + receipts;
        }

        Assert.Equal(0, early);
        Assert.Equal(10, server.Server.Snapshot(includeDeleted: true).Count(n => n.Deleted));

        // Day 46: day 0 is older than the horizon.
        time.Advance(TimeSpan.FromDays(2));
        var (purgedTombstones, purgedReceipts) = await retention.RunOnceAsync();

        Assert.Equal(10, purgedTombstones);
        Assert.Equal(21, purgedReceipts); // "kept", ten creates and ten deletes
        Assert.Equal(0, server.Server.Snapshot(includeDeleted: true).Count(n => n.Deleted));
        Assert.NotNull(server.Server.GetVersion("young")); // written on day 20, kept
        Assert.True(server.Server.ReceiptCount >= 1);
        Assert.True((await offline.Engine.SyncAsync()).ResetPerformed); // offline 46 days: past the horizon
        Assert.Equal(["kept", "young"], (await offline.Engine.QueryAsync()).Select(n => n.Id).Order());
    }

    [Fact(DisplayName = "D5: the hosted service runs on its interval against registered targets")]
    public async Task HostedServiceRunsOnInterval()
    {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var server = InMemorySyncServerRef.Create();
        var writer = new TestReplica(server, "writer");
        await writer.Engine.WriteAsync(new Note { Id = "gone" });
        await writer.Engine.SyncAsync();
        await writer.Engine.DeleteAsync("gone");
        await writer.Engine.SyncAsync();

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<ISyncRetentionTarget>(server.Server);
        builder.Services.AddSyncRetention(options =>
        {
            options.MaxOfflineHorizon = TimeSpan.FromDays(1);
            options.Interval = TimeSpan.FromHours(1);
            options.TimeProvider = time;
        });
        using var host = builder.Build();
        await host.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (server.Server.Snapshot(includeDeleted: true).Any(n => n.Deleted))
            {
                Assert.True(DateTime.UtcNow < deadline, "the tombstone was not purged");
                time.Advance(TimeSpan.FromHours(1));
                await Task.Delay(10);
            }

            Assert.True(time.GetUtcNow() - start >= TimeSpan.FromDays(1)); // not before the horizon
        }
        finally
        {
            await host.StopAsync();
        }
    }
}
