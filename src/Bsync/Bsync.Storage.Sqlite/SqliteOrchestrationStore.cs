using Bsync.Client;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>
/// Transactional account-scoped orchestration metadata. Uses independent versioned tables (schema 1), so it may
/// share a document database without changing its user_version. WAL and synchronous=FULL protect acknowledged writes
/// from process termination within SQLite's guarantees. Network calls never run in these transactions.
/// </summary>
public sealed class SqliteOrchestrationStore : IOrchestrationStore
{
    private readonly string _connectionString;
    private readonly string _account;
    private readonly TimeProvider _time;
    private bool _disposed;

    private SqliteOrchestrationStore(string connectionString, string account, TimeProvider time)
    {
        _connectionString = connectionString;
        _account = account;
        _time = time;
    }

    /// <summary>Opens metadata for one account. Encryption requires the SQLCipher bundle; unavailable/wrong keys never cause a wipe.</summary>
    public static async Task<SqliteOrchestrationStore> OpenAsync(string dataSource, AccountScope account,
        byte[]? encryptionKey = null, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);
        ArgumentNullException.ThrowIfNull(account);
        var store = new SqliteOrchestrationStore(SqliteStorePool.ConnectionString(dataSource, encryptionKey), account.StorageKey, timeProvider ?? TimeProvider.System);
        await using var connection = await store.ConnectionAsync(cancellationToken).ConfigureAwait(false);
        if (encryptionKey is not null && await ScalarAsync(connection, null, "PRAGMA cipher_version", cancellationToken).ConfigureAwait(false) is not string { Length: > 0 })
            throw new InvalidOperationException("Encrypted orchestration metadata requires SQLitePCLRaw.bundle_e_sqlcipher; never combine it with the plain bundle.");
        await using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var exists = await ScalarAsync(connection, transaction,
                "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='bs_orchestration_schema'", cancellationToken).ConfigureAwait(false);
            if (Convert.ToInt64(exists) != 0)
            {
                var version = Convert.ToInt64(await ScalarAsync(connection, transaction, "SELECT version FROM bs_orchestration_schema WHERE id=1", cancellationToken).ConfigureAwait(false));
                if (version != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported orchestration schema; nothing was changed.");
            }
            else
            {
                await ExecuteAsync(connection, transaction, """
                    CREATE TABLE bs_orchestration_schema(id INTEGER PRIMARY KEY CHECK(id=1), version INTEGER NOT NULL);
                    INSERT INTO bs_orchestration_schema VALUES(1,1);
                    CREATE TABLE bs_orchestration_records(
                        account TEXT NOT NULL, key TEXT NOT NULL COLLATE BINARY, revision TEXT NOT NULL,
                        payload TEXT NOT NULL, expires_at INTEGER NULL, PRIMARY KEY(account,key));
                    """, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        await ExecuteAsync(connection, null, "PRAGMA journal_mode=WAL", cancellationToken).ConfigureAwait(false);
        return store;
    }

    private async Task<SqliteConnection> ConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, "PRAGMA synchronous=FULL", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <inheritdoc />
    public async Task<OrchestrationRecord?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Key(key);
        await using var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, null, key, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OrchestrationRecord?> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction, string key, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key,revision,payload,expires_at FROM bs_orchestration_records WHERE account=$account AND key=$key";
        command.Parameters.AddWithValue("$account", _account);
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? Record(reader) : null;
    }

    private static OrchestrationRecord Record(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3));

    /// <inheritdoc />
    public async Task<IReadOnlyList<OrchestrationRecord>> ReadPageAsync(string prefix, string? afterKey, int limit, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Page(prefix, afterKey, limit);
        await using var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // ASCII key restriction makes this range identical to ordinal prefix matching in every provider.
        command.CommandText = """
            SELECT key,revision,payload,expires_at FROM bs_orchestration_records
            WHERE account=$account AND key >= $prefix AND key < $end AND key > $after
            ORDER BY key COLLATE BINARY LIMIT $limit
            """;
        command.Parameters.AddWithValue("$account", _account);
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$end", prefix + '\u007f');
        command.Parameters.AddWithValue("$after", afterKey ?? "");
        command.Parameters.AddWithValue("$limit", limit);
        var records = new List<OrchestrationRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) records.Add(Record(reader));
        return records;
    }

    /// <inheritdoc />
    public async Task<bool> TryWriteAsync(IReadOnlyList<OrchestrationMutation> mutations, OrchestrationFence? fence = null, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Batch(mutations, fence);
        await using var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        long? originalExpiration = null;
        if (fence is not null)
        {
            var lease = await ReadAsync(connection, transaction, fence.Key, cancellationToken).ConfigureAwait(false);
            if (lease?.Payload != fence.Token || lease.ExpiresAtUnixMilliseconds is not { } expiry
                || expiry <= _time.GetUtcNow().ToUnixTimeMilliseconds()) return false;
            originalExpiration = expiry;
        }
        foreach (var mutation in mutations)
        {
            var current = await ReadAsync(connection, transaction, mutation.Key, cancellationToken).ConfigureAwait(false);
            if (current?.Revision != mutation.ExpectedRevision) return false;
        }
        foreach (var mutation in mutations)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mutation.Payload is null
                ? "DELETE FROM bs_orchestration_records WHERE account=$account AND key=$key"
                : """
                    INSERT INTO bs_orchestration_records(account,key,revision,payload,expires_at) VALUES($account,$key,$revision,$payload,$expires)
                    ON CONFLICT(account,key) DO UPDATE SET revision=excluded.revision,payload=excluded.payload,expires_at=excluded.expires_at
                    """;
            command.Parameters.AddWithValue("$account", _account);
            command.Parameters.AddWithValue("$key", mutation.Key);
            if (mutation.Payload is not null)
            {
                command.Parameters.AddWithValue("$revision", Guid.NewGuid().ToString("N"));
                command.Parameters.AddWithValue("$payload", mutation.Payload);
                command.Parameters.AddWithValue("$expires", (object?)mutation.ExpiresAtUnixMilliseconds ?? DBNull.Value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        // Recheck expiration at the commit boundary, after potentially expensive mutations.
        if (originalExpiration <= _time.GetUtcNow().ToUnixTimeMilliseconds()) return false;
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return true;
    }

    /// <inheritdoc />
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await ConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM bs_orchestration_records WHERE account=$account";
        command.Parameters.AddWithValue("$account", _account);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
}
