using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Bsync.Tests.TestSupport;
using Microsoft.Data.Sqlite;

namespace Bsync.Tests.Sqlite;

/// <summary>A temporary SQLite database file, deleted on dispose.</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    public SqliteTestDatabase()
    {
        Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bsync-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        Path = System.IO.Path.Combine(Directory, "replica.db");
    }

    public string Directory { get; }

    public string Path { get; }

    public Task<SqliteLocalStore<Note>> OpenAsync(string collection = "notes", SqliteDurability durability = SqliteDurability.Full) =>
        SqliteLocalStore<Note>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = Path, Collection = collection, Durability = durability },
            NoteJsonContext.Default.Note);

    public SqliteLocalStore<Note> Open(string collection = "notes") => OpenAsync(collection).GetAwaiter().GetResult();

    public Task<SqliteLocalStore<Bsync.Testing.ConformanceDocument>> OpenConformanceAsync(IEnumerable<SyncIndex<Bsync.Testing.ConformanceDocument>>? indexes = null) =>
        SqliteLocalStore<Bsync.Testing.ConformanceDocument>.OpenAsync(
            new SqliteLocalStoreOptions { DataSource = Path, Collection = "conformance" },
            Bsync.Testing.ConformanceJsonContext.Default.ConformanceDocument,
            indexes ?? []);

    public void Dispose()
    {
        SqliteStorePool.Release(Path);
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A pooled handle may still be closing; the OS temp folder is cleaned eventually.
        }
    }
}
