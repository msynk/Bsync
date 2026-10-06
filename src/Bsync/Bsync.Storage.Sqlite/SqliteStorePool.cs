using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Connection-pool helpers.</summary>
public static class SqliteStorePool
{
    // Every connection string used per file (one per key), so Release closes all of their pools.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> Pools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Closes the pooled connections to one database file (for example before copying, restoring or deleting it).
    /// Unlike <see cref="SqliteConnection.ClearAllPools"/>, it does not touch other databases' connections.
    /// </summary>
    public static void Release(string dataSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        var strings = Pools.TryGetValue(dataSource, out var known) ? [.. known.Keys] : new List<string>();
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

    internal static string ConnectionString(string dataSource, byte[]? key = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        if (key is not null)
        {
            builder.Password = Passphrase(key);
        }

        var connectionString = builder.ToString();
        if (key is not null)
        {
            Pools.GetOrAdd(dataSource, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal)).TryAdd(connectionString, 0);
        }

        return connectionString;
    }

    /// <summary>
    /// The SQLCipher key string for a key: the raw-key form <c>x'…'</c>, so the 32 random bytes are the cipher key and
    /// no password derivation (256,000 PBKDF2 rounds per connection) runs.
    /// </summary>
    internal static string Passphrase(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("An encryption key is 32 bytes (256 bits).", nameof(key));
        }

        return $"x'{Convert.ToHexString(key)}'";
    }
}
