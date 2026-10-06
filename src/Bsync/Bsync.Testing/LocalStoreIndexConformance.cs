using Bsync.Clocks;
using Bsync.Storage;

namespace Bsync.Testing;

/// <summary>
/// The index behaviour every <see cref="ILocalStore{TDocument}"/> must provide (ADR-018). Each case's factory must open
/// an empty store declaring <see cref="Indexes"/>. Stores that do not maintain indexes pass through the default
/// (in-memory) implementation; stores that do must give the same results.
/// </summary>
public static class LocalStoreIndexConformance
{
    /// <summary>An index on the title (strings, ordinal order).</summary>
    public static SyncIndex<ConformanceDocument, string> Title { get; } = SyncIndex<ConformanceDocument>.Create("title", d => d.Title);

    /// <summary>An index on the title's length (numbers, numeric order).</summary>
    public static SyncIndex<ConformanceDocument, int> Length { get; } = SyncIndex<ConformanceDocument>.Create("length", d => d.Title.Length);

    /// <summary>An index on the part of the title after ':', or <see langword="null"/> without one.</summary>
    public static SyncIndex<ConformanceDocument, string?> Tag { get; } = SyncIndex<ConformanceDocument>.Create("tag", d => d.Title.IndexOf(':') is var i and >= 0 ? d.Title[(i + 1)..] : null);

    /// <summary>The indexes a store under test must be opened with.</summary>
    public static IReadOnlyList<SyncIndex<ConformanceDocument>> Indexes { get; } = [Title, Length, Tag];

    private static ConformanceDocument Document(string id, string title, bool deleted = false) =>
        new() { Id = id, Title = title, Deleted = deleted, UpdatedAt = new HlcTimestamp(10, 0, "n") };

    private static RecordUpdate<ConformanceDocument> Clean(string id, string title, long generation = 0) =>
        new(id, _ => new SyncRecord<ConformanceDocument>(Document(id, title), Document(id, title), IsDirty: false) { BaseVersion = 1, Generation = generation });

    private static RecordUpdate<ConformanceDocument> Edited(string id, string title) =>
        new(id, _ => new SyncRecord<ConformanceDocument>(Document(id, title), null, IsDirty: true) { LocalRevision = 1 });

    private static async Task<string[]> Ids(ILocalStore<ConformanceDocument> store, SyncIndexQuery<ConformanceDocument> query, int limit = 100, SyncIndexCursor? after = null) =>
        [.. (await store.QueryIndexAsync(query, after, limit)).Select(d => d.Id)];

    /// <summary>All cases.</summary>
    public static IReadOnlyList<ConformanceCase> Cases { get; } =
    [
        new("Index ADR-018: ranges in both directions; equal keys by id; null first; numbers in numeric order", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Clean("a", "b"), Clean("b", "a"), Edited("c", "c:x"), Clean("d", "b"), Clean("e", "bbbbbbbbbb"), Clean("f", "b:a")]);

