using Bsync.Conflicts;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Tests.TestSupport;
using Bsync.Transport;
using Xunit;

namespace Bsync.Tests;

/// <summary>I19 and protocol §4.1: dependency groups are uploaded together and applied all-or-nothing.</summary>
public sealed class GroupTests
{
    private static Note Doc(string id, string title) => new() { Id = id, Title = title };

    private static async Task<(InMemorySyncServerRef Server, TestReplica Other, TestReplica Client)> WithSharedDocumentsAsync(IConflictHandler<Note>? handler = null, Func<Server.SyncCallContext, Protocol.PushOperation<Note>, Note?, string?>? validator = null)
    {
        var server = new InMemorySyncServerRef(new InMemorySyncServer<Note>(NoteJson.ServerOptions(validator: validator)));
        var other = new TestReplica(server, "other");
        var client = new TestReplica(server, "client", handler);
        await other.Engine.WriteAsync(Doc("order", "v1"));
        await other.Engine.WriteAsync(Doc("line", "v1"));
        await other.Engine.SyncAsync();
        await client.Engine.SyncAsync();
        return (server, other, client);
    }

    [Fact(DisplayName = "I19: a group is committed locally at once, sent in one request and applied together")]
    public async Task AppliedTogether()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "client", options: new SyncOptions<Note> { PushBatchSize = 1 });
        await client.Engine.SyncAsync();

        var receipts = await client.Engine.WriteGroupAsync([Doc("a", "1"), Doc("b", "2"), Doc("c", "3")]);
        Assert.Equal(3, receipts.Count);
        Assert.Equal(3, await client.Engine.CountDirtyAsync());
        var result = await client.Engine.SyncAsync();

        Assert.True(result.IsComplete);
        var request = Assert.Single(client.Transport.PushLog);
        Assert.Equal(3, request.Operations.Count); // one request even with a push batch of 1
        Assert.All(request.Operations, o => Assert.Equal((request.Operations[0].Group, 3), (o.Group, o.GroupSize)));
        Assert.Equal(["a", "b", "c"], server.Server.Snapshot().Select(n => n.Id).Order());
        Assert.All(["a", "b", "c"], id => Assert.Null(client.RecordAsync(id).Result.Group));
    }

    [Fact(DisplayName = "I11 I19: a kept conflict in a group applies nothing and parks the rest; resolving it sends the whole group again")]
    public async Task ConflictParksAndResolveReleases()
    {
        var (server, other, client) = await WithSharedDocumentsAsync();
        await other.Engine.WriteAsync(Doc("line", "theirs"));
        await other.Engine.SyncAsync();

        await client.Engine.WriteGroupAsync([Doc("order", "total 30"), Doc("line", "3 items")]);
        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Conflicts);
        Assert.Equal("v1", server.Get("order").Title); // nothing of the group applied
        Assert.Equal(PushErrorCodes.GroupFailed, (await client.RecordAsync("order")).Rejection!.ErrorCode);
        Assert.NotNull((await client.RecordAsync("line")).Conflict);
        Assert.Equal(0, (await client.Engine.SyncAsync()).Pushed); // parked, not retried in a loop

        await client.Engine.ResolveConflictAsync("line", Doc("line", "3 items (merged)"));
        var resolved = await client.Engine.SyncAsync();

        Assert.True(resolved.IsComplete);
        Assert.Equal(("total 30", "3 items (merged)"), (server.Get("order").Title, server.Get("line").Title));
        var last = client.Transport.PushLog[^1].Operations;
        Assert.Equal(["line", "order"], last.Select(o => o.DocumentId).Order());
        Assert.Single(last.Select(o => o.Group).Distinct());
    }

    [Fact(DisplayName = "I19: a conflict the handler resolves is resent with the rest of the group in the same sync")]
    public async Task HandlerResolutionResendsGroup()
    {
        var (server, other, client) = await WithSharedDocumentsAsync(new ClientWinsConflictHandler<Note>());
        await other.Engine.WriteAsync(Doc("line", "theirs"));
        await other.Engine.SyncAsync();

        await client.Engine.WriteGroupAsync([Doc("order", "mine"), Doc("line", "mine")]);
        var result = await client.Engine.SyncAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(("mine", "mine"), (server.Get("order").Title, server.Get("line").Title));
        var aborted = client.Transport.PushLog[0];
        Assert.Equal(2, aborted.Operations.Count);
        Assert.Equal(aborted.Operations.Single(o => o.DocumentId == "order").OperationId, client.Transport.PushLog[^1].Operations.Single(o => o.DocumentId == "order").OperationId); // same operation, decided once
    }

    [Fact(DisplayName = "I19: a rejected member parks the group; retrying it after the fix applies the whole group")]
    public async Task RejectionAndRetry()
    {
        var strict = true;
        var (server, _, client) = await WithSharedDocumentsAsync(validator: (_, op, _) => strict && op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null);

        await client.Engine.WriteGroupAsync([Doc("order", "fine"), Doc("line", "bad")]);
        await client.Engine.SyncAsync();

        Assert.Equal(PushErrorCodes.Forbidden, (await client.RecordAsync("line")).Rejection!.ErrorCode);
        Assert.Equal(PushErrorCodes.GroupFailed, (await client.RecordAsync("order")).Rejection!.ErrorCode);
        Assert.Equal("v1", server.Get("order").Title);

        strict = false;
        await client.Engine.RetryRejectedAsync("line");
        Assert.True((await client.Engine.SyncAsync()).IsComplete);
        Assert.Equal(("fine", "bad"), (server.Get("order").Title, server.Get("line").Title));
    }

    private sealed class NoFeatures(ISyncTransport<Note> inner) : ISyncTransport<Note>
    {
        public async Task<PullResult<Note>> PullAsync(PullRequest request, CancellationToken cancellationToken = default) =>
            await inner.PullAsync(request, cancellationToken) with { Features = null };

        public Task<PushResult<Note>> PushAsync(PushRequest<Note> request, CancellationToken cancellationToken = default) => inner.PushAsync(request, cancellationToken);

        public IAsyncEnumerable<StreamEvent<Note>> StreamAsync(Checkpoint since, CancellationToken cancellationToken = default) => inner.StreamAsync(since, cancellationToken);
    }

    [Fact(DisplayName = "I19: a server without group support never receives a group piecemeal; other work continues")]
    public async Task UnsupportedServerParksGroups()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "client", transport: inner => new NoFeatures(inner));
        await client.Engine.WriteGroupAsync([Doc("a", "1"), Doc("b", "2")]);
        await client.Engine.WriteAsync(Doc("single", "ok"));

        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal(["single"], server.Server.Snapshot().Select(n => n.Id));
        Assert.Equal(PushErrorCodes.GroupsUnsupported, (await client.RecordAsync("a")).Rejection!.ErrorCode);
        Assert.DoesNotContain(client.Transport.PushLog.SelectMany(p => p.Operations), o => o.Group is not null);
    }

    [Fact(DisplayName = "I19: before the server's features are known, a push leaves groups for the next sync")]
    public async Task PushBeforePullWaits()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "client");
        await client.Engine.WriteGroupAsync([Doc("a", "1"), Doc("b", "2")]);

        var pushOnly = await client.Engine.PushAsync();
        Assert.Equal(0, pushOnly.Pushed);
        Assert.True(pushOnly.HasRemainingWork);

        Assert.Equal(2, (await client.Engine.SyncAsync()).Pushed);
    }

    [Fact(DisplayName = "I04 I05: the authority applies a group all-or-nothing; aborted members get no receipt; incomplete groups are refused")]
    public async Task AuthorityAtomicity()
    {
        var authority = new InMemorySyncServer<Note>(NoteJson.ServerOptions());
        Note Stamped(string id, string title) => new() { Id = id, Title = title, UpdatedAt = new Clocks.HlcTimestamp(1_000, 0, "n") };
        authority.Push(new PushRequest<Note>([new PushOperation<Note>("seed", "taken", null, Stamped("taken", "exists"))]));

        var outcome = authority.Push(new PushRequest<Note>(
        [
            new PushOperation<Note>("g1", "fresh", null, Stamped("fresh", "x")) { Group = "grp", GroupSize = 2 },
            new PushOperation<Note>("g2", "taken", null, Stamped("taken", "y")) { Group = "grp", GroupSize = 2 },
        ])).Outcomes;

        Assert.Equal((PushOutcomeKind.RetryLater, PushErrorCodes.GroupAborted), (outcome[0].Kind, outcome[0].ErrorCode));
        Assert.Equal(PushOutcomeKind.Conflict, outcome[1].Kind);
        Assert.Null(authority.GetVersion("fresh"));
        Assert.Equal(2, authority.ReceiptCount); // the seed and the conflict; the aborted member was not decided

        var incomplete = authority.Push(new PushRequest<Note>([new PushOperation<Note>("g3", "fresh", null, Stamped("fresh", "x")) { Group = "grp2", GroupSize = 2 }])).Outcomes;
        Assert.Equal(PushErrorCodes.Invalid, incomplete[0].ErrorCode);

        var retried = authority.Push(new PushRequest<Note>(
        [
            new PushOperation<Note>("g1", "fresh", null, Stamped("fresh", "x")) { Group = "grp", GroupSize = 2 },
            new PushOperation<Note>("g4", "taken", 1, Stamped("taken", "y")) { Group = "grp", GroupSize = 2 },
        ])).Outcomes;
        Assert.All(retried, o => Assert.Equal(PushOutcomeKind.Accepted, o.Kind));
        Assert.Equal(authority.GetVersion("fresh") + 1, authority.GetVersion("taken")); // committed together
    }
}
