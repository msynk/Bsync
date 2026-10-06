using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Encrypting an existing replica database, and changing its key (ADR-016). Close every store using the file first.</summary>
public static class SqliteEncryption
{
    /// <summary>The key length: 32 bytes (256 bits).</summary>
    public const int KeyLength = 32;

    /// <summary>A new random key. Keep it in the platform's protected store, never next to the database.</summary>
    public static byte[] NewKey() => System.Security.Cryptography.RandomNumberGenerator.GetBytes(KeyLength);

    /// <summary>Whether the file is a plain (unencrypted) SQLite database: plain files start with a fixed header.</summary>
    public static bool IsPlain(string dataSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        Span<byte> header = stackalloc byte[16];
        using var file = new FileStream(dataSource, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return file.Read(header) == 16 && header.SequenceEqual("SQLite format 3\0"u8);
    }

    /// <summary>
    /// Encrypts a plain database in place with <paramref name="key"/>: everything is exported into a new encrypted file
    /// (records, pending work, metadata and schema version), which then replaces the original. The original is kept
    /// until the replacement succeeded, then deleted.
    /// </summary>
    public static async Task EncryptAsync(string dataSource, byte[] key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        ArgumentNullException.ThrowIfNull(key);
        var passphrase = SqliteStorePool.Passphrase(key);
        SqliteStorePool.Release(dataSource);
        var encrypted = dataSource + ".encrypting";
        File.Delete(encrypted);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataSource, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            await using var export = connection.CreateCommand();
            export.CommandText = "ATTACH DATABASE $path AS encrypted KEY $key; SELECT sqlcipher_export('encrypted'); " +
                $"PRAGMA encrypted.user_version = {version.ToString(CultureInfo.InvariantCulture)}; DETACH DATABASE encrypted;";
            export.Parameters.AddWithValue("$path", encrypted);
            export.Parameters.AddWithValue("$key", passphrase);
            await export.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SqliteConnection.ClearAllPools();
        File.Move(encrypted, dataSource, overwrite: true);
        File.Delete(dataSource + "-wal");
        File.Delete(dataSource + "-shm");
    }

    /// <summary>Changes the key of an encrypted database. A failure leaves the old key valid.</summary>
    public static async Task RekeyAsync(string dataSource, byte[] currentKey, byte[] newKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        ArgumentNullException.ThrowIfNull(currentKey);
        ArgumentNullException.ThrowIfNull(newKey);
        var replacement = SqliteStorePool.Passphrase(newKey);
        SqliteStorePool.Release(dataSource);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Pooling = false,
            Password = SqliteStorePool.Passphrase(currentKey),
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ScalarAsync(connection, "SELECT count(*) FROM sqlite_master", cancellationToken).ConfigureAwait(false);

            // WAL mode: rekey needs the journal folded in first.
            await ScalarAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            await ScalarAsync(connection, "PRAGMA journal_mode = DELETE", cancellationToken).ConfigureAwait(false);
            await using var rekey = connection.CreateCommand();
            rekey.CommandText = $"PRAGMA rekey = {Quote(replacement)}";
            await rekey.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await ScalarAsync(connection, "PRAGMA journal_mode = WAL", cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 26)
        {
            throw new SqliteStoreUnreadableException("The database cannot be read with the current key; nothing was changed.", error);
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    // PRAGMA arguments cannot be parameters. The key string is x'<hex>', built here, so doubling quotes is enough.
    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
