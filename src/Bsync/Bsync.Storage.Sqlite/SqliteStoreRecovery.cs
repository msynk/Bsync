using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>
/// Detects a damaged replica database and rebuilds it without losing unsynchronized work that can still be read
/// (docs/operations/disaster-recovery.md).
/// </summary>
/// <remarks>
/// Close every engine and store that uses the file before calling <see cref="RebuildAsync"/>. The damaged file is
/// never deleted: it is renamed so it can be inspected or sent for support.
/// </remarks>
public static class SqliteStoreRecovery
{
    /// <summary>
    /// Runs SQLite's <c>quick_check</c> on the database. Returns the problems found; an empty list means the file
    /// is structurally sound. A file that cannot be opened as a database is reported, not thrown.
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(string dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        if (!File.Exists(dataSource))
        {
            return [$"The database file '{dataSource}' does not exist."];
        }

        try
        {
            await using var connection = new SqliteConnection(ReadOnly(dataSource));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check";
            var problems = new List<string>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var line = reader.GetString(0);
                if (line != "ok")
                {
                    problems.Add(line);
                }
            }

            return problems;
        }
        catch (SqliteException error)
        {
            return [error.Message];
        }
    }

    /// <summary>
    /// Moves the database (with its WAL and shared-memory files) aside, creates a new empty database in its place,
    /// and copies into it every record with unsynchronized local changes or a kept conflict that can still be read,
    /// with its pending operation unchanged (same operation id, so a resend is replayed, never applied twice).
    /// </summary>
    /// <remarks>
    /// The new database has a new replica id and incarnation; derive the clock node from it as usual. Each collection
    /// restarts from the beginning of the server feed in a new generation, so records the server no longer has are
    /// hidden after the first complete sync (protocol §6.1). Clean records are not copied; the server has them.
    /// </remarks>
    /// <exception cref="IOException">The file is still in use.</exception>
    public static async Task<SqliteRebuildReport> RebuildAsync(string dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        SqliteStorePool.Release(dataSource);
        var damaged = $"{dataSource}.damaged-{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{SqliteSchema.NewId()[..8]}";
        File.Move(dataSource, damaged);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dataSource + suffix))
            {
                File.Move(dataSource + suffix, damaged + suffix);
            }
        }

        var salvage = await SalvageAsync(damaged, cancellationToken).ConfigureAwait(false);

        await using var connection = new SqliteConnection(SqliteStorePool.ConnectionString(dataSource));
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var row in salvage.Rows)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO bs_records ({string.Join(", ", salvage.Columns)}) VALUES ({string.Join(", ", salvage.Columns.Select((_, i) => $"$p{i}"))})";
            for (var i = 0; i < row.Length; i++)
            {
                insert.Parameters.AddWithValue($"$p{i}", row[i]);
            }

            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Every collection that had records resnapshots in a generation newer than any salvaged record.
        var generationIndex = Array.IndexOf(salvage.Columns, "generation");
        foreach (var collection in salvage.Rows.Select(r => (string)r[0]).Concat(salvage.HighWater.Keys).Distinct(StringComparer.Ordinal))
        {
            var generation = salvage.Rows.Where(r => (string)r[0] == collection).Select(r => Convert.ToInt64(r[generationIndex], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max() + 1;
            await SqliteSchema.SetMetaAsync(connection, transaction, collection, "generation", generation.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await SqliteSchema.SetMetaAsync(connection, transaction, collection, "resnapshot", "1", cancellationToken).ConfigureAwait(false);
            await SqliteSchema.SetMetaAsync(connection, transaction, collection, "purge_missing", "0", cancellationToken).ConfigureAwait(false);
            var highWater = salvage.Rows.Where(r => (string)r[0] == collection).Select(r => (string)r[4])
                .Concat(salvage.HighWater.TryGetValue(collection, out var stored) ? [stored] : [])
                .DefaultIfEmpty(null)
                .Max(StringComparer.Ordinal);
            if (highWater is not null)
            {
                // Canonical HLC strings order like the timestamps they encode, so the new clock never moves back.
                await SqliteSchema.SetMetaAsync(connection, transaction, collection, "clock_high_water", highWater, cancellationToken).ConfigureAwait(false);
            }
        }

        transaction.Commit();
        return new SqliteRebuildReport(damaged, salvage.Rows.Count, salvage.Error, salvage.Discarded);
    }

    private sealed record Salvage(string[] Columns, List<object[]> Rows, Dictionary<string, string> HighWater, string? Error, int Discarded);

    private static readonly HashSet<string> JsonColumns = new(StringComparer.Ordinal)
    {
        "current", "base", "pending_payload", "observed", "conflict_server", "conflict_local", "conflict_base",
    };

    /// <summary>A readable row can still hold damaged bytes; copy it only if it is self-consistent.</summary>
    private static bool IsIntact(string[] columns, object[] row)
    {
        try
        {
            string? id = null;
            byte[]? idKey = null;
            for (var i = 0; i < columns.Length; i++)
            {
                switch (columns[i], row[i])
                {
                    case ("id", string text):
                        id = text;
                        break;
                    case ("id_key", byte[] key):
                        idKey = key;
                        break;
                    case ("updated_at", string stamp):
                        _ = Bsync.Clocks.HlcTimestamp.Parse(stamp);
                        break;
                    case ("updated_at", _):
                        return false;
                    case (var name, string json) when JsonColumns.Contains(name):
                        using (System.Text.Json.JsonDocument.Parse(json))
                        {
                        }

                        break;
                }
            }

            return id is not null && idKey is not null && SyncIds.IsValid(id) && System.Text.Encoding.BigEndianUnicode.GetBytes(id).AsSpan().SequenceEqual(idKey);
        }
        catch (Exception error) when (error is FormatException or System.Text.Json.JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static async Task<Salvage> SalvageAsync(string damaged, CancellationToken cancellationToken)
    {
        var columns = SqliteSchema.Version1Columns.Split(", ");
        var rows = new List<object[]>();
        var highWater = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            await using var connection = new SqliteConnection(ReadOnly(damaged));
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = await SqliteSchema.ReadVersionAsync(connection, null, cancellationToken).ConfigureAwait(false);
            if (version is < 1 or > SqliteSchema.CurrentVersion)
            {
                return new Salvage(columns, rows, highWater, $"Unsupported schema version {version}; nothing was copied.", 0);
            }

            if (version >= 2)
            {
                columns = [.. columns, .. SqliteSchema.Version2Columns.Split(", ")];
            }

            if (version >= 3)
            {
                columns = [.. columns, .. SqliteSchema.Version3Columns.Split(", ")];
            }

            if (version >= 4)
            {
                columns = [.. columns, .. SqliteSchema.Version4Columns.Split(", ")];
            }

            // Scan the table itself (an index may be the damaged part), in rowid order and in small ranges, so a damaged
            // page loses only the rows on it: after a read error, probe further ahead with growing steps.
            var filter = version >= 2 ? "(is_dirty = 1 OR conflict_local IS NOT NULL)" : "is_dirty = 1";
            var after = long.MinValue;
            var discarded = 0;
            var step = 1L;
            string? error = null;
            for (var failures = 0; failures < 64;)
            {
                try
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT rowid, {string.Join(", ", columns)} FROM bs_records NOT INDEXED WHERE rowid > $after AND {filter} ORDER BY rowid LIMIT 100";
                    command.Parameters.AddWithValue("$after", after);
                    var read = 0;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        after = reader.GetInt64(0);
                        var row = new object[columns.Length];
                        for (var i = 0; i < columns.Length; i++)
                        {
                            row[i] = reader.GetValue(i + 1);
                        }

                        if (IsIntact(columns, row))
                        {
                            rows.Add(row);
                        }
                        else
                        {
                            discarded++;
                        }

                        read++;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    (failures, step) = (0, 1);
                }
                catch (SqliteException damage) when ((damage.SqliteErrorCode & 0xFF) is 11 or 26) // SQLITE_CORRUPT*, SQLITE_NOTADB
                {
                    error ??= damage.Message;
                    after = after == long.MinValue ? 0 : after + step;
                    step = Math.Min(step * 2, 1L << 20);
                    failures++;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT collection, value FROM bs_meta WHERE key = 'clock_high_water'";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    highWater[reader.GetString(0)] = reader.GetString(1);
                }
            }

            return new Salvage(columns, rows, highWater, error ?? (discarded > 0 ? $"{discarded} damaged record(s) were skipped." : null), discarded);
        }
        catch (SqliteException error)
        {
            // Keep what was read before the damage.
            return new Salvage(columns, rows, highWater, error.Message, 0);
        }
    }

    private static string ReadOnly(string dataSource) => new SqliteConnectionStringBuilder
    {
        DataSource = dataSource,
        Mode = SqliteOpenMode.ReadOnly,
        Pooling = false,
    }.ToString();
}
