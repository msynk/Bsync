using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Documents;
using Bsync.Server;
using Bsync.Storage;
using Bsync.Testing;
using Xunit;

namespace Bsync.PackageConsumer.Tests;

public sealed class PackagedOfflineTests
{
    [Fact]
    public async Task EncryptedManagedCacheReopensAndRotatesWithoutChangingContentIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("bsync-package-encryption-");
        try
        {
            var account = new AccountScope("consumer", "local", "alice");
            var metadata = new InMemoryOrchestrationStore();
            var locks = new FileBlobCacheLock(Path.Combine(directory.FullName, "locks"));
            var bytes = new FileBlobCache(Path.Combine(directory.FullName, "ciphertext"));
            var keys = new Keys();
            ManagedBlobCache Open(int version) => new(new EncryptedBlobCache(bytes, metadata, locks, keys,
                new(account, "content", version), new PlatformBlobEncryption()), metadata, locks, 1024, 2048);
            var content = Encoding.UTF8.GetBytes("Package-only encrypted offline content");
            var original = await Open(1).ImportPinnedAsync(new MemoryStream(content), "pending-work");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), original.Sha256);
            await Open(2).RotateEncryptionAsync(original.Sha256);
            keys.MinimumVersion = 2;
            await using var stream = await Open(2).OpenReadAsync(original.Sha256);
            Assert.NotNull(stream);
            using var output = new MemoryStream();
            await stream.CopyToAsync(output);
            Assert.Equal(content, output.ToArray());
            Assert.DoesNotContain(original.Sha256, await bytes.ListAsync());
        }
        finally { directory.Delete(true); }
    }

    [Fact]
    public async Task DirectReadsEnforceMembershipBeforeTakingAPage()
    {
        var server = new InMemorySyncServer<ConformanceDocument>(new InMemorySyncServerOptions<ConformanceDocument>()
        {
            Cloner = DocumentCloner.Json(ConformanceJsonContext.Default.ConformanceDocument),
            Fingerprint = document => JsonSerializer.Serialize(document, ConformanceJsonContext.Default.ConformanceDocument),
            Readers = document => [document.Title],
            PrincipalKey = caller => caller.Principal.FindFirst(ClaimTypes.Name)?.Value,
        });
        var alice = new SyncCallContext(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "test")), "team");
        await server.UpsertAsync("team", new() { Id = "a-private", Title = "bob" });
        await server.UpsertAsync("team", new() { Id = "b-shared", Title = "alice" });
        Assert.Null(await server.GetAsync(alice, "a-private"));
        Assert.Equal("b-shared", Assert.Single(await server.ListAsync(alice, 1)).Document.Id);
        await server.UpsertAsync("team", new() { Id = "b-shared", Title = "bob" });
        Assert.Null(await server.GetAsync(alice, "b-shared"));
    }

    private sealed class Keys : ILocalKeyProvider
    {
        public int MinimumVersion { get; set; } = 1;
        public ValueTask<LocalKeyAccess> AcquireAsync(LocalKeyReference reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(reference.Version < MinimumVersion ? LocalKeyAccess.Unavailable(reference) :
                LocalKeyAccess.Available(reference, Enumerable.Repeat((byte)reference.Version, 32).ToArray()));
    }
}
