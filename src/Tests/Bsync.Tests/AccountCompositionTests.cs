using Bsync.Client;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class AccountCompositionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bsync-composition-" + Guid.NewGuid().ToString("N"));
    private static readonly AccountScope Account = new("tests", "local", "alice");
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    private static AccountResource Resource(string name, List<string> deleted) => new(name, (_, _) => Task.CompletedTask,
        (_, _) => { deleted.Add(name); return Task.CompletedTask; }) { InspectPendingAsync = (_, _) => Task.FromResult(new AccountResourcePendingWork(0)) };
    private sealed class Lifetime(Func<ValueTask> dispose) : IAsyncDisposable { public ValueTask DisposeAsync() => dispose(); }

    [Fact]
    public async Task SharedHeadlessScopeBlocksDeletionAndClosesResourcesBeforeReleasingOwnership()
    {
        await using var catalog = new InMemoryOrchestrationStore();
        var locks = new FileAccountResourceLock(_directory);
        var deleted = new List<string>(); var closed = new List<string>();
        var composition = new AccountComposition(catalog, locks)
            .Register(Resource("keys", deleted), (_, _) => Task.FromResult<IAsyncDisposable>(new Lifetime(() => { closed.Add("keys"); return ValueTask.CompletedTask; })), true)
            .Register(Resource("cache", deleted), (_, _) => Task.FromResult<IAsyncDisposable>(new Lifetime(() => { closed.Add("cache"); return ValueTask.CompletedTask; })));
        await using var coordinator = composition.CreateCoordinator();
        Assert.Equal(AccountOpenState.Opened, (await coordinator.OpenAccountAsync(Account)).State);
        var worker = await composition.OpenWorkAsync(Account);
        await Assert.ThrowsAsync<AccountResourcesInUseException>(() => coordinator.DeleteAccountAsync(Account));
        Assert.Empty(deleted);
        await worker.DisposeAsync();
        Assert.Equal(new[] { "cache", "keys" }, closed);
        await coordinator.OpenAccountAsync(Account);
        Assert.True(await coordinator.DeleteAccountAsync(Account));
        Assert.Equal(new[] { "cache", "keys" }, deleted);
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => composition.OpenWorkAsync(Account));
    }

    [Fact]
    public async Task OpeningAndNoncooperativeWorkConsumeSameBudgetAndAreJoined()
    {
        await using var catalog = new InMemoryOrchestrationStore();
        var time = new FakeTimeProvider(); var closed = false; var invoked = false;
        var composition = new AccountComposition(catalog, new FileAccountResourceLock(_directory))
            .Register(Resource("database", []), (_, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(3));
                return Task.FromResult<IAsyncDisposable>(new Lifetime(() => { closed = true; return ValueTask.CompletedTask; }));
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => composition.RunAsync(Account, TimeSpan.FromSeconds(5),
            (_, _) => { invoked = true; time.Advance(TimeSpan.FromSeconds(3)); return Task.FromResult(42); }, time));
        Assert.True(invoked); Assert.True(closed);
        await using var exclusive = await new FileAccountResourceLock(_directory).TryAcquireAsync(Account, true);
        Assert.NotNull(exclusive);
    }

    [Fact]
    public async Task FailedCloseRetainsOwnershipAndUnknownPendingBlocksDefaultDeletion()
    {
        await using var catalog = new InMemoryOrchestrationStore();
        var locks = new FileAccountResourceLock(_directory); var fail = true;
        var composition = new AccountComposition(catalog, locks).Register(new("unknown", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            (_, _) => Task.FromResult<IAsyncDisposable>(new Lifetime(() => fail ? ValueTask.FromException(new IOException("Close failed.")) : ValueTask.CompletedTask)));
        var scope = await composition.OpenWorkAsync(Account);
        await Assert.ThrowsAsync<IOException>(() => scope.DisposeAsync().AsTask());
        Assert.Null(await locks.TryAcquireAsync(Account, true));
        fail = false; await scope.DisposeAsync();
        await using var coordinator = composition.CreateCoordinator();
        Assert.Equal(1, (await coordinator.OpenAccountAsync(Account)).PendingWork!.UnknownResources);
        Assert.False(await coordinator.DeleteAccountAsync(Account));
    }
}
