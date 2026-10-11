namespace Bsync.Storage.Sqlite;

/// <summary>
/// The database file exists but cannot be read: it is encrypted with another key (or with a key while none was given,
/// or the reverse), or it is not a SQLite database (ADR-016). Nothing was changed; the file is not replaced by a new
/// replica.
/// </summary>
public sealed class SqliteStoreUnreadableException(string message, Exception? innerException = null) : IOException(message, innerException);
