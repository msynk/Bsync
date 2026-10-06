using System.Security.Claims;
using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>SQL Server-specific behaviour: concurrency across instances, delayed commits, durability, restore, hints, schema.</summary>
public sealed class SqlServerAuthorityTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await SqlServerDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static Note Stamped(string id, string title = "") =>
        new() { Id = id, Title = title, UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") };

    private static TestReplica Replica(ISyncAuthority<Note> authority, string node, IConflictHandler<Note>? handler = null, SyncCallContext? context = null, SyncOptions<Note>? options = null) =>
        new(InMemorySyncServerRef.Create(), node, handler, options, SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(authority, context));

    [Fact(DisplayName = "D5 F18: the retention service reads every feed's head and purges each scope by age")]
    public async Task RetentionPurgesEachScope()
    {
        await using var authority = await _database.AuthorityAsync();
        var tenant = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test")), "tenant-a");
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var retention = new SyncRetention(new SyncRetentionOptions { MaxOfflineHorizon = TimeSpan.FromDays(45), TimeProvider = time }, [authority]);
        foreach (var context in new[] { SyncCallContext.Anonymous, tenant })
        {
            var writer = Replica(authority, $"w-{context.Scope}", context: context);
            await writer.Engine.WriteAsync(Stamped("gone"));
            await writer.Engine.SyncAsync();
            await writer.Engine.DeleteAsync("gone");
            await writer.Engine.SyncAsync();
        }

        var heads = await authority.GetFeedHeadsAsync();
        var first = await retention.RunOnceAsync();
        time.Advance(TimeSpan.FromDays(44));
        var early = await retention.RunOnceAsync();
        time.Advance(TimeSpan.FromDays(2));
        var late = await retention.RunOnceAsync();

        Assert.Equal(["default", "tenant-a"], heads.Keys.Order());
        Assert.All(heads.Values, head => Assert.Equal(2, head));
        Assert.Equal((0, 0), first);
        Assert.Equal((0, 0), early);
        Assert.Equal((2, 4), late); // one tombstone and two receipts per scope
        var pull = await authority.PullAsync(tenant, new PullRequest(Checkpoint.Start, 10));
        Assert.Empty(pull.Changes);
        Assert.Equal(2, pull.RetentionHorizon);
    }

    [Fact(DisplayName = "T24 T30 I05 I06: concurrent writers on two instances never lose updates, and a concurrent reader never skips a version")]
    public async Task ConcurrentInstancesKeepCommittedPrefix()
    {
        await using var one = await _database.AuthorityAsync();
        await using var two = await _database.AuthorityAsync(connectionString: _database.SecondProcessConnectionString);
        var ids = Enumerable.Range(0, 5).Select(i => $"doc{i}").ToArray();
        var writers = Enumerable.Range(0, 12)
            .Select(i => Replica(i % 2 == 0 ? one : two, $"w{i}", new LastWriteWinsConflictHandler<Note>(), options: new SyncOptions<Note> { PushBatchSize = 3 }))
            .ToList();

        // A reader pulls continuously while the writers commit; it must see versions in increasing order, never skipping one.
        var seen = new List<long>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            var checkpoint = Checkpoint.Start;
            while (true)
            {
                // Read the flag before pulling: the last drain must start after every write committed.
                var stopping = stop.IsCancellationRequested;
                var page = await two.PullAsync(SyncCallContext.Anonymous, new PullRequest(checkpoint, 7));
                seen.AddRange(page.Changes.Select(c => c.Version));
                checkpoint = page.Checkpoint;
                if (!page.HasMore)
                {
                    if (stopping)
                    {
                        return;
                    }

                    await Task.Delay(5);
                }
            }
        });

        await Task.WhenAll(writers.Select((writer, w) => Task.Run(async () =>
        {
            var random = new Random(w);
            for (var step = 0; step < 25; step++)
            {
                await writer.Engine.WriteAsync(new Note { Id = ids[random.Next(ids.Length)], Title = $"w{w}-s{step}" });
                if (random.Next(3) == 0)
                {
                    await writer.Engine.SyncAsync();
                }
            }
        })));

        for (var round = 0; round < 10 && (await Task.WhenAll(writers.Select(w => w.Engine.SyncAsync()))).Any(r => !r.IsComplete || r.Pushed > 0 || r.Conflicts > 0); round++)
        {
        }

        await Task.WhenAll(writers.Select(w => w.Engine.SyncAsync()));
        stop.Cancel();
        await reader;

        var highest = await one.GetHighestVersionAsync("default");
        Assert.True(seen.SequenceEqual(seen.Order()), "versions arrived out of order");
        var documents = await one.ListAsync(SyncCallContext.Anonymous, 100);
        Assert.Equal(5, documents.Count);
        Assert.Subset(seen.ToHashSet(), documents.Select(d => d.Version).ToHashSet());
        Assert.Equal(highest, seen.Max());

        var expected = documents.ToDictionary(d => d.Document.Id, d => $"{d.Document.Title}|{d.Document.UpdatedAt}");
        foreach (var writer in writers)
        {
            Assert.Equal(0, await writer.Engine.CountDirtyAsync());
            Assert.Equal(expected, (await writer.Engine.QueryAsync()).ToDictionary(n => n.Id, n => $"{n.Title}|{n.UpdatedAt}"));
        }

        var sent = writers.SelectMany(w => w.Transport.PushLog).SelectMany(p => p.Operations).Select(o => o.OperationId).Distinct().Count();
        Assert.Equal(sent, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts")); // every operation decided exactly once
    }

    [Theory(DisplayName = "T27 T28 I06: a slower transaction holding a lower version is never skipped by a reader, whether it commits or rolls back")]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task DelayedCommitIsNeverSkipped(bool commit, bool readCommittedSnapshot)
    {
        await using var database = await SqlServerDatabase.CreateAsync(readCommittedSnapshot);
        await using var authority = await database.AuthorityAsync();
        await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("seed", "seed", null, Stamped("seed"))]));
        var afterSeed = (await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(Checkpoint.Start, 10))).Checkpoint;

        // A slow writer (another process, say) has taken version 2 in its own transaction and not committed yet.
        await using var slow = await database.OpenAsync();
        await using var transaction = slow.BeginTransaction();
        var taken = await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("slow", "slow", null, Stamped("slow"))]), transaction);
        Assert.Equal(2, taken.Outcomes.Single().Version);

        var fast = authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("fast", "fast", null, Stamped("fast"))]));
        var during = authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(afterSeed, 10));
        await Task.Delay(500);
        Assert.False(fast.IsCompleted); // the faster writer waits for the feed instead of committing version 3 first
        if (readCommittedSnapshot)
        {
            Assert.Empty((await during).Changes); // the reader sees the committed prefix without waiting
        }
        else
        {
            Assert.False(during.IsCompleted); // locking READ COMMITTED: the reader waits for the undecided version
        }

        if (commit)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }

        var accepted = (await fast).Outcomes.Single();
        Assert.Equal(commit ? 3 : 2, accepted.Version);
        var first = await during;
        var after = await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(first.Checkpoint, 10));
        var delivered = first.Changes.Concat(after.Changes).Select(c => c.Document.Id).ToList();
        Assert.Equal(commit ? ["slow", "fast"] : ["fast"], delivered);
    }

    [Fact(DisplayName = "T17 I01 I04: data, receipts and checkpoints survive a restart of the authority")]
    public async Task SurvivesRestart()
    {
        var first = await _database.AuthorityAsync();
        var client = Replica(first, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "durable" });
        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());
        await first.DisposeAsync();

        await using var restarted = await _database.AuthorityAsync(connectionString: _database.SecondProcessConnectionString);
        var afterRestart = new TestReplica(InMemorySyncServerRef.Create(), "c", physicalClock: SystemPhysicalClock.Instance, store: client.Store.Inner, transport: _ => new InProcessTransport<Note>(restarted));
        var result = await afterRestart.Engine.SyncAsync();
        var again = Replica(restarted, "c2");
        await again.Engine.SyncAsync();

        Assert.True(result.IsComplete);
        Assert.False(result.ResetPerformed);
        Assert.Single(afterRestart.Transport.PushLog.Single().Operations); // the lost response is replayed from the receipt
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));
        Assert.Equal("durable", (await again.RecordAsync("n1")).Current.Title);
    }

    [Fact(DisplayName = "T35 I14: after a restore, a new epoch with a version floor resets replicas and keeps their pending edits")]
    public async Task RestoreDrill()
    {
        await using var authority = await _database.AuthorityAsync();
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "before", Title = "in the backup" });
        await client.Engine.SyncAsync();
        await _database.ExecuteAsync("""
            CREATE SCHEMA restored;
            """);
        await _database.ExecuteAsync("""
            SELECT * INTO restored.feeds FROM bsync.feeds;
            SELECT * INTO restored.documents FROM bsync.documents;
            SELECT * INTO restored.receipts FROM bsync.receipts;
            """);
        await client.Engine.WriteAsync(new Note { Id = "after", Title = "lost by the restore" });
        await client.Engine.SyncAsync();
        var floor = await authority.GetHighestVersionAsync("default");
        await client.Engine.WriteAsync(new Note { Id = "pending", Title = "offline during the restore" });

        // The tables are restored from the backup; the operator starts a new epoch above everything issued.
        await _database.ExecuteAsync("""
            DELETE FROM bsync.receipts; DELETE FROM bsync.documents; DELETE FROM bsync.feeds;
            SET IDENTITY_INSERT bsync.feeds ON;
            INSERT INTO bsync.feeds (feed_id, collection_key, scope_key, collection, scope, sequence, purged_through) SELECT * FROM restored.feeds;
            SET IDENTITY_INSERT bsync.feeds OFF;
            INSERT INTO bsync.documents SELECT * FROM restored.documents;
            INSERT INTO bsync.receipts SELECT * FROM restored.receipts;
            """);
        await authority.BeginNewEpochAsync(floor);
        var result = await client.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal(1, result.MissingAfterReset);
        Assert.True((await client.RecordAsync("after")).MissingAfterReset);
        var server = await authority.ListAsync(SyncCallContext.Anonymous, 10);
        Assert.Equal(["before", "pending"], server.Select(d => d.Document.Id).Order());
        Assert.True(server.Single(d => d.Document.Id == "pending").Version > floor); // no version reused
    }

    [Fact(DisplayName = "T35 I14: a feed first written after a restore starts above the version floor")]
    public async Task NewFeedStartsAboveFloor()
    {
        await using var authority = await _database.AuthorityAsync();
        await authority.BeginNewEpochAsync(500);
        var tenant = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "u")], "test")), "tenant-new");

        var outcome = (await authority.PushAsync(tenant, new PushRequest<Note>([new PushOperation<Note>("o1", "a", null, Stamped("a"))]))).Outcomes.Single();

        Assert.Equal(501, outcome.Version);
    }

    [Fact(DisplayName = "I13: a commit on one instance is announced to subscribers of another instance by polling")]
    public async Task HintsAcrossInstances()
    {
        await using var listening = await _database.AuthorityAsync(
            connectionString: _database.SecondProcessConnectionString,
            configure: o => new() { ConnectionString = o.ConnectionString, DocumentType = o.DocumentType, Collection = o.Collection, CommitPollInterval = TimeSpan.FromMilliseconds(100) });
        var heard = new TaskCompletionSource<AuthorityCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        listening.Committed += commit => heard.TrySetResult(commit);
        await Task.Delay(300); // let the poller read the initial heads
        await using var writing = await _database.AuthorityAsync();

        var writer = Replica(writing, "w");
        await writer.Engine.WriteAsync(new Note { Id = "n1" });
        await writer.Engine.SyncAsync();

        var commit = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("default", commit.Scope);
    }

    [Fact(DisplayName = "I17: a database with a newer schema is refused and left untouched")]
    public async Task NewerSchemaRefused()
    {
        await using (await _database.AuthorityAsync())
        {
        }

        await _database.ExecuteAsync("UPDATE bsync.meta SET value = '99' WHERE [key] = 'schema_version'");

        var error = await Assert.ThrowsAsync<SqlServerSchemaException>(() => _database.AuthorityAsync());
        Assert.Contains("99", error.Message);
        Assert.Equal(99, await _database.ScalarAsync("SELECT CAST(value AS int) FROM bsync.meta WHERE [key] = 'schema_version'"));
    }

    [Fact(DisplayName = "I17: several processes creating the schema at once create it once")]
    public async Task ConcurrentSchemaCreation()
    {
        var authorities = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => _database.AuthorityAsync(collection: $"c{i}")));

        Assert.Single(authorities.Select(a => a.Epoch).Distinct());
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM sys.tables WHERE name = 'documents' AND SCHEMA_NAME(schema_id) = 'bsync'"));
    }

    [Fact(DisplayName = "I08: a database outage is reported as an error and nothing is half-committed")]
    public async Task OutageCommitsNothing()
    {
        await using var authority = await _database.AuthorityAsync();
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        await _database.ExecuteAsync("EXEC sp_rename 'bsync.receipts', 'receipts_away'");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.Engine.SyncAsync());
        await _database.ExecuteAsync("EXEC sp_rename 'bsync.receipts_away', 'receipts'");
        var result = await client.Engine.SyncAsync();

        Assert.NotNull(failure);
        Assert.True(result.IsComplete);
        Assert.Single(await authority.ListAsync(SyncCallContext.Anonymous, 10));
        Assert.Equal(1, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));
    }

    [Fact(DisplayName = "I05 I19: a dependency group is applied all-or-nothing and its rolled-back version is not skipped")]
    public async Task GroupsAreAtomic()
    {
        await using var authority = await _database.AuthorityAsync();
        await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("seed", "taken", null, Stamped("taken", "exists"))]));

        var aborted = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>(
        [
            new PushOperation<Note>("g1", "fresh", null, Stamped("fresh", "x")) { Group = "grp", GroupSize = 2 },
            new PushOperation<Note>("g2", "taken", null, Stamped("taken", "y")) { Group = "grp", GroupSize = 2 },
        ]))).Outcomes;

        Assert.Equal((PushOutcomeKind.RetryLater, PushErrorCodes.GroupAborted), (aborted[0].Kind, aborted[0].ErrorCode));
        Assert.Equal(PushOutcomeKind.Conflict, aborted[1].Kind);
        Assert.Null(await authority.GetAsync(SyncCallContext.Anonymous, "fresh"));
        Assert.Equal(2, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));
        Assert.Equal(1, await authority.GetHighestVersionAsync("default"));
    }

    [Fact(DisplayName = "I07: ids that differ only by trailing spaces or case are different documents")]
    public async Task IdsCompareExactly()
    {
        await using var authority = await _database.AuthorityAsync();

        var outcomes = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>(
        [
            new PushOperation<Note>("o1", "a", null, Stamped("a", "1")),
            new PushOperation<Note>("o2", "a ", null, Stamped("a ", "2")),
            new PushOperation<Note>("o3", "A", null, Stamped("A", "3")),
        ]))).Outcomes;

        Assert.All(outcomes, o => Assert.Equal(PushOutcomeKind.Accepted, o.Kind));
        Assert.Equal(["A", "a", "a "], (await authority.ListAsync(SyncCallContext.Anonymous, 10)).Select(d => d.Document.Id));
        Assert.Equal("2", (await authority.GetAsync(SyncCallContext.Anonymous, "a "))!.Document.Title);
    }
}
