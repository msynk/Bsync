using System.Security.Claims;
using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>The Server-Sent Events hint stream (docs/protocol.md §8) through the real endpoint and client.</summary>
public sealed class HintStreamTests
{
    private static PushRequest<Note> Push(string op, string id) =>
        new([new PushOperation<Note>(op, id, null, new Note { Id = id, UpdatedAt = new HlcTimestamp(1_000, 0, "n") })]);

    /// <summary>
    /// Reads hints with timeouts. A read that times out stays pending and is reused, because an async iterator allows
    /// only one outstanding MoveNextAsync; disposal cancels first.
    /// </summary>
    private sealed class HintReader(IAsyncEnumerable<StreamEvent<Note>> stream) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancel = new(TimeSpan.FromSeconds(30));
        private IAsyncEnumerator<StreamEvent<Note>>? _enumerator;
        private Task<bool>? _pending;

        public StreamEvent<Note>? Current => _enumerator?.Current;

        public async Task<bool> NextAsync(TimeSpan within)
        {
            _enumerator ??= stream.GetAsyncEnumerator(_cancel.Token);
            _pending ??= _enumerator.MoveNextAsync().AsTask();
            if (await Task.WhenAny(_pending, Task.Delay(within)) != _pending)
            {
                return false;
            }

            var moved = await _pending;
            _pending = null;
            return moved;
        }

        public async ValueTask DisposeAsync()
        {
            await _cancel.CancelAsync();
            if (_pending is { } pending)
            {
                try
                {
                    await pending;
                }
                catch (OperationCanceledException)
                {
                }
            }

            if (_enumerator is { } enumerator)
            {
                await enumerator.DisposeAsync();
            }

            _cancel.Dispose();
        }
    }

    [Fact(DisplayName = "I13: the hint stream announces on connect and after each commit")]
    public async Task AnnouncesCommits()
    {
        var server = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        await using var host = await SyncTestHost.StartAsync(server);
        await using var hints = new HintReader(host.Transport().StreamAsync(Checkpoint.Start));

        Assert.True(await hints.NextAsync(TimeSpan.FromSeconds(5)), "no event on connect");
        Assert.Equal(StreamEventKind.Resync, hints.Current!.Kind);

        await host.Transport().PushAsync(Push("o1", "n1"));
        Assert.True(await hints.NextAsync(TimeSpan.FromSeconds(5)), "no event after commit");
    }

    [Fact(DisplayName = "I07 I13: hints are scoped; another tenant's commits are not announced")]
    public async Task HintsAreScoped()
    {
        var authority = new ScopedAuthority<Note>(_ => new InMemorySyncServer<Note>(NoteJson.ServerOptions()), new AuthorityLimits(100, 100));
        await using var host = await SyncTestHost.StartAsync(authority, requireAuthentication: true);
        await using var aliceHints = new HintReader(host.Transport("alice", "tenant-a").StreamAsync(Checkpoint.Start));
        Assert.True(await aliceHints.NextAsync(TimeSpan.FromSeconds(5)));

        await host.Transport("bob", "tenant-b").PushAsync(Push("o1", "n1"));
        Assert.False(await aliceHints.NextAsync(TimeSpan.FromMilliseconds(700)), "tenant-b's commit was announced to tenant-a");

        await host.Transport("alice2", "tenant-a").PushAsync(Push("o2", "n2"));
        Assert.True(await aliceHints.NextAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact(DisplayName = "I13: a server without hints reports NotSupported, and unauthenticated callers are refused")]
    public async Task NotSupportedAndUnauthenticated()
    {
        await using var plain = await SyncTestHost.StartAsync(new NoHints(new InMemorySyncServer<Note>(NoteJson.ServerOptions())));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in plain.Transport().StreamAsync(Checkpoint.Start))
            {
            }
        });

        var authority = new ScopedAuthority<Note>(_ => new InMemorySyncServer<Note>(NoteJson.ServerOptions()), new AuthorityLimits(100, 100));
        await using var secured = await SyncTestHost.StartAsync(authority, requireAuthentication: true);
        var error = await Assert.ThrowsAsync<SyncTransportException>(async () =>
        {
            await foreach (var _ in secured.Transport().StreamAsync(Checkpoint.Start))
            {
            }
        });
        Assert.Equal(SyncErrorCodes.Unauthorized, error.ErrorCode);
    }

    /// <summary>Hides the commit notifier so the endpoint maps no hint stream.</summary>
    private sealed class NoHints(ISyncAuthority<Note> inner) : ISyncAuthority<Note>
    {
        public AuthorityLimits Limits => inner.Limits;

        public Task<PullResult<Note>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default) =>
            inner.PullAsync(context, request, cancellationToken);

        public Task<PushResult<Note>> PushAsync(SyncCallContext context, PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            inner.PushAsync(context, request, cancellationToken);
    }
}
