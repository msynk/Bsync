using System.Runtime.CompilerServices;
using Bsync.Client;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Xunit;

namespace Bsync.Tests;

public sealed class AccountLifecycleTests
{
    private readonly Dictionary<string, InMemoryLocalStore<Note>> _stores = [];
    private readonly AccountScope _alice = new("app", "test", "alice");
    private readonly AccountScope _bob = new("app", "test", "bob");

    private SyncSession<Note> Session(SyncCoordinator coordinator, ISyncTransport<Note> transport,
        Func<string, CancellationToken, Task<LocalReplica<Note>>>? open = null) => new(new()
    {
        Coordination = new(coordinator, "notes"),
        Cloner = NoteJson.Clone,
        OpenReplica = open ?? ((account, _) =>
        {
            if (!_stores.TryGetValue(account, out var store)) _stores[account] = store = new(NoteJson.Clone);
            return Task.FromResult(new LocalReplica<Note>(store, Guid.NewGuid().ToString("N")));
        }),
        CreateTransport = _ => transport,
        DeleteReplica = (account, _) => { _stores.Remove(account); return Task.CompletedTask; },
        LiveHints = true,
        Interval = TimeSpan.FromHours(1),
    });

    [Fact]
    public async Task DefaultDeletionRetainsNonDocumentWorkAndUnknownResources()
    {
        await using var catalog = new InMemoryOrchestrationStore();
        var deleted = false;
        var known = false;
        long pending = 2;
        await using var coordinator = new SyncCoordinator(new()
        {
            AccountCatalog = catalog,
            AccountResources = [new("workflows", (_, _) => Task.CompletedTask, (_, _) => { deleted = true; return Task.CompletedTask; })
            { InspectPendingAsync = (_, _) => Task.FromResult(new AccountResourcePendingWork(pending, known)) }],
        });
        await using var session = Session(coordinator, new ControlledTransport());
        var opened = await coordinator.OpenAccountAsync(_alice);
        Assert.Equal(0, opened.PendingWork!.Pending);
        Assert.Equal(2, opened.PendingWork.ResourcePending);
        Assert.Equal(1, opened.PendingWork.UnknownResources);
        Assert.False(await coordinator.DeleteAccountAsync(_alice));
        pending = 0;
        Assert.False(await coordinator.DeleteAccountAsync(_alice));
        Assert.False(deleted);
        known = true;
        Assert.True(await coordinator.DeleteAccountAsync(_alice));
        Assert.True(deleted);
    }

