using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>The SQLite store's schema: creation, forward migration and the version check (ADR-004, I17).</summary>
internal static class SqliteSchema
{
    public const int CurrentVersion = 5;

    public const string DatabaseScope = "";

    /// <summary>The <c>bs_records</c> columns of schema 1, in declaration order.</summary>
    public const string Version1Columns =
        "collection, id, id_key, current, updated_at, deleted, base, base_version, is_dirty, local_revision, pending_id, " +
        "pending_revision, pending_base_version, pending_payload, rejection_revision, rejection_code, rejection_message, " +
        "observed, observed_version, generation, missing";

    /// <summary>The columns schema 2 added.</summary>
    public const string Version2Columns = "conflict_server, conflict_server_version, conflict_local, conflict_base";

    /// <summary>The columns schema 3 added.</summary>
    public const string Version3Columns = "group_id, group_members, pending_group, pending_group_size";

    public const string Version4Columns = "base_same";

    /// <summary>The record columns schema 5 added.</summary>
    public const string Version5Columns = "rejection_arguments";

    private const string Version1 = """
        CREATE TABLE bs_meta (
            collection TEXT NOT NULL,
            key TEXT NOT NULL,
            value TEXT NOT NULL,
            PRIMARY KEY (collection, key)
        ) WITHOUT ROWID;

        CREATE TABLE bs_records (
            collection TEXT NOT NULL,
            id TEXT NOT NULL,
            id_key BLOB NOT NULL,
            current TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            deleted INTEGER NOT NULL,
            base TEXT,
            base_version INTEGER,
            is_dirty INTEGER NOT NULL,
            local_revision INTEGER NOT NULL,
            pending_id TEXT,
            pending_revision INTEGER,
            pending_base_version INTEGER,
            pending_payload TEXT,
            rejection_revision INTEGER,
            rejection_code TEXT,
            rejection_message TEXT,
            observed TEXT,
            observed_version INTEGER,
            generation INTEGER NOT NULL,
            missing INTEGER NOT NULL,
            PRIMARY KEY (collection, id)
        );

        CREATE INDEX bs_records_pending ON bs_records (collection, updated_at, id_key)
            WHERE is_dirty = 1 AND rejection_code IS NULL;
        CREATE INDEX bs_records_dirty ON bs_records (collection) WHERE is_dirty = 1;
        CREATE INDEX bs_records_stale ON bs_records (collection, id_key) WHERE is_dirty = 0 AND missing = 0;
        CREATE INDEX bs_records_visible ON bs_records (collection, id_key) WHERE missing = 0;
        """;

    // 1 -> 2: unresolved conflicts. Additive only.
    private const string MigrationTo2 = """
        ALTER TABLE bs_records ADD COLUMN conflict_server TEXT;
        ALTER TABLE bs_records ADD COLUMN conflict_server_version INTEGER;
        ALTER TABLE bs_records ADD COLUMN conflict_local TEXT;
        ALTER TABLE bs_records ADD COLUMN conflict_base TEXT;
        CREATE INDEX bs_records_conflicts ON bs_records (collection, id_key) WHERE conflict_local IS NOT NULL;
        """;

    // 2 -> 3: dependency groups. Additive only.
    // Schema 4 (D8): a clean record's base equals its current state; base_same = 1 stores it once (base is NULL).
    private const string MigrationTo4 = """
        ALTER TABLE bs_records ADD COLUMN base_same INTEGER NOT NULL DEFAULT 0;
        """;

    // Schema 5 (ADR-018): declared secondary indexes. Rows exist for live, visible records of indexes the writing store
    // declared; the bs_meta key 'indexes' names the set the rows were built for.
    private const string MigrationTo5 = """
        ALTER TABLE bs_records ADD COLUMN rejection_arguments TEXT;
        CREATE TABLE bs_index (
            collection TEXT NOT NULL,
            name TEXT NOT NULL,
            key BLOB NOT NULL,
            id_key BLOB NOT NULL,
            PRIMARY KEY (collection, name, key, id_key)
        ) WITHOUT ROWID;
        CREATE INDEX bs_index_record ON bs_index (collection, id_key);
        """;

    private const string MigrationTo3 = """
        ALTER TABLE bs_records ADD COLUMN group_id TEXT;
        ALTER TABLE bs_records ADD COLUMN group_members TEXT;
        ALTER TABLE bs_records ADD COLUMN pending_group TEXT;
        ALTER TABLE bs_records ADD COLUMN pending_group_size INTEGER;
        """;

    /// <summary>Creates the current schema, or upgrades an older one in place, in one transaction.</summary>
    /// <exception cref="SqliteStoreSchemaException">The database uses a newer schema; it is left untouched.</exception>
    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, null, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);

        await using var transaction = connection.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
        var version = await ReadVersionAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (version > CurrentVersion)
        {
            throw new SqliteStoreSchemaException(
                $"The database uses Bsync SQLite schema {version}; this version supports up to {CurrentVersion}. Upgrade the application.");
        }

        if (version == CurrentVersion)
        {
            return;
        }

        if (version == 0)
        {
            await ExecuteAsync(connection, transaction, Version1, cancellationToken).ConfigureAwait(false);
            await SetMetaAsync(connection, transaction, DatabaseScope, "replica_id", NewId(), cancellationToken).ConfigureAwait(false);
            await SetMetaAsync(connection, transaction, DatabaseScope, "incarnation", NewId(), cancellationToken).ConfigureAwait(false);
        }

        // Existing records, pending operations and metadata are untouched; every step commits or rolls back with the
        // version change.
        if (version < 2)
        {
            await ExecuteAsync(connection, transaction, MigrationTo2, cancellationToken).ConfigureAwait(false);
        }

        if (version < 3)
        {
            await ExecuteAsync(connection, transaction, MigrationTo3, cancellationToken).ConfigureAwait(false);
        }

        if (version < 4)
        {
            await ExecuteAsync(connection, transaction, MigrationTo4, cancellationToken).ConfigureAwait(false);
        }

        if (version < 5)
        {
            await ExecuteAsync(connection, transaction, MigrationTo5, cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction, $"PRAGMA user_version = {CurrentVersion}", cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public static async Task<int> ReadVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task SetMetaAsync(SqliteConnection connection, SqliteTransaction transaction, string scope, string key, string value, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO bs_meta (collection, key, value) VALUES ($c, $k, $v) ON CONFLICT (collection, key) DO UPDATE SET value = excluded.value";
        command.Parameters.AddWithValue("$c", scope);
        command.Parameters.AddWithValue("$k", key);
        command.Parameters.AddWithValue("$v", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string NewId() => Guid.NewGuid().ToString("N");
}
