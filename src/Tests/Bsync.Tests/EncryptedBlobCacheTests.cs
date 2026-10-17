using System.Security.Cryptography;
using System.Text;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Bsync.Storage.Sqlite;
using Xunit;

namespace Bsync.Tests;

public sealed class EncryptedBlobCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bsync-encrypted-blobs-" + Guid.NewGuid().ToString("N"));
    private readonly AccountScope _account = new("tests", "local", "account");
    private readonly Keys _keys = new();
    private readonly InMemoryOrchestrationStore _metadata = new();
    private FileBlobCache Backing => new(Path.Combine(_root, "ciphertext"));
    private EncryptedBlobCache Cache(IOrchestrationStore? metadata = null, int version = 1, BlobEncryption? encryption = null, int maximum = 16 * 1024 * 1024) =>
        new(Backing, metadata ?? _metadata, new FileBlobCacheLock(Path.Combine(_root, "locks")), _keys, new(_account, "blobs", version), encryption ?? new PlatformBlobEncryption(), maximum);
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync()
    {
        if (!Directory.Exists(_root)) return;
        await SqliteStorePool.DeleteDatabaseAsync(Path.Combine(_root, "metadata.db"));
        Directory.Delete(_root, true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Content => Encoding.UTF8.GetBytes("Private offline attachment with a stable plaintext identity.");

    [Fact]
    public async Task PartialRestartCompleteAndSeekNeverPersistPlaintext()
    {
        var bytes = Content; var hash = Hash(bytes);
        var cache = Cache();
        await cache.AppendPartialAsync(hash, bytes.AsMemory(0, 13));
        cache = Cache();
        Assert.Equal(13, await cache.PartialLengthAsync(hash));
        Assert.Null(await cache.SizeAsync(hash));
        await cache.AppendPartialAsync(hash, bytes.AsMemory(13));
        Assert.True(await cache.CompletePartialAsync(hash));
        Assert.Equal(bytes.Length, await Cache().SizeAsync(hash));
        await using var stream = await cache.OpenReadAsync(hash);
        stream!.Seek(8, SeekOrigin.Begin);
        var range = new byte[12]; await stream.ReadExactlyAsync(range);
        Assert.Equal(bytes[8..20], range);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(_root, "ciphertext"), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain("Private offline", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        Assert.Equal(0, await cache.ReconcileAsync());
        Assert.Equal(bytes.Length + 28, await cache.GetStoredBytesAsync());
    }

    [Fact]
    public async Task WrongKeyLockedKeyAndTamperingNeverEraseOrReplaceCiphertext()
    {
        var cache = Cache(); var blob = await cache.ImportAsync(new MemoryStream(Content));
        var before = await _metadata.ReadAsync("encrypted-blob/v1/" + blob.Sha256);
        _keys.Wrong = true;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.OpenReadAsync(blob.Sha256));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.ImportAsync(new MemoryStream(Content)));
        Assert.Equal(before, await _metadata.ReadAsync(before!.Key));
        _keys.Wrong = false; _keys.Locked = true;
        await Assert.ThrowsAsync<ReplicaKeyUnavailableException>(() => cache.SizeAsync(blob.Sha256));
        _keys.Locked = false;
        var cipher = Assert.Single(await Backing.ListAsync());
        var path = Path.Combine(_root, "ciphertext", "objects", cipher[..2], cipher);
        var original = await File.ReadAllBytesAsync(path);
        var corrupt = original.ToArray(); corrupt[14] ^= 1; await File.WriteAllBytesAsync(path, corrupt);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.OpenReadAsync(blob.Sha256));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.RotateAsync(blob.Sha256));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.ReconcileAsync());
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, original[..^1]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.OpenReadAsync(blob.Sha256));
        Assert.Equal(before, await _metadata.ReadAsync(before.Key));
    }

    [Fact]
    public async Task AuthenticatedLengthModeIdentityAndAccountPreventMetadataSubstitution()
    {
        var cache = Cache(); var hash = Hash(Content);
        await cache.AppendPartialAsync(hash, Content);
        var record = (await _metadata.ReadAsync("encrypted-blob/v1/" + hash))!;
        var state = System.Text.Json.JsonSerializer.Deserialize(record.Payload, EncryptedBlobJson.Default.EncryptedBlobState)!;
        await _metadata.TryWriteAsync([new(record.Key, record.Revision, System.Text.Json.JsonSerializer.Serialize(state with { Complete = state.Partial, Partial = null }, EncryptedBlobJson.Default.EncryptedBlobState))]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => cache.OpenReadAsync(hash));
        var otherAccount = new EncryptedBlobCache(Backing, _metadata, new FileBlobCacheLock(Path.Combine(_root, "locks")), _keys, new(new("tests", "local", "other"), "blobs", 1), new PlatformBlobEncryption());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => otherAccount.OpenReadAsync(hash));
    }

    [Fact]
    public async Task RotationFailureKeepsBothOldReferencesThenRetryCommitsBoth()
    {
        var cache = Cache(); var blob = await cache.ImportAsync(new MemoryStream(Content));
        await cache.AppendPartialAsync(blob.Sha256, Content.AsMemory(0, 8));
        var record = await _metadata.ReadAsync("encrypted-blob/v1/" + blob.Sha256);
        var failed = Cache(version: 2, encryption: new FailSecondSeal());
        await Assert.ThrowsAsync<IOException>(() => failed.RotateAsync(blob.Sha256));
        Assert.Equal(record, await _metadata.ReadAsync(record!.Key));
        Assert.Equal(8, await cache.PartialLengthAsync(blob.Sha256));
        Assert.Equal(Content.Length, await cache.SizeAsync(blob.Sha256));
        Assert.Equal(1, await cache.ReconcileAsync());
        await Cache(version: 2).RotateAsync(blob.Sha256);
        _keys.MinimumVersion = 2;
        cache = Cache(version: 2);
        Assert.Equal(8, await cache.PartialLengthAsync(blob.Sha256));
        await using var stream = await cache.OpenReadAsync(blob.Sha256);
        Assert.Equal(Content, await ReadAll(stream!));
        Assert.Equal(0, await cache.ReconcileAsync());
    }

    [Fact]
    public async Task SqliteRestartPreservesEncryptedPendingUploadMigrationAndUnknownBackingObjects()
    {
        Directory.CreateDirectory(_root);
        var legacy = new FileBlobCache(Path.Combine(_root, "legacy"));
        var blob = await legacy.ImportAsync(new MemoryStream(Content));
        var unknown = await Backing.ImportAsync(new MemoryStream([1, 2, 3]));
        var database = Path.Combine(_root, "metadata.db");
        await using (var metadata = await SqliteOrchestrationStore.OpenAsync(database, _account))
        {
            var cache = Cache(metadata);
            await cache.MigrateAsync(legacy, blob.Sha256);
            await cache.MigrateAsync(legacy, blob.Sha256); // repeated after an uncertain response
        }
        await using (var metadata = await SqliteOrchestrationStore.OpenAsync(database, _account))
        {
            var cache = Cache(metadata);
            Assert.Equal(blob.Size, await cache.SizeAsync(blob.Sha256));
            Assert.Equal(blob.Size, await legacy.SizeAsync(blob.Sha256));
            Assert.Equal(0, await cache.ReconcileAsync());
            Assert.Equal(3, await Backing.SizeAsync(unknown.Sha256));
        }
    }

    [Fact]
    public async Task BoundsAndHashMismatchLeavePriorCompleteUsable()
    {
        var cache = Cache(maximum: 64); var blob = await cache.ImportAsync(new MemoryStream(Content));
        await Assert.ThrowsAsync<IOException>(() => cache.ImportAsync(new MemoryStream(new byte[65])));
        await cache.AppendPartialAsync(blob.Sha256, new byte[64]);
        await Assert.ThrowsAsync<IOException>(() => cache.AppendPartialAsync(blob.Sha256, new byte[1]));
        Assert.Equal(64, await cache.PartialLengthAsync(blob.Sha256));
        Assert.False(await cache.CompletePartialAsync(blob.Sha256));
        Assert.Equal(0, await cache.PartialLengthAsync(blob.Sha256));
        Assert.Equal(Content.Length, await cache.SizeAsync(blob.Sha256));
    }

    [Fact]
    public async Task ManagedBudgetCountsEncryptionAndReplacementBeforeWriting()
    {
        var encrypted = Cache(); var locks = new FileBlobCacheLock(Path.Combine(_root, "locks"));
        var managed = new ManagedBlobCache(encrypted, _metadata, locks, 35, 35);
        var blob = await managed.ImportPinnedAsync(new MemoryStream(new byte[4]), "pending/upload");
        Assert.Equal(32, await encrypted.GetStoredBytesAsync());
        await Assert.ThrowsAsync<BlobCapacityException>(() => managed.AppendPartialAsync(blob.Sha256, new byte[1]));
        await Assert.ThrowsAsync<BlobCapacityException>(() => managed.RotateEncryptionAsync(blob.Sha256));
        Assert.Equal(32, await encrypted.GetStoredBytesAsync());
        Assert.Equal(4, await managed.SizeAsync(blob.Sha256));
        Assert.Contains("pending/upload", (await managed.GetEntryAsync(blob.Sha256)).Owners);
        var roomy = new ManagedBlobCache(Cache(version: 2), _metadata, locks, 128, 128);
        await roomy.RotateEncryptionAsync(blob.Sha256);
        _keys.MinimumVersion = 2;
        Assert.Equal(4, await roomy.SizeAsync(blob.Sha256));
        Assert.Equal(32, await encrypted.GetStoredBytesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessDeathAtRotationCommitKeepsExactlyOneUsableGeneration(bool afterCommit)
    {
        Directory.CreateDirectory(_root);
        var database = Path.Combine(_root, "metadata.db");
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { Path.Combine(AppContext.BaseDirectory, "Bsync.Tests.CrashHost.dll"), "encrypted-rotation", database, afterCommit ? "after" : "before" }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
            if (line is null) Assert.Fail("Crash host exited: " + await process.StandardError.ReadToEndAsync(timeout.Token));
            Assert.Equal("rotation-boundary", line);
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync(timeout.Token);
            await using var metadata = await SqliteOrchestrationStore.OpenAsync(database, _account);
            var hash = Hash([1, 2, 3, 4]);
            var record = (await metadata.ReadAsync("encrypted-blob/v1/" + hash))!;
            var state = System.Text.Json.JsonSerializer.Deserialize(record.Payload, EncryptedBlobJson.Default.EncryptedBlobState)!;
            Assert.Equal(afterCommit ? 2 : 1, state.Complete!.KeyVersion);
            Assert.Equal(state.Complete.KeyVersion, state.Partial!.KeyVersion);
            var cache = Cache(metadata, version: 2);
            Assert.Equal(4, await cache.SizeAsync(hash, timeout.Token));
            Assert.Equal(2, await cache.PartialLengthAsync(hash, timeout.Token));
            Assert.Equal(2, await cache.ReconcileAsync(timeout.Token));
            await cache.RotateAsync(hash, timeout.Token);
            _keys.MinimumVersion = 2;
            Assert.Equal(4, await cache.SizeAsync(hash, timeout.Token));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task NativeAeadRejectsReorderedCiphertextAndFreshNonceChangesEveryEnvelope()
    {
        var encryption = new PlatformBlobEncryption(); var key = new byte[32]; var content = new byte[64];
        RandomNumberGenerator.Fill(content);
        var one = await encryption.SealAsync(key, content, new byte[] { 1 });
        var two = await encryption.SealAsync(key, content, new byte[] { 1 });
        Assert.NotEqual(one[..12], two[..12]);
        Assert.Equal(content, await encryption.OpenAsync(key, one, new byte[] { 1 }));
        (one[13], one[48]) = (one[48], one[13]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => encryption.OpenAsync(key, one, new byte[] { 1 }).AsTask());
        await Assert.ThrowsAnyAsync<CryptographicException>(() => encryption.OpenAsync(key, two, new byte[] { 2 }).AsTask());
    }

    private static async Task<byte[]> ReadAll(Stream stream) { using var output = new MemoryStream(); await stream.CopyToAsync(output); return output.ToArray(); }
    private sealed class Keys : ILocalKeyProvider
    {
        public bool Wrong, Locked; public int MinimumVersion = 1;
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Locked ? LocalKeyAccess.Locked(reference) : reference.Version < MinimumVersion ? LocalKeyAccess.Unavailable(reference) :
                LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)(Wrong ? 99 : reference.Version), 32).ToArray()));
    }
    private sealed class FailSecondSeal : BlobEncryption
    {
        private int _count; private readonly PlatformBlobEncryption _inner = new();
        public override ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
            ++_count == 2 ? throw new IOException("Interrupted rotation") : _inner.SealAsync(key, plaintext, associatedData, cancellationToken);
        public override ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) =>
            _inner.OpenAsync(key, envelope, associatedData, cancellationToken);
    }
}
