using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Connection-pool helpers.</summary>
public static class SqliteStorePool
{
    // Every connection string in use per file (one per key), so Release closes all of their pools. Release and delete
    // forget them (A10): a store that keeps working re-registers its string on its next connection.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Pools = new(StringComparer.OrdinalIgnoreCase);

    private static volatile bool _nativeLibraryFound;

    // A10: connection strings never carry a key, so neither this class nor Microsoft.Data.Sqlite's pool registry keeps
    // one. Each physical connection is keyed once with PRAGMA key and tagged with a one-way fingerprint of its key ("plain"
    // for none), so a pooled connection is only ever reused for the same key.
    private static readonly ConditionalWeakTable<SQLitePCL.sqlite3, byte[]> Keyed = new();
    private static readonly byte[] Plain = [];

    /// <summary>
    /// Closes the pooled connections to one database file (for example before copying, restoring or deleting it).
    /// Unlike <see cref="SqliteConnection.ClearAllPools"/>, it does not touch other databases' connections.
    /// </summary>
    public static void Release(string dataSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        var strings = Pools.TryRemove(dataSource, out var known) ? [.. known.Keys] : new List<string>();
        strings.Add(ConnectionString(dataSource));
        foreach (var connectionString in strings.Distinct(StringComparer.Ordinal))
        {
            using var connection = new SqliteConnection(connectionString);
            SqliteConnection.ClearPool(connection);
        }
    }

    /// <summary>
    /// Deletes a replica database: closes its pooled connections, then removes the file and its <c>-wal</c>,
    /// <c>-shm</c> and <c>-journal</c> companions (task G3, for example on sign-out). Close every store using it first
    /// (stop its sessions). Succeeds when nothing exists. Retries briefly while another handle is being released.
    /// </summary>
    public static async Task DeleteDatabaseAsync(string dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        Release(dataSource);
        foreach (var path in new[] { dataSource, dataSource + "-wal", dataSource + "-shm", dataSource + "-journal" })
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Delete(path);
                    break;
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                    Release(dataSource);
                }
            }
        }
    }

    /// <summary>The connection string of a file: never with a key (A10).</summary>
    internal static string ConnectionString(string dataSource, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, bool pooling = true)
    {
        EnsureNativeLibrary();
        return new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            Pooling = pooling,
        }.ToString();
    }

    /// <summary>
    /// Opens a connection to <paramref name="dataSource"/>, keyed with <paramref name="key"/> (SQLCipher) or plain. A new
    /// physical connection gets <c>PRAGMA key</c> as its first statement; a pooled one is reused only when it was keyed with
    /// the same key, otherwise the file's idle pool is cleared and a new connection opened. A key without the SQLCipher
    /// bundle fails instead of writing a plain file.
    /// </summary>
    internal static async Task<SqliteConnection> OpenAsync(string dataSource, byte[]? key, CancellationToken cancellationToken,
        SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate, bool pooling = true)
    {
        var connectionString = ConnectionString(dataSource, mode, pooling);
        Pools.GetOrAdd(dataSource, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal)).TryAdd(connectionString, 0);
        var fingerprint = key is null ? Plain : Fingerprint(key);
        for (var attempt = 0; ; attempt++)
        {
            var connection = new SqliteConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                var handle = connection.Handle ?? throw new InvalidOperationException("The SQLite connection has no handle.");
                if (Keyed.TryGetValue(handle, out var used))
                {
                    if (CryptographicOperations.FixedTimeEquals(used, fingerprint))
                    {
                        return connection;
                    }

                    // Pooled under another key (or plain): never hand it out; drop the idle pool and open afresh.
                    if (attempt > 0)
                    {
                        throw new InvalidOperationException("A pooled SQLite connection keyed differently could not be replaced.");
                    }

                    SqliteConnection.ClearPool(connection);
                    await connection.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                if (key is not null)
                {
                    await using (var keying = connection.CreateCommand())
                    {
                        keying.CommandText = $"PRAGMA key = \"{Passphrase(key)}\"";
                        await keying.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await using var cipher = connection.CreateCommand();
                    cipher.CommandText = "PRAGMA cipher_version";
                    if (await cipher.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string { Length: > 0 })
                    {
                        throw new InvalidOperationException(
                            "An encryption key needs SQLitePCLRaw.bundle_e_sqlcipher (ADR-016); the plain SQLite library cannot encrypt. Never combine both bundles.");
                    }
                }

                Keyed.AddOrUpdate(handle, fingerprint);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>A one-way, non-secret tag of a key that tells pooled connections of different keys apart.</summary>
    private static byte[] Fingerprint(byte[] key)
    {
        Span<byte> input = stackalloc byte[32 + 21];
        "Bsync.sqlite.pool/v1\0"u8.CopyTo(input);
        key.CopyTo(input[21..]);
        try
        {
            return SHA256.HashData(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    /// <summary>
    /// The SQLCipher key string for a key: the raw-key form <c>x'…'</c>, so the 32 random bytes are the cipher key and
    /// no password derivation (256,000 PBKDF2 rounds per connection) runs.
    /// </summary>
    internal static string Passphrase(byte[] key)
    {
        EnsureNativeLibrary();
        if (key.Length != 32)
        {
            throw new ArgumentException("An encryption key is 32 bytes (256 bits).", nameof(key));
        }

        return $"x'{Convert.ToHexString(key)}'";
    }

    /// <summary>
    /// Fails with an actionable message when the application did not add a native SQLite bundle. The package depends on
    /// <c>Microsoft.Data.Sqlite.Core</c> only, so the plain (<c>e_sqlite3</c>) and SQLCipher (<c>e_sqlcipher</c>) builds
    /// stay the application's choice.
    /// </summary>
    internal static void EnsureNativeLibrary()
    {
        if (_nativeLibraryFound)
        {
            return;
        }

        // Creating a connection runs Microsoft.Data.Sqlite's bundle initializer; without a bundle no provider is set.
        using (new SqliteConnection())
        {
        }

        try
        {
            _ = SQLitePCL.raw.sqlite3_libversion_number();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new InvalidOperationException(
                "Bsync.Storage.Sqlite needs a native SQLite library. Add the package SQLitePCLRaw.bundle_e_sqlite3 to the " +
                "application, or SQLitePCLRaw.bundle_e_sqlcipher for encrypted databases (ADR-016); never both.", error);
        }

        _nativeLibraryFound = true;
    }
}
