using System.Security.Cryptography;
using Bsync.Blazor.Blobs;
using Bsync.Blazor.IndexedDb;
using Bsync.Blobs;
using Bsync.Client;
using Microsoft.JSInterop;

namespace Bsync.Tests.BrowserHost;

public static partial class Harness
{
    [JSInvokable(nameof(PackageSetChecks))]
    public static async Task<string> PackageSetChecks(string name, bool reopen)
    {
        var account = new AccountScope("tests", "browser", name);
        await using var metadata = await IndexedDbOrchestrationStore.OpenAsync(_js, account);
        await using var locks = await BrowserBlobCacheLock.OpenAsync(_js, account);
        var bytes = await BrowserBlobStore.OpenAsync(_js, account.StorageKey);
        var cache = new ManagedBlobCache(bytes, metadata, locks, 8, 8);
        using var http = new HttpClient { BaseAddress = _baseAddress };
        var transfers = new BlobTransferManager(metadata, cache, new(http));
        var authorized = true;
        var manager = new OfflinePackageManager(metadata, cache, transfers, locks, (_, _) => Task.FromResult(authorized));
        static OfflinePackageManifest[] Children(string revision, byte value) => new[] { "left", "right" }.Select(id =>
            new OfflinePackageManifest(1, id, revision, null, Enumerable.Range(0, 130).Select(i =>
                new OfflinePackageItem(i.ToString(), Convert.ToHexStringLower(SHA256.HashData(new[] { value })), 1, true, [])).ToArray())).ToArray();
        static OfflinePackageSetManifest Root(string revision, OfflinePackageManifest[] children) => new(1, "set", revision, null,
            children.Select(c => new OfflinePackageSetChild(c.PackageId, c.Revision, OfflinePackageManager.GetManifestSha256(c))).ToArray());
        var first = Children("one", 1); var next = Children("two", 2);
        if (!reopen)
        {
            await manager.StageSetAsync(Root("one", first), first);
            await cache.ImportAsync(new MemoryStream([1]));
            if (!await manager.TryActivateSetAsync("set", "one")) throw new Exception("First root did not activate.");
            await manager.StageSetAsync(Root("two", next), next);
            if (await manager.TryActivateSetAsync("set", "two")) throw new Exception("Incomplete set activated.");
            return "ok";
        }
        var pending = await manager.GetSetStatusAsync("set");
        if (pending.ActiveRevision != "one" || pending.BytesRemaining != 1 || pending.MissingRequired.Length != 260)
            throw new Exception("Reload lost root or shared-byte accounting.");
        await using (var old = await manager.OpenActiveSetAsync("set"))
        {
            await cache.ImportAsync(new MemoryStream([2]));
            if (!await manager.TryActivateSetAsync("set", "two")) throw new Exception("Replacement set did not activate.");
            await manager.RemoveRetiredSetAsync("set", "one");
            foreach (var child in first) await manager.RemoveRetiredAsync(child.PackageId, child.Revision);
            await using var read = await old.OpenAsync("right", "129"); var buffer = new byte[1];
            if (await read!.ReadAsync(buffer) != 1 || buffer[0] != 1) throw new Exception("Live reader mixed set revisions.");
        }
        authorized = false;
        try { await manager.GetSetStatusAsync("set"); throw new Exception("Revoked set disclosed readiness."); }
        catch (UnauthorizedAccessException) { }
        try { await using var denied = await manager.OpenActiveSetAsync("set"); throw new Exception("Revoked set opened."); }
        catch (UnauthorizedAccessException) { }
        authorized = true;
        await bytes.DeleteAsync(next[0].Items[0].Sha256);
        if ((await manager.GetSetStatusAsync("set")).ActiveMissingRequired.Length != 260) throw new Exception("Damaged set reported ready.");
        await metadata.DeleteAllAsync(); await BrowserBlobStore.DeleteAllAsync(_js, account.StorageKey);
        return "ok";
    }
}
