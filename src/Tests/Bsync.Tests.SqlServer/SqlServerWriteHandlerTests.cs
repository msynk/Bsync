using Bsync.Clocks;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.SqlServer;
using Bsync.Tests.TestSupport;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bsync.Tests.SqlServer;

/// <summary>
/// ADR-014: the write handler runs in the authority's transaction next to the application's own table, and writes
/// enlist in a caller's transaction.
/// </summary>
public sealed class SqlServerWriteHandlerTests : IAsyncLifetime
{
    private SqlServerDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await SqlServerDatabase.CreateAsync();
        await _database.ExecuteAsync("CREATE TABLE dbo.notes_domain (id nvarchar(256) NOT NULL PRIMARY KEY, title nvarchar(max) NOT NULL, title_length int NOT NULL, deleted bit NOT NULL)");
    }

    public async Task DisposeAsync() => await _database.DisposeAsync();

    private Task<SqlServerSyncAuthority<Note>> AuthorityAsync(DomainHandler handler) =>
        _database.AuthorityAsync(configure: o => new()
        {
            ConnectionString = o.ConnectionString,
            DocumentType = o.DocumentType,
            Collection = o.Collection,
            PhysicalClock = o.PhysicalClock,
            WriteHandler = handler,
        });

    private static Note Stamped(string id, string title) =>
        new() { Id = id, Title = title, UpdatedAt = new HlcTimestamp(SystemPhysicalClock.Instance.NowMilliseconds(), 0, "n") };

    private static TestReplica Replica(ISyncAuthority<Note> authority, string node) =>
        new(InMemorySyncServerRef.Create(), node, physicalClock: SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(authority));

    private Task<long> DomainRowsAsync() => _database.ScalarAsync("SELECT count(*) FROM dbo.notes_domain");

    [Fact(DisplayName = "ADR-014 I18: the handler writes the application's table and returns a canonical document in the same transaction")]
    public async Task HandlerWritesDomainAndCanonicalDocument()
    {
        var handler = new DomainHandler();
        await using var authority = await AuthorityAsync(handler);

        var outcome = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("o1", "n1", null, Stamped("n1", "hello"))]))).Outcomes.Single();

        Assert.Equal(PushOutcomeKind.Accepted, outcome.Kind);
        Assert.Equal("length 5", outcome.Document!.Body); // recomputed by the server
        Assert.Equal("length 5", (await authority.GetAsync(SyncCallContext.Anonymous, "n1"))!.Document.Body);
        Assert.Equal(5, await _database.ScalarAsync("SELECT title_length FROM dbo.notes_domain WHERE id = 'n1'"));
    }

    [Theory(DisplayName = "ADR-014 I01: a caller's transaction commits the domain write and the feed together, or rolls back both")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerTransactionDecidesBoth(bool commit)
    {
        await using var authority = await AuthorityAsync(new DomainHandler());
        await using var connection = await _database.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        await using (var audit = new SqlCommand("INSERT INTO dbo.notes_domain (id, title, title_length, deleted) VALUES ('audit', 'by the app', 0, 0)", connection, transaction))
        {
            await audit.ExecuteNonQueryAsync(); // the application's own write in its transaction (for example EF Core SaveChanges)
        }

        var outcome = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([new PushOperation<Note>("o1", "n1", null, Stamped("n1", "hi"))]), transaction)).Outcomes.Single();
        Assert.Equal(PushOutcomeKind.Accepted, outcome.Kind);
        Assert.NotNull(transaction.Connection); // the authority did not end the caller's transaction

        if (commit)
        {
            transaction.Commit();
        }
        else
        {
            transaction.Rollback();
        }

        Assert.Equal(commit ? 2 : 0, await DomainRowsAsync());
        Assert.Equal(commit, await authority.GetAsync(SyncCallContext.Anonymous, "n1") is not null);
        Assert.Equal(commit ? 1 : 0, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));
        Assert.Equal(commit ? 1 : 0, await authority.GetHighestVersionAsync("default"));
    }

    [Fact(DisplayName = "ADR-014 I01 I04: a failure after the handler wrote leaves no feed row, receipt or domain write, and the retry applies once")]
    public async Task FailureAfterHandlerCommitsNothing()
    {
        var handler = new DomainHandler { FailAfterWriting = true };
        await using var authority = await AuthorityAsync(handler);
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "once" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Engine.SyncAsync());
        Assert.Equal(0, await DomainRowsAsync());
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM bsync.documents"));
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));

        handler.FailAfterWriting = false;
        var result = await client.Engine.SyncAsync();

        Assert.True(result.IsComplete);
        Assert.Equal(1, await DomainRowsAsync());
        Assert.Equal(1, await authority.GetHighestVersionAsync("default"));
    }

    [Fact(DisplayName = "ADR-014 I04 I11: a duplicate operation id replays the stored outcome without calling the handler again")]
    public async Task ReplayDoesNotCallHandler()
    {
        var handler = new DomainHandler();
        await using var authority = await AuthorityAsync(handler);
        var operation = new PushOperation<Note>("o1", "n1", null, Stamped("n1", "x"));

        var first = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]))).Outcomes.Single();
        var replay = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]))).Outcomes.Single();

        Assert.Equal(1, handler.Calls);
        Assert.True(replay.IsDuplicate);
        Assert.Equal(first.Version, replay.Version);
        Assert.Equal("length 1", replay.Document!.Body); // the canonical document, from the receipt
    }

    [Fact(DisplayName = "ADR-014 I02: a canonical document reaches the replica; a local edit made during the round trip stays pending on the new base")]
    public async Task CanonicalDocumentReachesReplica()
    {
        await using var authority = await AuthorityAsync(new DomainHandler());
        var client = new TestReplica(InMemorySyncServerRef.Create(), "c", options: new SyncOptions<Note> { MaxPushBatches = 1 }, physicalClock: SystemPhysicalClock.Instance, transport: _ => new InProcessTransport<Note>(authority));
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "first" });
        client.Transport.AfterPush = async result =>
        {
            client.Transport.AfterPush = null;
            await client.Engine.WriteAsync(new Note { Id = "n1", Title = "edited meanwhile" });
            return result;
        };

        await client.Engine.PushAsync();
        var during = await client.RecordAsync("n1");

        Assert.Equal("length 5", during.Base!.Body); // the server's canonical state is the new base
        Assert.True(during.IsDirty);
        Assert.Equal("edited meanwhile", during.Current.Title);

        await client.Engine.SyncAsync();
        var settled = await client.RecordAsync("n1");
        Assert.Equal(during.BaseVersion, client.Transport.PushLog[^1].Operations.Single().BaseVersion); // sent on the new base
        Assert.False(settled.IsDirty);
        Assert.Equal("length 16", settled.Current.Body);
        Assert.Equal(16, await _database.ScalarAsync("SELECT title_length FROM dbo.notes_domain WHERE id = 'n1'"));
    }

    [Fact(DisplayName = "ADR-014 I19: a handler rejection parks only that record, keeps its code, and undoes its domain write")]
    public async Task RejectionParksOnlyThatRecord()
    {
        var handler = new DomainHandler();
        await using var authority = await AuthorityAsync(handler);
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "bad", Title = "forbidden" });
        await client.Engine.WriteAsync(new Note { Id = "good", Title = "fine" });

        var result = await client.Engine.SyncAsync();
        var replayed = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([client.Transport.PushLog.SelectMany(p => p.Operations).Single(o => o.DocumentId == "bad")]))).Outcomes.Single();

        Assert.Equal(1, result.Rejected);
        Assert.Equal("title-forbidden", (await client.RecordAsync("bad")).Rejection!.ErrorCode);
        Assert.False((await client.RecordAsync("good")).IsDirty);
        Assert.Equal(1, await DomainRowsAsync()); // the rejected write's domain row was rolled back
        Assert.Equal(("title-forbidden", true), (replayed.ErrorCode, replayed.IsDuplicate));
        Assert.Equal(2, handler.Calls);
    }

    [Fact(DisplayName = "ADR-014 I04: retry-later stores no receipt, so the same operation is decided once the condition clears")]
    public async Task RetryLaterStoresNoReceipt()
    {
        var handler = new DomainHandler { Busy = true };
        await using var authority = await AuthorityAsync(handler);
        var operation = new PushOperation<Note>("o1", "n1", null, Stamped("n1", "x"));

        var later = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]))).Outcomes.Single();
        Assert.Equal(0, await _database.ScalarAsync("SELECT count(*) FROM bsync.receipts"));
        Assert.Equal(0, await DomainRowsAsync());
        handler.Busy = false;
        var accepted = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>([operation]))).Outcomes.Single();

        Assert.Equal((PushOutcomeKind.RetryLater, "busy"), (later.Kind, later.ErrorCode));
        Assert.Equal(PushOutcomeKind.Accepted, accepted.Kind);
        Assert.False(accepted.IsDuplicate);
        Assert.Equal(1, accepted.Version); // the undecided attempt used no version
    }

    [Fact(DisplayName = "ADR-014 I05 I19: when a group member is rejected by the handler, the other members' domain writes roll back too")]
    public async Task GroupRollsBackHandlerWrites()
    {
        await using var authority = await AuthorityAsync(new DomainHandler());

        var outcomes = (await authority.PushAsync(SyncCallContext.Anonymous, new PushRequest<Note>(
        [
            new PushOperation<Note>("g1", "order", null, Stamped("order", "fine")) { Group = "grp", GroupSize = 2 },
            new PushOperation<Note>("g2", "line", null, Stamped("line", "forbidden")) { Group = "grp", GroupSize = 2 },
        ]))).Outcomes;

        Assert.Equal((PushOutcomeKind.RetryLater, PushErrorCodes.GroupAborted), (outcomes[0].Kind, outcomes[0].ErrorCode));
        Assert.Equal("title-forbidden", outcomes[1].ErrorCode);
        Assert.Equal(0, await DomainRowsAsync());
        Assert.Equal(0, await authority.GetHighestVersionAsync("default"));
    }

    [Fact(DisplayName = "ADR-014 I10: a delete becomes the application's soft delete and a tombstone in the feed")]
    public async Task DeleteIsSoft()
    {
        await using var authority = await AuthorityAsync(new DomainHandler());
        var client = Replica(authority, "c");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "x" });
        await client.Engine.SyncAsync();

        await client.Engine.DeleteAsync("n1");
        await client.Engine.SyncAsync();

        Assert.Equal(1, await _database.ScalarAsync("SELECT CAST(deleted AS int) FROM dbo.notes_domain WHERE id = 'n1'"));
        var feed = await authority.PullAsync(SyncCallContext.Anonymous, new PullRequest(Checkpoint.Start, 10));
        Assert.True(feed.Changes.Single().Document.Deleted);
    }

    /// <summary>Recomputes <see cref="Note.Body"/>, mirrors the note into dbo.notes_domain, and rejects one title.</summary>
    private sealed class DomainHandler : ISyncWriteHandler<Note>
    {
        private int _calls;

        public int Calls => _calls;

        public bool FailAfterWriting { get; set; }

        public bool Busy { get; set; }

        public async ValueTask<SyncWriteDecision<Note>> HandleAsync(SyncWriteContext<Note> write, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var note = write.Submitted;
            await using (var upsert = new SqlCommand(
                """
                UPDATE dbo.notes_domain SET title = @title, title_length = @length, deleted = @deleted WHERE id = @id;
                IF @@ROWCOUNT = 0 INSERT INTO dbo.notes_domain (id, title, title_length, deleted) VALUES (@id, @title, @length, @deleted);
                """,
                (SqlConnection)write.Connection!,
                (SqlTransaction)write.Transaction!))
            {
                upsert.Parameters.AddWithValue("@id", note.Id);
                upsert.Parameters.AddWithValue("@title", note.Title);
                upsert.Parameters.AddWithValue("@length", note.Title.Length);
                upsert.Parameters.AddWithValue("@deleted", note.Deleted);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            if (FailAfterWriting)
            {
                throw new InvalidOperationException("The process failed after the handler wrote.");
            }

            if (Busy)
            {
                return SyncWriteDecision<Note>.RetryLater("busy");
            }

            if (note.Title == "forbidden")
            {
                return SyncWriteDecision<Note>.Reject("title-forbidden", "This title is not allowed.");
            }

            note.Body = $"length {note.Title.Length}";
            return SyncWriteDecision<Note>.Accept(note);
        }
    }
}
