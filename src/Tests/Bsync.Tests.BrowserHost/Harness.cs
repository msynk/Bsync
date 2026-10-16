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
    private static IAsyncDisposable? _cacheLock;
    private static Stream? _managedReader;
    private static Bsync.Blobs.PackageSnapshot? _managedSnapshot;
    private static readonly List<IAsyncDisposable> AccountLocks = [];

    [JSInvokable(nameof(AccountResourceCheck))]
    public static async Task<string> AccountResourceCheck(string accountName, bool exclusive, bool hold)
    {
        var locks = await BrowserAccountResourceLock.OpenAsync(_js);
        var acquired = await locks.TryAcquireAsync(new("tests", "browser", accountName), exclusive);
        if (acquired is null) { await locks.DisposeAsync(); return "blocked"; }
        if (hold) { AccountLocks.Add(locks); AccountLocks.Add(acquired); }
        else { await acquired.DisposeAsync(); await locks.DisposeAsync(); }
        return "acquired";
    }

    [JSInvokable(nameof(ReaderRecoveryChecks))]
    public static async Task<string> ReaderRecoveryChecks(string accountName, bool hold)
    {
        var account = new Bsync.Client.AccountScope("tests", "browser", accountName);
        var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        var locks = await Bsync.Blazor.Blobs.BrowserBlobCacheLock.OpenAsync(_js, account);
        var bytes = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new Bsync.Blobs.ManagedBlobCache(bytes, metadata, locks, 8, 8);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1 }));
        if (hold)
        {
            // Deliberately keep the readers/resources alive until the test terminates this entire tab.
            var jobs = new Bsync.Blobs.BlobTransferManager(metadata, cache, new(new HttpClient { BaseAddress = _baseAddress }));
            var packages = new Bsync.Blobs.OfflinePackageManager(metadata, cache, jobs, locks, (_, _) => Task.FromResult(true));
            await cache.ImportPinnedAsync(new MemoryStream([1]), "pending/work");
            await packages.StageAsync(new(1, "manual", "one", null, [new("page", hash, 1, true, [])]));
            if (!await packages.TryActivateAsync("manual", "one")) throw new Exception("Activation failed.");
            _managedSnapshot = await packages.OpenActiveAsync("manual");
            _managedReader = await _managedSnapshot.OpenAsync("page");
            return "held";
        }
        try
        {
            await cache.ReconcileAsync();
            var owners = (await cache.GetEntryAsync(hash)).Owners;
            if (!owners.Contains("pending/work") || !owners.Any(p => p.StartsWith("package/", StringComparison.Ordinal))) throw new Exception("Durable work pin was reclaimed.");
            return owners.Count(p => p.StartsWith("reader-v2/", StringComparison.Ordinal) || p.StartsWith("snapshot-v2/", StringComparison.Ordinal)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { await metadata.DisposeAsync(); await locks.DisposeAsync(); }
    }

    [JSInvokable(nameof(ReadModelChecks))]
    public static async Task<string> ReadModelChecks(string accountName, bool reopen)
    {
        await using var store = await IndexedDbOrchestrationStore.OpenAsync(_js, new("tests", "browser", accountName));
        var model = new Bsync.Client.LocalReadModel(store, "catalog");
        static async IAsyncEnumerable<Bsync.Client.ReadModelRow> Rows(int count, bool fail)
        {
            for (var i = 0; i < count; i++) yield return new(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "derived");
            await Task.CompletedTask;
            if (fail) throw new IOException("Interrupted build.");
        }
        if (!reopen)
        {
            if (!await model.RebuildAsync("1", "snapshot1", Rows(3, false), TimeSpan.FromSeconds(15))) throw new Exception("Initial build failed.");
            try { await model.RebuildAsync("2", "snapshot2", Rows(130, true), TimeSpan.FromSeconds(15)); }
            catch (IOException) { return "ok"; }
            throw new Exception("Interrupted build unexpectedly completed.");
        }
        var status = await model.GetStatusAsync();
        if (status.Active?.RowCount != 3 || status.AppliedRows != 128 || status.BuildingGeneration is null) throw new Exception("Durable build progress lost.");
        var old = (await model.OpenActiveAsync())!;
        if (!await model.RebuildAsync("2", "new-epoch/snapshot", Rows(1, false), TimeSpan.FromSeconds(15))) throw new Exception("Rebuild failed.");
        if ((await old.ReadPageAsync(10)).Rows.Count != 3 || (await (await model.OpenActiveAsync())!.ReadPageAsync(10)).Rows.Count != 1)
            throw new Exception("Reader mixed generations or removal was not reconciled.");
        var report = await Bsync.Diagnostics.OfflineDiagnosticExport.ExportJsonAsync(store);
        if (report.Contains("new-epoch", StringComparison.Ordinal)) throw new Exception("Diagnostic report leaked source identity.");
        await store.DeleteAllAsync();
        return "ok";
    }

    [JSInvokable(nameof(CacheLockCheck))]
    public static async Task<string> CacheLockCheck(string accountName, bool hold)
    {
        if (hold && _cacheLock is not null) throw new InvalidOperationException("This test tab already holds the cache lock.");
        var locks = await Bsync.Blazor.Blobs.BrowserBlobCacheLock.OpenAsync(_js, new("tests", "browser", accountName));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            var acquired = await locks.AcquireAsync("metadata", timeout.Token);
            if (hold) _cacheLock = acquired;
            else { await acquired.DisposeAsync(); await locks.DisposeAsync(); }
            return "acquired";
        }
        catch (OperationCanceledException) { await locks.DisposeAsync(); return "blocked"; }
    }

    [JSInvokable(nameof(OrchestrationChecks))]
    public static async Task<string> OrchestrationChecks(string accountName, bool reopen)
    {
        await using var store = await IndexedDbOrchestrationStore.OpenAsync(_js, new("tests", "browser", accountName));
        if (reopen)
        {
            if ((await store.ReadAsync("jobs/a"))?.Payload != "durable") throw new Exception("Metadata did not survive reload.");
            await store.DeleteAllAsync();
            return "ok";
        }
        if (!await store.TryWriteAsync([new("jobs/a", null, "durable"), new("jobs/b", null, "b")])) throw new Exception("Initial write failed.");
        if (await store.TryWriteAsync([new("jobs/c", null, "partial"), new("jobs/a", null, "wrong")])) throw new Exception("Stale batch succeeded.");
        if (await store.ReadAsync("jobs/c") is not null) throw new Exception("Partial batch committed.");
        var first = (await store.ReadPageAsync("jobs/", null, 1)).Single();
        if (first.Key != "jobs/a" || (await store.ReadPageAsync("jobs/", first.Key, 1)).Single().Key != "jobs/b") throw new Exception("Bad page order.");
        if (!await store.TryWriteAsync([new("lease", null, "old", 1)])) throw new Exception("Lease setup failed.");
        if (await store.TryWriteAsync([new("stale", null, "bad")], new("lease", "old"))) throw new Exception("Expired owner committed.");
        await using var other = await IndexedDbOrchestrationStore.OpenAsync(_js, new("tests", "browser", accountName + "-other"));
        if (await other.ReadAsync("jobs/a") is not null) throw new Exception("Account leak.");
        await other.DeleteAllAsync();
        return "ok";
    }

    public static void Initialize(IJSRuntime js, Uri baseAddress)
    {
        _js = js;
        _baseAddress = baseAddress;
    }

    [JSInvokable(nameof(CacheRepairChecks))]
    public static async Task<string> CacheRepairChecks(string accountName, bool repair)
    {
        var account = new Bsync.Client.AccountScope("tests", "browser", accountName);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var locks = await Bsync.Blazor.Blobs.BrowserBlobCacheLock.OpenAsync(_js, account);
        var bytes = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new Bsync.Blobs.ManagedBlobCache(bytes, metadata, locks, 8, 8);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 1, 1, 1 }));
        if (!repair)
        {
            await cache.ImportPinnedAsync(new MemoryStream([1, 1, 1, 1]), "pending/work");
            return account.StorageKey + "/" + hash;
        }
        await cache.ImportAsync(new MemoryStream([1, 1, 1, 1]));
        await using (var content = await cache.OpenReadAsync(hash))
        {
            var buffer = new byte[4];
            await content!.ReadExactlyAsync(buffer);
            if (buffer.Any(b => b != 1)) throw new Exception("Reimport did not repair corrupted bytes.");
        }
        if (!(await cache.GetEntryAsync(hash)).Owners.Contains("pending/work")) throw new Exception("Repair lost pending pin.");
        await metadata.DeleteAllAsync();
        await Bsync.Blazor.Blobs.BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
    }

    [JSInvokable(nameof(PackageChecks))]
    public static async Task<string> PackageChecks(string accountName, bool reopen)
    {
        var account = new Bsync.Client.AccountScope("tests", "browser", accountName);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var locks = await Bsync.Blazor.Blobs.BrowserBlobCacheLock.OpenAsync(_js, account);
        var bytes = await Bsync.Blazor.Blobs.BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var estimate = await bytes.EstimateStorageAsync();
        if (estimate.QuotaBytes < 0 || estimate.UsageBytes < 0) throw new Exception("Invalid browser storage estimate.");
        var cache = new Bsync.Blobs.ManagedBlobCache(bytes, metadata, locks, 8, 8);
        var transfer = new Bsync.Blobs.BlobTransferManager(metadata, cache, new(new HttpClient { BaseAddress = _baseAddress }));
        var packages = new Bsync.Blobs.OfflinePackageManager(metadata, cache, transfer, locks, (_, _) => Task.FromResult(true));
        static Bsync.Blobs.OfflinePackageManifest Manifest(string revision, byte value) => new(1, "manual", revision, null,
            [new("page", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new[] { value })), 1, true, [])]);
        if (!reopen)
        {
            await packages.StageAsync(Manifest("one", 1));
            await cache.ImportAsync(new MemoryStream([1]));
            if (!await packages.TryActivateAsync("manual", "one")) throw new Exception("Complete candidate did not activate.");
            await packages.StageAsync(Manifest("two", 2));
            if (await packages.TryActivateAsync("manual", "two")) throw new Exception("Incomplete candidate activated.");
            return "ok";
        }
        if ((await packages.GetStatusAsync("manual")).ActiveRevision != "one") throw new Exception("Active revision lost across reload.");
        if ((await packages.GetLastVerificationAsync("manual"))?.ActiveRevision != "one") throw new Exception("Cached verification missing.");
        using (var metrics = new Bsync.Diagnostics.OfflineMetrics(metadata))
            if ((await metrics.RefreshAsync()).Counts["package.last_verified.ReadyKnownOutdated"] != 1) throw new Exception("Package diagnostic count missing.");
        await using (var snapshot = await packages.OpenActiveAsync("manual"))
        {
            await cache.ImportAsync(new MemoryStream([2]));
            if (!await packages.TryActivateAsync("manual", "two")) throw new Exception("New candidate did not activate.");
            await packages.RemoveRetiredAsync("manual", "one");
            await using var content = await snapshot.OpenAsync("page");
            var buffer = new byte[1];
            if (await content!.ReadAsync(buffer) != 1 || buffer[0] != 1) throw new Exception("Reader mixed revisions.");
        }
        await bytes.DeleteAsync(Manifest("two", 2).Items[0].Sha256);
        if ((await packages.GetStatusAsync("manual")).Readiness != Bsync.Blobs.OfflinePackageReadiness.Incomplete) throw new Exception("Storage loss still reported ready.");
        await metadata.DeleteAllAsync();
        await Bsync.Blazor.Blobs.BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
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
        steps.Add($"wiped:{(await store.ListAsync()).Count}");
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
