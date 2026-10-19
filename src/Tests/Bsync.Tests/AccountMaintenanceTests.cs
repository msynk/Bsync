using Bsync.Client;
using Bsync.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests;

public sealed class AccountMaintenanceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bsync-maintenance-").FullName;
    private readonly InMemoryOrchestrationStore _catalog = new();
    private readonly AccountScope _account = new("tests", "local", "maintenance");
    private AccountComposition Composition => new(_catalog, new FileAccountResourceLock(_root));
    private AccountMaintenance Runner => new(Composition, _catalog);
    private static Task<bool> NoUrgent(AccountWorkScope scope, CancellationToken ct) => Task.FromResult(false);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task ExclusiveOwnershipRefusesForegroundAndOtherMaintenanceWithoutExecutingCallbacks()
    {
        var calls = 0;
        AccountMaintenanceStep[] steps = [new("cache", "1", (_, _, _) => { calls++; return Task.FromResult(new MaintenancePageResult(1, 1, null)); })];
        await using (var reader = await Composition.OpenWorkAsync(_account))
            Assert.Equal(MaintenanceOutcome.Busy, (await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent)).Outcome);
        await using (var maintenance = await Composition.OpenMaintenanceAsync(_account))
        {
            Assert.Equal(MaintenanceOutcome.Busy, (await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent)).Outcome);
            await Assert.ThrowsAsync<AccountResourcesInUseException>(() => Composition.OpenWorkAsync(_account));
        }
        Assert.Equal(0, calls);
        Assert.Equal(MaintenanceOutcome.Completed, (await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent)).Outcome);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DurableCursorsResumeAcrossInstancesAndUrgentWorkWinsBetweenPages()
    {
        var seen = new List<string?>(); var urgent = false;
        AccountMaintenanceStep[] steps = [new("cache", "1", (_, page, _) =>
        {
            seen.Add(page.Continuation); urgent = true;
            return Task.FromResult(new MaintenancePageResult(1, 4, page.Continuation is null ? "next" : null));
        })];
        var result = await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), (_, _) => Task.FromResult(urgent));
        Assert.Equal(MaintenanceOutcome.UrgentWork, result.Outcome); Assert.Equal(1, result.Items);
        Assert.Single(await _catalog.ReadPageAsync("maintenance/", null, 10));
        urgent = false;
        result = await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), (_, _) => Task.FromResult(urgent));
        Assert.Equal(MaintenanceOutcome.Completed, result.Outcome);
        Assert.Equal(new string?[] { null, "next" }, seen);
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner.MaintainAsync(_account, "plan",
            [steps[0] with { Version = "2" }], TimeSpan.FromSeconds(5), NoUrgent));
    }

    [Fact]
    public async Task DryRunDoesNotAdvancePersistentCursorOrWriteCatalog()
    {
        var seen = new List<bool>();
        AccountMaintenanceStep[] steps = [new("cache", "1", (_, request, _) =>
        { seen.Add(request.DryRun); return Task.FromResult(new MaintenancePageResult(1, 2, "next")); })];
        var result = await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent, maximumItems: 1, dryRun: true);
        Assert.Equal(MaintenanceOutcome.RemainingWork, result.Outcome);
        Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
        await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent, maximumItems: 1);
        var before = Assert.Single(await _catalog.ReadPageAsync("maintenance/", null, 10));
        await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent, maximumItems: 1, dryRun: true);
        Assert.Equal(before, Assert.Single(await _catalog.ReadPageAsync("maintenance/", null, 10)));
        Assert.Equal(new[] { true, false, true }, seen);
    }

    [Fact]
    public async Task InterruptedPageReplaysWithoutLosingWorkAndBoundsCarryAcrossSteps()
    {
        var applied = new HashSet<string>(); var fail = true;
        AccountMaintenanceStep[] steps = [new("first", "1", (_, request, _) =>
        {
            applied.Add("effect");
            if (fail) { fail = false; throw new IOException("Lost acknowledgement after idempotent mutation."); }
            return Task.FromResult(new MaintenancePageResult(2, 7, null));
        }), new("second", "1", (_, request, _) =>
        {
            Assert.Equal(1, request.MaximumItems); Assert.Equal(3, request.MaximumBytes);
            return Task.FromResult(new MaintenancePageResult(0, 0, request.Continuation, true));
        })];
        await Assert.ThrowsAsync<IOException>(() => Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent, 3, 10));
        Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
        var result = await Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(5), NoUrgent, 3, 10);
        Assert.Equal(MaintenanceOutcome.RequiresLargerBudget, result.Outcome); Assert.Equal(2, result.Items); Assert.Equal(7, result.Bytes);
        Assert.Single(applied);
    }

    [Fact]
    public async Task DeadlineIncludesNoncooperativeWorkAndJoinedResourceDisposal()
    {
        var time = new FakeTimeProvider(); var closing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var composition = Composition.Register(new("resource", (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask),
            (_, _) => Task.FromResult<IAsyncDisposable>(new Resource(async () => { closing.SetResult(); await release.Task; })));
        var runner = new AccountMaintenance(composition, _catalog, time);
        var run = runner.MaintainAsync(_account, "plan", [new("step", "1", (_, _, _) =>
        { time.Advance(TimeSpan.FromSeconds(2)); return Task.FromResult(new MaintenancePageResult(1, 1, null)); })], TimeSpan.FromSeconds(1), NoUrgent);
        await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(run.IsCompleted);
        Assert.Null(await new FileAccountResourceLock(_root).TryAcquireAsync(_account, false));
        release.SetResult();
        Assert.Equal(MaintenanceOutcome.BudgetExpired, (await run).Outcome);
        Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
        await using var access = await new FileAccountResourceLock(_root).TryAcquireAsync(_account, true);
        Assert.NotNull(access);
    }

    [Fact]
    public async Task CallerCancellationAndDeletionNeverAcknowledgeOrProvisionWork()
    {
        var calls = 0;
        AccountMaintenanceStep[] steps = [new("step", "1", (_, _, _) => { calls++; return Task.FromResult(new MaintenancePageResult(0, 0, null)); })];
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(1), NoUrgent, cancellationToken: cancellation.Token));
        await _catalog.TryWriteAsync([new("deleting/" + _account.StorageKey, null, "tombstone")]);
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => Runner.MaintainAsync(_account, "plan", steps, TimeSpan.FromSeconds(1), NoUrgent));
        Assert.Equal(0, calls); Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
    }

    [Fact]
    public async Task CallbackCannotReleaseAccountOwnershipAndThenAcknowledgeProgress()
    {
        await Assert.ThrowsAsync<ObjectDisposedException>(() => Runner.MaintainAsync(_account, "plan",
            [new("step", "1", async (scope, _, _) => { await scope.DisposeAsync(); return new(1, 1, null); })],
            TimeSpan.FromSeconds(5), NoUrgent));
        Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
    }

    [Fact]
    public async Task NonAdvancingOrOverBudgetCallbacksCannotPersistProgress()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner.MaintainAsync(_account, "plan",
            [new("step", "1", (_, _, _) => Task.FromResult(new MaintenancePageResult(2, 1, null)))], TimeSpan.FromSeconds(5), NoUrgent, maximumItems: 1));
        Assert.Empty(await _catalog.ReadPageAsync("maintenance/", null, 10));
        var calls = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => Runner.MaintainAsync(_account, "plan",
            [new("step", "1", (_, _, _) => { calls++; return Task.FromResult(new MaintenancePageResult(0, 0, "stuck")); })], TimeSpan.FromSeconds(5), NoUrgent));
        Assert.Equal(2, calls);
    }

    private sealed class Resource(Func<Task> dispose) : IAsyncDisposable
    { public async ValueTask DisposeAsync() => await dispose(); }
}
