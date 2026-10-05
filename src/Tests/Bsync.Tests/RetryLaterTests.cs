using Bsync.Client;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bsync.Tests;

/// <summary>
/// Task D3: a transient condition (a dependency not yet on the server) is answered retry-later, which the replica retries
/// with backoff until it is accepted; terminal problems are rejections and park the record.
/// </summary>
public sealed class RetryLaterTests
{
    /// <summary>A child note (Body "parent:ID") is accepted only once its parent exists on the server.</summary>
    private sealed class ParentRequired(Func<InMemorySyncServer<Note>> server) : ISyncWriteHandler<Note>
    {
        public ValueTask<SyncWriteDecision<Note>> HandleAsync(SyncWriteContext<Note> write, CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                write.Submitted.Body.StartsWith("parent:", StringComparison.Ordinal) && server().GetVersion(write.Submitted.Body["parent:".Length..]) is null
                    ? SyncWriteDecision<Note>.RetryLater(PushErrorCodes.DependencyMissing, "The parent is not on the server yet.")
                    : SyncWriteDecision<Note>.Accept(write.Submitted));
    }

    private static InMemorySyncServer<Note> Server()
    {
        InMemorySyncServer<Note>? server = null;
        var options = NoteJson.ServerOptions(SystemPhysicalClock.Instance);
        server = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
        {
            Cloner = options.Cloner,
            Fingerprint = options.Fingerprint,
            PhysicalClock = options.PhysicalClock,
            WriteHandler = new ParentRequired(() => server!),
        });
        return server;
    }

    [Fact(DisplayName = "D3 I04 I19: retry-later stores no receipt; the same operation is accepted once its dependency arrives")]
    public async Task SameOperationAcceptedLater()
    {
        var server = new InMemorySyncServerRef(Server());
        var child = new TestReplica(server, "child", physicalClock: SystemPhysicalClock.Instance);
        var parent = new TestReplica(server, "parent", physicalClock: SystemPhysicalClock.Instance);
        await child.Engine.WriteAsync(new Note { Id = "line", Body = "parent:order" });

        var early = await child.Engine.SyncAsync();
        var waiting = await child.RecordAsync("line");
        await parent.Engine.WriteAsync(new Note { Id = "order" });
        await parent.Engine.SyncAsync();
        var later = await child.Engine.SyncAsync();
        var sent = child.Transport.PushLog.Select(p => p.Operations.Single().OperationId).ToList();

        Assert.Equal((0, 1, 0), (early.Pushed, early.Deferred, early.Rejected));
        Assert.Null(waiting.Rejection); // not parked: nothing for the user to do
        Assert.True(waiting.IsDirty);
        Assert.Equal(1, later.Pushed);
        Assert.Equal(2, sent.Count);
        Assert.Single(sent.Distinct()); // the same operation id, decided only the second time
        Assert.False((await child.RecordAsync("line")).IsDirty);
    }

    [Fact(DisplayName = "D3 I08 I19: the session retries deferred work with backoff, not in a tight loop, and it is accepted without user action")]
    public async Task SessionBacksOffThenSucceeds()
    {
        var authority = Server();
        var pushes = 0;
        var services = new ServiceCollection();
        services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note>
        {
            Cloner = NoteJson.Clone,
            OpenReplica = (_, _) => Task.FromResult(new LocalReplica<Note>(new InMemoryLocalStore<Note>(NoteJson.Clone), "child")),
            CreateTransport = _ => new CountingTransport(new InProcessTransport<Note>(authority), () => Interlocked.Increment(ref pushes)),
            Interval = TimeSpan.FromSeconds(30),
            MinBackoff = TimeSpan.FromMilliseconds(50),
            MaxBackoff = TimeSpan.FromMilliseconds(400),
        });
        await using var provider = services.BuildServiceProvider();
        var notes = provider.GetRequiredService<ISyncCollection<Note>>();

        await notes.SaveAsync(new Note { Id = "line", Body = "parent:order" });
        await Task.Delay(1_000);
        var pushesWhileWaiting = Volatile.Read(ref pushes);
        authority.Push(new PushRequest<Note>([new PushOperation<Note>("o-parent", "order", null, new Note { Id = "order", UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "p") })]));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await notes.GetItemStatusAsync("line"))!.State != SyncItemState.Synced && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        Assert.Equal(SyncItemState.Synced, (await notes.GetItemStatusAsync("line"))!.State);
        Assert.InRange(pushesWhileWaiting, 2, 12); // retried, with growing waits; a tight loop would push hundreds of times
        Assert.NotNull(authority.GetVersion("line"));
    }

    private sealed class CountingTransport(ISyncTransport<Note> inner, Action onPush) : ISyncTransport<Note>
    {
        public Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) => inner.PullAsync(request, cancellationToken);

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default)
        {
            onPush();
            return inner.PushAsync(request, cancellationToken);
        }

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
    }
}