    [Fact]
    public async Task SharedAccountLifetimesPreventDeletionAndExclusiveDeletionPreventsOpen()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-account-lock-").FullName;
        try
        {
            var locks = new FileAccountResourceLock(directory);
            await using var catalog = new InMemoryOrchestrationStore();
            var deleting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var options = new SyncCoordinatorOptions
            {
                AccountCatalog = catalog, AccountResourceLock = locks,
                AccountResources = [new("hold-deletion", (_, _) => Task.CompletedTask, async (_, _) =>
                { deleting.TrySetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(5)); })],
            };
            await using var first = new SyncCoordinator(options);
            await using var second = new SyncCoordinator(options);
            await using var one = Session(first, new ControlledTransport());
            await using var two = Session(second, new ControlledTransport());
            Assert.Equal(AccountOpenState.Opened, (await first.OpenAccountAsync(_alice)).State);
            Assert.Equal(AccountOpenState.Opened, (await second.OpenAccountAsync(_alice)).State);
            await (await two.GetEngineAsync(_alice.StorageKey)).WriteAsync(new Note { Id = "pending" });
            await Assert.ThrowsAsync<AccountResourcesInUseException>(() => first.DeleteAccountAsync(_alice, AccountDeletionPolicy.DiscardPending));
            Assert.Null(first.Account);
            Assert.Equal(1, (await second.GetPendingWorkAsync()).Pending);
            Assert.Null(await catalog.ReadAsync("deleting/" + _alice.StorageKey));
            await second.CloseAccountAsync();
            await first.OpenAccountAsync(_alice);
            Assert.False(await first.DeleteAccountAsync(_alice));
            Assert.Null(first.Account);
            await first.OpenAccountAsync(_alice);
            var removal = first.DeleteAccountAsync(_alice, AccountDeletionPolicy.DiscardPending);
            try
            {
                await deleting.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(AccountOpenState.InUse, (await second.OpenAccountAsync(_alice)).State);
            }
            finally { finish.TrySetResult(); }
            Assert.True(await removal);
            Assert.True(await first.DeleteAccountAsync(_alice));
            Assert.Equal(AccountOpenState.Opened, (await second.OpenAccountAsync(_alice)).State);
            Assert.Equal(0, (await second.GetPendingWorkAsync()).Pending);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task DeletionRechecksWritesCommittedDuringSharedToExclusiveTransition()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-account-gap-").FullName;
        try
        {
            var locks = new FileAccountResourceLock(directory);
            await using var catalog = new InMemoryOrchestrationStore();
            await using var other = new SyncCoordinator(new() { AccountResourceLock = locks, AccountCatalog = catalog });
            await using var otherSession = Session(other, new ControlledTransport());
            var injected = false;
            var hook = new HookLock(locks, async () =>
            {
                if (injected) return;
                injected = true;
                await other.OpenAccountAsync(_alice);
                await (await otherSession.GetEngineAsync(_alice.StorageKey)).WriteAsync(new Note { Id = "gap-write" });
                await other.CloseAccountAsync();
            });
            await using var coordinator = new SyncCoordinator(new() { AccountResourceLock = hook, AccountCatalog = catalog });
            await using var session = Session(coordinator, new ControlledTransport());
            await coordinator.OpenAccountAsync(_alice);
            Assert.Equal(0, (await coordinator.GetPendingWorkAsync()).Pending);
            Assert.False(await coordinator.DeleteAccountAsync(_alice));
            Assert.Null(await catalog.ReadAsync("deleting/" + _alice.StorageKey));
            Assert.Equal(1, (await coordinator.OpenAccountAsync(_alice)).PendingWork!.Pending);
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class HookLock(IAccountResourceLock inner, Func<Task> beforeExclusive) : IAccountResourceLock
    {
        public async ValueTask<IAsyncDisposable?> TryAcquireAsync(AccountScope account, bool exclusive, CancellationToken cancellationToken = default)
        {
            if (exclusive) await beforeExclusive();
            return await inner.TryAcquireAsync(account, exclusive, cancellationToken);
        }
    }

    [Fact]
    public async Task ColdOpenHasNoNetworkAndRetainsPendingWorkAcrossSwitches()
    {
        var transport = new ControlledTransport();
        await using var coordinator = new SyncCoordinator();
        await using var session = Session(coordinator, transport);
        Assert.Equal(AccountOpenState.Opened, (await coordinator.OpenAccountAsync(_alice)).State);
        var old = await session.GetEngineAsync(_alice.StorageKey);
        await old.WriteAsync(new Note { Id = "pending" });
        Assert.Equal(1, (await coordinator.GetPendingWorkAsync()).Pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => old.SyncAsync());
        Assert.Equal(0, transport.Calls);
        await coordinator.OpenAccountAsync(_bob);
        Assert.Equal(0, (await coordinator.GetPendingWorkAsync()).Pending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old.SyncAsync());
        var reopened = await coordinator.OpenAccountAsync(_alice);
        Assert.Equal(1, reopened.PendingWork!.Pending);
        Assert.Equal(0, transport.Calls);
        await coordinator.CloseAccountAsync();
        await coordinator.CloseAccountAsync();
    }

    [Fact]
    public async Task SuspendCancelsAndJoinsBeforeCredentialsMayChange()
    {
        var transport = new ControlledTransport();
        await using var coordinator = new SyncCoordinator();
        await using var session = Session(coordinator, transport);
        await coordinator.OpenAccountAsync(_alice, enableReplication: true);
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.SuspendAccountAsync();
        Assert.True(transport.Canceled.Task.IsCompletedSuccessfully);
        var calls = transport.Calls;
        await (await session.GetEngineAsync(_alice.StorageKey)).WriteAsync(new Note { Id = "offline" });
        session.RequestSync();
        Assert.Equal(calls, transport.Calls);
        Assert.Equal(1, (await coordinator.GetPendingWorkAsync()).Pending);
    }

    [Fact]
    public async Task InterruptedDeletionResumesAndDoesNotDeleteOtherAccounts()
    {
        await using var catalog = new InMemoryOrchestrationStore();
        var fails = true;
        var removed = new List<string>();
        var options = new SyncCoordinatorOptions
        {
            AccountCatalog = catalog,
            AccountResources = [new("blobs", (_, _) => Task.CompletedTask, (account, _) =>
            {
                if (fails) throw new IOException("simulated process interruption");
                removed.Add(account.StorageKey);
                return Task.CompletedTask;
            })],
        };
        await using (var first = new SyncCoordinator(options))
        await using (var session = Session(first, new ControlledTransport()))
        {
            await first.OpenAccountAsync(_bob);
            await (await session.GetEngineAsync(_bob.StorageKey)).WriteAsync(new Note { Id = "bob" });
            await first.OpenAccountAsync(_alice);
            await (await session.GetEngineAsync(_alice.StorageKey)).WriteAsync(new Note { Id = "alice" });
            Assert.False(await first.DeleteAccountAsync(_alice));
            Assert.True(_stores.ContainsKey(_alice.StorageKey));
            await Assert.ThrowsAsync<IOException>(() => first.DeleteAccountAsync(_alice, AccountDeletionPolicy.DiscardPending));
        }
        fails = false;
        await using var second = new SyncCoordinator(options);
        await using var reopened = Session(second, new ControlledTransport());
        Assert.Equal(AccountOpenState.Stopped, (await second.OpenAccountAsync(_alice)).State);
        Assert.True(await second.DeleteAccountAsync(_alice));
        Assert.Single(removed);
        Assert.False(_stores.ContainsKey(_alice.StorageKey));
        Assert.Equal("bob", Assert.Single(await _stores[_bob.StorageKey].QueryAsync()).Id);
        Assert.Equal(AccountOpenState.Opened, (await second.OpenAccountAsync(_alice)).State);
        Assert.Equal(0, (await second.GetPendingWorkAsync()).Pending);
    }

    [Fact]
    public async Task LockedKeyReturnsUnknownPendingCountWithoutDeletingAnything()
    {
        await using var coordinator = new SyncCoordinator();
        await using var session = Session(coordinator, new ControlledTransport(), (_, _) => throw new ReplicaKeyUnavailableException(locked: true));
        var result = await coordinator.OpenAccountAsync(_alice);
        Assert.Equal(AccountOpenState.Locked, result.State);
        Assert.Null(result.PendingWork);
        Assert.Null(coordinator.Account);
    }

    [Fact]
    public async Task MissingCredentialsPreserveLocalWritesAndOfflineOpenNeverRenews()
    {
        var available = false; var checks = 0;
        var transport = new ControlledTransport();
        await using var coordinator = new SyncCoordinator(new()
        {
            CheckNetworkCredentialsAsync = (account, _) =>
            { Assert.Equal(_alice, account); checks++; return Task.FromResult(available); },
        });
        await using var session = Session(coordinator, transport);
        Assert.Equal(AccountOpenState.Opened, (await coordinator.OpenAccountAsync(_alice)).State);
        Assert.Equal(0, checks);
        await (await session.GetEngineAsync(_alice.StorageKey)).WriteAsync(new Note { Id = "local" });
        var result = await coordinator.OpenAccountAsync(_alice, enableReplication: true);
        Assert.Equal(AccountOpenState.CredentialsRequired, result.State);
        Assert.Equal(1, result.PendingWork!.Pending);
        Assert.Equal(_alice, coordinator.Account);
        Assert.Equal(0, transport.Calls);
        await Assert.ThrowsAsync<AccountCredentialsUnavailableException>(() => coordinator.ResumeAccountAsync());
        Assert.Equal(1, (await coordinator.GetPendingWorkAsync()).Pending);
        available = true;
        await coordinator.ResumeAccountAsync();
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.SuspendAccountAsync();
    }

    [Fact]
    public async Task DeferredMigrationDoesNotOpenReplacementStorage()
    {
        await using var coordinator = new SyncCoordinator();
        await using var session = Session(coordinator, new ControlledTransport(), (_, _) => throw new ReplicaMigrationPendingException());
        var result = await coordinator.OpenAccountAsync(_alice);
        Assert.Equal(AccountOpenState.Migrating, result.State);
        Assert.Null(result.PendingWork);
        Assert.Null(coordinator.Account);
        Assert.Empty(_stores);
    }

    private sealed class ControlledTransport : ISyncTransport<Note>
    {
        public int Calls;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Canceled.TrySetResult(); throw; }
            throw new InvalidOperationException();
        }
        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public async IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }
}
