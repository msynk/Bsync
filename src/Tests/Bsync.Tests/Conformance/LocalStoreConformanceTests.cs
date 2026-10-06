using Bsync.Storage;
using Bsync.Testing;
using Xunit;

namespace Bsync.Tests.Conformance;

/// <summary>
/// Runs the shared <see cref="LocalStoreConformance"/> cases (also run in browsers for IndexedDB) against a
/// provider. A provider is not supported until every case passes.
/// </summary>
public abstract class LocalStoreConformanceTests
{
    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in LocalStoreConformance.Cases)
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    public static TheoryData<string> IndexCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var conformanceCase in LocalStoreIndexConformance.Cases)
        {
            data.Add(conformanceCase.Name);
        }

        return data;
    }

    /// <summary>Creates an empty store.</summary>
    protected abstract Task<ILocalStore<ConformanceDocument>> CreateStoreAsync();

    /// <summary>Creates an empty store that maintains <see cref="LocalStoreIndexConformance.Indexes"/>.</summary>
    protected abstract Task<ILocalStore<ConformanceDocument>> CreateIndexedStoreAsync();

    [Theory]
    [MemberData(nameof(CaseNames))]
    public Task Conformance(string name) =>
        LocalStoreConformance.Cases.Single(c => c.Name == name).RunAsync(CreateStoreAsync);

    [Theory]
    [MemberData(nameof(IndexCaseNames))]
    public Task IndexConformance(string name) =>
        LocalStoreIndexConformance.Cases.Single(c => c.Name == name).RunAsync(CreateIndexedStoreAsync);
}
