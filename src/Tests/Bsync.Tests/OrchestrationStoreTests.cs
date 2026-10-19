using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class OrchestrationStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"bsync-orchestration-{Guid.NewGuid():N}.db");
    private readonly AccountScope _account = new("app", "test", "alice");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero));
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await SqliteStorePool.DeleteDatabaseAsync(_path);
    private async Task<IOrchestrationStore> Open(bool sqlite) => sqlite
        ? await SqliteOrchestrationStore.OpenAsync(_path, _account, timeProvider: _time)
        : new InMemoryOrchestrationStore(_time);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBatchChangesNothing(bool sqlite)
    {
        await using var store = await Open(sqlite);
        Assert.True(await store.TryWriteAsync([new("a", null, "original")]));
        Assert.False(await store.TryWriteAsync([new("b", null, "new"), new("a", null, "overwrite")]));
        Assert.Null(await store.ReadAsync("b"));
        Assert.Equal("original", (await store.ReadAsync("a"))!.Payload);
        var original = (await store.ReadAsync("a"))!;
        Assert.True(await store.TryWriteAsync([new("a", original.Revision, null), new("b", null, "new")]));
        Assert.Null(await store.ReadAsync("a"));
        Assert.True(await store.TryWriteAsync([new("a", null, "recreated")]));
        Assert.False(await store.TryWriteAsync([new("a", original.Revision, "stale")]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FencesRejectExpiredAndSupersededOwners(bool sqlite)
    {
        await using var store = await Open(sqlite);
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        Assert.True(await store.TryWriteAsync([new("lease", null, "first", now + 1000)]));
        var fence = new OrchestrationFence("lease", "first");
        Assert.True(await store.TryWriteAsync([new("progress", null, "step1")], fence));
        var progress = (await store.ReadAsync("progress"))!;
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await store.TryWriteAsync([new("progress", progress.Revision, "stale")], fence));
        var old = (await store.ReadAsync("lease"))!;
        Assert.True(await store.TryWriteAsync([new("lease", old.Revision, "second", now + 3000)]));
        Assert.False(await store.TryWriteAsync([new("progress", progress.Revision, null)], fence));
        Assert.True(await store.TryWriteAsync([new("progress", progress.Revision, "step2")], new("lease", "second")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PagesAreBoundedOrdinalAndCancelBeforeWriting(bool sqlite)
    {
        await using var store = await Open(sqlite);
        Assert.True(await store.TryWriteAsync([new("jobs/a", null, "a"), new("jobs/A", null, "A"), new("jobs/z", null, "z"), new("other/a", null, "other")]));
        var first = await store.ReadPageAsync("jobs/", null, 2);
        Assert.Equal(new[] { "jobs/A", "jobs/a" }, first.Select(r => r.Key));
        Assert.Equal("jobs/z", Assert.Single(await store.ReadPageAsync("jobs/", first[^1].Key, 2)).Key);
        Assert.Empty(await store.ReadPageAsync("jobs/", "zzz", 2));
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryWriteAsync([new("never", null, "value")], cancellationToken: stopped.Token));
        Assert.Null(await store.ReadAsync("never"));
    }

    [Fact]
    public async Task SQLitePersistsAndIsolatesAccountsAcrossInstances()
    {
        await using (var initial = await Open(true)) Assert.True(await initial.TryWriteAsync([new("work", null, "durable")]));
        await using var reopened = await Open(true);
        await using var competing = await Open(true);
        var current = (await reopened.ReadAsync("work"))!;
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            (i % 2 == 0 ? reopened : competing).TryWriteAsync([new("work", current.Revision, i.ToString())]))));
        Assert.Equal(1, outcomes.Count(x => x));
        await using var other = await SqliteOrchestrationStore.OpenAsync(_path, new("app", "test", "bob"));
        Assert.Null(await other.ReadAsync("work"));
        Assert.True(await other.TryWriteAsync([new("work", null, "bob")]));
        await reopened.DeleteAllAsync();
        Assert.Equal("bob", (await other.ReadAsync("work"))!.Payload);
    }

    [Fact]
    public async Task NewerSchemaIsRefusedWithoutChangingIt()
    {
        await using (var original = await Open(true)) Assert.True(await original.TryWriteAsync([new("work", null, "keep")]));
        await using var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE bs_orchestration_schema SET version=3";
        await command.ExecuteNonQueryAsync();
        Assert.Equal("outdated", (await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => Open(true))).Reason);
        command.CommandText = "SELECT payload FROM bs_orchestration_records";
        Assert.Equal("keep", await command.ExecuteScalarAsync());
        command.CommandText = "SELECT version FROM bs_orchestration_schema";
        Assert.Equal(3L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LeaseExpiresAndOldDisposalPreservesSuccessor()
    {
        await using var store = await Open(true);
        var first = await ExecutionLease.TryAcquireAsync(store, "lease", TimeSpan.FromSeconds(3), _time);
        Assert.NotNull(first);
        Assert.Null(await ExecutionLease.TryAcquireAsync(store, "lease", TimeSpan.FromSeconds(3), _time));
        _time.Advance(TimeSpan.FromSeconds(4));
        Assert.True(first.Lost.IsCancellationRequested);
        await using var second = await ExecutionLease.TryAcquireAsync(store, "lease", TimeSpan.FromSeconds(3), _time);
        Assert.NotNull(second);
        await first.DisposeAsync();
        Assert.True(await store.TryWriteAsync([new("progress", null, "successor")], second.Fence));
        Assert.False(await store.TryWriteAsync([new("stale", null, "no")], first.Fence));
    }

    [Fact]
    public void IdentityIncludesEveryBoundaryWithoutDelimiterCollisions()
    {
        Assert.Equal(new AccountScope("a", "bc", "d").StorageKey, new AccountScope("a", "bc", "d").StorageKey);
        Assert.NotEqual(new AccountScope("a", "bc", "d").StorageKey, new AccountScope("ab", "c", "d").StorageKey);
        Assert.NotEqual(_account.StorageKey, new AccountScope("app", "prod", "alice").StorageKey);
        Assert.NotEqual(_account.StorageKey, new AccountScope("app", "test", "alice", "org").StorageKey);
        Assert.Throws<System.Text.EncoderFallbackException>(() => new AccountScope("app", "test", "\ud800"));
    }

    [Fact]
    public async Task SlowAcquisitionCannotReturnAnAlreadyExpiredLease()
    {
        await using var underlying = new InMemoryOrchestrationStore(_time);
        var slow = new DelayedWriteStore(underlying, () => _time.Advance(TimeSpan.FromSeconds(4)));
        Assert.Null(await ExecutionLease.TryAcquireAsync(slow, "lease", TimeSpan.FromSeconds(3), _time));
        await using var successor = await ExecutionLease.TryAcquireAsync(underlying, "lease", TimeSpan.FromSeconds(3), _time);
        Assert.NotNull(successor);
        Assert.False(successor.Lost.IsCancellationRequested);
    }
    private sealed class DelayedWriteStore(IOrchestrationStore inner, Action delay) : IOrchestrationStore
    {
        public Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default) => inner.ReadPageAsync(prefix, afterKey, limit, cancellationToken);
        public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
        { var result = await inner.TryWriteAsync(mutations, fence, cancellationToken); delay(); return result; }
        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => inner.DeleteAllAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
