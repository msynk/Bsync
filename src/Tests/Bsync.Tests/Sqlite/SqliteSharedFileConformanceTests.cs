using Bsync.Storage;
using Bsync.Testing;
using Bsync.Tests.Conformance;

namespace Bsync.Tests.Sqlite;

/// <summary>The shared store contract with two store instances on one file (writes through one, reads through the other).</summary>
public sealed class SqliteSharedFileConformanceTests : LocalStoreConformanceTests, IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    protected override async Task<ILocalStore<ConformanceDocument>> CreateStoreAsync() =>
        new SplitStore<ConformanceDocument>(await _database.OpenConformanceAsync(), await _database.OpenConformanceAsync());

    protected override async Task<ILocalStore<ConformanceDocument>> CreateIndexedStoreAsync() =>
        new SplitStore<ConformanceDocument>(await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes), await _database.OpenConformanceAsync(LocalStoreIndexConformance.Indexes));

    public void Dispose() => _database.Dispose();
}
