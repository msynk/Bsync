using Bsync.Storage;

namespace Bsync.Client;

/// <summary>A bounded, in-memory query over a collection.</summary>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed record SyncQuery<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>The largest allowed <see cref="Limit"/>.</summary>
    public const int MaxLimit = 1000;

    /// <summary>Keeps only matching documents. Default: all.</summary>
    public Func<TDocument, bool>? Where { get; init; }

    /// <summary>
    /// Sort order evaluated in memory, which reads the whole collection. Default: by id (ordinal). Prefer
    /// <see cref="Index"/> for large collections; the two cannot be combined.
    /// </summary>
    public Comparison<TDocument>? Order { get; init; }

    /// <summary>
    /// A range of a declared index and its direction (ADR-018), for example <c>Due.From(today).Descending()</c>. Stores
    /// opened with the index read only the requested page; <see cref="Where"/> filters what the range returns.
    /// </summary>
    public SyncIndexQuery<TDocument>? Index { get; init; }

    /// <summary>How many matching documents to skip before the first one returned. Default 0.</summary>
    public int Skip { get; init; }

    /// <summary>Maximum number of documents returned (1 to <see cref="MaxLimit"/>). Default 100.</summary>
    public int Limit { get; init; } = 100;

    /// <summary>
    /// Evaluates the query in memory: drops deleted documents, keeps those matching <see cref="Where"/>, sorts by
    /// <see cref="Order"/> and returns at most <see cref="Limit"/>. For <see cref="ISyncCollection{TDocument}"/>
    /// implementations that read their documents from elsewhere.
    /// </summary>
    /// <param name="documents">The documents to evaluate.</param>
    public IReadOnlyList<TDocument> Apply(IEnumerable<TDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Queries.Validate(this);

        var matching = documents.Where(d => !d.Deleted && (Where?.Invoke(d) ?? true)).ToList();
        if (Index is { } index)
        {
            var keyed = matching
                .Select(d => (Cursor: Storage.LocalStoreIndexing.CursorOf(index, d), Document: d))
                .Where(e => index.Contains(e.Cursor.Key))
                .ToList();
            keyed.Sort((a, b) => index.Compare(a.Cursor, b.Cursor));
            matching = [.. keyed.Select(e => e.Document)];
        }
        else
        {
            matching.Sort(Order ?? ((a, b) => string.CompareOrdinal(a.Id, b.Id)));
        }

        return [.. matching.Skip(Skip).Take(Limit)];
    }
}
