using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Samples.NativeBackground;

public sealed record BackgroundDownloadReceipt(int SchemaVersion, string AccountKey, string Id, string Sha256,
    long Size, string PinOwner, bool Imported = false);

/// <summary>A fresh, resource-locked account lifetime; never return a scope for a deleted or unavailable account.</summary>
public interface IBackgroundDownloadScope : IAsyncDisposable
{
    AccountScope Account { get; }
    ManagedBlobCache Cache { get; }
}

/// <summary>
/// Durable handoff from OS download callbacks to a fresh account/cache lifetime. Use a dedicated per-account store,
/// spool and lock directory. The spool contains plaintext: enable this recipe only when the host's file-protection
/// policy permits OS-owned plaintext downloads. It is not covered by encrypted-cache guarantees.
/// </summary>
public sealed class BackgroundDownloadInbox
{
    private const string Prefix = "native-download/v1/";
    private readonly IOrchestrationStore _store;
    private readonly IBlobCacheLock _locks;
    private readonly string _directory;
    private readonly long _budget;
    public AccountScope Account { get; }

    public BackgroundDownloadInbox(AccountScope account, IOrchestrationStore store, IBlobCacheLock locks,
        string directory, long budgetBytes = 512L * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budgetBytes, 0);
        Account = account; _store = store; _locks = locks; _budget = budgetBytes;
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    private static string Key(string id)
    {
        if (!Guid.TryParseExact(id, "N", out var parsed) || parsed.ToString("N") != id)
            throw new ArgumentException("A download ID is a lowercase GUID in N format.", nameof(id));
        return Prefix + id;
    }
    private string FilePath(string id) { _ = Key(id); return Path.Combine(_directory, id + ".download"); }
    private BackgroundDownloadReceipt Decode(OrchestrationRecord record)
    {
        var receipt = JsonSerializer.Deserialize(record.Payload, DownloadInboxJson.Default.BackgroundDownloadReceipt)
            ?? throw new InvalidDataException("Missing download receipt.");
        if (receipt.SchemaVersion != 1 || receipt.AccountKey != Account.StorageKey || Key(receipt.Id) != record.Key ||
            receipt.Size < 0 || receipt.Size > _budget || string.IsNullOrWhiteSpace(receipt.PinOwner) || receipt.PinOwner.Length > 256)
            throw new InvalidDataException("Unsupported or mismatched download receipt; retained.");
        ValidateHash(receipt.Sha256);
        return receipt;
    }
    private static void ValidateHash(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !(c is >= 'a' and <= 'f' or >= '0' and <= '9')))
            throw new ArgumentException("Expected a lowercase SHA-256.", nameof(hash));
    }
    private async Task<IReadOnlyList<OrchestrationRecord>> Records(CancellationToken ct)
    {
        var records = await _store.ReadPageAsync(Prefix, null, 101, ct).ConfigureAwait(false);
        if (records.Count > 100) throw new InvalidDataException("Download catalog exceeds the 100-job bound.");
        return records;
    }

    /// <summary>Persist before creating/resuming an OS task. No URL, token or cookie enters this catalog.</summary>
    public async Task<string> PrepareAsync(string hash, long size, string pinOwner, CancellationToken ct = default)
    {
        ValidateHash(hash);
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentException.ThrowIfNullOrWhiteSpace(pinOwner);
        if (pinOwner.Length > 256) throw new ArgumentException("Pin owner exceeds 256 characters.", nameof(pinOwner));
        await using var held = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false);
        var records = await Records(ct).ConfigureAwait(false);
        if (records.Count >= 100) throw new BlobCapacityException();
        long reserved = 0;
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var existing = Decode(record);
            var path = FilePath(existing.Id); known.Add(path);
            reserved = checked(reserved + Math.Max(existing.Size, File.Exists(path) ? new FileInfo(path).Length : 0));
        }
        // Unknown files are retained and charged, never swept merely because their record is missing.
        var files = Directory.EnumerateFiles(_directory).Take(1001).ToArray();
        if (files.Length > 1000) throw new BlobCapacityException();
        foreach (var file in files.Where(file => !known.Contains(file))) reserved = checked(reserved + new FileInfo(file).Length);
        if (size > _budget || reserved > _budget - size) throw new BlobCapacityException();
        var id = Guid.NewGuid().ToString("N");
        var receipt = new BackgroundDownloadReceipt(1, Account.StorageKey, id, hash, size, pinOwner);
        if (!await _store.TryWriteAsync([new(Key(id), null, JsonSerializer.Serialize(receipt, DownloadInboxJson.Default.BackgroundDownloadReceipt))], cancellationToken: ct).ConfigureAwait(false))
            throw new IOException("Download catalog changed outside its lock.");
        return id;
    }

    public async Task<BackgroundDownloadReceipt?> GetAsync(string id, CancellationToken ct = default) =>
        await _store.ReadAsync(Key(id), ct).ConfigureAwait(false) is { } record ? Decode(record) : null;

    public async Task<IReadOnlyList<BackgroundDownloadReceipt>> ListAsync(CancellationToken ct = default) =>
        (await Records(ct).ConfigureAwait(false)).Select(Decode).ToArray();

    public bool HasStagedFile(string id) => File.Exists(FilePath(id));

    /// <summary>
    /// Call synchronously before returning from the OS temporary-file callback. Moving bytes before any state update
    /// makes a process death recoverable by the deterministic spool name. Bytes remain untrusted until DrainAsync.
    /// </summary>
    public async Task AcceptAsync(string id, string temporaryPath, int httpStatus, CancellationToken ct = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false);
        var receipt = await GetAsync(id, ct).ConfigureAwait(false) ?? throw new InvalidDataException("Unknown OS download.");
        if (receipt.Imported) return; // Late/duplicate OS callback cannot replace completed content.
        if (httpStatus != 200 || new FileInfo(temporaryPath).Length != receipt.Size)
            throw new InvalidDataException("Download HTTP status or length does not match its receipt.");
        var destination = FilePath(id);
        if (File.Exists(destination)) return; // First delivery wins; verification occurs before import.
        File.Move(temporaryPath, destination);
    }

    /// <summary>
    /// Run on launch/foreground or within an OS-granted budget. Locked keys, wrong accounts, quota errors and interrupted
    /// imports retain the receipt and spool. Replays after import use the same durable pin owner. No UI is required.
    /// </summary>
    public async Task<int> DrainAsync(Func<string, CancellationToken, Task<IBackgroundDownloadScope?>> openAccount, CancellationToken ct = default)
    {
        var imported = 0;
        foreach (var candidate in await Records(ct).ConfigureAwait(false))
        {
            await using var held = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false);
            var current = await _store.ReadAsync(candidate.Key, ct).ConfigureAwait(false);
            if (current is null) continue;
            var receipt = Decode(current);
            var path = FilePath(receipt.Id);
            if (receipt.Imported) { File.Delete(path); continue; }
            if (!File.Exists(path)) continue; // Missing OS task is rescheduled by the host with a fresh ticket.
            await using var scope = await openAccount(receipt.AccountKey, ct).ConfigureAwait(false);
            if (scope is null) continue;
            if (scope.Account.StorageKey != receipt.AccountKey) throw new InvalidOperationException("Account resolver returned another account.");
            await using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length != receipt.Size || Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false)) != receipt.Sha256)
                    throw new InvalidDataException("Downloaded content failed verification; receipt and spool retained for explicit repair.");
                input.Position = 0;
                await scope.Cache.ImportPinnedAsync(input, receipt.PinOwner, ct).ConfigureAwait(false);
            }
            if (!await _store.TryWriteAsync([new(current.Key, current.Revision,
                JsonSerializer.Serialize(receipt with { Imported = true }, DownloadInboxJson.Default.BackgroundDownloadReceipt))], cancellationToken: ct).ConfigureAwait(false))
                throw new IOException("Download receipt changed outside its lock.");
            File.Delete(path);
            imported++;
        }
        return imported;
    }

    /// <summary>Only after the host cancels/joins this OS task. Does not release the application's content pin.</summary>
    public async Task ForgetAsync(string id, CancellationToken ct = default)
    {
        await using var held = await _locks.AcquireAsync("metadata", ct).ConfigureAwait(false);
        var record = await _store.ReadAsync(Key(id), ct).ConfigureAwait(false);
        if (record is null) return;
        _ = Decode(record);
        File.Delete(FilePath(id));
        if (!await _store.TryWriteAsync([new(record.Key, record.Revision, null)], cancellationToken: ct).ConfigureAwait(false))
            throw new IOException("Download receipt changed outside its lock.");
    }
}

[JsonSerializable(typeof(BackgroundDownloadReceipt))]
internal partial class DownloadInboxJson : JsonSerializerContext;
