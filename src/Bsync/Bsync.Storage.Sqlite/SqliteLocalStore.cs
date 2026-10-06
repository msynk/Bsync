using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>
/// A durable <see cref="ILocalStore{TDocument}"/> backed by SQLite (Microsoft.Data.Sqlite), for native
/// hosts. Every <see cref="UpdateAsync"/> is one <c>BEGIN IMMEDIATE</c> transaction, so updates from any
/// number of store instances and processes on the same file are serialized and atomic.
/// </summary>
/// <remarks>
/// <para>
/// Documents are stored as JSON produced by the supplied <see cref="JsonTypeInfo{T}"/> (source-generated
/// metadata keeps the store trim/AOT safe). Ids are ordered by an extra UTF-16 big-endian key column, so
/// SQLite's byte ordering equals .NET ordinal ordering without a custom collation, and the file stays
/// readable by standard SQLite tools.
/// </para>
/// <para>
/// The schema version is kept in <c>PRAGMA user_version</c>. Opening a database with a newer schema throws
/// <see cref="SqliteStoreSchemaException"/> and changes nothing.
/// </para>
/// </remarks>
/// <typeparam name="TDocument">The synchronized entity type.</typeparam>
public sealed class SqliteLocalStore<TDocument> : ILocalStore<TDocument>
    where TDocument : class, ISyncEntity
{
    /// <summary>The schema version this implementation creates and understands.</summary>
    public const int SchemaVersion = SqliteSchema.CurrentVersion;

    private const string DatabaseScope = "";
    private const string Columns =
        "id, current, base, base_version, is_dirty, local_revision, pending_id, pending_revision, pending_base_version, " +
        "pending_payload, rejection_revision, rejection_code, rejection_message, observed, observed_version, generation, missing, " +
        "conflict_server, conflict_server_version, conflict_local, conflict_base, group_id, group_members, pending_group, pending_group_size, base_same";

    private readonly string _connectionString;
    private readonly string _collection;
    private readonly JsonTypeInfo<TDocument> _typeInfo;
    private readonly string _synchronous;
    private readonly int _busyTimeoutMs;

    private SqliteLocalStore(SqliteLocalStoreOptions options, JsonTypeInfo<TDocument> typeInfo)
    {
        _connectionString = SqliteStorePool.ConnectionString(options.DataSource);
        _collection = options.Collection;
        _typeInfo = typeInfo;
        _synchronous = options.Durability == SqliteDurability.Full ? "FULL" : "NORMAL";
        _busyTimeoutMs = (int)options.BusyTimeout.TotalMilliseconds;
    }

    /// <summary>Opens (creating or upgrading if needed) a store.</summary>
    /// <exception cref="SqliteStoreSchemaException">The database uses a newer schema.</exception>
    public static async Task<SqliteLocalStore<TDocument>> OpenAsync(
        SqliteLocalStoreOptions options,
        JsonTypeInfo<TDocument> typeInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(typeInfo);
        ArgumentException.ThrowIfNullOrEmpty(options.DataSource);
        if (!SyncIds.IsValid(options.Collection))
        {
            throw new ArgumentException("The collection name must be a valid identifier.", nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(options.BusyTimeout, TimeSpan.Zero, nameof(options.BusyTimeout));

        var store = new SqliteLocalStore<TDocument>(options, typeInfo);
        await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }

    /// <inheritdoc />
    public async Task<SyncRecord<TDocument>?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRecordAsync(connection, null, id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RecordUpdateResult<TDocument>>> UpdateAsync(
        IReadOnlyList<RecordUpdate<TDocument>> updates,
        ReplicaCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        cancellationToken.ThrowIfCancellationRequested();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (!seen.Add(update.Id))
            {
                throw new ArgumentException($"Duplicate record id '{update.Id}' in one update.", nameof(updates));
            }
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE

        var results = new List<RecordUpdateResult<TDocument>>(updates.Count);
        var highWater = await ReadHighWaterAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var newHighWater = highWater;
        foreach (var update in updates)
        {
            var existing = await ReadRecordAsync(connection, transaction, update.Id, cancellationToken).ConfigureAwait(false);
            var next = update.Transform(existing);
            if (next is null)
            {
                // Re-read so the caller never receives the transform's (possibly mutated) working copy.
                results.Add(new RecordUpdateResult<TDocument>(
                    existing is null ? null : await ReadRecordAsync(connection, transaction, update.Id, cancellationToken).ConfigureAwait(false),
                    Changed: false));
                continue;
            }

            if (!string.Equals(next.Current.Id, update.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Transform for '{update.Id}' returned a record with id '{next.Current.Id}'.");
            }

            var written = await WriteRecordAsync(connection, transaction, next, cancellationToken).ConfigureAwait(false);
            newHighWater = Max(newHighWater, next.Current.UpdatedAt);
            if (next.Pending is { } pending)
            {
                newHighWater = Max(newHighWater, pending.Payload.UpdatedAt);
            }

            // The committed state, rebuilt from the JSON just written: as independent as a re-read, without the query.
            results.Add(new RecordUpdateResult<TDocument>(written, Changed: true));
        }

        if (newHighWater > highWater)
        {
            await SetMetaAsync(connection, transaction, _collection, "clock_high_water", newHighWater.Encode(), cancellationToken).ConfigureAwait(false);
        }

        if (cursor is { } committed)
        {
            if (committed.Checkpoint.IsStart)
            {
                await DeleteMetaAsync(connection, transaction, "checkpoint", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SetMetaAsync(connection, transaction, _collection, "checkpoint", committed.Checkpoint.Value!, cancellationToken).ConfigureAwait(false);
            }

            await SetMetaAsync(connection, transaction, _collection, "generation", committed.Generation.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            await SetMetaAsync(connection, transaction, _collection, "resnapshot", committed.Resnapshot ? "1" : "0", cancellationToken).ConfigureAwait(false);
            await SetMetaAsync(connection, transaction, _collection, "purge_missing", committed.PurgeMissing ? "1" : "0", cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetPendingAsync(
        int limit,
        IReadOnlySet<string>? exclude = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // Keyset pagination over the pending index; excluded ids are skipped without an unbounded IN list.
        var found = new List<SyncRecord<TDocument>>(limit);
        string? lastUpdatedAt = null;
        byte[]? lastKey = null;
        var page = limit + Math.Min(exclude?.Count ?? 0, 1000);
        while (found.Count < limit)
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT {Columns}, updated_at, id_key FROM bs_records " +
                "WHERE collection = $c AND is_dirty = 1 AND rejection_code IS NULL " +
                (lastKey is null ? string.Empty : "AND (updated_at > $u OR (updated_at = $u AND id_key > $k)) ") +
                "ORDER BY updated_at, id_key LIMIT $n";
            command.Parameters.AddWithValue("$c", _collection);
            command.Parameters.AddWithValue("$n", page);
            if (lastKey is not null)
            {
                command.Parameters.AddWithValue("$u", lastUpdatedAt);
                command.Parameters.AddWithValue("$k", lastKey);
            }

            var rows = 0;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    rows++;
                    lastUpdatedAt = reader.GetString(26);
                    lastKey = (byte[])reader.GetValue(27);
                    var record = ReadRecord(reader);
                    if ((exclude is null || !exclude.Contains(record.Current.Id)) && found.Count < limit)
                    {
                        found.Add(record);
                    }
                }
            }

            if (rows < page)
            {
                break;
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<int> CountDirtyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM bs_records WHERE collection = $c AND is_dirty = 1";
        command.Parameters.AddWithValue("$c", _collection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public async Task<int> PurgeTombstonesAsync(long throughVersion, long generation, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM bs_records
            WHERE collection = $c AND deleted = 1 AND is_dirty = 0 AND conflict_local IS NULL AND base_version <= $v AND generation = $g
            """;
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$v", throughVersion);
        command.Parameters.AddWithValue("$g", generation);
        var removed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc />
    public async Task<SyncIssueCounts> CountIssuesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                COALESCE(SUM(CASE WHEN conflict_local IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN is_dirty = 1 AND rejection_code IS NOT NULL THEN 1 ELSE 0 END), 0),
                COALESCE(SUM(CASE WHEN is_dirty = 1 AND rejection_code = 'group-failed' THEN 1 ELSE 0 END), 0)
            FROM bs_records WHERE collection = $c AND (conflict_local IS NOT NULL OR (is_dirty = 1 AND rejection_code IS NOT NULL))
            """;
        command.Parameters.AddWithValue("$c", _collection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new SyncIssueCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetStaleAsync(long generation, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {Columns} FROM bs_records WHERE collection = $c AND is_dirty = 0 AND missing = 0 AND generation < $g " +
            "ORDER BY id_key LIMIT $n";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$g", generation);
        command.Parameters.AddWithValue("$n", limit);
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetConflictsAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM bs_records WHERE collection = $c AND conflict_local IS NOT NULL ORDER BY id_key LIMIT $n";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$n", limit);
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncRecord<TDocument>>> GetRejectedAsync(int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Rejected records are dirty, so the partial dirty index bounds the scan.
        command.CommandText = $"SELECT {Columns} FROM bs_records WHERE collection = $c AND is_dirty = 1 AND rejection_code IS NOT NULL ORDER BY id_key LIMIT $n";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$n", limit);
        return await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> PurgeAsync(IReadOnlyList<string> ids, long generation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        cancellationToken.ThrowIfCancellationRequested();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        var removed = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM bs_records WHERE collection = $c AND id = $id AND is_dirty = 0 AND conflict_local IS NULL AND generation < $g";
            command.Parameters.AddWithValue("$c", _collection);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$g", generation);
            removed += await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return removed;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryAsync(bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT current FROM bs_records WHERE collection = $c AND missing = 0 AND ($all = 1 OR deleted = 0) ORDER BY id_key";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$all", includeDeleted ? 1 : 0);
        var documents = new List<TDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(Deserialize(reader.GetString(0)));
        }

        return documents;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryPageAsync(string? afterId, int limit, bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The partial index bs_records_visible (collection, id_key) WHERE missing = 0 serves the range and the order.
        command.CommandText =
            "SELECT current FROM bs_records WHERE collection = $c AND missing = 0 AND ($all = 1 OR deleted = 0) AND ($after IS NULL OR id_key > $after) ORDER BY id_key LIMIT $n";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$all", includeDeleted ? 1 : 0);
        command.Parameters.AddWithValue("$after", afterId is null ? DBNull.Value : OrdinalKey(afterId));
        command.Parameters.AddWithValue("$n", limit);
        var documents = new List<TDocument>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(Deserialize(reader.GetString(0)));
        }

        return documents;
    }

    /// <inheritdoc />
    public async Task<ReplicaCursor> GetCursorAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var checkpoint = await GetMetaAsync(connection, null, _collection, "checkpoint", cancellationToken).ConfigureAwait(false);
        var generation = await GetMetaAsync(connection, null, _collection, "generation", cancellationToken).ConfigureAwait(false);
        var resnapshot = await GetMetaAsync(connection, null, _collection, "resnapshot", cancellationToken).ConfigureAwait(false);
        var purge = await GetMetaAsync(connection, null, _collection, "purge_missing", cancellationToken).ConfigureAwait(false);
        return new ReplicaCursor(
            new Checkpoint(checkpoint),
            generation is null ? 0 : long.Parse(generation, CultureInfo.InvariantCulture),
            resnapshot == "1",
            purge == "1");
    }

    /// <inheritdoc />
    public async Task<HlcTimestamp> GetClockHighWaterAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadHighWaterAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns the database's replica id and current incarnation.</summary>
    public async Task<ReplicaIdentity> GetReplicaIdentityAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return new ReplicaIdentity(
            (await GetMetaAsync(connection, null, DatabaseScope, "replica_id", cancellationToken).ConfigureAwait(false))!,
            (await GetMetaAsync(connection, null, DatabaseScope, "incarnation", cancellationToken).ConfigureAwait(false))!);
    }

    /// <summary>
    /// Assigns a new incarnation id. Call this when the database file may be a copy (restored from a device
    /// backup, cloned to another device) before using it, and derive the HLC node id from the result.
    /// </summary>
    public async Task<ReplicaIdentity> BeginNewIncarnationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        await SetMetaAsync(connection, transaction, DatabaseScope, "incarnation", NewId(), cancellationToken).ConfigureAwait(false);
        transaction.Commit();
        return await GetReplicaIdentityAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static HlcTimestamp Max(HlcTimestamp a, HlcTimestamp b) => a >= b ? a : b;

    /// <summary>UTF-16 big-endian bytes: memcmp order equals <see cref="string.CompareOrdinal(string, string)"/>.</summary>
    private static byte[] OrdinalKey(string id) => Encoding.BigEndianUnicode.GetBytes(id);

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await SqliteSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                connection,
                null,
                $"PRAGMA synchronous = {_synchronous}; PRAGMA busy_timeout = {_busyTimeoutMs.ToString(CultureInfo.InvariantCulture)}",
                cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> GetMetaAsync(SqliteConnection connection, SqliteTransaction? transaction, string scope, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM bs_meta WHERE collection = $c AND key = $k";
        command.Parameters.AddWithValue("$c", scope);
        command.Parameters.AddWithValue("$k", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private static async Task SetMetaAsync(SqliteConnection connection, SqliteTransaction transaction, string scope, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO bs_meta (collection, key, value) VALUES ($c, $k, $v) ON CONFLICT (collection, key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$c", scope);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteMetaAsync(SqliteConnection connection, SqliteTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM bs_meta WHERE collection = $c AND key = $k";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$k", key);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HlcTimestamp> ReadHighWaterAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        var value = await GetMetaAsync(connection, transaction, _collection, "clock_high_water", cancellationToken).ConfigureAwait(false);
        return value is null ? HlcTimestamp.MinValue : HlcTimestamp.Parse(value);
    }

    private async Task<SyncRecord<TDocument>?> ReadRecordAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {Columns} FROM bs_records WHERE collection = $c AND id = $id";
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$id", id);
        var records = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
        return records.Count == 0 ? null : records[0];
    }

    private async Task<IReadOnlyList<SyncRecord<TDocument>>> ReadAllAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var records = new List<SyncRecord<TDocument>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            records.Add(ReadRecord(reader));
        }

        return records;
    }

    private SyncRecord<TDocument> ReadRecord(SqliteDataReader reader)
    {
        long? NullableInt64(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
        string? NullableString(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        var pendingId = NullableString(6);
        var rejectionCode = NullableString(11);
        var observed = NullableString(13);
        var current = reader.GetString(1);
        var baseDocument = reader.GetInt64(25) != 0 ? current : NullableString(2);
        return new SyncRecord<TDocument>(Deserialize(current), baseDocument is null ? null : Deserialize(baseDocument), reader.GetInt64(4) != 0)
        {
            BaseVersion = NullableInt64(3),
            LocalRevision = reader.GetInt64(5),
            Pending = pendingId is null
                ? null
                : new PendingOperation<TDocument>(pendingId, reader.GetInt64(7), NullableInt64(8), Deserialize(reader.GetString(9)))
                {
                    Group = NullableString(23),
                    GroupSize = reader.IsDBNull(24) ? 0 : reader.GetInt32(24),
                },
            Rejection = rejectionCode is null ? null : new SyncRejection(reader.GetInt64(10), rejectionCode, NullableString(12)),
            Observed = observed is null ? null : Deserialize(observed),
            ObservedVersion = NullableInt64(14),
            Generation = reader.GetInt64(15),
            MissingAfterReset = reader.GetInt64(16) != 0,
            Conflict = reader.IsDBNull(19)
                ? null
                : new SyncConflict<TDocument>(
                    Deserialize(reader.GetString(17)),
                    reader.GetInt64(18),
                    Deserialize(reader.GetString(19)),
                    NullableString(20) is { } conflictBase ? Deserialize(conflictBase) : null),
            Group = NullableString(21) is { } groupId
                ? new SyncGroup(groupId, JsonSerializer.Deserialize(reader.GetString(22), SqliteJson.Default.StringArray)!)
                : null,
        };
    }

    private async Task<SyncRecord<TDocument>> WriteRecordAsync(SqliteConnection connection, SqliteTransaction transaction, SyncRecord<TDocument> record, CancellationToken cancellationToken)
    {
        // Each distinct document instance is serialized once (current, base and payload are often the same one), and the
        // returned copy is deserialized from exactly what was written (D9).
        var texts = new Dictionary<TDocument, string>(4, ReferenceEqualityComparer.Instance);
        var copies = new Dictionary<string, TDocument>(4, StringComparer.Ordinal);
        string Text(TDocument document) => texts.TryGetValue(document, out var text) ? text : texts[document] = Serialize(document);
        TDocument Copy(string text) => copies.TryGetValue(text, out var copy) ? copy : copies[text] = Deserialize(text);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO bs_records (
                collection, id, id_key, current, updated_at, deleted, base, base_version, is_dirty, local_revision,
                pending_id, pending_revision, pending_base_version, pending_payload,
                rejection_revision, rejection_code, rejection_message, observed, observed_version, generation, missing,
                conflict_server, conflict_server_version, conflict_local, conflict_base,
                group_id, group_members, pending_group, pending_group_size, base_same)
            VALUES ($c, $id, $key, $current, $updated, $deleted, $base, $baseVersion, $dirty, $revision,
                $pendingId, $pendingRevision, $pendingBase, $pendingPayload,
                $rejectionRevision, $rejectionCode, $rejectionMessage, $observed, $observedVersion, $generation, $missing,
                $conflictServer, $conflictServerVersion, $conflictLocal, $conflictBase,
                $groupId, $groupMembers, $pendingGroup, $pendingGroupSize, $baseSame)
            """;
        var p = command.Parameters;
        p.AddWithValue("$c", _collection);
        p.AddWithValue("$id", record.Current.Id);
        p.AddWithValue("$key", OrdinalKey(record.Current.Id));
        var current = Text(record.Current);
        var baseJson = record.Base is null ? null : Text(record.Base);
        var baseSame = baseJson is not null && baseJson == current;
        p.AddWithValue("$current", current);
        p.AddWithValue("$updated", record.Current.UpdatedAt.Encode());
        p.AddWithValue("$deleted", record.Current.Deleted ? 1 : 0);
        p.AddWithValue("$base", baseSame || baseJson is null ? DBNull.Value : baseJson);
        p.AddWithValue("$baseSame", baseSame ? 1 : 0);
        p.AddWithValue("$baseVersion", (object?)record.BaseVersion ?? DBNull.Value);
        p.AddWithValue("$dirty", record.IsDirty ? 1 : 0);
        p.AddWithValue("$revision", record.LocalRevision);
        p.AddWithValue("$pendingId", (object?)record.Pending?.OperationId ?? DBNull.Value);
        p.AddWithValue("$pendingRevision", (object?)record.Pending?.Revision ?? DBNull.Value);
        p.AddWithValue("$pendingBase", (object?)record.Pending?.BaseVersion ?? DBNull.Value);
        var payload = record.Pending is null ? null : Text(record.Pending.Payload);
        p.AddWithValue("$pendingPayload", (object?)payload ?? DBNull.Value);
        p.AddWithValue("$rejectionRevision", (object?)record.Rejection?.Revision ?? DBNull.Value);
        p.AddWithValue("$rejectionCode", (object?)record.Rejection?.ErrorCode ?? DBNull.Value);
        p.AddWithValue("$rejectionMessage", (object?)record.Rejection?.Message ?? DBNull.Value);
        var observed = record.Observed is null ? null : Text(record.Observed);
        p.AddWithValue("$observed", (object?)observed ?? DBNull.Value);
        p.AddWithValue("$observedVersion", (object?)record.ObservedVersion ?? DBNull.Value);
        p.AddWithValue("$generation", record.Generation);
        p.AddWithValue("$missing", record.MissingAfterReset ? 1 : 0);
        var conflictServer = record.Conflict is { } c ? Text(c.Server) : null;
        var conflictLocal = record.Conflict is { } cl ? Text(cl.Local) : null;
        var conflictBase = record.Conflict?.Base is { } cb ? Text(cb) : null;
        p.AddWithValue("$conflictServer", (object?)conflictServer ?? DBNull.Value);
        p.AddWithValue("$conflictServerVersion", (object?)record.Conflict?.ServerVersion ?? DBNull.Value);
        p.AddWithValue("$conflictLocal", (object?)conflictLocal ?? DBNull.Value);
        p.AddWithValue("$conflictBase", (object?)conflictBase ?? DBNull.Value);
        p.AddWithValue("$groupId", (object?)record.Group?.Id ?? DBNull.Value);
        p.AddWithValue("$groupMembers", record.Group is { } g ? JsonSerializer.Serialize(g.Members.ToArray(), SqliteJson.Default.StringArray) : DBNull.Value);
        p.AddWithValue("$pendingGroup", (object?)record.Pending?.Group ?? DBNull.Value);
        p.AddWithValue("$pendingGroupSize", record.Pending is { Group: not null } pg ? pg.GroupSize : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return record with
        {
            Current = Copy(current),
            Base = baseJson is null ? null : Copy(baseJson),
            Pending = record.Pending is { } pending ? pending with { Payload = Copy(payload!) } : null,
            Observed = observed is null ? null : Copy(observed),
            Conflict = record.Conflict is { } conflict
                ? conflict with { Server = Copy(conflictServer!), Local = Copy(conflictLocal!), Base = conflictBase is null ? null : Copy(conflictBase) }
                : null,
            Group = record.Group is { } group ? group with { Members = [.. group.Members] } : null,
        };
    }

    private string Serialize(TDocument document) => JsonSerializer.Serialize(document, _typeInfo);

    private TDocument Deserialize(string json) =>
        JsonSerializer.Deserialize(json, _typeInfo) ?? throw new InvalidDataException("A stored document deserialized to null.");
}
