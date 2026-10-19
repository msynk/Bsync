using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Bsync.Samples.Tasks.Server;
using Bsync.Server.AspNetCore.Blobs;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class S3CompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletionPublishesVerifiedSnapshotEvenIfPresignedKeyIsReplaced(bool failPublication)
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-snapshot-").FullName;
        try
        {
            var s3 = DispatchProxy.Create<IAmazonS3, MutableS3>();
            var fake = (MutableS3)s3;
            fake.FailPublication = failPublication;
            var original = fake.Staged.ToArray();
            var hash = Convert.ToHexStringLower(SHA256.HashData(original));
            var store = new S3BlobStore(s3, "test-bucket", new FileSystemBlobStore(root)) { PresignUploads = true };
            if (failPublication)
            {
                await Assert.ThrowsAsync<IOException>(() => store.CompleteAsync("upload", hash));
                Assert.False(fake.Deleted);
            }
            else
            {
                Assert.True(await store.CompleteAsync("upload", hash));
                Assert.Equal(original, fake.Published);
                Assert.NotEqual(original, fake.Staged);
                Assert.True(fake.Deleted);
            }
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
            Assert.Equal(0, fake.Copies);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(2, 4)] // announced size exceeds the disk ceiling
    [InlineData(10, 2)] // streamed bytes exceed the announced size
    [InlineData(10, 5)] // truncated stream
    public async Task InvalidSnapshotBoundsRetainRemoteStagingAndRemoveLocalPlaintext(long maximum, long announced)
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-bounds-").FullName;
        try
        {
            var s3 = DispatchProxy.Create<IAmazonS3, MutableS3>();
            var fake = (MutableS3)s3; fake.AnnouncedLength = announced;
            var store = new S3BlobStore(s3, "test-bucket", new FileSystemBlobStore(root)) { PresignUploads = true, MaximumStagedBytes = maximum };
            await Assert.ThrowsAsync<InvalidDataException>(() => store.CompleteAsync("upload", Convert.ToHexStringLower(SHA256.HashData(fake.Staged))));
            Assert.Null(fake.Published); Assert.False(fake.Deleted);
            Assert.Empty(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, true); }
    }

    public class MutableS3 : DispatchProxy
    {
        public byte[] Staged = [1, 2, 3, 4];
        public byte[]? Published;
        public long AnnouncedLength = 4;
        public bool Deleted, FailPublication;
        public int Copies;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "GetObjectMetadataAsync":
                    var key = (string)args![1]!;
                    if (key.StartsWith("uploads/", StringComparison.Ordinal))
                        return Task.FromResult(new GetObjectMetadataResponse { ContentLength = Staged.Length });
                    // Replace the untrusted staging key exactly between hashing and publication.
                    Staged = [9, 8, 7, 6];
                    return Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound });
                case "GetObjectAsync":
                    return Task.FromResult(new GetObjectResponse { ContentLength = AnnouncedLength, ResponseStream = new MemoryStream(Staged.ToArray()) });
                case "PutObjectAsync":
                    Assert.False(((PutObjectRequest)args![0]!).UseChunkEncoding);
                    if (FailPublication) return Task.FromException<PutObjectResponse>(new IOException("Injected publication interruption."));
                    using (var bytes = new MemoryStream()) { ((PutObjectRequest)args![0]!).InputStream.CopyTo(bytes); Published = bytes.ToArray(); }
                    Assert.Equal(Convert.ToBase64String(SHA256.HashData(Published)), ((PutObjectRequest)args![0]!).ChecksumSHA256);
                    return Task.FromResult(new PutObjectResponse());
                case "CopyObjectAsync":
                    Copies++; Published = Staged.ToArray(); return Task.FromResult(new CopyObjectResponse());
                case "DeleteObjectAsync":
                    Deleted = true; return Task.FromResult(new DeleteObjectResponse());
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
