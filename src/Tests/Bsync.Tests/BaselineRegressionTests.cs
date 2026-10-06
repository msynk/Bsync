using Bsync.Clocks;
using Bsync.Conflicts;
using Bsync.Tests.TestSupport;
using Xunit;

namespace Bsync.Tests;

/// <summary>
/// Desired-behaviour regressions for the ten scenarios reproduced against commit 0083a02
/// (docs/review/baseline.md). Each test names the scenario (S), catalogue test (T) and invariant (I).
/// </summary>
public sealed class BaselineRegressionTests
{
    [Fact(DisplayName = "S01 T03 I08: push drains every batch when nothing conflicts")]
    public async Task S01_PushDrainsAllBatches()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a", options: new SyncOptions<Note> { PushBatchSize = 2 });
        for (var i = 0; i < 5; i++)
        {
            await client.Engine.WriteAsync(new Note { Id = $"n{i}" });
        }

        var result = await client.Engine.PushAsync();

        Assert.Equal(5, result.Pushed);
        Assert.True(result.IsComplete);
        Assert.Equal(5, server.Server.Snapshot().Count);
        Assert.Equal(3, client.Transport.PushLog.Count);
    }

    [Fact(DisplayName = "S02 T07 I02: an edit committed while an acknowledgement is applied survives")]
    public async Task S02_EditDuringAcknowledgementSurvives()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a");
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v1" });

        // Interleave a local edit immediately before the acknowledgement commits (call 2 is the
        // operation-preparation commit, call 3 the acknowledgement).
        client.Store.BeforeUpdate = async (call, _, _) =>
        {
            if (call == 3)
            {
                client.Store.BeforeUpdate = null;
                await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v2" });
            }
        };
        await client.Engine.PushAsync();

        var record = await client.RecordAsync("n1");
        Assert.Equal("v2", record.Current.Title);
        Assert.Equal("v2", server.Get("n1").Title); // the later edit was pushed in the same run
        Assert.False(record.IsDirty);
    }

    [Fact(DisplayName = "S02b T06 I02: an acknowledgement for revision N leaves revision N+1 dirty")]
    public async Task S02b_AcknowledgementDoesNotCleanLaterRevision()
    {
        var server = InMemorySyncServerRef.Create();
        var client = new TestReplica(server, "a", options: new SyncOptions<Note> { MaxPushBatches = 1 });
        await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v1" });

        client.Transport.AfterPush = async result =>
        {
            client.Transport.AfterPush = null;
            await client.Engine.WriteAsync(new Note { Id = "n1", Title = "v2" });
            return result;
        };
        var push = await client.Engine.PushAsync();

        var record = await client.RecordAsync("n1");
        Assert.Equal("v2", record.Current.Title);
        Assert.True(record.IsDirty);
        Assert.Equal("v1", record.Base!.Title);
        Assert.Equal(server.Server.GetVersion("n1"), record.BaseVersion);
        Assert.True(push.HasRemainingWork);
    }

    [Fact(DisplayName = "S03 T08 I03: an edit committed while a pull page is applied survives")]
    public async Task S03_EditDuringPullSurvives()
    {
        var server = InMemorySyncServerRef.Create();
        var other = new TestReplica(server, "b");
        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "base" });
        await other.Engine.SyncAsync();

        var client = new TestReplica(server, "a");
        await client.Engine.PullAsync();

        await other.Engine.WriteAsync(new Note { Id = "n1", Title = "remote" });
        await other.Engine.SyncAsync();

        client.Transport.AfterPull = async page =>
        {
            client.Transport.AfterPull = null;
            await client.Engine.WriteAsync(new Note { Id = "n1", Title = "local" });
            return page;
        };
        await client.Engine.PullAsync();

        var record = await client.RecordAsync("n1");
        Assert.Equal("local", record.Current.Title);
        Assert.True(record.IsDirty);
        Assert.Equal("base", record.Base!.Title); // base still names what the edit was made against
    }

    [Fact(DisplayName = "S04 T11 I04: retrying after a lost response does not repeat the logical write")]
    public async Task S04_LostResponseRetryDoesNotRepeatMerge()
    {
        var server = InMemorySyncServerRef.Create();
        var merges = 0;
        var merger = new DelegateConflictHandler<Note>(c =>
        {
            merges++;
            c.Fork.Body = $"{c.RealMaster.Body}|{c.Fork.Body}";
            return ConflictResolution<Note>.Resolve(c.Fork);
        });
        var client = new TestReplica(server, "a", merger);
        await client.Engine.WriteAsync(new Note { Id = "n1", Body = "one" });

        client.Transport.LoseResponses = 1;
        await Assert.ThrowsAsync<InjectedFaultException>(() => client.Engine.PushAsync());
        var retry = await client.Engine.PushAsync();

        Assert.Equal(0, merges);
        Assert.Equal(1, retry.Pushed);
        Assert.Equal("one", server.Get("n1").Body);
        Assert.Equal("one", (await client.RecordAsync("n1")).Current.Body);
        Assert.Equal(client.Transport.PushLog[0].Operations[0].OperationId, client.Transport.PushLog[1].Operations[0].OperationId);
    }

    [Fact(DisplayName = "S05 T21 I12: a far-future client timestamp is rejected and does not affect other writes")]
    public async Task S05_FarFutureTimestampIsRejected()
    {
        var world = new ManualClock(1_000_000);
        var server = InMemorySyncServerRef.Create(world);
        var future = new ManualClock(world.NowMilliseconds() + TimeSpan.FromDays(365).Ticks / TimeSpan.TicksPerMillisecond);

        // The server refuses the timestamp (shown with re-stamping off, so the rejection stays on the record).
        var evil = new TestReplica(server, "evil", physicalClock: future, options: new SyncOptions<Note> { RestampSkewedWrites = false });
        var good = new TestReplica(server, "good", physicalClock: world);

        await evil.Engine.WriteAsync(new Note { Id = "evil" });
        var evilResult = await evil.Engine.SyncAsync();
        await good.Engine.WriteAsync(new Note { Id = "good" });
        await good.Engine.SyncAsync();

        Assert.Equal(1, evilResult.Rejected);
        Assert.Equal("clock-skew", (await evil.RecordAsync("evil")).Rejection!.ErrorCode);
        Assert.DoesNotContain(server.Server.Snapshot(), n => n.Id == "evil");
        Assert.True(server.Get("good").UpdatedAt.WallTime <= world.NowMilliseconds());

        // By default (ADR-017 part 2) the replica re-stamps the write from the server's time and uploads it: the far-future
        // timestamp still never reaches the server or any other replica.
        var fixedUp = new TestReplica(server, "fixed", physicalClock: future);
        await fixedUp.Engine.WriteAsync(new Note { Id = "restamped" });
        var fixedResult = await fixedUp.Engine.SyncAsync();

        Assert.Equal((1, 0), (fixedResult.Pushed, fixedResult.Rejected));
        Assert.True(server.Get("restamped").UpdatedAt.WallTime <= world.NowMilliseconds() + 5_000);
        Assert.All(server.Server.Snapshot(), n => Assert.True(n.UpdatedAt.WallTime <= world.NowMilliseconds() + 5_000));
    }

    [Fact(DisplayName = "S06 T22 I12: counter overflow is rejected or carried into wall time")]
    public void S06_CounterOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HlcTimestamp(1_000, int.MaxValue, "b"));

        var clock = new HybridLogicalClock("a", new ManualClock(1_000));
        var remote = new HlcTimestamp(1_000, HlcTimestamp.MaxCounter, "b");
        var next = clock.Update(remote);

        Assert.True(next > remote);
        Assert.Equal(1_001, next.WallTime);
        Assert.Equal(0, next.Counter);
        Assert.True(clock.Now() > next);
    }

    [Fact(DisplayName = "S07 T24 I12: encoded ordering matches numeric ordering at the counter boundary")]
    public void S07_EncodedOrderingAtBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HlcTimestamp(1_000, 1_000_000, "n"));

        var a = new HlcTimestamp(1_000, HlcTimestamp.MaxCounter, "n");
        var b = new HlcTimestamp(1_001, 0, "n");
        Assert.True(a < b);
        Assert.True(string.CompareOrdinal(a.Encode(), b.Encode()) < 0);
    }

    [Fact(DisplayName = "S08 T23 I12: a restarted replica does not reuse a timestamp")]
    public async Task S08_RestartDoesNotReuseTimestamp()
    {
        var frozen = new ManualClock(1_000);
        var server = InMemorySyncServerRef.Create();
        var durable = new Storage.InMemoryLocalStore<Note>(NoteJson.Clone);

        var first = new TestReplica(server, "a", physicalClock: frozen, store: durable);
        var t1 = (await first.Engine.WriteAsync(new Note { Id = "n1" })).UpdatedAt;

        var restarted = new TestReplica(server, "a", physicalClock: frozen, store: durable);
        var t2 = (await restarted.Engine.WriteAsync(new Note { Id = "n2" })).UpdatedAt;

        Assert.True(t2 > t1);
    }

    [Theory(DisplayName = "S09 T01 I08: out-of-range options are rejected")]
    [InlineData(0, 1, 1, 1, 1)]
    [InlineData(1, 0, 1, 1, 1)]
    [InlineData(1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 0, 1)]
    [InlineData(1, 1, 1, 1, 0)]
    public void S09_InvalidOptionsAreRejected(int pushBatch, int pullBatch, int pushBatches, int pullPages, int conflictRetries)
    {
        var options = new SyncOptions<Note>
        {
            PushBatchSize = pushBatch,
            PullBatchSize = pullBatch,
            MaxPushBatches = pushBatches,
            MaxPullPages = pullPages,
            MaxConflictRetries = conflictRetries,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new TestReplica(InMemorySyncServerRef.Create(), "a", options: options));
    }

    [Theory(DisplayName = "S10 T25 I11: last-write-wins does not depend on upload order")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S10_LastWriteWinsIsUploadOrderIndependent(bool aUploadsFirst)
    {
        var world = new ManualClock(100_000);
        var server = InMemorySyncServerRef.Create(world);
        var clockA = new ManualClock(10_000);
        var clockB = new ManualClock(10_000);
        var lww = new LastWriteWinsConflictHandler<Note>();
        var a = new TestReplica(server, "a", lww, physicalClock: clockA);
        var b = new TestReplica(server, "b", lww, physicalClock: clockB);

        await a.Engine.WriteAsync(new Note { Id = "n1", Title = "base" });
        await a.Engine.SyncAsync();
        await b.Engine.SyncAsync();

        clockA.Set(20_000);
        clockB.Set(30_000);
        await a.Engine.WriteAsync(new Note { Id = "n1", Title = "A" });
        await b.Engine.WriteAsync(new Note { Id = "n1", Title = "B" });

        var (first, second) = aUploadsFirst ? (a, b) : (b, a);
        await first.Engine.SyncAsync();
        await second.Engine.SyncAsync();
        await first.Engine.SyncAsync();

        Assert.Equal("B", server.Get("n1").Title);
        Assert.Equal(30_000, server.Get("n1").UpdatedAt.WallTime); // origin timestamp preserved
        Assert.Equal("B", (await a.RecordAsync("n1")).Current.Title);
        Assert.Equal("B", (await b.RecordAsync("n1")).Current.Title);
    }
}
