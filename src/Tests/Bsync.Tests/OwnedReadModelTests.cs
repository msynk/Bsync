using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class OwnedReadModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bsync-owned-model-" + Guid.NewGuid().ToString("N"));
    private readonly AccountScope _account = new("tests", "local", "alice");
    private LocalReadModel Model(IOrchestrationStore store) => new(store, "tasks", _account, new FileAccountResourceLock(_directory));
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static async IAsyncEnumerable<ReadModelRow> Rows(int count, bool fail = false)
    {
        for (var i = 0; i < count; i++) yield return new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "payload");
        await Task.CompletedTask;
        if (fail) throw new IOException("Interrupted source snapshot.");
    }

    [Fact]
    public async Task LiveReadersProtectRetiredGenerationAndDisposalPermitsBoundedCollection()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = Model(store);
        Assert.True(await model.RebuildAsync("1", "one", Rows(20), TimeSpan.FromSeconds(10)));
        var old = (await model.OpenActiveAsync())!;
        Assert.True(await Model(store).RebuildAsync("1", "two", Rows(1), TimeSpan.FromSeconds(10)));
        var held = await Model(store).CollectRetiredAsync(rowLimit: 7);
        Assert.Equal(1, held.ProtectedGenerations);
        Assert.Equal(0, held.RemovedRows);
        Assert.Equal(20, (await old.ReadPageAsync(100)).Rows.Count);
        await old.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => old.ReadPageAsync(1));
        var removed = 0;
        for (var i = 0; i < 10; i++)
        {
            var result = await Model(store).CollectRetiredAsync(rowLimit: 7);
            Assert.InRange(result.RemovedRows, 0, 7);
            removed += result.RemovedRows;
        }
        Assert.Equal(20, removed);
        await using var active = (await model.OpenActiveAsync())!;
        Assert.Single((await active.ReadPageAsync(10)).Rows);
    }

    [Fact]
    public async Task RetentionBudgetRefusesStagingUntilProtectedOldGenerationCanBeCollected()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "limited", _account, new FileAccountResourceLock(_directory), retention: new() { MaximumRows = 3 });
        await model.RebuildAsync("1", "first", Rows(2), TimeSpan.FromSeconds(10));
        var old = (await model.OpenActiveAsync())!;
        await model.RebuildAsync("1", "second", Rows(1), TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ReadModelCapacityException>(() => model.RebuildAsync("1", "third", Rows(1), TimeSpan.FromSeconds(10)));
        Assert.Equal("second", (await model.GetStatusAsync()).Active!.SourceVersion);
        Assert.Equal(3, (await model.GetStorageStatusAsync(TimeSpan.FromSeconds(5))).Rows);
        await old.DisposeAsync(); await model.CollectRetiredAsync();
        Assert.True(await model.RebuildAsync("1", "third", Rows(1), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AbortedLatestBuildCanBeCollectedAtGenerationCeilingWithoutStartingAnotherBuild()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "limited-abort", _account, new FileAccountResourceLock(_directory),
            retention: new() { MaximumGenerations = 1, MaximumRows = 128 });
        await Assert.ThrowsAsync<IOException>(() => model.RebuildAsync("1", "broken", Rows(128, true), TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<ReadModelCapacityException>(() => model.RebuildAsync("1", "retry", Rows(1), TimeSpan.FromSeconds(5)));
        Assert.Equal(128, (await model.CollectRetiredAsync(rowLimit: 200)).RemovedRows);
        Assert.Null((await model.GetStatusAsync()).BuildingGeneration);
        Assert.True(await model.RebuildAsync("1", "retry", Rows(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AbortedBuildsAreIndexedAndLegacySnapshotsRemainUntouched()
    {
        await using var store = new InMemoryOrchestrationStore();
        var legacy = new LocalReadModel(store, "tasks");
        await legacy.RebuildAsync("1", "legacy", Rows(3), TimeSpan.FromSeconds(10));
        await using var legacyReader = (await legacy.OpenActiveAsync())!;
        var model = Model(store);
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<IOException>(() => model.RebuildAsync("1", "broken", Rows(130, true), TimeSpan.FromSeconds(10)));
        await model.RebuildAsync("1", "done", Rows(2), TimeSpan.FromSeconds(10));
        var removed = 0;
        for (var i = 0; i < 20; i++) removed += (await model.CollectRetiredAsync(rowLimit: 64)).RemovedRows;
        Assert.Equal(384, removed);
        Assert.Equal(3, (await legacyReader.ReadPageAsync(10)).Rows.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => legacy.CollectRetiredAsync());
    }
}
