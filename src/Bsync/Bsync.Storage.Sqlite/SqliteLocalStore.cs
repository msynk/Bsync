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
        "conflict_server, conflict_server_version, conflict_local, conflict_base, group_id, group_members, pending_group, pending_group_size, base_same, " +
        "rejection_arguments";

    private readonly string _connectionString;
    private readonly string _collection;
    private readonly JsonTypeInfo<TDocument> _typeInfo;
    private readonly string _synchronous;
    private readonly int _busyTimeoutMs;
    private readonly IReadOnlyList<SyncIndex<TDocument>> _indexes;
    private readonly string _indexSignature;

    private SqliteLocalStore(SqliteLocalStoreOptions options, JsonTypeInfo<TDocument> typeInfo, IReadOnlyList<SyncIndex<TDocument>> indexes)
    {
        _indexes = indexes;
        _indexSignature = LocalStoreIndexing.Signature(indexes);
#if BSYNC_SQLCIPHER
        _connectionString = SqliteStorePool.ConnectionString(options.DataSource, options.EncryptionKey);
#else
        _connectionString = SqliteStorePool.ConnectionString(options.DataSource);
#endif
        _collection = options.Collection;
        _typeInfo = typeInfo;
        _synchronous = options.Durability == SqliteDurability.Full ? "FULL" : "NORMAL";
        _busyTimeoutMs = (int)options.BusyTimeout.TotalMilliseconds;
    }

    /// <summary>Opens (creating or upgrading if needed) a store.</summary>
    /// <exception cref="SqliteStoreSchemaException">The database uses a newer schema.</exception>
    public static Task<SqliteLocalStore<TDocument>> OpenAsync(
        SqliteLocalStoreOptions options,
        JsonTypeInfo<TDocument> typeInfo,
        CancellationToken cancellationToken = default) =>
        OpenAsync(options, typeInfo, [], cancellationToken);

    /// <summary>
    /// Opens (creating or upgrading if needed) a store that maintains <paramref name="indexes"/> (ADR-018). When the
    /// declared set differs from the one the stored index rows were built for, they are rebuilt once, here. Every store
    /// instance of a collection should declare the same indexes: a writer with a different set marks the rows unusable,
    /// and queries then evaluate in memory until a store with the right set is opened again.
    /// </summary>
    /// <exception cref="SqliteStoreSchemaException">The database uses a newer schema.</exception>
    public static async Task<SqliteLocalStore<TDocument>> OpenAsync(
        SqliteLocalStoreOptions options,
        JsonTypeInfo<TDocument> typeInfo,
        IEnumerable<SyncIndex<TDocument>> indexes,
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

        var store = new SqliteLocalStore<TDocument>(options, typeInfo, LocalStoreIndexing.Validate(indexes));
        await store.EnsureSchemaAsync(cancellationToken).ConfigureAwait(false);
        await store.RebuildIndexesAsync(cancellationToken).ConfigureAwait(false);
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
        await using var commands = new UpdateCommands(); // prepared once per transaction, not per record (D9)
        var maintainIndexes = await IndexesUsableAsync(connection, transaction, invalidate: true, cancellationToken).ConfigureAwait(false);
        var highWater = await ReadHighWaterAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        var newHighWater = highWater;
        foreach (var update in updates)
        {
            var existing = await ReadRecordAsync(connection, transaction, update.Id, cancellationToken, commands).ConfigureAwait(false);
            var next = update.Transform(existing);
            if (next is null)
            {
                // Re-read so the caller never receives the transform's (possibly mutated) working copy.
                results.Add(new RecordUpdateResult<TDocument>(
                    existing is null ? null : await ReadRecordAsync(connection, transaction, update.Id, cancellationToken, commands).ConfigureAwait(false),
                    Changed: false));
                continue;
            }

            if (!string.Equals(next.Current.Id, update.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Transform for '{update.Id}' returned a record with id '{next.Current.Id}'.");
            }

            var written = await WriteRecordAsync(connection, transaction, next, commands, cancellationToken).ConfigureAwait(false);
            if (maintainIndexes && _indexes.Count > 0)
            {
                await WriteIndexRowsAsync(connection, transaction, written, cancellationToken).ConfigureAwait(false);
            }
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
                    lastUpdatedAt = reader.GetString(27);
                    lastKey = (byte[])reader.GetValue(28);
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
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                removed++;

                // Index rows exist exactly for live, visible records, so counts need not look at the records.
                await using var rows = connection.CreateCommand();
                rows.Transaction = transaction;
                rows.CommandText = "DELETE FROM bs_index WHERE collection = $c AND id_key = $key";
                rows.Parameters.AddWithValue("$c", _collection);
                rows.Parameters.AddWithValue("$key", OrdinalKey(id));
                await rows.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
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

    /// <inheritdoc />
    public async Task ResetClockHighWaterAsync(HlcTimestamp value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await SetMetaAsync(connection, transaction, _collection, "clock_high_water", value.Encode(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<TDocument>> QueryIndexAsync(SyncIndexQuery<TDocument> query, SyncIndexCursor? after, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!Maintains(query.Index) || !await IndexesUsableAsync(connection, transaction, invalidate: false, cancellationToken).ConfigureAwait(false))
        {
            return await LocalStoreIndexing.QueryAsync(this, query, after, limit, cancellationToken).ConfigureAwait(false);
        }

        await using var command = IndexCommand(connection, transaction, query, "r.current", after);
        command.CommandText += $" ORDER BY i.key {(query.IsDescending ? "DESC" : "ASC")}, i.id_key {(query.IsDescending ? "DESC" : "ASC")} LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        var documents = new List<TDocument>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            documents.Add(Deserialize(reader.GetString(0)));
        }

        return documents;
    }

    /// <inheritdoc />
    public async Task<int> CountIndexAsync(SyncIndexQuery<TDocument> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        if (!Maintains(query.Index) || !await IndexesUsableAsync(connection, transaction, invalidate: false, cancellationToken).ConfigureAwait(false))
        {
            return await LocalStoreIndexing.CountAsync(this, query, cancellationToken).ConfigureAwait(false);
        }

        await using var command = IndexCommand(connection, transaction, query, "COUNT(*)", null, join: false);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private bool Maintains(SyncIndex<TDocument> index) =>
        _indexes.Any(i => i.Name == index.Name && i.Version == index.Version);

    /// <summary>
    /// Whether the stored index rows were built for exactly this store's indexes. A writer with a different set
    /// (<paramref name="invalidate"/>) marks them unusable, since it cannot keep rows of indexes it does not know.
    /// </summary>
    private async Task<bool> IndexesUsableAsync(SqliteConnection connection, SqliteTransaction transaction, bool invalidate, CancellationToken cancellationToken)
    {
        var stored = await GetMetaAsync(connection, transaction, _collection, "indexes", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (stored == _indexSignature)
        {
            return true;
        }

        if (invalidate && stored != "!")
        {
            await SetMetaAsync(connection, transaction, _collection, "indexes", "!", cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Rebuilds the index rows when the stored set differs from this store's (in one write transaction). A store without
    /// indexes leaves them alone; if it writes, it marks them unusable instead (<see cref="IndexesUsableAsync"/>).
    /// </summary>
    private async Task RebuildIndexesAsync(CancellationToken cancellationToken)
    {
        if (_indexes.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var stored = await GetMetaAsync(connection, transaction, _collection, "indexes", cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (stored == _indexSignature)
        {
            return;
        }

        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM bs_index WHERE collection = $c";
            clear.Parameters.AddWithValue("$c", _collection);
            await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_indexes.Count > 0)
        {
            var live = new List<(byte[] IdKey, TDocument Document)>();
            await using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT id_key, current FROM bs_records WHERE collection = $c AND missing = 0 AND deleted = 0";
                read.Parameters.AddWithValue("$c", _collection);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    live.Add(((byte[])reader.GetValue(0), Deserialize(reader.GetString(1))));
                }
            }

            await using var insert = InsertIndexCommand(connection, transaction);
            foreach (var (idKey, document) in live)
            {
                foreach (var index in _indexes)
                {
                    insert.Parameters["$name"].Value = index.Name;
                    insert.Parameters["$key"].Value = OrdinalKey(index.KeyOf(document));
                    insert.Parameters["$id"].Value = idKey;
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await SetMetaAsync(connection, transaction, _collection, "indexes", _indexSignature, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces a record's index rows: one per declared index while the record is live and visible.</summary>
    private async Task WriteIndexRowsAsync(SqliteConnection connection, SqliteTransaction transaction, SyncRecord<TDocument> record, CancellationToken cancellationToken)
    {
        var idKey = OrdinalKey(record.Current.Id);
        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM bs_index WHERE collection = $c AND id_key = $id";
            delete.Parameters.AddWithValue("$c", _collection);
            delete.Parameters.AddWithValue("$id", idKey);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_indexes.Count == 0 || record.MissingAfterReset || record.Current.Deleted)
        {
            return;
        }

        await using var insert = InsertIndexCommand(connection, transaction);
        foreach (var index in _indexes)
        {
            insert.Parameters["$name"].Value = index.Name;
            insert.Parameters["$key"].Value = OrdinalKey(index.KeyOf(record.Current));
            insert.Parameters["$id"].Value = idKey;
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private SqliteCommand InsertIndexCommand(SqliteConnection connection, SqliteTransaction transaction)
    {
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR REPLACE INTO bs_index (collection, name, key, id_key) VALUES ($c, $name, $key, $id)";
        insert.Parameters.AddWithValue("$c", _collection);
        insert.Parameters.Add("$name", SqliteType.Text);
        insert.Parameters.Add("$key", SqliteType.Blob);
        insert.Parameters.Add("$id", SqliteType.Blob);
        return insert;
    }

    /// <summary>
    /// The range of <paramref name="query"/> over bs_index joined to the live, visible records (rows of purged or hidden
    /// records never surface), after <paramref name="after"/> in the query's direction. CROSS JOIN makes SQLite walk the
    /// index range first (in order, stopping at the limit) instead of scanning the records.
    /// </summary>
    private SqliteCommand IndexCommand(SqliteConnection connection, SqliteTransaction transaction, SyncIndexQuery<TDocument> query, string select, SyncIndexCursor? after, bool join = true)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        var sql = new StringBuilder(join
            ? $"""
                SELECT {select} FROM bs_index i
                CROSS JOIN bs_records r ON r.collection = i.collection AND r.id_key = i.id_key AND r.missing = 0 AND r.deleted = 0
                WHERE i.collection = $c AND i.name = $name
                """
            : $"SELECT {select} FROM bs_index i WHERE i.collection = $c AND i.name = $name");
        command.Parameters.AddWithValue("$c", _collection);
        command.Parameters.AddWithValue("$name", query.Index.Name);
        if (query.Lower is { } lower)
        {
            sql.Append(query.LowerExclusive ? " AND i.key > $lower" : " AND i.key >= $lower");
            command.Parameters.AddWithValue("$lower", OrdinalKey(lower));
        }

        if (query.Upper is { } upper)
        {
            sql.Append(query.UpperExclusive ? " AND i.key < $upper" : " AND i.key <= $upper");
            command.Parameters.AddWithValue("$upper", OrdinalKey(upper));
        }

        if (after is { } position)
        {
            sql.Append(query.IsDescending ? " AND (i.key, i.id_key) < ($afterKey, $afterId)" : " AND (i.key, i.id_key) > ($afterKey, $afterId)");
            command.Parameters.AddWithValue("$afterKey", OrdinalKey(position.Key));
            command.Parameters.AddWithValue("$afterId", OrdinalKey(position.Id));
        }

        command.CommandText = sql.ToString();
        return command;
    }

    private static HlcTimestamp Max(HlcTimestamp a, HlcTimestamp b) => a >= b ? a : b;

    /// <summary>UTF-16 big-endian bytes: memcmp order equals <see cref="string.CompareOrdinal(string, string)"/>.</summary>
    private static byte[] OrdinalKey(string id) => Encoding.BigEndianUnicode.GetBytes(id);

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SqliteSchema.EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 26)
        {
            // SQLITE_NOTADB: encrypted with another key, encrypted while no key was given (or the reverse), or not SQLite.
            throw new SqliteStoreUnreadableException(
                "The database cannot be read: it is encrypted with another key, or not encrypted as expected, or not a SQLite database. Nothing was changed.",
                error);
        }
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

    private async Task<SyncRecord<TDocument>?> ReadRecordAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken cancellationToken, UpdateCommands? commands = null)
    {
        var command = commands?.Read;
        if (command is null)
        {
            command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {Columns} FROM bs_records WHERE collection = $c AND id = $id";
            command.Parameters.AddWithValue("$c", _collection);
            command.Parameters.AddWithValue("$id", id);
            if (commands is not null)
            {
                commands.Read = command;
            }
        }
        else
        {
            command.Parameters["$id"].Value = id;
        }

        try
        {
            var records = await ReadAllAsync(command, cancellationToken).ConfigureAwait(false);
            return records.Count == 0 ? null : records[0];
        }
        finally
        {
            if (commands is null)
            {
                await command.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>The commands of one <see cref="UpdateAsync"/> transaction, reused for every record.</summary>
    private sealed class UpdateCommands : IAsyncDisposable
    {
        public SqliteCommand? Read { get; set; }

        public SqliteCommand? Write { get; set; }

        public async ValueTask DisposeAsync()
        {
            if (Read is not null)
            {
                await Read.DisposeAsync().ConfigureAwait(false);
            }

            if (Write is not null)
            {
                await Write.DisposeAsync().ConfigureAwait(false);
            }
        }
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

        // Equal texts (a pending payload is usually the current document) share one deserialized copy: documents in
        // records are never mutated in place (D9).
        var copies = new Dictionary<string, TDocument>(2, StringComparer.Ordinal);
        TDocument Deserialize(string text) => copies.TryGetValue(text, out var copy) ? copy : copies[text] = this.Deserialize(text);

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
            Rejection = rejectionCode is null
                ? null
                : new SyncRejection(reader.GetInt64(10), rejectionCode, NullableString(12))
                {
                    Arguments = NullableString(26) is { } arguments ? JsonSerializer.Deserialize(arguments, SqliteJson.Default.DictionaryStringString) : null,
                },
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

    private async Task<SyncRecord<TDocument>> WriteRecordAsync(SqliteConnection connection, SqliteTransaction transaction, SyncRecord<TDocument> record, UpdateCommands commands, CancellationToken cancellationToken)
    {
        // Each distinct document instance is serialized once (current, base and payload are often the same one), and the
        // returned copy is deserialized from exactly what was written (D9).
        var texts = new Dictionary<TDocument, string>(4, ReferenceEqualityComparer.Instance);
        var copies = new Dictionary<string, TDocument>(4, StringComparer.Ordinal);
        string Text(TDocument document) => texts.TryGetValue(document, out var text) ? text : texts[document] = Serialize(document);
        TDocument Copy(string text) => copies.TryGetValue(text, out var copy) ? copy : copies[text] = Deserialize(text);

        var reused = commands.Write is not null;
        var command = commands.Write ??= connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = reused ? command.CommandText : """
            INSERT OR REPLACE INTO bs_records (
                collection, id, id_key, current, updated_at, deleted, base, base_version, is_dirty, local_revision,
                pending_id, pending_revision, pending_base_version, pending_payload,
                rejection_revision, rejection_code, rejection_message, observed, observed_version, generation, missing,
                conflict_server, conflict_server_version, conflict_local, conflict_base,
                group_id, group_members, pending_group, pending_group_size, base_same, rejection_arguments)
            VALUES ($c, $id, $key, $current, $updated, $deleted, $base, $baseVersion, $dirty, $revision,
                $pendingId, $pendingRevision, $pendingBase, $pendingPayload,
                $rejectionRevision, $rejectionCode, $rejectionMessage, $observed, $observedVersion, $generation, $missing,
                $conflictServer, $conflictServerVersion, $conflictLocal, $conflictBase,
                $groupId, $groupMembers, $pendingGroup, $pendingGroupSize, $baseSame, $rejectionArguments)
            """;
        var p = command.Parameters;
        void Set(string name, object value)
        {
            if (reused)
            {
                p[name].Value = value;
            }
            else
            {
                p.AddWithValue(name, value);
            }
        }

        Set("$c", _collection);
        Set("$id", record.Current.Id);
        Set("$key", OrdinalKey(record.Current.Id));
        var current = Text(record.Current);
        var baseJson = record.Base is null ? null : Text(record.Base);
        var baseSame = baseJson is not null && baseJson == current;
        Set("$current", current);
        Set("$updated", record.Current.UpdatedAt.Encode());
        Set("$deleted", record.Current.Deleted ? 1 : 0);
        Set("$base", baseSame || baseJson is null ? DBNull.Value : baseJson);
        Set("$baseSame", baseSame ? 1 : 0);
        Set("$baseVersion", (object?)record.BaseVersion ?? DBNull.Value);
        Set("$dirty", record.IsDirty ? 1 : 0);
        Set("$revision", record.LocalRevision);
        Set("$pendingId", (object?)record.Pending?.OperationId ?? DBNull.Value);
        Set("$pendingRevision", (object?)record.Pending?.Revision ?? DBNull.Value);
        Set("$pendingBase", (object?)record.Pending?.BaseVersion ?? DBNull.Value);
        var payload = record.Pending is null ? null : Text(record.Pending.Payload);
        Set("$pendingPayload", (object?)payload ?? DBNull.Value);
        Set("$rejectionRevision", (object?)record.Rejection?.Revision ?? DBNull.Value);
        Set("$rejectionCode", (object?)record.Rejection?.ErrorCode ?? DBNull.Value);
        Set("$rejectionMessage", (object?)record.Rejection?.Message ?? DBNull.Value);
        Set(
            "$rejectionArguments",
            record.Rejection?.Arguments is { Count: > 0 } rejectionArguments
                ? JsonSerializer.Serialize(new Dictionary<string, string>(rejectionArguments, StringComparer.Ordinal), SqliteJson.Default.DictionaryStringString)
                : DBNull.Value);
        var observed = record.Observed is null ? null : Text(record.Observed);
        Set("$observed", (object?)observed ?? DBNull.Value);
        Set("$observedVersion", (object?)record.ObservedVersion ?? DBNull.Value);
        Set("$generation", record.Generation);
        Set("$missing", record.MissingAfterReset ? 1 : 0);
        var conflictServer = record.Conflict is { } c ? Text(c.Server) : null;
        var conflictLocal = record.Conflict is { } cl ? Text(cl.Local) : null;
        var conflictBase = record.Conflict?.Base is { } cb ? Text(cb) : null;
        Set("$conflictServer", (object?)conflictServer ?? DBNull.Value);
        Set("$conflictServerVersion", (object?)record.Conflict?.ServerVersion ?? DBNull.Value);
        Set("$conflictLocal", (object?)conflictLocal ?? DBNull.Value);
        Set("$conflictBase", (object?)conflictBase ?? DBNull.Value);
        Set("$groupId", (object?)record.Group?.Id ?? DBNull.Value);
        Set("$groupMembers", record.Group is { } g ? JsonSerializer.Serialize(g.Members.ToArray(), SqliteJson.Default.StringArray) : DBNull.Value);
        Set("$pendingGroup", (object?)record.Pending?.Group ?? DBNull.Value);
        Set("$pendingGroupSize", record.Pending is { Group: not null } pg ? pg.GroupSize : DBNull.Value);
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