            Check.Sequence(["b", "a", "d", "f", "e", "c"], await Ids(store, Title.All())); // ":" (3A) sorts before "b" (62)
            Check.Sequence(["c", "e", "f", "d", "a", "b"], await Ids(store, Title.All().Descending()));
            Check.Sequence(["a", "d"], await Ids(store, Title.Equal("b")));
            Check.Sequence(["a", "d", "f", "e", "c"], await Ids(store, Title.Between("b", "c:x")));
            Check.Sequence(["f", "e", "c"], await Ids(store, Title.From("b", exclusive: true)));
            Check.Sequence(["b", "a", "d"], await Ids(store, Title.Before("b", inclusive: true)));
            Check.Sequence(["b"], await Ids(store, Title.Before("b")));
            Check.Sequence(["d", "a"], await Ids(store, Title.Equal("b").Descending()));
            Check.Sequence(["a", "b", "d", "c", "f", "e"], await Ids(store, Length.All())); // 1, 1, 1, 3, 3, 10: not "10" < "3"
            Check.Sequence(["e"], await Ids(store, Length.From(4)));
            Check.Sequence(["a", "b", "d", "e", "f", "c"], await Ids(store, Tag.All())); // four nulls first, then "a", "x"
            Check.Sequence(["f", "c"], await Ids(store, Tag.From("")));
            Check.Equal(2, await store.CountIndexAsync(Title.Equal("b")));
            Check.Equal(6, await store.CountIndexAsync(Length.All()));
            Check.Equal(0, await store.CountIndexAsync(Title.Between("x", "a")));
        }),

        new("Index ADR-018 I08: pages resume after the cursor across equal keys, in both directions, without gaps or repeats", async create =>
        {
            var store = await create();
            await store.UpdateAsync([.. Enumerable.Range(0, 11).Select(i => Clean($"s{i:D2}", "same")), Clean("first", "a"), Clean("last", "z")]);

            foreach (var query in new[] { Title.All(), Title.All().Descending(), Title.Equal("same") })
            {
                var all = await Ids(store, query);
                var paged = new List<string>();
                SyncIndexCursor? after = null;
                while (true)
                {
                    var page = await store.QueryIndexAsync(query, after, 3);
                    paged.AddRange(page.Select(d => d.Id));
                    if (page.Count < 3)
                    {
                        break;
                    }

                    after = LocalStoreIndexing.CursorOf(query, page[^1]);
                }

                Check.Sequence(all, paged);
            }

            Check.Equal(13, (await Ids(store, Title.All())).Length);
        }),

        new("Index ADR-018 I10: a changed document moves; deleted, hidden and purged documents leave the index", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Clean("a", "a"), Clean("b", "b"), Clean("c", "c"), Clean("old", "d", generation: 0), Clean("gone", "e")]);

            await store.UpdateAsync([
                new("a", r => r! with { Current = Document("a", "zz"), IsDirty = true, LocalRevision = 2 }),
                new("b", r => r! with { Current = Document("b", "b", deleted: true), IsDirty = true, LocalRevision = 2 }),
                new("c", r => r! with { MissingAfterReset = true }),
            ]);
            await store.PurgeAsync(["old"], generation: 1);

            Check.Sequence(["gone", "a"], await Ids(store, Title.All()));
            Check.Equal(2, await store.CountIndexAsync(Title.All()));
            Check.Sequence(["a"], await Ids(store, Length.From(2)));

            await store.UpdateAsync([new("c", r => r! with { MissingAfterReset = false })]);
            Check.Sequence(["c", "gone", "a"], await Ids(store, Title.All()));
        }),

        new("Index ADR-018 I03: a failed update leaves the index unchanged", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Clean("a", "a")]);

            await Check.Throws<ConformanceFault>(() => store.UpdateAsync([
                new("a", r => r! with { Current = Document("a", "moved") }),
                new("b", _ => throw new ConformanceFault("boom")),
            ]));

            Check.Sequence(["a"], await Ids(store, Title.Equal("a")));
            Check.Equal(0, await store.CountIndexAsync(Title.Equal("moved")));
        }),

        new("Index ADR-018 T59: string keys compare by UTF-16 code units (ordinal), also beyond the BMP", async create =>
        {
            var store = await create();
            await store.UpdateAsync([Clean("1", "�"), Clean("2", "z"), Clean("3", "\U0001F600"), Clean("4", "é"), Clean("5", "Z")]);

            // Ordinal: 'Z' (5A) < 'z' (7A) < 'é' (E9) < U+1F600 (D83D DE00) < U+FFFD. UTF-8 byte order would put U+FFFD first.
            Check.Sequence(["5", "2", "4", "3", "1"], await Ids(store, Title.All()));
        }),

        new("Index ADR-018: an index the store was not opened with is evaluated in memory, with the same results", async create =>
        {
            var store = await create();
            var reversed = SyncIndex<ConformanceDocument>.Create("reversed", d => new string([.. d.Title.Reverse()]));
            await store.UpdateAsync([Clean("a", "ab"), Clean("b", "ba"), Clean("c", "ca")]);

            Check.Sequence(["b", "c", "a"], await Ids(store, reversed.All()));
            Check.Equal(2, await store.CountIndexAsync(reversed.Between("a", "az")));
        }),
    ];
}
