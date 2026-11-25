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
    /// <remarks>
    /// The plain write-ahead log is folded into the database and removed before the swap, so a process killed at any point
    /// leaves either the complete plain database or the encrypted one, never an encrypted database beside a plain log
    /// (A11). Only this file's pooled connections are closed. Deleting a file does not erase its blocks: flash storage and
    /// file-system journals can keep plain pages of the old file until they are reused, so encrypt before storing
    /// sensitive data whenever possible.
    /// </remarks>
    public static Task EncryptAsync(string dataSource, byte[] key, CancellationToken cancellationToken = default) =>
        EncryptAsync(dataSource, key, reached: null, cancellationToken);

    /// <summary>
    /// <see cref="EncryptAsync(string, byte[], CancellationToken)"/>, reporting each durable step to
    /// <paramref name="reached"/> (process-kill tests).
    /// </summary>
    internal static async Task EncryptAsync(string dataSource, byte[] key, Func<string, Task>? reached, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        ArgumentNullException.ThrowIfNull(key);
        reached ??= static _ => Task.CompletedTask;
        var passphrase = SqliteStorePool.Passphrase(key);
        SqliteStorePool.Release(dataSource);
        var encrypted = dataSource + ".encrypting";
        File.Delete(encrypted);
        File.Delete(encrypted + "-journal");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataSource, Pooling = false }.ToString()))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            // Fold the plain write-ahead log into the database and leave WAL mode, which deletes the log (A11).
            await ScalarAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken).ConfigureAwait(false);
            if (await ScalarAsync(connection, "PRAGMA journal_mode = DELETE", cancellationToken).ConfigureAwait(false) is not "delete")
            {
                throw new IOException("The plain write-ahead log could not be folded in; close every store using the database first.");
            }

            await reached("folded").ConfigureAwait(false);
            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version", cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            await using var export = connection.CreateCommand();
            export.CommandText = "ATTACH DATABASE $path AS encrypted KEY $key; SELECT sqlcipher_export('encrypted'); " +
                $"PRAGMA encrypted.user_version = {version.ToString(CultureInfo.InvariantCulture)}; DETACH DATABASE encrypted;";
            export.Parameters.AddWithValue("$path", encrypted);
            export.Parameters.AddWithValue("$key", passphrase);
            await export.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Nothing of the plain log may outlive the swap; normally neither file exists any more.
        SqliteStorePool.Release(dataSource);
        File.Delete(dataSource + "-wal");
        File.Delete(dataSource + "-shm");
        File.Delete(dataSource + "-journal");
        await reached("exported").ConfigureAwait(false);
        File.Move(encrypted, dataSource, overwrite: true);
        await reached("replaced").ConfigureAwait(false);
    }

    /// <summary>Changes the key of an encrypted database. A failure leaves the old key valid.</summary>
    public static async Task RekeyAsync(string dataSource, byte[] currentKey, byte[] newKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        ArgumentNullException.ThrowIfNull(currentKey);
        ArgumentNullException.ThrowIfNull(newKey);
        var replacement = SqliteStorePool.Passphrase(newKey);
        SqliteStorePool.Release(dataSource);
        try
        {
            await using var connection = await SqliteStorePool.OpenAsync(dataSource, currentKey, cancellationToken, pooling: false).ConfigureAwait(false);
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
