using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Connection-pool helpers.</summary>
public static class SqliteStorePool
{
    /// <summary>
    /// Closes the pooled connections to one database file (for example before copying, restoring or deleting it).
    /// Unlike <see cref="SqliteConnection.ClearAllPools"/>, it does not touch other databases' connections.
    /// </summary>
    public static void Release(string dataSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);
        using var connection = new SqliteConnection(ConnectionString(dataSource));
        SqliteConnection.ClearPool(connection);
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

    internal static string ConnectionString(string dataSource) => new SqliteConnectionStringBuilder
    {
        DataSource = dataSource,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = true,
    }.ToString();
}
