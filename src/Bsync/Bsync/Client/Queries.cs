namespace Bsync.Client;

/// <summary>Shared query evaluation.</summary>
internal static class Queries
{
    private static readonly System.Diagnostics.Metrics.Counter<long> MaterializedRecords = Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.query.materialized", "{document}", "Documents materialized by collection queries, including predicate misses and skipped records.");
    private static readonly System.Diagnostics.Metrics.Counter<long> ReturnedRecords = Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.query.returned", "{document}", "Documents returned by collection queries.");
    public static void Materialized(int count, string mode) => MaterializedRecords.Add(count, new KeyValuePair<string, object?>("bsync.query.mode", mode));
    public static void Returned(int count, string mode) => ReturnedRecords.Add(count, new KeyValuePair<string, object?>("bsync.query.mode", mode));
    public const int PageSize = 200;

    public static void Validate<TDocument>(SyncQuery<TDocument> query)
        where TDocument : class, ISyncEntity
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Limit, 1, nameof(query.Limit));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Limit, SyncQuery<TDocument>.MaxLimit, nameof(query.Limit));
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip, nameof(query.Skip));
        if (query.Order is not null && query.Index is not null)
        {
            throw new ArgumentException("A query is ordered either by Order or by Index, not both.", nameof(query));
        }
    }
}
