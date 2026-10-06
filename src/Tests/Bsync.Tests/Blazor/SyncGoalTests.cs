using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Xunit;

namespace Bsync.Tests.Blazor;

/// <summary>Task C4: awaitable sync goals on top of the background loop.</summary>
public sealed class SyncGoalTests
{
    /// <summary>A transport whose pushes can be held at a gate and that records concurrency.</summary>
    private sealed class GatedTransport(ISyncTransport<Note> inner) : ISyncTransport<Note>
    {
        private int _active;

        public TaskCompletionSource? PushGate { get; set; }

        public Exception? Failure { get; set; }

        public int Pushes;

        public int MaxConcurrent;

        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            Failure is { } failure ? throw failure : inner.PullAsync(request, cancellationToken);

        public async Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _active);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                Interlocked.Increment(ref Pushes);
                if (Failure is { } failure)
                {
                    throw failure;
                }

                var result = await inner.PushAsync(request, cancellationToken);
                if (PushGate is { } gate)
                {
                    await gate.Task.WaitAsync(cancellationToken); // the server committed; the response is delayed
                }

                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        public Setup()
        {
            Server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
            Session = new SyncSession<Note>(new SyncSessionOptions<Note>
            {
                Cloner = NoteJson.Clone,
                OpenReplica = (account, _) => Task.FromResult(new LocalReplica<Note>(Stores.GetOrAdd(account, _ => new InMemoryLocalStore<Note>(NoteJson.Clone)), $"node-{account}")),
                CreateTransport = account => Transports.GetOrAdd(account, _ => new GatedTransport(new ServerRefTransport(Server))),
                Interval = TimeSpan.FromMinutes(10),
                MinBackoff = TimeSpan.FromMilliseconds(50),
                MaxBackoff = TimeSpan.FromMilliseconds(200),
            });
            Notes = new LocalSyncCollection<Note>(Session, _ => Task.FromResult(Account));
        }

        public InMemorySyncServerRef Server { get; }

        public System.Collections.Concurrent.ConcurrentDictionary<string, InMemoryLocalStore<Note>> Stores { get; } = new();

        public System.Collections.Concurrent.ConcurrentDictionary<string, GatedTransport> Transports { get; } = new();

        public SyncSession<Note> Session { get; }

        public LocalSyncCollection<Note> Notes { get; }

        public string Account { get; set; } = "alice";

