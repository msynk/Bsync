using Npgsql;

namespace Bsync.Server.PostgreSql;

/// <summary>Creates and migrates the authority's tables (ADR-011). Safe to run from several processes at once.</summary>
internal static class PostgreSqlSchema
{
    public const int CurrentVersion = 2;

    // Serializes schema changes across processes for the duration of the transaction.
    private const long SchemaLock = 0x426c617a53796e63; // "BlazSync"

    private const string Version1 = """
        CREATE TABLE bs_meta (
            key text PRIMARY KEY,
            value text NOT NULL
        );

        CREATE TABLE bs_feeds (
            collection text NOT NULL,
            scope text NOT NULL,
            sequence bigint NOT NULL,
            purged_through bigint NOT NULL,
            PRIMARY KEY (collection, scope)
        );

        CREATE TABLE bs_documents (
            collection text NOT NULL,
            scope text NOT NULL,
            id text NOT NULL,
            id_key bytea NOT NULL,
            version bigint NOT NULL,
            deleted boolean NOT NULL,
            document text NOT NULL,
            PRIMARY KEY (collection, scope, id)
        );

        CREATE UNIQUE INDEX bs_documents_feed ON bs_documents (collection, scope, version);
        CREATE INDEX bs_documents_list ON bs_documents (collection, scope, id_key) WHERE NOT deleted;

        CREATE TABLE bs_receipts (
            collection text NOT NULL,
            scope text NOT NULL,
            operation_id text NOT NULL,
            fingerprint text NOT NULL,
            kind smallint NOT NULL,
            version bigint,
            error_code text,
            message text,
            document text,
            PRIMARY KEY (collection, scope, operation_id)
        );
        """;

    // Version 2 (ADR-015): one access row per document and principal that can or could read it, at the version of its last
    // change; a member's pull reads its own rows in version order.
    private const string Version2 = """
        CREATE TABLE bs_document_access (
            collection text NOT NULL,
            scope text NOT NULL,
            principal_key text NOT NULL,
            version bigint NOT NULL,
            id text NOT NULL,
            granted boolean NOT NULL,
            PRIMARY KEY (collection, scope, principal_key, version)
        );

        CREATE UNIQUE INDEX bs_document_access_document ON bs_document_access (collection, scope, id, principal_key) INCLUDE (granted);
        """;

    /// <summary>Ensures the schema exists at the current version and returns the epoch.</summary>
    public static async Task<string> EnsureAsync(NpgsqlDataSource source, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var lockSchema = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({SchemaLock})", connection, transaction))
        {
            await lockSchema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        int version;
        await using (var exists = new NpgsqlCommand("SELECT to_regclass('bs_meta') IS NOT NULL", connection, transaction))
        {
            version = (bool)(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! ? -1 : 0;
        }

        if (version == -1)
        {
            await using var read = new NpgsqlCommand("SELECT value FROM bs_meta WHERE key = 'schema_version'", connection, transaction);
            version = int.Parse((string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (version > CurrentVersion)
        {
            throw new PostgreSqlSchemaException($"The database uses Bsync PostgreSQL schema {version}; this version supports up to {CurrentVersion}. Upgrade the application.");
        }

        if (version == 0)
        {
            await using (var create = new NpgsqlCommand(Version1, connection, transaction))
            {
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var meta = new NpgsqlCommand("INSERT INTO bs_meta (key, value) VALUES ('schema_version', '1'), ('epoch', $1)", connection, transaction);
            meta.Parameters.Add(new() { Value = NewEpoch() });
            await meta.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            version = 1;
        }

        // Forward, in place, under the lock; existing feeds, documents and receipts are kept.
        if (version == 1)
        {
            await using (var upgrade = new NpgsqlCommand(Version2, connection, transaction))
            {
                await upgrade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var mark = new NpgsqlCommand("UPDATE bs_meta SET value = '2' WHERE key = 'schema_version'", connection, transaction);
            await mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        string epoch;
        await using (var readEpoch = new NpgsqlCommand("SELECT value FROM bs_meta WHERE key = 'epoch'", connection, transaction))
        {
            epoch = (string)(await readEpoch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return epoch;
    }

    public static string NewEpoch() => $"pg-{Guid.NewGuid():N}";
}
