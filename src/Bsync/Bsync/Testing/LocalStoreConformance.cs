using Bsync.Clocks;
using Bsync.Storage;

namespace Bsync.Testing;

/// <summary>
/// The behaviour every <see cref="ILocalStore{TDocument}"/> must provide (ADR-004). A provider is not supported
/// until every case passes against it, in every runtime where it is used (for example each browser engine).
/// Durability across process restarts is tested separately per provider.
/// </summary>
public static class LocalStoreConformance
{
    private static SyncRecord<ConformanceDocument> Dirty(string id, long wall, string title = "t") =>
        new(new ConformanceDocument { Id = id, Title = title, UpdatedAt = new HlcTimestamp(wall, 0, "n") }, null, IsDirty: true) { LocalRevision = 1 };

    private static SyncRecord<ConformanceDocument> Conflicted(string id)
    {
        var server = new ConformanceDocument { Id = id, Title = "theirs", UpdatedAt = new HlcTimestamp(8, 2, "s") };
        return new SyncRecord<ConformanceDocument>(server, server, IsDirty: false)
        {
            BaseVersion = 9_007_199_254_740_993,
            LocalRevision = 2,
            Conflict = new SyncConflict<ConformanceDocument>(
                new ConformanceDocument { Id = id, Title = "theirs", UpdatedAt = new HlcTimestamp(8, 2, "s") },
                9_007_199_254_740_993,
                new ConformanceDocument { Id = id, Title = "mine", Deleted = true, UpdatedAt = new HlcTimestamp(7, 0, "c") },
                new ConformanceDocument { Id = id, Title = "base", UpdatedAt = new HlcTimestamp(1, 0, "b") }),
        };
    }

    private static RecordUpdate<ConformanceDocument> Put(SyncRecord<ConformanceDocument> record) => new(record.Current.Id, _ => record);

    private static ReplicaCursor Cursor(string checkpoint, long generation = 0, bool resnapshot = false) =>
        new(new Checkpoint(checkpoint), generation, resnapshot);

