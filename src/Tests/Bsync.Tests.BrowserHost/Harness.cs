using System.Text.Json;
using Bsync.Clocks;
using Bsync.Documents;
using Bsync.Protocol;
using Bsync.Storage;
using Bsync.Blazor.IndexedDb;
using Bsync.Testing;
using Bsync.Transport;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

/// <summary>
/// Entry points the Playwright tests call with <c>DotNet.invokeMethodAsync("Bsync.Tests.BrowserHost", ...)</c>.
/// Every method returns JSON text so no reflection-based serialization is needed in the trimmed app.
/// </summary>
public static class Harness
{
    private const string Assembly = "Bsync.Tests.BrowserHost";

    private static IJSRuntime _js = null!;
    private static Uri _baseAddress = null!;
    private static Replica? _replica;
    private static IndexedDbReplicaLease? _lease;

    public static void Initialize(IJSRuntime js, Uri baseAddress)
    {
        _js = js;
        _baseAddress = baseAddress;
    }

    [JSInvokable(nameof(RunStoreConformance))]
    public static async Task<string> RunStoreConformance(string databasePrefix, bool encrypted)
    {
        var key = encrypted ? System.Security.Cryptography.RandomNumberGenerator.GetBytes(32) : null;
        var results = new List<CaseResult>();
        var n = 0;
        foreach (var (conformanceCase, indexes) in LocalStoreConformance.Cases.Select(c => (c, (IReadOnlyList<SyncIndex<ConformanceDocument>>)[]))
            .Concat(LocalStoreIndexConformance.Cases.Select(c => (c, LocalStoreIndexConformance.Indexes))))
        {
            var opened = new List<IndexedDbLocalStore<ConformanceDocument>>();
            var database = $"{databasePrefix}-{n++}";
            try
            {
                await conformanceCase.RunAsync(async () =>
                {
                    var store = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(
                        _js,
                        new IndexedDbStoreOptions { DatabaseName = database, Collection = "conformance", EncryptionKey = key },
                        ConformanceJsonContext.Default.ConformanceDocument,
                        indexes);
                    opened.Add(store);
                    return store;
                });
                results.Add(new CaseResult(conformanceCase.Name, true, null));
            }
            catch (Exception error)
            {
                results.Add(new CaseResult(conformanceCase.Name, false, $"{error.GetType().Name}: {error.Message}"));
            }
            finally
            {
                foreach (var store in opened)
                {
                    await store.DisposeAsync();
                }

                await IndexedDbLocalStore<ConformanceDocument>.DeleteDatabaseAsync(_js, database);
            }
        }

        return JsonSerializer.Serialize(results, HarnessJson.Default.ListCaseResult);
    }

