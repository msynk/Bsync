using System.Security.Cryptography;
using Bsync.Samples.Tasks.Server;
using Bsync.Server.AspNetCore.Blobs;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class VersionedFileBridgeTests
{
    [Fact]
    public async Task LostCompletionReplyRecoversImmutableMappingAndAnotherUploaderMustProveBytes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bsync-versioned-file-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var staging = new FileSystemBlobStore(directory); var service = new Files();
            var bridge = new VersionedFileBlobStore(staging, metadata, service);
            var bytes = new byte[] { 1, 2, 3, 4 }; var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            var upload = FileSystemBlobStore.UploadKey("authorized-uploader", hash);
            Assert.Equal(2, await bridge.AppendAsync(upload, 0, new MemoryStream(bytes[..2]), bytes.Length));
            Assert.Equal(2, await bridge.GetReceivedAsync(upload));
            Assert.Equal(4, await bridge.AppendAsync(upload, 2, new MemoryStream(bytes[2..]), bytes.Length));
            await Assert.ThrowsAsync<IOException>(() => bridge.CompleteAsync(upload, hash));
            Assert.False(await bridge.ExistsAsync(hash));
            Assert.Equal(4, await bridge.GetReceivedAsync(upload));
            bridge = new(staging, metadata, service);
            Assert.True(await bridge.CompleteAsync(upload, hash));
            Assert.Equal(1, service.Puts);
            Assert.True(await bridge.ExistsAsync(hash));
            await using (var read = (await bridge.OpenReadAsync(hash))!)
                Assert.Equal(hash, Convert.ToHexStringLower(await SHA256.HashDataAsync(read)));
            var other = FileSystemBlobStore.UploadKey("another-uploader", hash);
            await bridge.AppendAsync(other, 0, new MemoryStream([9, 9, 9, 9]), 4);
            Assert.False(await bridge.CompleteAsync(other, hash));
            Assert.Equal(1, service.Puts);
            await bridge.DeleteAsync(hash);
            Assert.False(await bridge.ExistsAsync(hash));
            Assert.Equal(1, service.Deletes);
        }
        finally { Directory.Delete(directory, true); }
    }
    private sealed class Files : IVersionedFileService
    {
        private readonly Dictionary<string, ExternalFileVersion> _completed = [];
        private byte[] _bytes = [];
        public int Puts { get; private set; }
        public int Deletes { get; private set; }
        public Task<ExternalFileVersion?> FindCompletionAsync(string operationId, CancellationToken cancellationToken) => Task.FromResult(_completed.GetValueOrDefault(operationId));
        public async Task<ExternalFileVersion> PutAsync(string operationId, Stream verifiedContent, string sha256, long size, CancellationToken cancellationToken)
        {
            if (_completed.TryGetValue(operationId, out var existing)) return existing;
            Puts++; using var copy = new MemoryStream(); await verifiedContent.CopyToAsync(copy, cancellationToken); _bytes = copy.ToArray();
            var file = new ExternalFileVersion("logical-file", "immutable-1", sha256, size, "application/octet-stream");
            _completed.Add(operationId, file);
            throw new IOException("External completion succeeded; reply was lost.");
        }
        public Task<Stream?> OpenReadAsync(ExternalFileVersion version, CancellationToken cancellationToken) => Task.FromResult<Stream?>(new MemoryStream(_bytes));
        public Task DeleteAsync(ExternalFileVersion version, CancellationToken cancellationToken) { Deletes++; return Task.CompletedTask; }
    }
}
