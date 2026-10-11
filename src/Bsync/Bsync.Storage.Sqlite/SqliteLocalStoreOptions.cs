using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bsync.Clocks;
using Microsoft.Data.Sqlite;

namespace Bsync.Storage.Sqlite;

/// <summary>Options for <see cref="SqliteLocalStore{TDocument}"/>.</summary>
public sealed class SqliteLocalStoreOptions
{
    /// <summary>Path of the database file. Created if missing.</summary>
    public required string DataSource { get; init; }

    /// <summary>
    /// Collection name. Several collections may share one database file; each has its own records,
    /// checkpoint, generation and clock high-water mark.
    /// </summary>
    public string Collection { get; init; } = "default";

    /// <summary>Durability level. Default <see cref="SqliteDurability.Full"/>.</summary>
    public SqliteDurability Durability { get; init; } = SqliteDurability.Full;

    /// <summary>How long a writer waits for another connection's write lock. Default 30 seconds.</summary>
    public TimeSpan BusyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The 32-byte key the database is encrypted with (SQLCipher, ADR-016), or <see langword="null"/> for a plain
    /// database. Keep it in the platform's protected store (DPAPI, Keystore, Keychain), never next to the file. Opening
    /// with a wrong or missing key fails with <see cref="SqliteStoreUnreadableException"/>; it never creates a new replica.
    /// Needs the SQLCipher native library: the application references <c>SQLitePCLRaw.bundle_e_sqlcipher</c> instead of
    /// <c>SQLitePCLRaw.bundle_e_sqlite3</c>; with the plain library, opening with a key throws.
    /// </summary>
    public byte[]? EncryptionKey { get; init; }
}
