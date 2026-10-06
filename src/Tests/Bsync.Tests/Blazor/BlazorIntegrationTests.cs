using System.Security.Claims;
using Bsync.Blazor;
using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Bsync.Tests.Blazor;

/// <summary>ADR-007: the component-facing collection, the local session loop and the server-connected collection.</summary>
public sealed class BlazorIntegrationTests
{
    // A wall-clock deadline: counting Task.Delay(10) iterations depends on the OS timer resolution (about 15 ms on
    // Windows) and on machine load, which made the effective budget vary between runs.
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail($"Timed out after {watch.Elapsed.TotalSeconds:F1} s waiting for: {what}");
    }

    /// <summary>A local-replica session over per-account in-memory stores (they survive session restarts, like device storage).</summary>
    private sealed class LocalHarness
    {
        public LocalHarness(
            Func<string, CancellationToken, Task<IAsyncDisposable?>>? lease = null,
            Func<SyncSessionOptions<Note>, SyncSessionOptions<Note>>? configure = null)
        {
            // Clients stamp real time, so the server's skew check must use real time too.
            Server = InMemorySyncServerRef.Create(SystemPhysicalClock.Instance);
            Session = new SyncSession<Note>(new SyncSessionOptions<Note>
            {
                OpenReplica = (account, _) => Task.FromResult(new LocalReplica<Note>(Stores.GetOrAdd(account, _ => new InMemoryLocalStore<Note>(NoteJson.Clone)), $"node-{account}")),
                CreateTransport = account => Transports.GetOrAdd(account, _ => new SwitchableTransport(new ServerRefTransport(Server))),
                Cloner = NoteJson.Clone,
                AcquireLease = lease,
                Host = "test",
                Interval = TimeSpan.FromMinutes(10),
                MinBackoff = TimeSpan.FromSeconds(1),
                MaxBackoff = TimeSpan.FromMinutes(1),
                FollowerRefresh = TimeSpan.FromSeconds(2),
                TimeProvider = Time,
            } is var options && configure is not null ? configure(options) : options);
            Collection = new LocalSyncCollection<Note>(Session, _ => Task.FromResult(Account));
        }

        public FakeTimeProvider Time { get; } = new();

        public InMemorySyncServerRef Server { get; }

        public System.Collections.Concurrent.ConcurrentDictionary<string, InMemoryLocalStore<Note>> Stores { get; } = new();

        public System.Collections.Concurrent.ConcurrentDictionary<string, SwitchableTransport> Transports { get; } = new();

        public SyncSession<Note> Session { get; }

        public LocalSyncCollection<Note> Collection { get; }

        public string Account { get; set; } = "alice";

        public SwitchableTransport Transport => Transports[Account];
    }

    /// <summary>A transport that can fail transiently (optionally with Retry-After) and counts attempts.</summary>
    private sealed class SwitchableTransport(ISyncTransport<Note> inner) : ISyncTransport<Note>
    {
        private int _attempts;

        private int _streamOpens;

        public Exception? Failure { get; set; }

        /// <summary>Thrown by the next pull only.</summary>
        public Exception? FailOnce { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public int StreamOpens => Volatile.Read(ref _streamOpens);

        /// <summary>When set, StreamAsync yields one hint per item written; completing the writer ends the stream.</summary>
        public System.Threading.Channels.Channel<bool>? Hints { get; set; }

        /// <summary>When set, StreamAsync fails immediately (a broken hint connection).</summary>
        public bool HintsFail { get; set; }

        public async Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _attempts);
            if (Gate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            if (Interlocked.Exchange(ref _failOnceUsed, 1) == 0 && FailOnce is { } once)
            {
                throw once;
            }

            return Failure is { } failure ? throw failure : await inner.PullAsync(request, cancellationToken);
        }

        private int _failOnceUsed;

        public void ArmFailOnce(Exception error)
        {
            FailOnce = error;
            Volatile.Write(ref _failOnceUsed, 0);
        }

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            Failure is { } failure ? throw failure : inner.PushAsync(request, cancellationToken);

        public async IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _streamOpens);
            if (HintsFail)
            {
                throw new SyncTransportException(SyncErrorCodes.Unavailable, "hint connection failed", isTransient: true);
            }

            if (Hints is not { } hints)
            {
                throw new NotSupportedException();
            }

            await foreach (var _ in hints.Reader.ReadAllAsync(cancellationToken))
            {
                yield return StreamEvent<Note>.Resync();
            }
        }
    }

    private static SyncTransportException Unreachable(TimeSpan? retryAfter = null) =>
        new(SyncErrorCodes.Unavailable, "unreachable", isTransient: true, retryAfter);

    [Fact(DisplayName = "I16: a local save is confirmed locally, then uploaded by the session loop and reported as synced")]
    public async Task LocalSaveThenBackgroundUpload()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;

        var result = await harness.Collection.SaveAsync(new Note { Id = "n1", Title = "hello" });

        Assert.Equal(SyncConfirmation.SavedLocally, result.Confirmation);
        Assert.True(harness.Collection.Capabilities.DurableOfflineWrites);
        Assert.False(harness.Collection.Capabilities.WritesConfirmedByServer);
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "synced");
        Assert.Equal("hello", harness.Server.Get("n1").Title);
        Assert.NotNull(harness.Collection.Status.LastSynced);
    }

    private static LocalHarness WithHints(System.Threading.Channels.Channel<bool>? hints, bool fail = false)
    {
        var harness = new LocalHarness(configure: o => o with { LiveHints = true });
        harness.Transports["alice"] = new SwitchableTransport(new ServerRefTransport(harness.Server)) { Hints = hints, HintsFail = fail };
        return harness;
    }

    [Fact(DisplayName = "I13: a server hint makes the session sync at once instead of waiting for the interval")]
    public async Task HintTriggersSync()
    {
        var hints = System.Threading.Channels.Channel.CreateUnbounded<bool>();
        var harness = WithHints(hints);
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");
        await WaitUntil(() => harness.Transport.StreamOpens == 1, "hint stream open");
        var attempts = harness.Transport.Attempts;

        await hints.Writer.WriteAsync(true);

        await WaitUntil(() => harness.Transport.Attempts > attempts, "hinted sync");
        Assert.True(harness.Collection.Capabilities.LiveUpdates);
    }

    [Fact(DisplayName = "I13: when every hint is lost, the interval still reconciles")]
    public async Task LostHintsStillConverge()
    {
        var hints = System.Threading.Channels.Channel.CreateUnbounded<bool>(); // never written: every hint lost
        var harness = WithHints(hints);
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        // Another replica writes to the server; no hint arrives.
        var writer = new TestReplica(harness.Server, "writer", physicalClock: SystemPhysicalClock.Instance);
        await writer.Engine.WriteAsync(new Note { Id = "remote" });
        await writer.Engine.SyncAsync();
        Assert.Null(await harness.Collection.GetAsync("remote"));

        harness.Time.Advance(TimeSpan.FromMinutes(10));
        await WaitUntil(() => harness.Collection.GetAsync("remote").Result is not null, "reconciled by interval");
    }

    [Fact(DisplayName = "I08 I13: a broken hint connection reconnects with backoff")]
    public async Task HintReconnects()
    {
        var harness = WithHints(null, fail: true);
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Transport.StreamOpens == 1, "first attempt");

        harness.Time.Advance(TimeSpan.FromMilliseconds(400)); // below the first backoff
        await Task.Delay(100);
        Assert.Equal(1, harness.Transport.StreamOpens);

        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => harness.Transport.StreamOpens == 2, "reconnect");
    }

    [Fact(DisplayName = "I15: a paused session does not replicate; resuming syncs at once")]
    public async Task PauseAndResume()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Session.Pause();
        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Paused, "paused");
        var attempts = harness.Transport.Attempts;
        await harness.Collection.SaveAsync(new Note { Id = "while-paused" });
        harness.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(150);
        Assert.Equal(attempts, harness.Transport.Attempts);
        Assert.Equal(1, harness.Collection.Status.Pending);

        harness.Session.Resume();
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "resumed and synced");
        Assert.NotNull(harness.Server.Server.GetVersion("while-paused"));
    }

    [Fact(DisplayName = "T53 I16: unauthorized triggers one credential renewal; a second failure needs the user")]
    public async Task CredentialRenewal()
    {
        var renewals = 0;
        var harness = new LocalHarness(configure: o => o with { RenewCredentials = (_, _) => { renewals++; return Task.FromResult(Client.CredentialRenewal.Renewed); } });
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Transport.ArmFailOnce(new SyncTransportException(SyncErrorCodes.Unauthorized, "expired", isTransient: false));
        harness.Session.RequestSync();
        await WaitUntil(() => renewals == 1 && harness.Collection.Status.State == SyncState.Synced, "renewed and synced");

        harness.Transport.Failure = new SyncTransportException(SyncErrorCodes.Unauthorized, "revoked", isTransient: false);
        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.AttentionRequired, "attention");
        Assert.Equal(2, renewals);
    }

    [Fact(DisplayName = "D6 I01 I16: a renewal that cannot reach the identity provider keeps local saves and the queue, and is tried again")]
    public async Task CredentialRenewalOffline()
    {
        var renewals = 0;
        var reachable = false;
        LocalHarness harness = null!;
        harness = new LocalHarness(configure: o => o with
        {
            RenewCredentials = (_, _) =>
            {
                renewals++;
                if (!reachable)
                {
                    return Task.FromResult(Client.CredentialRenewal.Offline);
                }

                harness.Transport.Failure = null; // the new token is accepted
                return Task.FromResult(Client.CredentialRenewal.Renewed);
            },
        });
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Transport.Failure = new SyncTransportException(SyncErrorCodes.Unauthorized, "expired", isTransient: false);
        var saved = await harness.Collection.SaveAsync(new Note { Id = "while-expired" });
        await WaitUntil(() => renewals == 1 && harness.Collection.Status.State == SyncState.Offline, "offline, not signed out");
        Assert.Equal(SyncConfirmation.SavedLocally, saved.Confirmation);
        Assert.Equal(1, harness.Collection.Status.Pending);

        reachable = true;
        harness.Time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "renewed after backoff and uploaded");
        Assert.Equal(2, renewals);
        Assert.NotNull(harness.Server.Server.GetVersion("while-expired"));
    }

    [Fact(DisplayName = "D6 I16: when sign-in is required, uploads stop until asked again while local saves keep working")]
    public async Task CredentialRenewalSignInRequired()
    {
        var renewals = 0;
        var harness = new LocalHarness(configure: o => o with { RenewCredentials = (_, _) => { renewals++; return Task.FromResult(Client.CredentialRenewal.SignInRequired); } });
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Transport.Failure = new SyncTransportException(SyncErrorCodes.Unauthorized, "revoked", isTransient: false);
        harness.Session.RequestSync();
        await WaitUntil(() => renewals == 1 && harness.Collection.Status.State == SyncState.AttentionRequired, "sign-in required");
        var attempts = harness.Transport.Attempts;
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100);

        Assert.Equal(attempts, harness.Transport.Attempts); // no periodic upload attempts while the user must act
        Assert.Equal(SyncConfirmation.SavedLocally, (await harness.Collection.SaveAsync(new Note { Id = "kept" })).Confirmation);
        Assert.Equal(1, renewals);
    }

    [Fact(DisplayName = "D6: concurrent renewals for one account share one call; other accounts renew separately")]
    public async Task RenewalsCoalesce()
    {
        var calls = 0;
        var gate = new TaskCompletionSource();
        var renew = CredentialRenewals.Coalesce(async (_, _) =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return Client.CredentialRenewal.Renewed;
        });

        var first = renew("alice", CancellationToken.None);
        var second = renew("alice", CancellationToken.None);
        var other = renew("bob", CancellationToken.None);
        gate.SetResult();

        Assert.Equal([Client.CredentialRenewal.Renewed, Client.CredentialRenewal.Renewed, Client.CredentialRenewal.Renewed], await Task.WhenAll(first, second, other));
        Assert.Equal(2, calls);
        Assert.Equal(Client.CredentialRenewal.Renewed, await renew("alice", CancellationToken.None)); // a later failure renews again
        Assert.Equal(3, calls);
    }

    [Fact(DisplayName = "C5 I11 I16: a kept conflict never reads as synced, also after a restart of the session")]
    public async Task KeptConflictIsNotSynced()
    {
        var harness = new LocalHarness();
        await harness.Collection.SaveAsync(new Note { Id = "n1", Title = "v1" });
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "synced");
        var other = new TestReplica(harness.Server, "other", physicalClock: SystemPhysicalClock.Instance);
        await other.Engine.SyncAsync();
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await other.Engine.SyncAsync();

        await harness.Collection.SaveAsync(new Note { Id = "n1", Title = "mine" });
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.AttentionRequired, Conflicts: 1 }, "conflict reported");
        await harness.Session.DisposeAsync();

        // The app restarts on the same device storage: the conflict is still reported, never "synced".
        var restarted = new LocalHarness();
        restarted.Stores["alice"] = harness.Stores["alice"];
        restarted.Transports["alice"] = new SwitchableTransport(new ServerRefTransport(harness.Server));
        await using var _ = restarted.Session;
        await restarted.Collection.QueryAsync();
        await WaitUntil(() => restarted.Collection.Status is { State: SyncState.AttentionRequired, Conflicts: 1 }, "conflict reported after restart");
        Assert.Equal(1, restarted.Collection.Status.Conflicts);
    }

    [Fact(DisplayName = "C5 I16 I19: a parked rejection is still reported on a later run that has no new outcomes")]
    public async Task ParkedRejectionStaysReported()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        harness.Transports["alice"] = new SwitchableTransport(new Bsync.Server.InProcessTransport<Note>(new InMemorySyncServer<Note>(
            NoteJson.ServerOptions(SystemPhysicalClock.Instance, validator: (_, op, _) => op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null))));
        await harness.Collection.SaveAsync(new Note { Id = "n1", Title = "bad" });
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.AttentionRequired, Rejected: 1 }, "rejected");
        var attempts = harness.Transport.Attempts;

        harness.Session.RequestSync();
        await WaitUntil(() => harness.Transport.Attempts > attempts, "a later run");
        await Task.Delay(100);

        Assert.Equal(SyncState.AttentionRequired, harness.Collection.Status.State);
        Assert.Equal(1, harness.Collection.Status.Rejected);
        Assert.Equal(SyncItemState.Rejected, (await harness.Collection.GetItemStatusAsync("n1"))!.State);
    }

    [Fact(DisplayName = "C5 I16: issues are listed in pages with a total, with stable error codes")]
    public async Task IssuesArePaged()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        harness.Transports["alice"] = new SwitchableTransport(new Bsync.Server.InProcessTransport<Note>(new InMemorySyncServer<Note>(
            NoteJson.ServerOptions(SystemPhysicalClock.Instance, validator: (_, op, _) => op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null))));
        for (var i = 0; i < 5; i++)
        {
            await harness.Collection.SaveAsync(new Note { Id = $"n{i}", Title = "bad" });
        }

        await WaitUntil(() => harness.Collection.Status.Rejected == 5, "five rejections");
        var first = await harness.Collection.GetIssuesAsync(0, 2);
        var last = await harness.Collection.GetIssuesAsync(4, 2);

        Assert.Equal(5, first.Total);
        Assert.Equal(["n0", "n1"], first.Items.Select(i => i.Id));
        Assert.All(first.Items, i => Assert.Equal((SyncIssueKind.Rejected, PushErrorCodes.Forbidden), (i.Kind, i.ErrorCode)));
        Assert.Equal(["n4"], last.Items.Select(i => i.Id));
    }

    [Fact(DisplayName = "C3 C5 F14: an idle background cycle notifies no subscriber; staleness follows the last pull")]
    public async Task IdleCyclesAreQuiet()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.SaveAsync(new Note { Id = "n1" });
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "synced");
        var notifications = 0;
        using var subscription = harness.Collection.Subscribe(() => Interlocked.Increment(ref notifications));
        var attempts = harness.Transport.Attempts;
        var pulledAt = harness.Collection.Status.LastPulled;

        // Advance past the interval until the loop has armed its timer and run one idle cycle.
        await WaitUntil(() => { harness.Time.Advance(TimeSpan.FromMinutes(10)); return harness.Transport.Attempts > attempts && harness.Collection.Status.LastPulled > pulledAt; }, "an idle cycle");
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref notifications));
        var pulled = harness.Collection.Status.LastPulled!.Value;
        Assert.Equal(TimeSpan.FromMinutes(5), harness.Collection.Status.Staleness(pulled.AddMinutes(5)));
        Assert.False(harness.Collection.Status.IsStale(TimeSpan.FromMinutes(1), pulled));
        Assert.True(harness.Collection.Status.IsStale(TimeSpan.FromMinutes(1), pulled.AddMinutes(2)));
    }

    [Fact(DisplayName = "I15: a lifecycle attachment lives exactly as long as the replica")]
    public async Task LifecycleAttachment()
    {
        var attached = 0;
        var detached = 0;
        var harness = new LocalHarness(configure: o => o with
        {
            AttachLifecycle = (_, _, _) =>
            {
                attached++;
                return Task.FromResult<IAsyncDisposable?>(new Detach(() => detached++));
            },
        });

        await harness.Collection.QueryAsync();
        harness.Account = "bob";
        await harness.Collection.QueryAsync();
        await harness.Session.DisposeAsync();

        Assert.Equal(2, attached);
        Assert.Equal(2, detached);
    }

    [Fact(DisplayName = "I16: per-item status reports pending, synced and rejected documents")]
    public async Task ItemStatus()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        harness.Transports["alice"] = new SwitchableTransport(new Bsync.Server.InProcessTransport<Note>(new InMemorySyncServer<Note>(
            NoteJson.ServerOptions(SystemPhysicalClock.Instance, validator: (_, op, _) => op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null))));
        harness.Transport.Gate = new TaskCompletionSource();

        await harness.Collection.SaveAsync(new Note { Id = "good" });
        await harness.Collection.SaveAsync(new Note { Id = "bad", Title = "bad" });
        Assert.Equal(SyncItemState.Pending, (await harness.Collection.GetItemStatusAsync("good"))!.State);
        Assert.Null(await harness.Collection.GetItemStatusAsync("unknown"));

        harness.Transport.Gate.SetResult();
        await WaitUntil(() => harness.Collection.GetItemStatusAsync("good").Result!.State == SyncItemState.Synced, "good synced");
        var bad = (await harness.Collection.GetItemStatusAsync("bad"))!;
        Assert.Equal(new SyncItemStatus(SyncItemState.Rejected, PushErrorCodes.Forbidden), bad);
    }

    [Fact(DisplayName = "I11 I16: a kept conflict is listed, shown as conflicted, and resolved or discarded through the collection")]
    public async Task ConflictsThroughCollection()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        var other = new TestReplica(harness.Server, "other", physicalClock: SystemPhysicalClock.Instance);
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "base" });
        await other.Engine.WriteAsync(new Note { Id = "n2", Title = "base" });
        await other.Engine.SyncAsync();
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.GetAsync("n2").Result is not null, "initial pull");

        harness.Transport.Gate = new TaskCompletionSource();
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await other.Engine.WriteAsync(new Note { Id = "n2", Title = "theirs" });
        await other.Engine.SyncAsync();
        await harness.Collection.SaveAsync(new Note { Id = "n1", Title = "mine" });
        await harness.Collection.SaveAsync(new Note { Id = "n2", Title = "mine" });
        harness.Transport.Gate.SetResult();
        await WaitUntil(() => harness.Collection.GetConflictsAsync().Result.Count == 2, "two kept conflicts");

        var conflict = (await harness.Collection.GetConflictsAsync()).Single(c => c.Id == "n1");
        Assert.Equal(("mine", "theirs", "base"), (conflict.Local.Title, conflict.Server.Title, conflict.Base!.Title));
        Assert.Equal(SyncItemState.Conflicted, (await harness.Collection.GetItemStatusAsync("n1"))!.State);
        Assert.Equal("theirs", (await harness.Collection.GetAsync("n1"))!.Title);
        // C5: nothing is left to push, but the status asks for attention instead of claiming "synced".
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.AttentionRequired, Pending: 0, Conflicts: 2 }, "nothing left to push while the decision waits");

        Assert.Equal(SyncConfirmation.SavedLocally, (await harness.Collection.ResolveConflictAsync("n1", new Note { Id = "n1", Title = "merged" })).Confirmation);
        Assert.True(await harness.Collection.DiscardConflictAsync("n2"));
        await WaitUntil(() => harness.Server.Get("n1").Title == "merged", "resolution uploaded");

        Assert.Empty(await harness.Collection.GetConflictsAsync());
        Assert.Equal("theirs", harness.Server.Get("n2").Title);
        Assert.Equal(SyncConfirmation.NotFound, (await harness.Collection.ResolveConflictAsync("n1", new Note { Id = "n1" })).Confirmation);
        Assert.False(await harness.Collection.DiscardConflictAsync("n2"));
        await WaitUntil(() => harness.Collection.GetItemStatusAsync("n1").Result!.State == SyncItemState.Synced, "n1 synced");
    }

    [Fact(DisplayName = "I16 I19: a rejected document can be retried or reverted through the collection")]
    public async Task RetryAndRevertThroughCollection()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        var strict = true;
        harness.Transports["alice"] = new SwitchableTransport(new Bsync.Server.InProcessTransport<Note>(new InMemorySyncServer<Note>(
            NoteJson.ServerOptions(SystemPhysicalClock.Instance, validator: (_, op, _) => strict && op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null))));
        await harness.Collection.SaveAsync(new Note { Id = "retry", Title = "bad" });
        await harness.Collection.SaveAsync(new Note { Id = "revert", Title = "bad" });
        await WaitUntil(() => harness.Collection.GetItemStatusAsync("revert").Result!.State == SyncItemState.Rejected, "rejected");
        await WaitUntil(() => harness.Collection.GetItemStatusAsync("retry").Result!.State == SyncItemState.Rejected, "rejected");

        strict = false;
        Assert.Equal(SyncConfirmation.SavedLocally, (await harness.Collection.RetryAsync("retry")).Confirmation);
        Assert.True(await harness.Collection.RevertAsync("revert"));
        await WaitUntil(() => harness.Collection.GetItemStatusAsync("retry").Result!.State == SyncItemState.Synced, "retried and accepted");

        Assert.Null(await harness.Collection.GetAsync("revert")); // never confirmed by the server: hidden
        Assert.Equal(SyncConfirmation.NotFound, (await harness.Collection.RetryAsync("retry")).Confirmation);
        Assert.False(await harness.Collection.RevertAsync("retry"));
        await WaitUntil(() => harness.Collection.Status is { Pending: 0 }, "nothing pending");
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Error)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    [Fact(DisplayName = "I15 I16: an unexpected failure is logged and reported instead of ending the loop; the next request recovers")]
    public async Task UnexpectedFailureIsReportedAndRecovers()
    {
        var logger = new CapturingLogger();
        var harness = new LocalHarness(configure: o => o with { Logger = logger });
        await using var _ = harness.Session;
        await harness.Collection.SaveAsync(new Note { Id = "secret-id", Title = "secret title" });
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Transport.Failure = new InvalidOperationException("a bug somewhere");
        await harness.Collection.SaveAsync(new Note { Id = "n2", Title = "queued" });
        await WaitUntil(() => harness.Collection.Status.State == SyncState.AttentionRequired, "attention");
        Assert.Contains(nameof(InvalidOperationException), harness.Collection.Status.Detail);

        harness.Transport.Failure = null;
        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "recovered");

        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error && e.Error is InvalidOperationException);
        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.Contains("AttentionRequired", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("secret", StringComparison.Ordinal) || e.Message.Contains("alice", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "The local recipe gives the session the container's logger")]
    public async Task RecipeUsesContainerLogging()
    {
        var provider = new CapturingProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(provider).SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Debug));
        var harness = new LocalHarness();
        services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note>
        {
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone), "node")),
            CreateTransport = _ => new ServerRefTransport(harness.Server),
            Cloner = NoteJson.Clone,
        });
        await using var container = services.BuildServiceProvider();
        await using var session = container.GetRequiredService<SyncSession<Note>>();

        await container.GetRequiredService<ISyncCollection<Note>>().SaveAsync(new Note { Id = "n1" });
        await WaitUntil(() => session.Status.State == SyncState.Synced, "synced");

        Assert.Contains(provider.Logger.Entries, e => e.Message.Contains("Synced", StringComparison.Ordinal));
        Assert.Equal("Bsync.SyncSession", provider.Category);
    }

    private sealed class CapturingProvider : Microsoft.Extensions.Logging.ILoggerProvider
    {
        public CapturingLogger Logger { get; } = new();

        public string? Category { get; private set; }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            if (categoryName.StartsWith("Bsync", StringComparison.Ordinal))
            {
                Category = categoryName;
                return Logger;
            }

            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }

    [Fact(DisplayName = "I11: a server-connected collection never keeps conflicts")]
    public async Task ServerCollectionHasNoKeptConflicts()
    {
        using var collection = ServerCollection(new InMemorySyncServer<Note>(NoteJson.ServerOptions()));

        Assert.Empty(await collection.GetConflictsAsync());
        Assert.Equal(SyncConfirmation.NotFound, (await collection.ResolveConflictAsync("n1", new Note { Id = "n1" })).Confirmation);
        Assert.False(await collection.DiscardConflictAsync("n1"));
        Assert.Equal(SyncConfirmation.NotFound, (await collection.RetryAsync("n1")).Confirmation);
        Assert.False(await collection.RevertAsync("n1"));
    }

    private sealed class Detach(Action action) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            action();
            return ValueTask.CompletedTask;
        }
    }

    [Fact(DisplayName = "I16: the pending count reflects a local write immediately, before any sync runs")]
    public async Task PendingUpdatesImmediately()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "synced");
        harness.Transport.Gate = new TaskCompletionSource(); // hold the next sync

        await harness.Collection.SaveAsync(new Note { Id = "n1" });

        Assert.Equal(1, harness.Collection.Status.Pending);
        harness.Transport.Gate.SetResult();
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "uploaded");
    }

    [Fact(DisplayName = "T52 I08: transient failures back off exponentially with jitter; a request retries at once")]
    public async Task BackoffAndTrigger()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "first sync");
        harness.Transport.Failure = Unreachable();

        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Offline, "offline");
        var attempts = harness.Transport.Attempts;

        harness.Time.Advance(TimeSpan.FromMilliseconds(400)); // below the first backoff (0.5-1 s)
        await Task.Delay(100);
        Assert.Equal(attempts, harness.Transport.Attempts);

        harness.Time.Advance(TimeSpan.FromMilliseconds(700));
        await WaitUntil(() => harness.Transport.Attempts == attempts + 1, "second attempt");

        harness.Time.Advance(TimeSpan.FromMilliseconds(900)); // below the second backoff (1-2 s)
        await Task.Delay(100);
        Assert.Equal(attempts + 1, harness.Transport.Attempts);

        harness.Session.RequestSync(); // e.g. the browser reported the network is back
        await WaitUntil(() => harness.Transport.Attempts == attempts + 2, "triggered attempt");

        harness.Transport.Failure = null;
        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "recovered");
    }

    [Fact(DisplayName = "T53 I08: a server Retry-After is respected even when a sync is requested")]
    public async Task RetryAfterRespected()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        harness.Transports["alice"] = new SwitchableTransport(new ServerRefTransport(harness.Server)) { Failure = Unreachable(TimeSpan.FromSeconds(60)) };

        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Offline, "offline");
        var attempts = harness.Transport.Attempts;

        harness.Session.RequestSync();
        harness.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(150);
        Assert.Equal(attempts, harness.Transport.Attempts);

        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => harness.Transport.Attempts == attempts + 1, "attempt after Retry-After");
    }

    [Fact(DisplayName = "I08: backoff is capped, jittered and never shorter than Retry-After")]
    public void BackoffBounds()
    {
        var session = new LocalHarness().Session;
        for (var failures = 1; failures < 12; failures++)
        {
            var expected = Math.Min(Math.Pow(2, failures - 1), 60);
            var delay = session.Backoff(failures, null).TotalSeconds;
            Assert.InRange(delay, expected / 2, expected);
        }

        Assert.True(session.Backoff(1, TimeSpan.FromSeconds(30)) >= TimeSpan.FromSeconds(30));
    }

    [Fact(DisplayName = "T42: a session without the lease follows: it never replicates and refreshes readers periodically")]
    public async Task FollowerDoesNotReplicate()
    {
        var harness = new LocalHarness(lease: (_, _) => Task.FromResult<IAsyncDisposable?>(null));
        await using var _ = harness.Session;
        var notifications = 0;
        using var subscription = harness.Collection.Subscribe(() => Interlocked.Increment(ref notifications));

        await harness.Collection.SaveAsync(new Note { Id = "n1" });
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Follower, "follower");
        var before = Volatile.Read(ref notifications);
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntil(() => Volatile.Read(ref notifications) > before, "follower refresh");

        Assert.Equal(0, harness.Transport.Attempts);
        Assert.Empty(harness.Server.Server.Snapshot());
        Assert.Equal(1, harness.Collection.Status.Pending);
    }

    [Fact(DisplayName = "T39 I07: switching account isolates replicas and abandons the previous account's in-flight sync")]
    public async Task AccountSwitch()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.SaveAsync(new Note { Id = "alice-note" });
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "alice synced");

        // Hold alice's next sync in flight, then switch.
        harness.Transport.Gate = new TaskCompletionSource();
        await harness.Collection.SaveAsync(new Note { Id = "alice-pending" });
        await WaitUntil(() => harness.Transport.Attempts >= 2, "alice sync in flight");

        // Bob's data lives in his own server scope (as with ScopedAuthority on a real server).
        harness.Transports["bob"] = new SwitchableTransport(new Bsync.Server.InProcessTransport<Note>(
            new InMemorySyncServer<Note>(NoteJson.ServerOptions(SystemPhysicalClock.Instance))));
        harness.Account = "bob";

        var bobView = await harness.Collection.QueryAsync();

        Assert.Empty(bobView);
        Assert.Equal("bob", harness.Session.Account);
        Assert.Null(await harness.Stores["bob"].GetAsync("alice-note"));
        Assert.True((await harness.Stores["alice"].GetAsync("alice-pending"))!.IsDirty); // kept for alice
        await harness.Collection.SaveAsync(new Note { Id = "bob-note" });
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "bob synced");
        Assert.Null(await harness.Stores["alice"].GetAsync("bob-note"));
    }

    [Fact(DisplayName = "I16 I19: permanent failures and rejections put the session in AttentionRequired")]
    public async Task AttentionRequired()
    {
        var harness = new LocalHarness();
        await using var _ = harness.Session;
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        harness.Transport.Failure = new SyncTransportException(SyncErrorCodes.Unauthorized, "sign in", isTransient: false);
        harness.Session.RequestSync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.AttentionRequired, "attention");
        Assert.Contains("unauthorized", harness.Collection.Status.Detail);
    }

    [Fact(DisplayName = "I15: stopping the session ends its loop and reports Stopped")]
    public async Task StopEndsLoop()
    {
        var harness = new LocalHarness();
        await harness.Collection.QueryAsync();
        await WaitUntil(() => harness.Collection.Status.State == SyncState.Synced, "synced");

        await harness.Session.DisposeAsync();
        var attempts = harness.Transport.Attempts;
        harness.Time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100);

        Assert.Equal(SyncState.Stopped, harness.Collection.Status.State);
        Assert.Equal(attempts, harness.Transport.Attempts);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => harness.Collection.QueryAsync());
    }

    private static ServerSyncCollection<Note> ServerCollection(ISyncAuthority<Note> authority, string user = "alice", string scope = "default") =>
        new(authority,
            _ => Task.FromResult(new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")), scope)),
            new HybridLogicalClock($"server-{user}", new ManualClock(1_000)),
            NoteJson.Clone);

    [Fact(DisplayName = "I05 I16 I18: server-connected writes are confirmed by the server and conflict instead of overwriting")]
    public async Task ServerCollectionConflicts()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        using var alice = ServerCollection(authority);
        using var bob = ServerCollection(authority, "bob");

        Assert.Equal(SyncConfirmation.AcceptedByServer, (await alice.SaveAsync(new Note { Id = "n1", Title = "v1" })).Confirmation);
        var bobCopy = (await bob.GetAsync("n1"))!;
        var aliceCopy = (await alice.GetAsync("n1"))!;

        bobCopy.Title = "bob";
        Assert.Equal(SyncConfirmation.AcceptedByServer, (await bob.SaveAsync(bobCopy)).Confirmation);
        aliceCopy.Title = "alice";
        var conflict = await alice.SaveAsync(aliceCopy);

        Assert.Equal(SyncConfirmation.Conflict, conflict.Confirmation);
        Assert.Equal("bob", (await alice.GetAsync("n1"))!.Title);
        aliceCopy.Title = "alice after reload";
        Assert.Equal(SyncConfirmation.AcceptedByServer, (await alice.SaveAsync(aliceCopy)).Confirmation);
        Assert.True(alice.Capabilities.WritesConfirmedByServer);
        Assert.False(alice.Capabilities.DurableOfflineWrites);
    }

    [Fact(DisplayName = "I19: SaveAllAsync writes a group all-or-nothing on server-connected and local hosts")]
    public async Task SaveAllIsAtomic()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        using var alice = ServerCollection(authority);
        using var bob = ServerCollection(authority, "bob");
        Assert.All(await alice.SaveAllAsync([new Note { Id = "order", Title = "1" }, new Note { Id = "line", Title = "1" }]), r => Assert.Equal(SyncConfirmation.AcceptedByServer, r.Confirmation));
        var bobsOrder = (await bob.GetAsync("order"))!;
        var bobsLine = (await bob.GetAsync("line"))!;
        var alicesLine = (await alice.GetAsync("line"))!;
        alicesLine.Title = "changed by alice";
        await alice.SaveAsync(alicesLine);

        bobsOrder.Title = "2";
        bobsLine.Title = "stale";
        var results = await bob.SaveAllAsync([bobsOrder, bobsLine]);

        Assert.All(results, r => Assert.Equal(SyncConfirmation.Conflict, r.Confirmation));
        Assert.Equal("1", (await alice.GetAsync("order"))!.Title); // nothing of the group was written

        var harness = new LocalHarness();
        await using var _ = harness.Session;
        var saved = await harness.Collection.SaveAllAsync([new Note { Id = "a", Title = "1" }, new Note { Id = "b", Title = "2" }]);
        Assert.All(saved, r => Assert.Equal(SyncConfirmation.SavedLocally, r.Confirmation));
        await WaitUntil(() => harness.Collection.Status is { State: SyncState.Synced, Pending: 0 }, "group uploaded");
        Assert.Equal(["a", "b"], harness.Server.Server.Snapshot().Select(n => n.Id).Order());
    }

    [Fact(DisplayName = "I05: a server-connected save of a document it never read cannot overwrite it")]
    public async Task ServerCollectionNeverOverwritesUnseen()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        using var writer = ServerCollection(authority);
        using var other = ServerCollection(authority, "bob");
        await writer.SaveAsync(new Note { Id = "n1", Title = "original" });

        var blind = await other.SaveAsync(new Note { Id = "n1", Title = "blind overwrite" });

        Assert.Equal(SyncConfirmation.Conflict, blind.Confirmation);
        Assert.Equal("original", (await writer.GetAsync("n1"))!.Title);
    }

    [Fact(DisplayName = "I10: server-connected delete, get and not-found")]
    public async Task ServerCollectionDelete()
    {
        using var collection = ServerCollection(new InMemorySyncServer<Note>(NoteJson.ServerOptions()));
        await collection.SaveAsync(new Note { Id = "n1" });

        Assert.Equal(SyncConfirmation.NotFound, (await collection.DeleteAsync("missing")).Confirmation);
        Assert.Equal(SyncConfirmation.AcceptedByServer, (await collection.DeleteAsync("n1")).Confirmation);
        Assert.Null(await collection.GetAsync("n1"));
        Assert.Empty(await collection.QueryAsync());
        Assert.Equal(SyncConfirmation.NotFound, (await collection.DeleteAsync("n1")).Confirmation);
    }

    [Fact(DisplayName = "I07: server-connected collections are isolated by scope and respect read authorization")]
    public async Task ServerCollectionScopesAndReadAuthorization()
    {
        var options = NoteJson.ServerOptions();
        var scoped = new ScopedAuthority<Note>(
            _ => new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
            {
                Cloner = options.Cloner,
                Fingerprint = options.Fingerprint,
                PhysicalClock = options.PhysicalClock,
                CanRead = (context, note) => note.Body == context.Principal.Identity!.Name || note.Body == "shared",
            }),
            new AuthorityLimits(100, 100));
        using var alice = ServerCollection(scoped, "alice", "tenant-a");
        using var bob = ServerCollection(scoped, "bob", "tenant-a");
        using var carol = ServerCollection(scoped, "carol", "tenant-b");

        await alice.SaveAsync(new Note { Id = "private", Body = "alice" });
        await alice.SaveAsync(new Note { Id = "team", Body = "shared" });

        Assert.Equal(["private", "team"], (await alice.QueryAsync()).Select(n => n.Id));
        Assert.Equal(["team"], (await bob.QueryAsync()).Select(n => n.Id));
        Assert.Null(await bob.GetAsync("private"));
        Assert.Empty(await carol.QueryAsync());
    }

    [Fact(DisplayName = "I13 I15: other circuits in the same scope get change hints; unsubscribing and disposal stop them")]
    public async Task ServerCollectionLiveHints()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        using var writer = ServerCollection(authority);
        var reader = ServerCollection(authority, "bob");
        using var otherScope = ServerCollection(authority, "carol", "elsewhere");
        await reader.QueryAsync();
        await otherScope.QueryAsync();
        var hints = 0;
        var otherHints = 0;
        var subscription = reader.Subscribe(() => hints++);
        using var otherSubscription = otherScope.Subscribe(() => otherHints++);

        await writer.SaveAsync(new Note { Id = "n1" });
        Assert.Equal(1, hints);
        Assert.Equal(0, otherHints);
        Assert.True(reader.Capabilities.LiveUpdates);

        subscription.Dispose();
        await writer.SaveAsync(new Note { Id = "n2" });
        Assert.Equal(1, hints);

        reader.Subscribe(() => hints++);
        reader.Dispose();
        await writer.SaveAsync(new Note { Id = "n3" });
        Assert.Equal(1, hints);
        Assert.Equal(0, reader.SubscriberCount);
    }

    [Fact(DisplayName = "I08: a default-ordered query on a local replica reads bounded pages and never the whole collection")]
    public async Task DefaultOrderQueriesArePaged()
    {
        var store = new InterceptingStore<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone));
        await store.UpdateAsync(Enumerable.Range(0, 1000).Select(i => new RecordUpdate<Note>($"n{i:D4}", _ => new SyncRecord<Note>(new Note { Id = $"n{i:D4}", Title = i % 3 == 0 ? "match" : "other", Deleted = i == 3 }, null, false))).ToList());
        await using var session = new SyncSession<Note>(new SyncSessionOptions<Note>
        {
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(store, "node")),
            CreateTransport = _ => new ServerRefTransport(InMemorySyncServerRef.Create()),
            Cloner = NoteJson.Clone,
            Interval = TimeSpan.FromHours(1),
        });
        var collection = new LocalSyncCollection<Note>(session, _ => Task.FromResult("a"));

        var page = await collection.QueryAsync(new SyncQuery<Note> { Where = n => n.Title == "match", Limit = 5 });
        var ordered = await collection.QueryAsync(new SyncQuery<Note> { Order = (a, b) => string.CompareOrdinal(b.Id, a.Id), Limit = 2 });

        Assert.Equal(["n0000", "n0006", "n0009", "n0012", "n0015"], page.Select(n => n.Id)); // n0003 is deleted
        Assert.Equal(["n0999", "n0998"], ordered.Select(n => n.Id));
        Assert.Equal(1, store.FullQueries); // only the custom order read everything
    }

    [Fact(DisplayName = "I08: queries filter, order and bound their results")]
    public async Task QueryShape()
    {
        using var collection = ServerCollection(new InMemorySyncServer<Note>(NoteJson.ServerOptions()));
        for (var i = 0; i < 20; i++)
        {
            await collection.SaveAsync(new Note { Id = $"n{i:D2}", Title = i % 2 == 0 ? "even" : "odd" });
        }

        var result = await collection.QueryAsync(new SyncQuery<Note>
        {
            Where = n => n.Title == "even",
            Order = (a, b) => string.CompareOrdinal(b.Id, a.Id),
            Limit = 3,
        });

        Assert.Equal(["n18", "n16", "n14"], result.Select(n => n.Id));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => collection.QueryAsync(new SyncQuery<Note> { Limit = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => collection.QueryAsync(new SyncQuery<Note> { Limit = SyncQuery<Note>.MaxLimit + 1 }));
    }

    [Fact(DisplayName = "I07: a local replica cannot be registered in a server container")]
    public void LocalReplicaRefusedOnServer()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(Microsoft.AspNetCore.Hosting.IWebHostEnvironment), _ => null!);

        Assert.Throws<InvalidOperationException>(() => services.AddLocalSyncCollection<Note>(_ => null!));
    }

    [Fact(DisplayName = "I07 I18: the server recipe is scoped per circuit and uses the authenticated user for scope")]
    public async Task ServerRecipe()
    {
        // The recipe's server clock stamps real time, so the skew check uses real time too.
        var authority = new ScopedAuthority<Note>(_ => new InMemorySyncServer<Note>(NoteJson.ServerOptions(SystemPhysicalClock.Instance)), new AuthorityLimits(100, 100));
        var services = new ServiceCollection();
        services.AddScoped<AuthenticationStateProvider, FixedUser>();
        services.AddServerSyncCollection<Note>(_ => authority, NoteJson.Clone, user => user.FindFirst("tenant")?.Value);
        await using var provider = services.BuildServiceProvider();

        await using var first = provider.CreateAsyncScope();
        await using var second = provider.CreateAsyncScope();
        var a = first.ServiceProvider.GetRequiredService<ISyncCollection<Note>>();
        var b = second.ServiceProvider.GetRequiredService<ISyncCollection<Note>>();
        Assert.NotSame(a, b);

        await a.SaveAsync(new Note { Id = "n1" });
        Assert.Single(await b.QueryAsync()); // same user, same tenant
        Assert.Single(await authority.ListAsync(new SyncCallContext(new ClaimsPrincipal(), "t1"), 10));
        Assert.Empty(await authority.ListAsync(new SyncCallContext(new ClaimsPrincipal(), "other"), 10));
    }

    private sealed class FixedUser : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice"), new Claim("tenant", "t1")], "test"))));
    }
}
