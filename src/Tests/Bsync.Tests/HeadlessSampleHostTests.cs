using Bsync.Client;
using Bsync.Samples.NativeBackground;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class HeadlessSampleHostTests
{
    [Fact]
    public async Task BootstrapWithoutUiDiscoversWorkAndDeletedRegistrationCannotBeResurrected()
    {
        var root = Directory.CreateTempSubdirectory("bsync-headless-").FullName;
        var previous = NativeBackgroundRuntime.Current;
        try
        {
            var host = new HeadlessSampleHost(root); host.Initialize();
            Assert.Equal(BackgroundWorkOutcome.DeferredByHost, await host.RequestDemoAsync(new Deferred()));
            new HeadlessSampleHost(root).Initialize();
            Assert.True(await NativeBackgroundRuntime.Current!.ReconcileAsync(TimeSpan.FromSeconds(5), default));
            await using var catalog = await SqliteOrchestrationStore.OpenAsync(Path.Combine(root, "catalog.db"), new("Bsync.HeadlessSample", "local", "catalog"));
            Assert.Empty(await catalog.ReadPageAsync("demo-work/", null, 100));
            Assert.Single(await catalog.ReadPageAsync("demo-result/", null, 100));
            var key = "registered/" + HeadlessSampleHost.DemoAccount.StorageKey;
            var registration = (await catalog.ReadAsync(key))!;
            await catalog.TryWriteAsync([new(key, registration.Revision, null)]);
            Assert.Equal(BackgroundWorkOutcome.DeferredByHost, await NativeBackgroundRuntime.Current.RunAsync(HeadlessSampleHost.DemoAccount.StorageKey, TimeSpan.FromSeconds(5), default));
            Assert.Null(await catalog.ReadAsync(key));
        }
        finally { NativeBackgroundRuntime.Current = previous; SqliteStorePool.Release(Path.Combine(root, "catalog.db")); Directory.Delete(root, true); }
    }
    private sealed class Deferred : IBackgroundWorkScheduler
    {
        public BackgroundCapabilities Capabilities { get; } = new(true, true, true);
        public Task<BackgroundWorkOutcome> ScheduleAsync(BackgroundWorkRequest request, CancellationToken cancellationToken = default) => Task.FromResult(BackgroundWorkOutcome.DeferredByHost);
    }
}
