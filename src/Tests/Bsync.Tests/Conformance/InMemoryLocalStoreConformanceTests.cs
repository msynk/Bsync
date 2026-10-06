using Bsync.Storage;
using Bsync.Testing;

namespace Bsync.Tests.Conformance;

public sealed class InMemoryLocalStoreConformanceTests : LocalStoreConformanceTests
{
    protected override Task<ILocalStore<ConformanceDocument>> CreateStoreAsync() =>
        Task.FromResult<ILocalStore<ConformanceDocument>>(new InMemoryLocalStore<ConformanceDocument>(Documents.DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument)));

    protected override Task<ILocalStore<ConformanceDocument>> CreateIndexedStoreAsync() =>
        Task.FromResult<ILocalStore<ConformanceDocument>>(new InMemoryLocalStore<ConformanceDocument>(Documents.DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument), LocalStoreIndexConformance.Indexes));
}