    /// <summary>
    /// ADR-018 in the browser: keys are kept and used (not only evaluated in memory), a writer without the indexes makes
    /// them unusable while queries stay correct, and the next indexed open rebuilds them.
    /// </summary>
    [JSInvokable(nameof(RunIndexLifecycle))]
    public static async Task<string> RunIndexLifecycle(string database)
    {
        var steps = new List<string>();
        var options = new IndexedDbStoreOptions { DatabaseName = database, Collection = "conformance" };
        var title = LocalStoreIndexConformance.Title;
        static RecordUpdate<ConformanceDocument> Put(string id, string text) =>
            new(id, _ => new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = id, Title = text }, null, IsDirty: true) { LocalRevision = 1 });
        async Task<string> Ids(ILocalStore<ConformanceDocument> store) =>
            string.Join(",", (await store.QueryIndexAsync(title.All(), null, 100)).Select(d => d.Id));
        try
        {
            var plain = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, options, ConformanceJsonContext.Default.ConformanceDocument);
            await plain.UpdateAsync([Put("a", "b"), Put("b", "a")]); // written before any index existed
            var indexed = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, options, ConformanceJsonContext.Default.ConformanceDocument, LocalStoreIndexConformance.Indexes);
            steps.Add($"rebuilt:{await indexed.IndexStateAsync()}|{await Ids(indexed)}");
            await indexed.UpdateAsync([Put("c", "0")]);
            steps.Add($"maintained:{await indexed.IndexStateAsync()}|{await Ids(indexed)}|{await indexed.CountIndexAsync(title.All())}");
            await plain.UpdateAsync([Put("d", "1")]);
            steps.Add($"invalidated:{await indexed.IndexStateAsync()}|{await Ids(indexed)}");
            await indexed.DisposeAsync();
            var reopened = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, options, ConformanceJsonContext.Default.ConformanceDocument, LocalStoreIndexConformance.Indexes);
            steps.Add($"reopened:{await reopened.IndexStateAsync()}|{await Ids(reopened)}");
            await reopened.DisposeAsync();
            await plain.DisposeAsync();
        }
        catch (Exception error)
        {
            steps.Add($"error:{error.GetType().Name}: {error.Message}");
        }
        finally
        {
            await IndexedDbLocalStore<ConformanceDocument>.DeleteDatabaseAsync(_js, database);
        }

        return string.Join(" ;; ", steps);
    }

    /// <summary>ADR-016 in the browser: documents are stored sealed; a missing or wrong key is refused explicitly.</summary>
    [JSInvokable(nameof(RunEncryptionChecks))]
    public static async Task<string> RunEncryptionChecks(string database)
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        IndexedDbStoreOptions Options(byte[]? k) => new() { DatabaseName = database, Collection = "conformance", EncryptionKey = k };
        var steps = new List<string>();
        var store = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, Options(key), ConformanceJsonContext.Default.ConformanceDocument);
        await store.UpdateAsync([new("n1", _ => new SyncRecord<ConformanceDocument>(new ConformanceDocument { Id = "n1", Title = "secret-title-0123456789" }, null, IsDirty: true) { LocalRevision = 1 })]);
        await store.DisposeAsync();
        foreach (var (name, k) in new[] { ("none", (byte[]?)null), ("wrong", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) })
        {
            try
            {
                await (await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, Options(k), ConformanceJsonContext.Default.ConformanceDocument)).DisposeAsync();
                steps.Add($"{name}:opened");
            }
            catch (LocalStoreUnavailableException error)
            {
                steps.Add($"{name}:{error.Reason}");
            }
        }

        var reopened = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(_js, Options(key), ConformanceJsonContext.Default.ConformanceDocument);
        steps.Add($"right:{(await reopened.GetAsync("n1"))!.Current.Title}");
        await reopened.DisposeAsync();
        return string.Join(" ;; ", steps);
    }

    private static byte[] BlobContent(int seed, int size)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>F1 in the browser: import and ranged reads; a corrupted transfer is refused; wipe.</summary>
    [JSInvokable(nameof(RunBlobChecks))]
    public static async Task<string> RunBlobChecks(string name)
    {
        var steps = new List<string>();
        var store = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, name);
        var content = BlobContent(1, 3 * 1024 * 1024 + 17);
        var (sha, size) = await store.ImportAsync(new MemoryStream(content));
        await using (var read = store.OpenRead(sha, size))
        {
            var copy = new MemoryStream();
            await read.CopyToAsync(copy);
            steps.Add($"import:{size == content.Length && copy.ToArray().AsSpan().SequenceEqual(content) && sha == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))}");
        }

        var other = BlobContent(2, 1024 * 1024);
        var claimed = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(other));
        await store.AppendPartialAsync(claimed, BlobContent(3, 1024 * 1024)); // not the bytes the hash names
        steps.Add($"corrupt:{await store.CompletePartialAsync(claimed)}|{await store.SizeAsync(claimed) is null}|{await store.PartialLengthAsync(claimed)}");
        steps.Add($"list:{string.Join(",", await store.ListAsync()) == sha}");
        await Bsync.Blazor.Blobs.BrowserBlobStore.DeleteAllAsync(_js, name);
        steps.Add($"wiped:{(await store.ListAsync()).Length}");
        return string.Join(" ;; ", steps);
    }

    /// <summary>F1: the first half of a transfer, before the page reloads.</summary>
    [JSInvokable(nameof(BlobFirstHalf))]
    public static async Task<string> BlobFirstHalf(string name)
    {
        var content = BlobContent(4, 2 * 1024 * 1024);
        var store = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, name);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        await store.AppendPartialAsync(sha, content[..(1024 * 1024)]);
        return sha;
    }

    /// <summary>F1: after the reload, the transfer continues from what is kept, and completes verified.</summary>
    [JSInvokable(nameof(BlobSecondHalf))]
    public static async Task<string> BlobSecondHalf(string name, string sha)
    {
        var content = BlobContent(4, 2 * 1024 * 1024);
        var store = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, name);
        var kept = await store.PartialLengthAsync(sha);
        await store.AppendPartialAsync(sha, content[(int)kept..]);
        var completed = await store.CompletePartialAsync(sha);
        var size = await store.SizeAsync(sha);
        await Bsync.Blazor.Blobs.BrowserBlobStore.DeleteAllAsync(_js, name);
        return $"kept:{kept} ;; completed:{completed} ;; size:{size}";
    }

    [JSInvokable(nameof(OpenReplica))]
    public static async Task<string> OpenReplica(string database, string node)
    {
        try
        {
            if (_replica is not null)
            {
                await _replica.Store.DisposeAsync();
            }

            var store = await IndexedDbLocalStore<ConformanceDocument>.OpenAsync(
                _js,
                new IndexedDbStoreOptions { DatabaseName = database, Collection = "notes" },
                ConformanceJsonContext.Default.ConformanceDocument);
            var http = new HttpClient { BaseAddress = _baseAddress };
            var transport = new HttpSyncTransport<ConformanceDocument>(
                http,
                new HttpSyncTransportOptions { Collection = "notes", SchemaId = "notes-v1", RequestTimeout = TimeSpan.FromSeconds(10) },
                SyncJsonTypes<ConformanceDocument>.From(ConformanceJsonContext.Default));
            var engine = new SyncEngine<ConformanceDocument>(
                store,
                transport,
                new HybridLogicalClock(node),
                DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument));
            _replica = new Replica(store, engine);
            return Ok();
        }
        catch (Exception error)
        {
            return Error(error);
        }
    }

    [JSInvokable(nameof(Write))]
    public static Task<string> Write(string id, string title) =>
        Guard(async replica => (await replica.Engine.WriteAsync(new ConformanceDocument { Id = id, Title = title })).LocalRevision.ToString());

    [JSInvokable(nameof(Delete))]
    public static Task<string> Delete(string id) =>
        Guard(async replica => (await replica.Engine.DeleteAsync(id))?.LocalRevision.ToString() ?? "none");

    [JSInvokable(nameof(Sync))]
    public static Task<string> Sync() =>
        Guard(async replica =>
        {
            var result = await replica.Engine.SyncAsync();
            return JsonSerializer.Serialize(
                new SyncSummary(result.Pulled, result.Pushed, result.Conflicts, result.IsComplete, result.ResetPerformed, result.MissingAfterReset),
                HarnessJson.Default.SyncSummary);
        });

    [JSInvokable(nameof(Query))]
    public static Task<string> Query() =>
        Guard(async replica =>
        {
            var documents = await replica.Engine.QueryAsync(includeDeleted: true);
            var views = new List<DocumentView>();
            foreach (var document in documents.OrderBy(d => d.Id, StringComparer.Ordinal))
            {
                var record = await replica.Engine.GetAsync(document.Id);
                views.Add(new DocumentView(document.Id, document.Title, document.Deleted, record!.IsDirty));
            }

            return JsonSerializer.Serialize(views, HarnessJson.Default.ListDocumentView);
        });

    [JSInvokable(nameof(Identity))]
    public static Task<string> Identity() =>
        Guard(async replica => (await replica.Store.GetReplicaIdentityAsync()).ReplicaId);

    [JSInvokable(nameof(CloseReplica))]
    public static async Task<string> CloseReplica()
    {
        if (_replica is not null)
        {
            await _replica.Store.DisposeAsync();
            _replica = null;
        }

        return Ok();
    }

    [JSInvokable(nameof(TryLease))]
    public static async Task<string> TryLease(string name)
    {
        try
        {
            _lease = await IndexedDbReplicaLease.TryAcquireAsync(_js, name);
            return _lease is null ? "busy" : "acquired";
        }
        catch (Exception error)
        {
            return Error(error);
        }
    }

    [JSInvokable(nameof(ReleaseLease))]
    public static async Task<string> ReleaseLease()
    {
        if (_lease is not null)
        {
            await _lease.DisposeAsync();
            _lease = null;
        }

        return Ok();
    }

    [JSInvokable(nameof(DeleteDatabase))]
    public static async Task<string> DeleteDatabase(string name)
    {
        try
        {
            await IndexedDbLocalStore<ConformanceDocument>.DeleteDatabaseAsync(_js, name);
            return Ok();
        }
        catch (Exception error)
        {
            return Error(error);
        }
    }

    private static async Task<string> Guard(Func<Replica, Task<string>> action)
    {
        if (_replica is null)
        {
            return Error(new InvalidOperationException("No replica is open."));
        }

        try
        {
            return await action(_replica);
        }
        catch (Exception error)
        {
            return Error(error);
        }
    }

    private static string Ok() => "ok";

    private static string Error(Exception error) => error switch
    {
        LocalStoreUnavailableException store => $"error:store:{store.Reason}",
        SyncTransportException transport => $"error:transport:{transport.ErrorCode}",
        _ => $"error:{error.GetType().Name}:{error.Message}",
    };

    private sealed record Replica(IndexedDbLocalStore<ConformanceDocument> Store, SyncEngine<ConformanceDocument> Engine);
}