        public GatedTransport Transport => Transports[Account];

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Fact(DisplayName = "C4 T10: compatible waits share runs; replication stays single-flight; the Complete profile reports progress")]
    public async Task CompatibleWaitsCoalesce()
    {
        await using var setup = new Setup();
        await setup.Notes.QueryAsync();
        for (var i = 0; i < 5; i++)
        {
            await setup.Notes.SaveAsync(new Note { Id = $"n{i}" });
        }

        var reports = new List<SyncProgress>();
        var waits = await Task.WhenAll(
            setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10), new SynchronousProgress(reports)),
            setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10)),
            setup.Notes.SyncAsync(SyncGoal.Background, TimeSpan.FromSeconds(10)));

        Assert.All(waits, w => Assert.True(w.Reached));
        Assert.Equal(1, setup.Transport.MaxConcurrent);
        Assert.NotEmpty(reports);
        Assert.Equal(0, waits[0].Pending);
        Assert.Equal(5, setup.Server.Server.Snapshot().Count);
    }

    [Fact(DisplayName = "C4 I02: a wait for revision N is never completed by the acceptance of an older revision")]
    public async Task OlderAcknowledgementDoesNotComplete()
    {
        await using var setup = new Setup();
        await setup.Notes.QueryAsync();
        var engine = await setup.Session.GetEngineAsync("alice");
        setup.Transport.PushGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = await engine.WriteAsync(new Note { Id = "doc", Title = "v1" });
        setup.Session.RequestSync();
        await WaitUntil(() => setup.Server.Server.GetVersion("doc") is not null, "revision 1 committed on the server, response held");
        var second = await engine.WriteAsync(new Note { Id = "doc", Title = "v2" });
        var wait = setup.Notes.SyncAsync(SyncGoal.Accepted("doc", second.LocalRevision), TimeSpan.FromSeconds(10));

        setup.Transport.PushGate.SetResult(); // revision 1 is acknowledged
        await WaitUntil(() => engine.GetAsync("doc").Result!.Pending is null || engine.GetAsync("doc").Result!.Pending!.Revision == second.LocalRevision, "revision 1 acknowledged");
        var completedEarly = wait.IsCompletedSuccessfully && (await wait).Reached && setup.Server.Get("doc").Title != "v2";
        setup.Transport.PushGate = null;
        var result = await wait;

        Assert.Equal(1, first.LocalRevision);
        Assert.False(completedEarly);
        Assert.True(result.Reached);
        Assert.Equal("v2", setup.Server.Get("doc").Title);
    }

    [Fact(DisplayName = "C4 I01 I15: budget expiry returns an unreached result; cancelling a wait keeps the local write")]
    public async Task BudgetAndCancellation()
    {
        await using var setup = new Setup();
        await setup.Notes.QueryAsync();
        setup.Transport.Failure = new SyncTransportException(SyncErrorCodes.Unavailable, "offline", isTransient: true);
        await setup.Notes.SaveAsync(new Note { Id = "offline" });

        var expired = await setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromMilliseconds(200));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10), cancellationToken: cancel.Token));

        Assert.False(expired.Reached);
        Assert.Equal(1, expired.Pending);
        Assert.NotNull(await setup.Notes.GetAsync("offline")); // nothing undone
        Assert.Equal(SyncItemState.Pending, (await setup.Notes.GetItemStatusAsync("offline"))!.State);

        setup.Transport.Failure = null;
        Assert.True((await setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10))).Reached);
    }

    [Fact(DisplayName = "C4 I07 I15: switching account ends the old account's wait unreached, and its in-flight sync cannot write the new account's status")]
    public async Task AccountSwitchEndsWait()
    {
        await using var setup = new Setup();
        await setup.Notes.QueryAsync();
        setup.Transport.PushGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var alicesTransport = setup.Transport;
        await setup.Notes.SaveAsync(new Note { Id = "alice-note" });
        var wait = setup.Notes.SyncAsync(SyncGoal.Complete, TimeSpan.FromSeconds(10));
        await WaitUntil(() => setup.Server.Server.GetVersion("alice-note") is not null, "alice's push in flight");

        setup.Account = "bob";
        await setup.Notes.QueryAsync(); // opens bob's replica, closing alice's
        alicesTransport.PushGate.SetResult();
        var result = await wait;
        await Task.Delay(100);

        Assert.False(result.Reached);
        Assert.Equal("bob", setup.Session.Account);
        Assert.Equal(0, setup.Notes.Status.Pending); // bob has nothing pending; alice's late callback did not leak into his status
    }

    [Fact(DisplayName = "C4 I08: the one-shot helper stops at its time budget with resumable work and finishes on the next call")]
    public async Task OneShotHelper()
    {
        var server = InMemorySyncServerRef.Create();
        var source = new TestReplica(server, "source");
        for (var i = 0; i < 50; i++)
        {
            await source.Engine.WriteAsync(new Note { Id = $"n{i:00}" });
        }

        await source.Engine.SyncAsync();
        var slow = new TestReplica(server, "slow", options: new SyncOptions<Note> { PullBatchSize = 5, MaxPullPages = 1 }, transport: inner => new DelayedPulls(inner));

        var first = await slow.Engine.SyncForAsync(TimeSpan.FromMilliseconds(150));
        var pulledSoFar = (await slow.Engine.QueryAsync()).Count;
        var rest = await slow.Engine.SyncForAsync(TimeSpan.FromSeconds(30));

        Assert.True(first.HasRemainingWork);
        Assert.InRange(pulledSoFar, 1, 49); // committed pages are kept
        Assert.False(rest.HasRemainingWork);
        Assert.Equal(50, (await slow.Engine.QueryAsync()).Count);
    }

    private sealed class DelayedPulls(ISyncTransport<Note> inner) : ISyncTransport<Note>
    {
        public async Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            await Task.Delay(40, cancellationToken);
            return await inner.PullAsync(request, cancellationToken);
        }

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) => inner.PushAsync(request, cancellationToken);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
    }

    private sealed class SynchronousProgress(List<SyncProgress> reports) : IProgress<SyncProgress>
    {
        public void Report(SyncProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }
}
