namespace Bsync.Storage;

/// <summary>
/// In-memory evaluation of index queries (ADR-018): the default for stores that do not maintain an index, and the
/// fallback of stores that do, for indexes they were not opened with. It reads the whole collection.
/// </summary>
public static class LocalStoreIndexing
{
    /// <summary>Evaluates <paramref name="query"/> over every live, visible document of <paramref name="store"/>.</summary>
    public static async Task<IReadOnlyList<TDocument>> QueryAsync<TDocument>(
        ILocalStore<TDocument> store,
        SyncIndexQuery<TDocument> query,
        SyncIndexCursor? after,
        int limit,
        CancellationToken cancellationToken = default)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var entries = await EntriesAsync(store, query, cancellationToken).ConfigureAwait(false);
        entries.Sort((a, b) => query.Compare(a.Cursor, b.Cursor));
        return [.. entries.Where(e => after is not { } position || query.Compare(e.Cursor, position) > 0).Take(limit).Select(e => e.Document)];
    }

    /// <summary>Counts the live, visible documents of <paramref name="store"/> within <paramref name="query"/>'s range.</summary>
    public static async Task<int> CountAsync<TDocument>(ILocalStore<TDocument> store, SyncIndexQuery<TDocument> query, CancellationToken cancellationToken = default)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(query);
        return (await EntriesAsync(store, query, cancellationToken).ConfigureAwait(false)).Count;
    }

    /// <summary>The position of <paramref name="document"/> in <paramref name="query"/>'s index.</summary>
    public static SyncIndexCursor CursorOf<TDocument>(SyncIndexQuery<TDocument> query, TDocument document)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(document);
        return new SyncIndexCursor(query.Index.KeyOf(document), document.Id);
    }

    /// <summary>A stable signature of a set of indexes (names and versions), for detecting when stored keys must be rebuilt.</summary>
    public static string Signature<TDocument>(IEnumerable<SyncIndex<TDocument>> indexes)
        where TDocument : class, ISyncEntity
    {
        ArgumentNullException.ThrowIfNull(indexes);
        return string.Join(";", indexes.Select(i => $"{i.Name}@{i.Version}").Order(StringComparer.Ordinal));
    }

    /// <summary>Throws when two indexes share a name.</summary>
    public static IReadOnlyList<SyncIndex<TDocument>> Validate<TDocument>(IEnumerable<SyncIndex<TDocument>>? indexes)
        where TDocument : class, ISyncEntity
    {
        var list = indexes?.ToList() ?? [];
        if (list.Any(i => i is null))
        {
            throw new ArgumentException("An index is null.", nameof(indexes));
        }

        if (list.GroupBy(i => i.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } duplicate)
        {
            throw new ArgumentException($"Two indexes are named '{duplicate.Key}'.", nameof(indexes));
        }

        return list;
    }

    private static async Task<List<(SyncIndexCursor Cursor, TDocument Document)>> EntriesAsync<TDocument>(ILocalStore<TDocument> store, SyncIndexQuery<TDocument> query, CancellationToken cancellationToken)
        where TDocument : class, ISyncEntity =>
        [.. (await store.QueryAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .Select(d => (Cursor: CursorOf(query, d), Document: d))
            .Where(e => query.Contains(e.Cursor.Key))];
}
