using Bsync.Storage;
using Bsync.Testing;
using Bsync.Tests.Conformance;

namespace Bsync.Tests.Sqlite;

/// <summary>The shared store contract, run against a real SQLite file.</summary>
public sealed class SqliteLocalStoreConformanceTests : LocalStoreConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    protected override async Task<ILocalStore<ConformanceDocument>> CreateStoreAsync() =>
        await _database.OpenConformanceAsync();

    protected override async Task<ILocalStore<ConformanceDocument>> CreateIndexedStoreAsync() =>
        await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes);

    public void Dispose() => _database.Dispose();
}
