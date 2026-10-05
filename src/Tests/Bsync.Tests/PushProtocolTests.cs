using Bsync.Conflicts;
using Bsync.Protocol;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>Push batching, operation identity, outcome correlation and fairness.</summary>
public sealed class PushProtocolTests
{
    [Theory(DisplayName = "T01-T04 I08: batching drains zero, exact, batch+1 and many batches")]
    [InlineData(0, 3, 0)]
    [InlineData(3, 3, 1)]
    [InlineData(4, 3, 2)]
    [InlineData(25, 3, 9)]
    public async Task Batching(int records, int batchSize, int expectedRequests)
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a", options: new SyncOptions<Note> { PushBatchSize = batchSize });
        for (var i = 0; i < records; i++)
        {
            await client.Engine.WriteAsync(new Note { Id = $"n{i:D2}" });
        }

        var result = await client.Engine.PushAsync();

        Assert.Equal(records, result.Pushed);
        Assert.True(result.IsComplete);
        Assert.Equal(expectedRequests, client.Transport.PushLog.Count);
        Assert.Equal(0, await client.Engine.CountDirtyAsync());
        Assert.All(client.Transport.PushLog, r => Assert.InRange(r.Operations.Count, 1, batchSize));
    }

    [Fact(DisplayName = "T05 I08 I19: mixed accepted, conflicting and rejected rows report residual status")]
    public async Task MixedOutcomes()
    {
        var server = new InMemorySyncServerRef(new Server.InMemorySyncServer<Note>(NoteJson.ServerOptions(validator: (_, op, _) => op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null)));
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "c", Title = "server" });
        await other.Engine.SyncAsync();

        var client = new TestReplica(server, "a", new ServerWinsConflictHandler<Note>());
        await client.Engine.WriteAsync(new Note { Id = "ok", Title = "fine" });
        await client.Engine.WriteAsync(new Note { Id = "bad", Title = "bad" });
        await client.Engine.WriteAsync(new Note { Id = "c", Title = "mine" }); // same-id insert: conflicts

        var result = await client.Engine.PushAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(1, result.Rejected);
        Assert.False(result.HasRemainingWork); // the rejected record is parked, not pending
        Assert.Equal("server", (await client.RecordAsync("c")).Current.Title);
        var bad = await client.RecordAsync("bad");
        Assert.True(bad.IsDirty);
        Assert.Equal(PushErrorCodes.Forbidden, bad.Rejection!.ErrorCode);
        Assert.Equal(1, await client.Engine.CountDirtyAsync());
    }

    [Fact(DisplayName = "T57 I19: a rejected record does not block later records and is retried after a new edit")]
    public async Task RejectedRecordDoesNotStarveQueue()
    {
        var server = new InMemorySyncServerRef(new Server.InMemorySyncServer<Note>(NoteJson.ServerOptions(validator: (_, op, _) => op.Document.Title == "bad" ? PushErrorCodes.Forbidden : null)));
        var client = new TestReplica(server, "a", options: new SyncOptions<Note> { PushBatchSize = 1 });
        await client.Engine.WriteAsync(new Note { Id = "a-first", Title = "bad" }); // oldest, head of queue
        for (var i = 0; i < 3; i++)
        {
            await client.Engine.WriteAsync(new Note { Id = $"later{i}", Title = "ok" });
        }

        var first = await client.Engine.PushAsync();
        var second = await client.Engine.PushAsync();

        Assert.Equal(3, first.Pushed);
        Assert.Equal(0, second.Pushed + second.Rejected); // parked record is not resent
        Assert.Equal(4, client.Transport.PushLog.Count);

        await client.Engine.WriteAsync(new Note { Id = "a-first", Title = "fixed" });
        var third = await client.Engine.PushAsync();
        Assert.Equal(1, third.Pushed);
        Assert.Null((await client.RecordAsync("a-first")).Rejection);
    }

    [Fact(DisplayName = "T15 I09: a delayed old response cannot regress a newer accepted operation")]
    public async Task StaleResponseIsIgnored()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v1" });
        PushResult<Note>? captured = null;
        client.Transport.AfterPush = r => { captured = r; return Task.FromResult(r); };
        await client.Engine.PushAsync();
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v2" });
        await client.Engine.PushAsync();
        var before = await client.RecordAsync("n1");

        // Replay the first (now stale) response into a third push.
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v3" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>([.. r.Outcomes, .. captured!.Outcomes]));
        await Assert.ThrowsAsync<SyncProtocolException>(() => client.Engine.PushAsync());

        var after = await client.RecordAsync("n1");
        Assert.Equal(before.BaseVersion, after.BaseVersion);
        Assert.Equal("v3", after.Current.Title);
        Assert.True(after.IsDirty);
    }

    [Fact(DisplayName = "T11 T15 I03 I09: a replayed acknowledgement does not hide a newer version pulled while dirty")]
    public async Task ReplayedAcknowledgementAdoptsNewerObservedVersion()
    {
        // Found by RandomizedConvergenceTests before the fix: the pull skipped v2 (record dirty) and moved
        // the checkpoint past it, then the retried push received the replayed v1 acknowledgement.
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        var other = new TestReplica(server, "b");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "mine" });
        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());

        await other.Engine.SyncAsync();
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await other.Engine.SyncAsync();

        await client.Engine.PullAsync();
        Assert.Equal(server.Server.GetVersion("n1"), (await client.RecordAsync("n1")).ObservedVersion);
        var retry = await client.Engine.PushAsync();

        Assert.True(retry.IsComplete);
        var record = await client.RecordAsync("n1");
        Assert.Equal("theirs", record.Current.Title);
        Assert.False(record.IsDirty);
        Assert.Equal(server.Server.GetVersion("n1"), record.BaseVersion);
        Assert.Null(record.Observed);
    }

    [Fact(DisplayName = "I09: an outcome for an unknown operation is rejected before anything is applied")]
    public async Task UnknownOutcomeIsProtocolError()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>([.. r.Outcomes, PushOutcome<Note>.Accepted("ghost", 9, new Note { Id = "zz" })]));

        await Assert.ThrowsAsync<SyncProtocolException>(() => client.Engine.PushAsync());

        var record = await client.RecordAsync("n1");
        Assert.True(record.IsDirty);
        Assert.NotNull(record.Pending); // resent with the same id next time
    }

    [Fact(DisplayName = "I09: a duplicated outcome is rejected")]
    public async Task DuplicatedOutcomeIsProtocolError()
    {
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>([.. r.Outcomes, .. r.Outcomes]));

        await Assert.ThrowsAsync<SyncProtocolException>(() => client.Engine.PushAsync());
        Assert.True((await client.RecordAsync("n1")).IsDirty);
    }

    [Fact(DisplayName = "I09: an accepted outcome for the wrong document is rejected")]
    public async Task MismatchedDocumentIsProtocolError()
    {
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>(
            [PushOutcome<Note>.Accepted(r.Outcomes[0].OperationId, 5, new Note { Id = "other" })]));

        await Assert.ThrowsAsync<SyncProtocolException>(() => client.Engine.PushAsync());
        Assert.True((await client.RecordAsync("n1")).IsDirty);
    }

    [Fact(DisplayName = "I09 I15: an omitted outcome leaves the operation pending and is not marked clean")]
    public async Task OmittedOutcomeIsDeferred()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        await client.Engine.WriteAsync(new Note { Id = "n2" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>([r.Outcomes[0]]));

        var result = await client.Engine.PushAsync();

        Assert.Equal(1, result.Pushed);
        Assert.Equal(1, result.Deferred);
        Assert.True(result.HasRemainingWork);
        Assert.False(result.IsComplete);
        var n2 = await client.RecordAsync("n2");
        Assert.True(n2.IsDirty);
        var pendingId = n2.Pending!.OperationId;

        client.Transport.AfterPush = null;
        var retry = await client.Engine.PushAsync();
        Assert.Equal(1, retry.Pushed);
        Assert.Equal(pendingId, client.Transport.PushLog[^1].Operations.Single().OperationId);
    }

    [Fact(DisplayName = "I08: a retryable outcome keeps the operation pending")]
    public async Task RetryLaterKeepsOperation()
    {
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        client.Transport.AfterPush = r => Task.FromResult(new PushResult<Note>(
            [PushOutcome<Note>.RetryLater(r.Outcomes[0].OperationId, PushErrorCodes.Unavailable)]));

        var result = await client.Engine.PushAsync();

        Assert.Equal(1, result.Deferred);
        Assert.Single(client.Transport.PushLog); // not retried in a tight loop
        Assert.NotNull((await client.RecordAsync("n1")).Pending);
    }

    [Fact(DisplayName = "T09 I08 I11: a document that keeps conflicting is bounded and does not starve others")]
    public async Task ConflictBudget()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "hot", Title = "0" });
        await other.Engine.SyncAsync();

        var client = new TestReplica(server, "a", new ClientWinsConflictHandler<Note>(), new SyncOptions<Note> { MaxConflictRetries = 2, PushBatchSize = 1 });
        await client.Engine.SyncAsync();
        await client.Engine.WriteAsync(new Note { Id = "hot", Title = "mine" });
        await client.Engine.WriteAsync(new Note { Id = "zz-cold", Title = "cold" });

        // Another writer commits before every push from the client.
        var n = 0;
        client.Transport.BeforePush = async request =>
        {
            if (request.Operations.Any(o => o.DocumentId == "hot"))
            {
                await other.Engine.WriteAsync(new Note { Id = "hot", Title = $"other {++n}" });
                await other.Engine.PushAsync();
            }
        };
        var result = await client.Engine.PushAsync();

        Assert.Equal(2, result.Conflicts);
        Assert.Equal(1, result.Deferred);
        Assert.True(result.HasRemainingWork);
        Assert.Equal("cold", server.Get("zz-cold").Title);
        Assert.Equal("mine", (await client.RecordAsync("hot")).Current.Title); // client-wins intent preserved
    }

    [Fact(DisplayName = "T05 I16: a conflict that settles on the final retry is not reported as deferred")]
    public async Task SettledConflictAtBudgetIsComplete()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await other.Engine.SyncAsync();
        var client = new TestReplica(server, "a", new ServerWinsConflictHandler<Note>(), new SyncOptions<Note> { MaxConflictRetries = 1 });
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "mine" });

        var result = await client.Engine.PushAsync();

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Deferred);
        Assert.True(result.IsComplete);
    }

    [Fact(DisplayName = "T09 I02: an edit made while a conflict is being resolved is not lost")]
    public async Task EditDuringConflictResolution()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "base" });
        await other.Engine.SyncAsync();
        var forksSeen = new List<string>();
        var handler = new DelegateConflictHandler<Note>(c =>
        {
            forksSeen.Add(c.Fork.Title);
            return ConflictResolution<Note>.AcceptMaster();
        });
        var client = new TestReplica(server, "a", handler);
        await client.Engine.SyncAsync();

        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "theirs" });
        await other.Engine.SyncAsync();
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "mine 1" });

        // Commit a newer local edit after the resolution for "mine 1" was computed but before it commits
        // (single-record commits: 1 = prepare operation, 2 = conflict resolution).
        var calls = 0;
        client.Store.BeforeUpdate = async (_, updates, _) =>
        {
            if (updates.Count == 1 && ++calls == 2)
            {
                client.Store.BeforeUpdate = null;
                await client.Engine.WriteAsync(new Note { Id = "n1", Title = "mine 2" });
            }
        };
        await client.Engine.PushAsync();

        // The resolution for "mine 1" was not applied to "mine 2"; the policy was re-run on "mine 2".
        Assert.Equal(["mine 1", "mine 2"], forksSeen);
        var record = await client.RecordAsync("n1");
        Assert.Equal("theirs", record.Current.Title);
        Assert.False(record.IsDirty);
    }

    [Fact(DisplayName = "I11: a merge resolution is re-stamped and pushed against the current version")]
    public async Task MergeResolutionIsPushed()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "t", Body = "base" });
        await other.Engine.SyncAsync();
        var merger = new DelegateConflictHandler<Note>(c =>
        {
            c.Fork.Body = $"{c.RealMaster.Body}+{c.Fork.Body}";
            return ConflictResolution<Note>.Resolve(c.Fork);
        });
        var client = new TestReplica(server, "a", merger);
        await client.Engine.SyncAsync();

        await other.Engine.WriteAsync(new Note { Id = "n1", Body = "theirs" });
        await other.Engine.SyncAsync();
        var local = await client.Engine.WriteAsync(new Note { Id = "n1", Body = "mine" });
        var result = await client.Engine.SyncAsync();

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(1, result.Pushed);
        Assert.Equal("theirs+mine", server.Get("n1").Body);
        Assert.True(server.Get("n1").UpdatedAt > local.UpdatedAt);
        Assert.False((await client.RecordAsync("n1")).IsDirty);
    }

    [Fact(DisplayName = "I02: WriteAsync does not mutate the caller's object and returns a receipt")]
    public async Task WriteDoesNotMutateCaller()
    {
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a");
        var note = new Note { Id = "n1", Title = "x" };

        var receipt = await client.Engine.WriteAsync(note);
        note.Title = "mutated later";

        Assert.Equal(Clocks.HlcTimestamp.MinValue, note.UpdatedAt);
        Assert.Equal(1, receipt.LocalRevision);
        Assert.Equal("x", (await client.RecordAsync("n1")).Current.Title);
    }

    [Theory(DisplayName = "T59 I08: adversarial ids are rejected locally")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AdversarialIdsRejected(int index)
    {
        // Built in code: xUnit replaces unpaired surrogates in serialized inline data.
        string[] ids = ["", "a\u0000b", "\ud800", "x\udc00", new string('x', SyncIds.MaxLength + 1)];
        var client = new TestReplica(InMemorySyncServerRef.Create(), "a");
        await Assert.ThrowsAsync<ArgumentException>(() => client.Engine.WriteAsync(new Note { Id = ids[index] }));
    }

    [Fact(DisplayName = "T59 I08: non-ASCII and maximum-length ids round-trip")]
    public async Task NonAsciiIdsRoundTrip()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        var ids = new[] { "ノート-😀", new string('x', SyncIds.MaxLength) };
        foreach (var id in ids)
        {
            await client.Engine.WriteAsync(new Note { Id = id });
        }

        await client.Engine.SyncAsync();

        var reader = new TestReplica(server, "b");
        await reader.Engine.SyncAsync();
        Assert.Equal(ids.Order(StringComparer.Ordinal), (await reader.Engine.QueryAsync()).Select(n => n.Id).Order(StringComparer.Ordinal));
    }

    [Fact(DisplayName = "T59 I08: the server refuses oversized push requests")]
    public void OversizedPushRefused()
    {
        var server = new Server.InMemorySyncServer<Note>(NoteJson.ServerOptions(maxOperationsPerPush: 2));
        var ops = Enumerable.Range(0, 3).Select(i => new PushOperation<Note>($"op{i}", $"n{i}", null, new Note { Id = $"n{i}" })).ToList();

        var error = Assert.Throws<SyncTransportException>(() => server.Push(new PushRequest<Note>(ops)));
        Assert.Equal(SyncErrorCodes.PayloadTooLarge, error.ErrorCode);
        Assert.Empty(server.Snapshot());
    }
}
