using System.Runtime.CompilerServices;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class LocalReadModelTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private static async IAsyncEnumerable<ReadModelRow> Rows(int count, string payload = "v1", [EnumeratorCancellation] CancellationToken token = default)
    {
        for (var i = 0; i < count; i++)
        { token.ThrowIfCancellationRequested(); yield return new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), payload); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FailedBuildRetainsActiveAndRestartReconcilesRemovalsAndSchema()
    {
        var path = Path.Combine(Path.GetTempPath(), "bsync-read-model-" + Guid.NewGuid().ToString("N") + ".db");
        var account = new AccountScope("test", "local", "alice");
        try
        {
            await using (var store = await SqliteOrchestrationStore.OpenAsync(path, account))
            {
                var model = new LocalReadModel(store, "tasks");
                Assert.True(await model.RebuildAsync("1", "epoch1/snapshot1", Rows(3), Budget));
                var original = (await model.OpenActiveAsync())!;
                async IAsyncEnumerable<ReadModelRow> Interrupted()
                {
                    await foreach (var row in Rows(130, "v2")) yield return row;
                    throw new IOException("Process stopped before snapshot completion.");
                }
                await Assert.ThrowsAsync<IOException>(() => model.RebuildAsync("2", "epoch1/snapshot2", Interrupted(), Budget));
                var status = await model.GetStatusAsync();
                Assert.NotNull(status.BuildingGeneration);
                Assert.Equal(128, status.AppliedRows);
                Assert.Equal(original.Generation, status.Active);
                Assert.Equal(3, (await original.ReadPageAsync(10)).Rows.Count);
            }
            await using (var store = await SqliteOrchestrationStore.OpenAsync(path, account))
            {
                var model = new LocalReadModel(store, "tasks");
                var old = (await model.OpenActiveAsync())!;
                Assert.Equal(128, (await model.GetStatusAsync()).AppliedRows);
                // A source reset and schema migration are full reconciliations; missing IDs do not linger.
                Assert.True(await model.RebuildAsync("2", "epoch2/snapshot1", Rows(1, "reset"), Budget));
                var current = (await model.OpenActiveAsync())!;
                Assert.Equal("2", current.Generation.SchemaVersion);
                Assert.Equal("epoch2/snapshot1", current.Generation.SourceVersion);
                Assert.Equal("reset", Assert.Single((await current.ReadPageAsync(10)).Rows).Payload);
                Assert.Equal(3, (await old.ReadPageAsync(10)).Rows.Count);
                Assert.Null((await model.GetStatusAsync()).BuildingGeneration);
                // A complete empty snapshot reconciles every deletion.
                Assert.True(await model.RebuildAsync("2", "epoch2/snapshot2", Rows(0), Budget));
                Assert.Empty((await (await model.OpenActiveAsync())!.ReadPageAsync(10)).Rows);
            }
        }
        finally { SqliteConnection.ClearAllPools(); File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    [Fact]
    public async Task CompetingBuildIsExcludedAndCanceledBuildDoesNotActivate()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "tasks");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async IAsyncEnumerable<ReadModelRow> Blocked([EnumeratorCancellation] CancellationToken token = default)
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            yield return new("never", "visible");
        }
        using var stop = new CancellationTokenSource();
        var building = model.RebuildAsync("1", "captured", Blocked(), Budget, stop.Token);
        await entered.Task;
        Assert.False(await new LocalReadModel(store, "tasks").RebuildAsync("1", "other", Rows(1), Budget));
        Assert.Null(await model.OpenActiveAsync());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => building);
        Assert.Null(await model.OpenActiveAsync());
        Assert.True(await model.RebuildAsync("1", "next", Rows(1), Budget));
    }

    [Fact]
    public async Task SnapshotPagesRemainStableAcrossActivationAndRejectForeignContinuation()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "tasks");
        Assert.True(await model.RebuildAsync("1", "snapshot1", Rows(257), Budget));
        var old = (await model.OpenActiveAsync())!;
        var first = await old.ReadPageAsync(100);
        Assert.True(await model.RebuildAsync("1", "snapshot2", Rows(1, "new"), Budget));
        var current = (await model.OpenActiveAsync())!;
        await Assert.ThrowsAsync<ArgumentException>(() => current.ReadPageAsync(100, first.Continuation));
        var second = await old.ReadPageAsync(100, first.Continuation);
        var third = await old.ReadPageAsync(100, second.Continuation);
        var all = first.Rows.Concat(second.Rows).Concat(third.Rows).ToArray();
        Assert.Equal(257, all.Select(r => r.Id).Distinct().Count());
        Assert.All(all, r => Assert.Equal("v1", r.Payload));
        Assert.Null(third.Continuation);
    }

    [Fact]
    public async Task DuplicateSourceIdentityAcrossBatchesCannotActivate()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "tasks");
        async IAsyncEnumerable<ReadModelRow> Duplicates()
        {
            await foreach (var row in Rows(128)) yield return row;
            yield return new("0", "changed");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => model.RebuildAsync("1", "invalid", Duplicates(), Budget));
        Assert.Null(await model.OpenActiveAsync());
        Assert.Equal(128, (await model.GetStatusAsync()).AppliedRows);
    }

    [Fact]
    public async Task NewerMetadataRefusedWithoutMutationAndModelsAreIsolated()
    {
        await using var store = new InMemoryOrchestrationStore();
        var model = new LocalReadModel(store, "tasks");
        Assert.True(await model.RebuildAsync("1", "snapshot", Rows(1), Budget));
        Assert.Null(await new LocalReadModel(store, "other").OpenActiveAsync());
        var state = Assert.Single(await store.ReadPageAsync("read-model/", null, 10), r => r.Key.EndsWith("/state", StringComparison.Ordinal));
        Assert.True(await store.TryWriteAsync([new(state.Key, state.Revision, "{\"FormatVersion\":99}")]));
        var unsupported = await store.ReadAsync(state.Key);
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => model.RebuildAsync("1", "new", Rows(1), Budget));
        Assert.Equal(unsupported, await store.ReadAsync(state.Key));
    }
}