    /// <summary>All cases.</summary>
    public static IReadOnlyList<ConformanceCase> Cases { get; } =
    [
        new("Store: an absent record reads as null and the cursor starts at Start", async create =>
        {
            var store = await create();
            Check.Null(await store.GetAsync("missing"));
            Check.Equal(ReplicaCursor.Initial, await store.GetCursorAsync());
            Check.Equal(HlcTimestamp.MinValue, await store.GetClockHighWaterAsync());
            Check.Equal(0, await store.CountDirtyAsync());
            Check.Equal(0, (await store.GetPendingAsync(10)).Count);
        }),

        new("Store I02 I12: every SyncRecord field round-trips, including 64-bit versions", async create =>
        {
            var store = await create();
            var pending = new PendingOperation<ConformanceDocument>("op-1", 6, 9_007_199_254_740_993, new ConformanceDocument { Id = "n1", Title = "sent", UpdatedAt = new HlcTimestamp(4, 0, "a") });
            var record = new SyncRecord<ConformanceDocument>(
                new ConformanceDocument { Id = "n1", Title = "current", UpdatedAt = new HlcTimestamp(5, 1, "a") },
                new ConformanceDocument { Id = "n1", Title = "base", UpdatedAt = new HlcTimestamp(3, 0, "b") },
                IsDirty: true)
            {
                BaseVersion = 9_007_199_254_740_993,
                LocalRevision = 7,
                Pending = pending,
                Rejection = new SyncRejection(7, "forbidden", "no") { Arguments = new Dictionary<string, string> { ["limit"] = "200", ["field"] = "títle" } },
                Observed = new ConformanceDocument { Id = "n1", Title = "observed", UpdatedAt = new HlcTimestamp(6, 0, "c") },
                ObservedVersion = long.MaxValue,
                Generation = 3,
                MissingAfterReset = false,
            };

            await store.UpdateAsync([Put(record)]);
            var read = (await store.GetAsync("n1"))!;

            Check.Equal("current", read.Current.Title);
            Check.Equal(new HlcTimestamp(5, 1, "a"), read.Current.UpdatedAt);
            Check.Equal("base", read.Base!.Title);
            Check.True(read.IsDirty);
            Check.Equal(9_007_199_254_740_993L, read.BaseVersion);
            Check.Equal(7L, read.LocalRevision);
            Check.Equal(pending with { Payload = read.Pending!.Payload }, read.Pending);
            Check.Equal("sent", read.Pending.Payload.Title);
            Check.Equal(record.Rejection, read.Rejection);
            Check.Equal("observed", read.Observed!.Title);
            Check.Equal(long.MaxValue, read.ObservedVersion);
            Check.Equal(3L, read.Generation);
        }),

        new("Store I03: a batch and its cursor commit together; a throwing transform commits nothing", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Dirty("a", 1))], Cursor("cp-1"));

            await Check.Throws<ConformanceFault>(() => store.UpdateAsync(
                [
                    new("a", r => r! with { IsDirty = false }),
                    Put(Dirty("b", 2)),
                    new("c", _ => throw new ConformanceFault("boom")),
                ],
                Cursor("cp-2")));

            Check.True((await store.GetAsync("a"))!.IsDirty);
            Check.Null(await store.GetAsync("b"));
            Check.Equal(new Checkpoint("cp-1"), (await store.GetCursorAsync()).Checkpoint);
        }),

        new("Store I03: a null cursor leaves the stored cursor unchanged", async create =>
        {
            var store = await create();
            await store.UpdateAsync([], Cursor("cp-1"));
            await store.UpdateAsync([Put(Dirty("a", 1))]);
            Check.Equal(new Checkpoint("cp-1"), (await store.GetCursorAsync()).Checkpoint);
        }),

        new("Store: a transform returning null reports no change and writes nothing", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Dirty("a", 1, "original"))]);

            var results = await store.UpdateAsync(
            [
                new("a", r =>
                {
                    r!.Current.Title = "mutated inside a no-op transform";
                    return null;
                }),
                new("absent", _ => null),
            ]);

            Check.True(!results[0].Changed);
            Check.Equal("original", results[0].Record!.Current.Title);
            Check.True(!results[1].Changed);
            Check.Null(results[1].Record);
            Check.Equal("original", (await store.GetAsync("a"))!.Current.Title);
            Check.Null(await store.GetAsync("absent"));
        }),

        new("Store: duplicate ids in one batch and mismatched result ids are refused without writing", async create =>
        {
            var store = await create();
            await Check.Throws<ArgumentException>(() => store.UpdateAsync([Put(Dirty("a", 1)), Put(Dirty("a", 2))]));
            await Check.Throws<InvalidOperationException>(() => store.UpdateAsync([Put(Dirty("b", 1)), new("c", _ => Dirty("other", 1))]));
            Check.Null(await store.GetAsync("a"));
            Check.Null(await store.GetAsync("b"));
        }),

        new("Store I15: a cancelled update commits nothing", async create =>
        {
            var store = await create();
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            await Check.Throws<OperationCanceledException>(() => store.UpdateAsync([Put(Dirty("a", 1))], Cursor("x"), cts.Token));
            Check.Null(await store.GetAsync("a"));
            Check.True((await store.GetCursorAsync()).Checkpoint.IsStart);
        }),

        new("Store I02: stored state never aliases caller objects", async create =>
        {
            var store = await create();
            var record = Dirty("a", 1, "stored") with
            {
                Pending = new PendingOperation<ConformanceDocument>("op", 1, null, new ConformanceDocument { Id = "a", Title = "payload" }),
                Observed = new ConformanceDocument { Id = "a", Title = "observed" },
            };
            var results = await store.UpdateAsync([Put(record)]);

            record.Current.Title = "changed input";
            record.Pending!.Payload.Title = "changed input";
            record.Observed!.Title = "changed input";
            results[0].Record!.Current.Title = "changed result";
            (await store.GetAsync("a"))!.Current.Title = "changed read";
            (await store.QueryAsync())[0].Title = "changed query";
            (await store.GetPendingAsync(1))[0].Current.Title = "changed pending";

            var final = (await store.GetAsync("a"))!;
            Check.Equal("stored", final.Current.Title);
            Check.Equal("payload", final.Pending!.Payload.Title);
            Check.Equal("observed", final.Observed!.Title);
        }),

        new("Store I19: pending records are ordered by origin time then id, and honour limit and exclusions", async create =>
        {
            var store = await create();
            await store.UpdateAsync(
            [
                Put(Dirty("b", 2)),
                Put(Dirty("a", 2)),
                Put(Dirty("z", 1)),
                Put(Dirty("clean", 0) with { IsDirty = false }),
                Put(Dirty("rejected", 0) with { Rejection = new SyncRejection(1, "forbidden", null) }),
                Put(Dirty("é", 2)),
            ]);

            Check.Sequence(["z", "a", "b", "é"], (await store.GetPendingAsync(10)).Select(r => r.Current.Id));
            Check.Sequence(["z", "a"], (await store.GetPendingAsync(2)).Select(r => r.Current.Id));
            Check.Sequence(["b", "é"], (await store.GetPendingAsync(10, new HashSet<string>(StringComparer.Ordinal) { "z", "a" })).Select(r => r.Current.Id));
            Check.Equal(5, await store.CountDirtyAsync());
            await Check.Throws<ArgumentOutOfRangeException>(() => store.GetPendingAsync(0));
        }),

        new("Store I14: the replica cursor round-trips, including generation and resnapshot mode", async create =>
        {
            var store = await create();
            await store.UpdateAsync([], Cursor("cp", generation: 3, resnapshot: true));
            Check.Equal(Cursor("cp", 3, true), await store.GetCursorAsync());

            await store.UpdateAsync([], new ReplicaCursor(Checkpoint.Start, 4, false));
            Check.Equal(new ReplicaCursor(Checkpoint.Start, 4, false), await store.GetCursorAsync());
        }),

        new("Store I10 I14: stale records are the clean, visible records of older generations; missing ones are hidden", async create =>
        {
            var store = await create();
            await store.UpdateAsync(
            [
                Put(Dirty("b-old", 1) with { IsDirty = false, Generation = 0 }),
                Put(Dirty("a-old", 1) with { IsDirty = false, Generation = 0 }),
                Put(Dirty("current", 1) with { IsDirty = false, Generation = 1 }),
                Put(Dirty("dirty-old", 1) with { Generation = 0 }),
                Put(Dirty("missing-old", 1) with { IsDirty = false, Generation = 0, MissingAfterReset = true }),
            ]);

            Check.Sequence(["a-old", "b-old"], (await store.GetStaleAsync(1, 10)).Select(r => r.Current.Id));
            Check.Sequence(["a-old"], (await store.GetStaleAsync(1, 1)).Select(r => r.Current.Id));
            Check.Equal(0, (await store.GetStaleAsync(0, 10)).Count);
            Check.True(!(await store.QueryAsync(includeDeleted: true)).Any(n => n.Id == "missing-old"));
            Check.True((await store.GetAsync("missing-old"))!.MissingAfterReset);
            await Check.Throws<ArgumentOutOfRangeException>(() => store.GetStaleAsync(1, 0));
        }),

        new("Store I10 I11 I14: purge removes only clean records of older generations without a kept conflict", async create =>
        {
            var store = await create();
            await store.UpdateAsync(
            [
                Put(Dirty("clean-old", 1) with { IsDirty = false, Generation = 0 }),
                Put(Dirty("dirty-old", 1) with { Generation = 0 }),
                Put(Dirty("clean-current", 1) with { IsDirty = false, Generation = 2 }),
                Put(Conflicted("conflict-old") with { Generation = 0 }),
            ]);

            var removed = await store.PurgeAsync(["clean-old", "dirty-old", "clean-current", "conflict-old", "absent"], 2);

            Check.Equal(1, removed);
            Check.Null(await store.GetAsync("clean-old"));
            Check.True((await store.GetAsync("dirty-old"))!.IsDirty);
            Check.Equal(2L, (await store.GetAsync("clean-current"))!.Generation);
            Check.Equal("mine", (await store.GetAsync("conflict-old"))!.Conflict!.Local.Title);
        }),

        new("Store I11: a kept conflict round-trips and is listed in id order until cleared", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Conflicted("b")), Put(Conflicted("a")), Put(Dirty("plain", 1) with { IsDirty = false })]);

            var read = (await store.GetAsync("b"))!.Conflict!;
            Check.Equal(("theirs", "mine", "base"), (read.Server.Title, read.Local.Title, read.Base!.Title));
            Check.Equal(9_007_199_254_740_993L, read.ServerVersion);
            Check.Equal(new HlcTimestamp(8, 2, "s"), read.Server.UpdatedAt);
            Check.True(read.Local.Deleted);
            Check.Equal("a,b", string.Join(",", (await store.GetConflictsAsync(10)).Select(r => r.Current.Id)));
            Check.Equal("a", string.Join(",", (await store.GetConflictsAsync(1)).Select(r => r.Current.Id)));

            await store.UpdateAsync([new("a", r => r! with { Conflict = null })]);
            Check.Null((await store.GetAsync("a"))!.Conflict);
            Check.Equal("b", string.Join(",", (await store.GetConflictsAsync(10)).Select(r => r.Current.Id)));

            var noBase = (await store.GetAsync("b"))! with { Conflict = read with { Base = null } };
            await store.UpdateAsync([Put(noBase)]);
            Check.Null((await store.GetAsync("b"))!.Conflict!.Base);
        }),

        new("Store I19: a dependency group and a grouped pending operation round-trip", async create =>
        {
            var store = await create();
            var payload = new ConformanceDocument { Id = "g1", Title = "p", UpdatedAt = new HlcTimestamp(2, 0, "n") };
            await store.UpdateAsync([Put(Dirty("g1", 2) with
            {
                Group = new SyncGroup("grp-1", ["g1", "g2", "é"]),
                Pending = new PendingOperation<ConformanceDocument>("op-g", 1, null, payload) { Group = "grp-1", GroupSize = 3 },
            }), Put(Dirty("plain", 3))]);

            var read = (await store.GetAsync("g1"))!;
            Check.Equal("grp-1", read.Group!.Id);
            Check.Equal("g1,g2,é", string.Join(",", read.Group.Members));
            Check.Equal(("grp-1", 3), (read.Pending!.Group, read.Pending.GroupSize));
            var plain = (await store.GetAsync("plain"))!;
            Check.Null(plain.Group);

            await store.UpdateAsync([new("g1", r => r! with { Group = null, Pending = null })]);
            Check.Null((await store.GetAsync("g1"))!.Group);
        }),

        new("Store I19: rejected records are listed in id order, bounded, until a new edit clears the rejection", async create =>
        {
            var store = await create();
            await store.UpdateAsync(
            [
                Put(Dirty("b", 1) with { Rejection = new SyncRejection(1, "forbidden", null) }),
                Put(Dirty("a", 2) with { Rejection = new SyncRejection(1, "invalid", "bad") }),
                Put(Dirty("pending", 3)),
                Put(Dirty("clean", 4) with { IsDirty = false }),
            ]);

            Check.Equal("a,b", string.Join(",", (await store.GetRejectedAsync(10)).Select(r => r.Current.Id)));
            Check.Equal("a", string.Join(",", (await store.GetRejectedAsync(1)).Select(r => r.Current.Id)));
            Check.Equal("bad", (await store.GetRejectedAsync(1))[0].Rejection!.Message);

            await store.UpdateAsync([new("a", r => r! with { Rejection = null, LocalRevision = 2 })]);
            Check.Equal("b", string.Join(",", (await store.GetRejectedAsync(10)).Select(r => r.Current.Id)));
        }),

        new("Store I14: the cursor's purge flag round-trips", async create =>
        {
            var store = await create();
            await store.UpdateAsync([], new ReplicaCursor(Checkpoint.Start, 2, Resnapshot: true, PurgeMissing: true));
            Check.Equal(new ReplicaCursor(Checkpoint.Start, 2, true, true), await store.GetCursorAsync());
            await store.UpdateAsync([], new ReplicaCursor(new Checkpoint("cp"), 2, Resnapshot: false, PurgeMissing: false));
            Check.Equal(new ReplicaCursor(new Checkpoint("cp"), 2, false, false), await store.GetCursorAsync());
        }),

        new("Store I08 T59: paged queries walk the visible documents in ordinal id order, bounded and resumable", async create =>
        {
            var store = await create();
            var ids = new[] { "b", "a", "\u00e9", "Z", "\ud83d\ude00", "\uffff", "a0", "tomb", "hidden" };
            await store.UpdateAsync(ids.Select(id => Put(id switch
            {
                "tomb" => Dirty(id, 1) with { Current = new ConformanceDocument { Id = id, Deleted = true, UpdatedAt = new HlcTimestamp(1, 0, "n") } },
                "hidden" => Dirty(id, 1) with { IsDirty = false, MissingAfterReset = true },
                _ => Dirty(id, 1),
            })).ToList());

            var walked = new List<string>();
            string? after = null;
            while (true)
            {
                var page = await store.QueryPageAsync(after, 2);
                Check.True(page.Count <= 2);
                if (page.Count == 0)
                {
                    break;
                }

                walked.AddRange(page.Select(d => d.Id));
                after = page[^1].Id;
            }

            var expected = ids.Where(id => id is not "tomb" and not "hidden").Order(StringComparer.Ordinal).ToList();
            Check.Equal(string.Join("|", expected), string.Join("|", walked));
            Check.Equal("tomb", string.Join("|", (await store.QueryPageAsync("b", 10, includeDeleted: true)).Select(d => d.Id).Where(id => id == "tomb")));
            Check.Equal(0, (await store.QueryPageAsync("\uffff", 10)).Count);
        }),

        new("Store: queries hide tombstones unless asked", async create =>
        {
            var store = await create();
            var tombstone = Dirty("gone", 1);
            tombstone.Current.Deleted = true;
            await store.UpdateAsync([Put(Dirty("live", 1)), Put(tombstone)]);

            Check.Sequence(["live"], (await store.QueryAsync()).Select(n => n.Id));
            Check.Equal(2, (await store.QueryAsync(includeDeleted: true)).Count);
        }),

        new("Store I12: the clock high-water mark covers committed current and pending timestamps and never decreases", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Dirty("a", 5))]);
            await store.UpdateAsync([Put(Dirty("b", 3) with { Pending = new PendingOperation<ConformanceDocument>("op", 1, null, new ConformanceDocument { Id = "b", UpdatedAt = new HlcTimestamp(9, 0, "n") }) })]);
            Check.Equal(new HlcTimestamp(9, 0, "n"), await store.GetClockHighWaterAsync());

            await store.UpdateAsync([Put(Dirty("b", 1))]);
            await Check.Throws<ConformanceFault>(() => store.UpdateAsync([Put(Dirty("c", 50)), new("d", _ => throw new ConformanceFault("x"))]));
            Check.Equal(new HlcTimestamp(9, 0, "n"), await store.GetClockHighWaterAsync());
        }),

        new("Store ADR-017 I12: the clock high-water mark can be lowered explicitly and stays lowered across reads (or the store says it cannot)", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Dirty("a", 90_000))]);
            try
            {
                await store.ResetClockHighWaterAsync(new HlcTimestamp(40, 0, "n"));
            }
            catch (NotSupportedException)
            {
                return;
            }

            Check.Equal(new HlcTimestamp(40, 0, "n"), await store.GetClockHighWaterAsync());
            await store.UpdateAsync([Put(Dirty("b", 50))]);
            Check.Equal(new HlcTimestamp(50, 0, "n"), await store.GetClockHighWaterAsync()); // rises again from the new mark
        }),

        new("Store T59: non-ASCII and maximum-length ids are stored and compared ordinally", async create =>
        {
            var store = await create();

            // U+E000 sorts after the surrogate pair of U+1F600 in UTF-16 but before it in UTF-8.
            var ids = new[] { "ノート", "😀", "", "A", "a", new string('x', SyncIds.MaxLength) };
            await store.UpdateAsync(ids.Select(id => Put(Dirty(id, 1))).ToList());

            foreach (var id in ids)
            {
                Check.Equal(id, (await store.GetAsync(id))!.Current.Id);
            }

            Check.Sequence(ids.Order(StringComparer.Ordinal), (await store.GetPendingAsync(10)).Select(r => r.Current.Id));
        }),

        new("Store I02: concurrent transforms on one record are serialized (no lost increments)", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Put(Dirty("counter", 1) with { LocalRevision = 0 })]);

            await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() =>
                store.UpdateAsync([new("counter", r => r! with { LocalRevision = r.LocalRevision + 1 })]))));

            Check.Equal(30L, (await store.GetAsync("counter"))!.LocalRevision);
        }),
    ];
}
