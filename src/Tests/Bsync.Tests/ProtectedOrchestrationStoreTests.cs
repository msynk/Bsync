using System.Security.Cryptography;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class ProtectedOrchestrationStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "bsync-protected-store-" + Guid.NewGuid().ToString("N") + ".db");
    public void Dispose() { Bsync.Storage.Sqlite.SqliteStorePool.Release(_path); if (File.Exists(_path)) File.Delete(_path); }
    private static readonly AccountScope Account = new("tests", "local", "protected");
    private sealed class Keys : ILocalKeyProvider
    {
        public bool Locked { get; set; }
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Locked ? LocalKeyAccess.Locked(reference) : LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)reference.Version, 32).ToArray()));
    }
    private static ProtectedOrchestrationStore Wrap(IOrchestrationStore store, Keys keys, int version = 1, AccountScope? account = null) =>
        new(store, new(account ?? Account, "metadata", version), keys, new PlatformBlobEncryption(), key => key.StartsWith("data/", StringComparison.Ordinal) ? "workflow" : null, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CiphertextPreservesAtomicCasAndFencesWithoutPublishingSecrets(bool sqlite)
    {
        await using IOrchestrationStore raw = sqlite ? await Bsync.Storage.Sqlite.SqliteOrchestrationStore.OpenAsync(_path, Account) : new InMemoryOrchestrationStore();
        await using var store = Wrap(raw, new());
        var expiration = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds();
        Assert.True(await store.TryWriteAsync([new("owner", null, "token", expiration), new("data/a", null, "private-a")]));
        var a = (await store.ReadAsync("data/a"))!;
        Assert.DoesNotContain("private-a", (await raw.ReadAsync("data/a"))!.Payload);
        Assert.False(await store.TryWriteAsync([new("data/a", a.Revision, "changed"), new("data/b", null, "private-b")], new("owner", "stale")));
        Assert.Null(await raw.ReadAsync("data/b"));
        Assert.Equal(a, await store.ReadAsync("data/a"));
        Assert.True(await store.TryWriteAsync([new("data/a", a.Revision, "changed"), new("data/b", null, "private-b")], new("owner", "token")));
        Assert.Equal(new[] { "changed", "private-b" }, (await store.ReadPageAsync("data/", null, 10)).Select(r => r.Payload));
        Assert.False(await store.TryWriteAsync([new("data/a", a.Revision, "stale")], new("owner", "token")));
    }

    [Fact]
    public async Task RegisteredPolicyRejectsUnknownPayloadsBeforeAnyBatchMutation()
    {
        var policy = new OrchestrationPayloadPolicy().Protect("work/", "workflow").Control("work/owner");
        Assert.Throws<ArgumentException>(() => policy.Protect("work/nested/", "other"));
        var selector = policy.CreateSelector();
        Assert.Throws<InvalidOperationException>(() => policy.Control("later"));
        await using var raw = new InMemoryOrchestrationStore();
        await using var store = new ProtectedOrchestrationStore(raw, new(Account, "metadata", 1), new Keys(), new PlatformBlobEncryption(), selector, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TryWriteAsync([new("work/owner", null, "lease"), new("unregistered", null, "secret")]));
        Assert.Empty(await raw.ReadPageAsync("", null, 10));
        Assert.True(await store.TryWriteAsync([new("work/owner", null, "lease"), new("work/payload", null, "secret")]));
        Assert.Equal("lease", (await raw.ReadAsync("work/owner"))!.Payload);
        Assert.DoesNotContain("secret", (await raw.ReadAsync("work/payload"))!.Payload);
    }

    [Fact]
    public async Task SubstitutionAccountMismatchAndExpirationTamperingAreRejected()
    {
        await using var raw = new InMemoryOrchestrationStore();
        await using var store = Wrap(raw, new());
        await store.TryWriteAsync([new("data/a", null, "secret")]);
        var encrypted = (await raw.ReadAsync("data/a"))!;
        await raw.TryWriteAsync([new("data/b", null, encrypted.Payload)]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAsync("data/b"));
        await using var foreign = Wrap(raw, new(), account: new("tests", "local", "other"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => foreign.ReadAsync("data/a"));
        await raw.TryWriteAsync([new("data/a", encrypted.Revision, encrypted.Payload, 123)]);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => store.ReadAsync("data/a"));
    }

    [Fact]
    public async Task LockedKeyAndExpansionFailureLeaveWholeBatchUnchanged()
    {
        await using var raw = new InMemoryOrchestrationStore();
        var keys = new Keys { Locked = true };
        await using var store = Wrap(raw, keys);
        await Assert.ThrowsAsync<ReplicaKeyUnavailableException>(() => store.TryWriteAsync([new("owner", null, "token"), new("data/a", null, "secret")]));
        Assert.Empty(await raw.ReadPageAsync("", null, 10));
        keys.Locked = false;
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryWriteAsync([new("owner", null, "token"), new("data/a", null, new string('\u4e00', 200000))]));
        Assert.Empty(await raw.ReadPageAsync("", null, 10));
    }

    [Fact]
    public async Task ExplicitMigrationAndRotationRetainReadableValuesAndStrictDowngradeBoundary()
    {
        await using var raw = new InMemoryOrchestrationStore();
        await raw.TryWriteAsync([new("data/a", null, "legacy"), new("data/b", null, "second"), new("owner", null, "token")]);
        await using var store = Wrap(raw, new());
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => store.ReadAsync("data/a"));
        var continuation = await store.ProtectPageAsync("data/", null, 1);
        Assert.NotNull(continuation);
        Assert.Equal("legacy", (await store.ReadAsync("data/a"))!.Payload);
        await store.ProtectPageAsync("data/", continuation, 10);
        var before = (await raw.ReadAsync("data/a"))!.Payload;
        await using var rotated = Wrap(raw, new(), 2);
        await rotated.ProtectPageAsync("data/", null);
        Assert.NotEqual(before, (await raw.ReadAsync("data/a"))!.Payload);
        Assert.Equal("legacy", (await rotated.ReadAsync("data/a"))!.Payload);
        Assert.Equal("token", (await raw.ReadAsync("owner"))!.Payload);
        // Explicit marker versions are refused without any provider mutation.
        var record = (await raw.ReadAsync("data/a"))!;
        await raw.TryWriteAsync([new(record.Key, record.Revision, record.Payload.Replace("protected/1:", "protected/99:", StringComparison.Ordinal))]);
        await Assert.ThrowsAsync<LocalStoreUnavailableException>(() => rotated.ReadAsync("data/a"));
    }
}
