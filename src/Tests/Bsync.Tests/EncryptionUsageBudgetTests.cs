using Bsync.Blobs;
using Bsync.Storage;
using System.Security.Cryptography;
using Xunit;

namespace Bsync.Tests;

public sealed class EncryptionUsageBudgetTests
{
    [Fact]
    public async Task SqliteRestartDoesNotResetTheSameKeysUsageCeiling()
    {
        var root = Directory.CreateTempSubdirectory("bsync-key-budget-").FullName;
        var path = Path.Combine(root, "budget.db"); var key = RandomNumberGenerator.GetBytes(32);
        try
        {
            await using (var first = await Bsync.Storage.Sqlite.SqliteOrchestrationStore.OpenAsync(path, new("app", "test", "alice")))
                await new BudgetedBlobEncryption(new PlatformBlobEncryption(), new OrchestrationEncryptionUsageBudget(first, 1)).SealAsync(key, new byte[] { 1 }, default);
            await using (var reopened = await Bsync.Storage.Sqlite.SqliteOrchestrationStore.OpenAsync(path, new("app", "test", "alice")))
                await Assert.ThrowsAsync<CryptographicException>(() => new BudgetedBlobEncryption(new PlatformBlobEncryption(), new OrchestrationEncryptionUsageBudget(reopened, 100)).SealAsync(key, new byte[] { 2 }, default).AsTask());
        }
        finally { await Bsync.Storage.Sqlite.SqliteStorePool.DeleteDatabaseAsync(path); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SameKeyAcrossInstancesAndPurposesSharesDurableCeiling()
    {
        await using var store = new InMemoryOrchestrationStore();
        var key = RandomNumberGenerator.GetBytes(32);
        var first = new BudgetedBlobEncryption(new PlatformBlobEncryption(), new OrchestrationEncryptionUsageBudget(store, 2));
        var envelope = await first.SealAsync(key, new byte[] { 1 }, "chunk"u8.ToArray());
        var reopened = new BudgetedBlobEncryption(new PlatformBlobEncryption(), new OrchestrationEncryptionUsageBudget(store, 100));
        await reopened.SealAsync(key.ToArray(), new byte[] { 2 }, "metadata"u8.ToArray());
        await Assert.ThrowsAsync<CryptographicException>(() => reopened.SealAsync(key, new byte[] { 3 }, default).AsTask());
        Assert.Equal(new byte[] { 1 }, await reopened.OpenAsync(key, envelope, "chunk"u8.ToArray()));
        await reopened.SealAsync(RandomNumberGenerator.GetBytes(32), new byte[] { 3 }, default);
    }

    [Fact]
    public async Task ConcurrentAdmissionCannotOverspendAndFailedSealIsCharged()
    {
        await using var store = new InMemoryOrchestrationStore();
        var encryption = new BudgetedBlobEncryption(new FailingEncryption(), new OrchestrationEncryptionUsageBudget(store, 3));
        var key = RandomNumberGenerator.GetBytes(32);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(async _ =>
        {
            try { await encryption.SealAsync(key, default, default); return false; }
            catch (IOException) { return true; }
            catch (CryptographicException) { return false; }
        }));
        Assert.Equal(3, results.Count(x => x));
        await Assert.ThrowsAsync<CryptographicException>(() => encryption.SealAsync(key, default, default).AsTask());
    }

    [Fact]
    public async Task UnknownLedgerVersionIsRetainedAndRefusesAdmission()
    {
        await using var store = new InMemoryOrchestrationStore();
        var fingerprint = new string('a', 64);
        var key = "encryption-usage/v1/" + fingerprint;
        await store.TryWriteAsync([new(key, null, "{\"Version\":2,\"Used\":0,\"Limit\":10}")]);
        var before = await store.ReadAsync(key);
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => new OrchestrationEncryptionUsageBudget(store, 10).ReserveAsync(fingerprint));
        Assert.Equal(before, await store.ReadAsync(key));
    }

    private sealed class FailingEncryption : BlobEncryption
    {
        public override ValueTask<byte[]> SealAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> plaintext, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) => throw new IOException("Injected failure after admission.");
        public override ValueTask<byte[]> OpenAsync(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> envelope, ReadOnlyMemory<byte> associatedData, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
