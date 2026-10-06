using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bsync.Server.SqlServer;

/// <summary>Creates and migrates the authority's tables (ADR-011, ADR-014). Safe to run from several processes at once.</summary>
internal static partial class SqlServerSchema
{
    public const int CurrentVersion = 2;

    /// <summary>
    /// Version 1. Keys are <c>varbinary</c> holding UTF-16BE bytes: exact equality (an <c>nvarchar</c> comparison ignores
    /// trailing spaces even with a binary collation) and UTF-16 ordinal order. <c>varbinary</c> comparison ignores trailing
    /// zero bytes, which cannot make two valid ids equal because <see cref="SyncIds"/> excludes U+0000.
    /// <c>feed_id</c> is a surrogate key only, never a feed position. Documents are clustered in feed order.
    /// </summary>
    private const string Version1 = """
        CREATE TABLE {0}.[meta] (
            [key] varchar(64) NOT NULL CONSTRAINT [meta_pk] PRIMARY KEY,
            [value] nvarchar(400) NOT NULL
        );

        CREATE TABLE {0}.[feeds] (
            [feed_id] int IDENTITY(1, 1) NOT NULL CONSTRAINT [feeds_pk] PRIMARY KEY,
            [collection_key] varbinary(512) NOT NULL,
            [scope_key] varbinary(512) NOT NULL,
            [collection] nvarchar(256) NOT NULL,
            [scope] nvarchar(256) NOT NULL,
            [sequence] bigint NOT NULL,
            [purged_through] bigint NOT NULL
        );

        CREATE UNIQUE INDEX [feeds_key] ON {0}.[feeds] ([collection_key], [scope_key]);

        CREATE TABLE {0}.[documents] (
            [feed_id] int NOT NULL,
            [id_key] varbinary(512) NOT NULL,
            [id] nvarchar(256) NOT NULL,
            [version] bigint NOT NULL,
            [deleted] bit NOT NULL,
            [document] nvarchar(max) NOT NULL,
            CONSTRAINT [documents_pk] PRIMARY KEY NONCLUSTERED ([feed_id], [id_key])
        );

        -- Feed order is the clustered index: a pull reads one index and holds one row lock at a time, so under locking
        -- READ COMMITTED it can wait for a writer but never deadlock with one (no key lookup).
        CREATE UNIQUE CLUSTERED INDEX [documents_feed] ON {0}.[documents] ([feed_id], [version]);

        CREATE TABLE {0}.[receipts] (
            [feed_id] int NOT NULL,
            [operation_key] varbinary(512) NOT NULL,
            [fingerprint] char(64) NOT NULL,
            [kind] smallint NOT NULL,
            [version] bigint NULL,
            [error_code] nvarchar(256) NULL,
            [message] nvarchar(max) NULL,
            [document] nvarchar(max) NULL,
            CONSTRAINT [receipts_pk] PRIMARY KEY CLUSTERED ([feed_id], [operation_key])
        );

        CREATE INDEX [receipts_accepted] ON {0}.[receipts] ([feed_id], [version]) WHERE [kind] = 0;
        """;

    /// <summary>
    /// Version 2 (ADR-015): one access row per document and principal that can or could read it, at the version of its last
    /// change. Clustered per principal in version order, so a member's pull reads only its own rows.
    /// </summary>
    private const string Version2 = """
        CREATE TABLE {0}.[document_access] (
            [feed_id] int NOT NULL,
            [principal_key] varbinary(512) NOT NULL,
            [version] bigint NOT NULL,
            [id_key] varbinary(512) NOT NULL,
            [id] nvarchar(256) NOT NULL,
            [granted] bit NOT NULL,
            CONSTRAINT [document_access_pk] PRIMARY KEY CLUSTERED ([feed_id], [principal_key], [version])
        );

        CREATE UNIQUE INDEX [document_access_document] ON {0}.[document_access] ([feed_id], [id_key], [principal_key]) INCLUDE ([granted]);
        """;

    /// <summary>Validates and quotes a schema name.</summary>
    public static string Quote(string schema)
    {
        if (!SchemaName().IsMatch(schema))
        {
            throw new ArgumentException("The schema name must be 1-64 letters, digits or underscores, starting with a letter or underscore.", nameof(schema));
        }

        return $"[{schema}]";
    }

    /// <summary>Ensures the schema exists at the current version and returns the epoch.</summary>
    public static async Task<string> EnsureAsync(string connectionString, string schema, CancellationToken cancellationToken)
    {
        var quoted = Quote(schema);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serializes creation and upgrades across processes until the transaction ends.
        await using (var applock = new SqlCommand("EXEC sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 60000", connection, transaction))
        {
            applock.Parameters.AddWithValue("@resource", $"bsync-schema-{schema}");
            await applock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int version;
        await using (var exists = new SqlCommand("SELECT OBJECT_ID(@meta, N'U')", connection, transaction))
        {
            exists.Parameters.AddWithValue("@meta", $"{quoted}.[meta]");
            version = await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is DBNull or null ? 0 : -1;
        }

        if (version == -1)
        {
            await using var read = new SqlCommand($"SELECT [value] FROM {quoted}.[meta] WHERE [key] = 'schema_version'", connection, transaction);
            version = int.Parse((string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, CultureInfo.InvariantCulture);
        }

        if (version > CurrentVersion)
        {
            throw new SqlServerSchemaException($"The database uses Bsync SQL Server schema {version}; this version supports up to {CurrentVersion}. Upgrade the application.");
        }

        if (version == 0)
        {
            await using (var createSchema = new SqlCommand("IF SCHEMA_ID(@schema) IS NULL EXEC (N'CREATE SCHEMA ' + @quoted)", connection, transaction))
            {
                createSchema.Parameters.AddWithValue("@schema", schema);
                createSchema.Parameters.AddWithValue("@quoted", quoted);
                await createSchema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var create = new SqlCommand(string.Format(CultureInfo.InvariantCulture, Version1, quoted), connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var meta = new SqlCommand($"INSERT INTO {quoted}.[meta] ([key], [value]) VALUES ('schema_version', '1'), ('epoch', @epoch), ('version_floor', '0')", connection, transaction);
            meta.Parameters.AddWithValue("@epoch", NewEpoch());
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            version = 1;
        }

        // Forward, in place, under the lock; existing feeds, documents and receipts are kept.
        if (version == 1)
        {
            await using (var upgrade = new SqlCommand(string.Format(CultureInfo.InvariantCulture, Version2, quoted), connection, transaction))
            {
                await upgrade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var mark = new SqlCommand($"UPDATE {quoted}.[meta] SET [value] = '2' WHERE [key] = 'schema_version'", connection, transaction);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string epoch;
        await using (var readEpoch = new SqlCommand($"SELECT [value] FROM {quoted}.[meta] WHERE [key] = 'epoch'", connection, transaction))
        {
            epoch = (string)(await readEpoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return epoch;
    }

    public static string NewEpoch() => $"mssql-{Guid.NewGuid():N}";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex SchemaName();
}
