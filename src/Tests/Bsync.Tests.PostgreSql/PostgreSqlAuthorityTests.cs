using System.Security.Claims;
using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.PostgreSql;
using Bsync.Tests.TestSupport;
using Npgsql;
using Xunit;

namespace Bsync.Tests.PostgreSql;

/// <summary>PostgreSQL-specific behaviour: concurrency across instances, durability, restore, retention, scopes, hints, schema.</summary>
public sealed class PostgreSqlAuthorityTests : IAsyncLifetime
{
    private PostgresDatabase _database = null!;

    public async Task InitializeAsync() => _database = await PostgresDatabase.CreateAsync();

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private static SyncCallContext As(string scope, string user = "u") =>
        new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, user)], "test")), scope);

    private static TestReplica Replica(ISyncAuthority<Note> authority, string node, IConflictHandler<Note>? handler = null, SyncCallContext? context = null, SyncOptions<Note>? options = null) =>
        new(InMemorySyncServerRef.Create(), node, handler, options, SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(authority, context));

    [Fact(DisplayName = "T24 I05 I06: concurrent writers on two instances never lose updates, and a concurrent reader never skips a version")]
    public async Task ConcurrentInstancesKeepCommittedPrefix()
    {
        await using var second = _database.NewDataSource();
        var one = await _database.AuthorityAsync();
        var two = await _database.AuthorityAsync(source: second);
        var ids = Enumerable.Range(0, 5).Select(i => $"doc{i}").ToArray();
        var writers = Enumerable.Range(0, 12)
            .Select(i => Replica(i % 2 == 0 ? one : two, $"w{i}", new LastWriteWinsConflictHandler<Note>(), options: new SyncOptions<Note> { PushBatchSize = 3 }))
            .ToList();

        // A reader pulls continuously while the writers commit; it must see every version exactly once, in order.
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

        // Every version of the feed was delivered, in increasing order; the latest versions are the documents' versions.
        var highest = await one.GetHighestVersionAsync("default");
        Assert.True(seen.SequenceEqual(seen.Order()), "versions arrived out of order");
        var documents = await one.ListAsync(SyncCallContext.Anonymous, 100);
        Assert.Equal(5, documents.Count);
        Assert.Subset(seen.ToHashSet(), documents.Select(d => d.Version).ToHashSet());
        Assert.Equal(highest, seen.Max());

        // Last-writer-wins over authoring time, whichever instance each writer used, and every replica converged.
        var expected = documents.ToDictionary(d => d.Document.Id, d => $"{d.Document.Title}|{d.Document.UpdatedAt}");
        foreach (var writer in writers)
        {
            Assert.Equal(0, await writer.Engine.CountDirtyAsync());
            Assert.Equal(expected, (await writer.Engine.QueryAsync()).ToDictionary(n => n.Id, n => $"{n.Title}|{n.UpdatedAt}"));
        }

        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await using var receipts = new NpgsqlCommand("SELECT count(*) FROM bs_receipts", connection);
        var sent = writers.SelectMany(w => w.Transport.PushLog).SelectMany(p => p.Operations).Select(o => o.OperationId).Distinct().Count();
        Assert.Equal((long)sent, (long)(await receipts.ExecuteScalarAsync())!); // every operation decided exactly once
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

        await using var fresh = _database.NewDataSource();
        var restarted = await _database.AuthorityAsync(source: fresh);
        var again = Replica(restarted, "c2");
        var afterRestart = new TestReplica(InMemorySyncServerRef.Create(), "c", physicalClock: SystemPhysicalClock.Instance, store: client.Store.Inner, transport: _ => new InProcessTransport<Note>(restarted));
        var result = await afterRestart.Engine.SyncAsync();

        Assert.True(result.IsComplete);
        Assert.False(result.ResetPerformed);
        Assert.Single(afterRestart.Transport.PushLog.Single().Operations); // the lost response is replayed from the receipt
        await again.Engine.SyncAsync();
        Assert.Equal("durable", (await again.RecordAsync("n1")).Current.Title);
    }

    [Fact(DisplayName = "T35 I14: after a restore, a new epoch with a version floor resets replicas and keeps their pending edits")]
    public async Task RestoreDrill()
    {
        var authority = await _database.AuthorityAsync();
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "before", Title = "in the backup" });
        await client.Engine.SyncAsync();
        await ExecuteAsync("CREATE SCHEMA backup; CREATE TABLE backup.feeds AS TABLE bs_feeds; CREATE TABLE backup.documents AS TABLE bs_documents; CREATE TABLE backup.receipts AS TABLE bs_receipts;");
        await client.Engine.WriteAsync(new Note { Id = "after", Title = "lost by the restore" });
        await client.Engine.SyncAsync();
        var floor = await authority.GetHighestVersionAsync("default");
        await client.Engine.WriteAsync(new Note { Id = "pending", Title = "offline during the restore" });

        // The database is restored from the backup; the operator starts a new epoch above everything issued.
        await ExecuteAsync("TRUNCATE bs_feeds, bs_documents, bs_receipts; INSERT INTO bs_feeds SELECT * FROM backup.feeds; INSERT INTO bs_documents SELECT * FROM backup.documents; INSERT INTO bs_receipts SELECT * FROM backup.receipts;");
        await authority.BeginNewEpochAsync(floor);
        var result = await client.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal(1, result.MissingAfterReset);
        Assert.True((await client.RecordAsync("after")).MissingAfterReset);
        var server = await authority.ListAsync(SyncCallContext.Anonymous, 10);
        Assert.Equal(["before", "pending"], server.Select(d => d.Document.Id).Order());
        Assert.True(server.Single(d => d.Document.Id == "pending").Version > floor); // no version reused
    }

    [Fact(DisplayName = "T32 T33 I10: purged tombstones expire old checkpoints and refuse resurrection; purged receipts never double-apply")]
    public async Task Retention()
    {
        var authority = await _database.AuthorityAsync();
        var writer = Replica(authority, "w");
        var offline = Replica(authority, "o");
        await writer.Engine.WriteAsync(new Note { Id = "gone", Title = "x" });
        await writer.Engine.WriteAsync(new Note { Id = "kept", Title = "x" });
        await writer.Engine.SyncAsync();
        await offline.Engine.SyncAsync();
        await writer.Engine.DeleteAsync("gone");
        await writer.Engine.SyncAsync();
        await offline.Engine.WriteAsync(new Note { Id = "gone", Title = "edited offline" });

        Assert.Equal(1, await authority.PurgeTombstonesAsync("default", await authority.GetHighestVersionAsync("default")));
        var result = await offline.Engine.SyncAsync();

        Assert.True(result.ResetPerformed);
        Assert.Equal(PushErrorCodes.BaseExpired, (await offline.RecordAsync("gone")).Rejection!.ErrorCode);
        Assert.Null(await authority.GetAsync(SyncCallContext.Anonymous, "gone"));
        await offline.Engine.RetryRejectedAsync("gone");
        await offline.Engine.SyncAsync();
        Assert.Equal("edited offline", (await authority.GetAsync(SyncCallContext.Anonymous, "gone"))!.Document.Title); // recreated explicitly

        var accepted = await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("op-r", "kept", 2, new Note { Id = "kept", Title = "y", UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") })]));
        Assert.Equal(PushOutcomeKind.Accepted, accepted.Outcomes[0].Kind);
        Assert.True(await authority.PurgeReceiptsAsync("default", long.MaxValue) > 0);
        var replay = await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("op-r", "kept", 2, new Note { Id = "kept", Title = "y", UpdatedAt = new HlcTimestamp(1, 0, "n") })]));
        Assert.Equal(PushOutcomeKind.Conflict, replay.Outcomes[0].Kind);
    }

    [Fact(DisplayName = "I07: scopes are isolated feeds in one database; a changed scope fingerprint resets; unreadable documents are withheld")]
    public async Task Scopes()
    {
        var grants = new HashSet<string> { "open" };
        var authority = await _database.AuthorityAsync(configure: o => new PostgreSqlSyncAuthorityOptions<Note>
        {
            DataSource = o.DataSource,
            DocumentType = o.DocumentType,
            Collection = o.Collection,
            PhysicalClock = o.PhysicalClock,
            CanRead = (context, note) => context.Scope != "tenant-b" || grants.Contains(note.Id),
            ScopeFingerprint = context => string.Join(",", grants.Order()),
        });
        var a = Replica(authority, "a", context: As("tenant-a"));
        var b = Replica(authority, "b", context: As("tenant-b"));
        await a.Engine.WriteAsync(new Note { Id = "open", Title = "tenant a's" });
        await a.Engine.SyncAsync();
        await b.Engine.WriteAsync(new Note { Id = "open", Title = "tenant b's" });
        await b.Engine.WriteAsync(new Note { Id = "secret", Title = "b" });
        await b.Engine.SyncAsync();

        Assert.Equal("tenant a's", (await authority.GetAsync(As("tenant-a"), "open"))!.Document.Title);
        Assert.Equal("tenant b's", (await authority.GetAsync(As("tenant-b"), "open"))!.Document.Title);
        Assert.Null(await authority.GetAsync(As("tenant-b"), "secret"));
        var checkpointOfA = (await authority.PullAsync(As("tenant-a"), new PullRequest(Checkpoint.Start, 10))).Checkpoint;
        await Assert.ThrowsAsync<SyncResetRequiredException>(() => authority.PullAsync(As("tenant-b"), new PullRequest(checkpointOfA, 10)));

        grants.Add("secret");
        var reset = await b.Engine.SyncAsync();
        Assert.True(reset.ResetPerformed);
        Assert.NotNull(await authority.GetAsync(As("tenant-b"), "secret"));
    }

    [Fact(DisplayName = "I13: a commit on one instance is announced to subscribers of another instance")]
    public async Task HintsAcrossInstances()
    {
        await using var second = _database.NewDataSource();
        await using var listening = await _database.AuthorityAsync(source: second);
        var heard = new TaskCompletionSource<AuthorityCommit>(TaskCreationOptions.RunContinuationsAsynchronously);
        listening.Committed += commit => heard.TrySetResult(commit);
        await Task.Delay(500); // let the listener connect
        var writing = await _database.AuthorityAsync();

        await Replica(writing, "w").Engine.WriteAsync(new Note { Id = "n1" });
        await Replica(writing, "w").Engine.SyncAsync();
        var writer = Replica(writing, "w2");
        await writer.Engine.WriteAsync(new Note { Id = "n2" });
        await writer.Engine.SyncAsync();

        var commit = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("default", commit.Scope);
    }

    [Fact(DisplayName = "I17: a database with a newer schema is refused and left untouched")]
    public async Task NewerSchemaRefused()
    {
        await _database.AuthorityAsync();
        await ExecuteAsync("UPDATE bs_meta SET value = '99' WHERE key = 'schema_version'");

        var error = await Assert.ThrowsAsync<PostgreSqlSchemaException>(() => _database.AuthorityAsync());
        Assert.Contains("99", error.Message);
    }

    [Fact(DisplayName = "I08: a database outage is reported as a retryable error and nothing is half-committed")]
    public async Task OutageIsRetryable()
    {
        var authority = await _database.AuthorityAsync();
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1" });
        await ExecuteAsync("ALTER TABLE bs_receipts RENAME TO bs_receipts_away");

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => client.Engine.SyncAsync());
        await ExecuteAsync("ALTER TABLE bs_receipts_away RENAME TO bs_receipts");
        var result = await client.Engine.SyncAsync();

        Assert.NotNull(failure);
        Assert.True(result.IsComplete);
        Assert.Single(await authority.ListAsync(SyncCallContext.Anonymous, 10));
        Assert.Equal(1L, await ScalarAsync("SELECT count(*) FROM bs_receipts"));
    }

    [Fact(DisplayName = "I05 I19: PostgreSQL applies a dependency group all-or-nothing and rolls back the members' writes and receipts")]
    public async Task GroupsAreAtomic()
    {
        var authority = await _database.AuthorityAsync();
        Note Stamped(string id, string title) => new() { Id = id, Title = title, UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") };
        await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("seed", "taken", null, Stamped("taken", "exists"))]));

        var aborted = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>(
        [
            new PushOperation<Note>("g1", "fresh", null, Stamped("fresh", "x")) { Group = "grp", GroupSize = 2 },
            new PushOperation<Note>("g2", "taken", null, Stamped("taken", "y")) { Group = "grp", GroupSize = 2 },
        ]))).Outcomes;

        Assert.Equal((PushOutcomeKind.RetryLater, PushErrorCodes.GroupAborted), (aborted[0].Kind, aborted[0].ErrorCode));
        Assert.Equal(PushOutcomeKind.Conflict, aborted[1].Kind);
        Assert.Null(await authority.GetAsync(SyncCallContext.Anonymous, "fresh"));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM bs_receipts"));
        Assert.Equal(1L, await authority.GetHighestVersionAsync("default")); // the rolled-back version is not skipped

        var client = Replica(authority, "client", new ClientWinsConflictHandler<Note>());
        await client.Engine.SyncAsync();
        await client.Engine.WriteGroupAsync([new Note { Id = "order", Title = "o" }, new Note { Id = "line", Title = "l" }]);
        Assert.True((await client.Engine.SyncAsync()).IsComplete);
        var versions = (await authority.ListAsync(SyncCallContext.Anonymous, 10)).Where(d => d.Document.Id is "order" or "line").Select(d => d.Version).Order().ToList();
        Assert.Equal(versions[0] + 1, versions[1]);
    }

    [Theory(DisplayName = "T27 T28 I06: a slower transaction holding a lower version is never skipped by a reader, whether it commits or rolls back")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DelayedCommitIsNeverSkipped(bool commit)
    {
        var authority = await _database.AuthorityAsync();
        Note Stamped(string id) => new() { Id = id, UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") };
        await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("seed", "seed", null, Stamped("seed"))]));
        var afterSeed = (await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(Checkpoint.Start, 10))).Checkpoint;

        // A slow writer (another process, say) has taken version 2 and not committed yet.
        await using var slow = await _database.DataSource.OpenConnectionAsync();
        await using var transaction = await slow.BeginTransactionAsync();
        await using (var take = new NpgsqlCommand(
            "SELECT sequence FROM bs_feeds WHERE collection = 'notes' AND scope = 'default' FOR UPDATE; " +
            "INSERT INTO bs_documents (collection, scope, id, id_key, version, deleted, document) VALUES ('notes', 'default', 'slow', '\x0073006c006f0077'::bytea, 2, false, '{\"Id\":\"slow\",\"UpdatedAt\":\"000000000001000:000000:n\",\"Deleted\":false,\"Title\":\"\",\"Body\":\"\"}'); " +
            "UPDATE bs_feeds SET sequence = 2 WHERE collection = 'notes' AND scope = 'default';", slow, transaction))
        {
            await take.ExecuteNonQueryAsync();
        }

        var fast = authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("fast", "fast", null, Stamped("fast"))]));
        await Task.Delay(500);
        Assert.False(fast.IsCompleted); // the faster writer waits for the feed instead of committing version 3 first
        var during = await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(afterSeed, 10));
        Assert.Empty(during.Changes);

        if (commit)
        {
            await transaction.CommitAsync();
        }
        else
        {
            await transaction.RollbackAsync();
        }

        var accepted = (await fast).Outcomes.Single();
        Assert.Equal(commit ? 3 : 2, accepted.Version);
        var after = await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(during.Checkpoint, 10));
        Assert.Equal(commit ? ["slow", "fast"] : ["fast"], after.Changes.Select(c => c.Document.Id));
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await _database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
