using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>The outcome of <see cref="SqliteStoreRecovery.RebuildAsync(string, CancellationToken)"/>.</summary>
/// <param name="DamagedCopy">Where the old database file now is (never deleted).</param>
/// <param name="SalvagedRecords">Records with local changes or kept conflicts copied into the new database.</param>
/// <param name="ReadError">The first read problem, or <see langword="null"/> if the old file was read completely.</param>
/// <param name="DiscardedRecords">Rows that could be read but were damaged (malformed JSON, timestamp or key) and were not copied.</param>
/// <remarks>Records on unreadable pages are not counted anywhere: the damage hides how many there were.</remarks>
public sealed record SqliteRebuildReport(string DamagedCopy, int SalvagedRecords, string? ReadError, int DiscardedRecords = 0)
{
    /// <summary>Whether every record with local changes could be read and copied.</summary>
    public bool Complete => ReadError is null;
}
