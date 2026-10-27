using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Bsync.Blobs;
using Bsync.Samples.Tasks.Server;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests.SqlServer;

public sealed class S3MultipartTests
{
    [Fact]
    public async Task SweepRetainsVerifiedCompletionForDelayedReplay()
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-verified-sweep-").FullName;
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var sdk = DispatchProxy.Create<IAmazonS3, Service>(); var service = (Service)sdk; var grants = 0;
            var provider = new S3MultipartProtocol(sdk, "bucket", "", "scope", metadata, new FileBlobCacheLock(root), new("https://authority.test/"),
                (_, _, _) => { grants++; return Task.CompletedTask; });
            var id = Guid.NewGuid().ToString("N"); var hash = Convert.ToHexStringLower(SHA256.HashData(service.Bytes));
            await Assert.ThrowsAsync<IOException>(() => provider.StartAsync(id, hash, 4, default));
            await provider.StartAsync(id, hash, 4, default); await provider.CompleteAsync(id, hash, default);
            var record = Assert.Single(await metadata.ReadPageAsync("s3-multipart/", null, 10));
            var state = System.Text.Json.Nodes.JsonNode.Parse(record.Payload)!;
            state["LastActivityUtc"] = DateTimeOffset.UtcNow.AddDays(-2);
            Assert.True(await metadata.TryWriteAsync([new(record.Key, record.Revision, state.ToJsonString())]));
            var swept = await provider.SweepAsync(DateTimeOffset.UtcNow.AddHours(-1));
            Assert.Equal(0, swept.Aborted); Assert.Equal(1, swept.Retained);
            Assert.True((await provider.StartAsync(id, hash, 4, default))!.VerifiedComplete);
            Assert.Equal(2, grants); Assert.Equal(1, service.Completions); Assert.Equal(1, service.Publications);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SweepRecoversLostInitiationOrphanRetainsUnknownStateAndRetriesFailedCleanup()
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-sweep-").FullName;
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var sdk = DispatchProxy.Create<IAmazonS3, Service>(); var service = (Service)sdk;
            var provider = new S3MultipartProtocol(sdk, "bucket", "", "scope", metadata, new FileBlobCacheLock(root), new("https://authority.test/"), (_, _, _) => Task.CompletedTask);
            var id = Guid.NewGuid().ToString("N"); var hash = new string('a', 64);
            await Assert.ThrowsAsync<IOException>(() => provider.StartAsync(id, hash, 4, default));
            Assert.Equal(1, (await provider.SweepAsync(DateTimeOffset.UtcNow.AddHours(-1))).Retained);
            var record = Assert.Single(await metadata.ReadPageAsync("s3-multipart/", null, 10));
            var state = System.Text.Json.Nodes.JsonNode.Parse(record.Payload)!;
            state["LastActivityUtc"] = DateTimeOffset.UtcNow.AddDays(-2);
            var unknownKey = record.Key[..^32] + Guid.NewGuid().ToString("N");
            Assert.True(await metadata.TryWriteAsync([new(record.Key, record.Revision, state.ToJsonString()),new(unknownKey,null,"{\"Version\":99}")]));
            service.AbortFailures = 1;
            var failed = await provider.SweepAsync(DateTimeOffset.UtcNow.AddHours(-1));
            Assert.Equal(1, failed.Failed); Assert.Equal(1, failed.Retained);
            Assert.Contains("\"Aborted\":true", (await metadata.ReadAsync(record.Key))!.Payload);
            var recovered = await provider.SweepAsync(DateTimeOffset.UtcNow.AddHours(-1));
            Assert.Equal(1, recovered.Aborted); Assert.Equal(2, service.Aborts);
            Assert.Equal("{\"Version\":99}", (await metadata.ReadAsync(unknownKey))!.Payload);
            Assert.Equal(0, (await provider.SweepAsync(DateTimeOffset.UtcNow.AddHours(-1))).Aborted);
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.StartAsync(id, hash, 4, default));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task HashMismatchNeverPublishesOrGrantsAccess()
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-multipart-").FullName;
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var sdk = DispatchProxy.Create<IAmazonS3, Service>(); var service = (Service)sdk; var grants = 0;
            var provider = new S3MultipartProtocol(sdk, "bucket", "", "scope", metadata, new FileBlobCacheLock(root), new("https://authority.test/"), (_, _, _) => { grants++; return Task.CompletedTask; });
            var id = Guid.NewGuid().ToString("N"); var wrongHash = new string('a', 64);
            await Assert.ThrowsAsync<IOException>(() => provider.StartAsync(id, wrongHash, 4, default));
            await provider.StartAsync(id, wrongHash, 4, default);
            await Assert.ThrowsAsync<InvalidDataException>(() => provider.CompleteAsync(id, wrongHash, default));
            Assert.Equal(0, grants); Assert.Null(service.Published); Assert.True(service.Staged);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task LostInitiationAndScopedGrantRecoverWithoutDuplicateUploadOrHashOnlyAccess()
    {
        var root = Directory.CreateTempSubdirectory("bsync-s3-multipart-").FullName;
        try
        {
            await using var metadata = new InMemoryOrchestrationStore();
            var sdk = DispatchProxy.Create<IAmazonS3, Service>(); var service = (Service)sdk;
            var hash = Convert.ToHexStringLower(SHA256.HashData(service.Bytes));
            var id = Guid.NewGuid().ToString("N"); int grants = 0;
            S3MultipartProtocol Provider(string scope) => new(sdk, "bucket", "", scope, metadata, new FileBlobCacheLock(root), new("https://authority.test/multipart/"), (_, _, _) =>
            { grants++; if (grants == 1) throw new IOException("Grant transaction failed."); return Task.CompletedTask; });
            await Assert.ThrowsAsync<IOException>(() => Provider("alice-generation").StartAsync(id, hash, 4, default));
            var session = await Provider("alice-generation").StartAsync(id, hash, 4, default);
            Assert.Equal(id, session!.SessionId); Assert.Equal(1, service.Initiations);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => Provider("bob-generation").CompleteAsync(id, hash, default));
            await Assert.ThrowsAsync<InvalidDataException>(() => Provider("alice-generation").StartAsync(id, new string('b', 64), 4, default));
            await Assert.ThrowsAsync<IOException>(() => Provider("alice-generation").CompleteAsync(id, hash, default));
            Assert.Equal(service.Bytes, service.Published);
            Assert.True((await Provider("alice-generation").StartAsync(id, hash, 4, default))!.VerifiedComplete);
            Assert.Equal(2, grants); Assert.Equal(1, service.Completions); Assert.Equal(1, service.Publications);
            Assert.False(service.Staged);
            await Provider("alice-generation").AbortAsync(id, hash, default);
            await Provider("alice-generation").AbortAsync(id, hash, default);
            await Assert.ThrowsAsync<InvalidDataException>(() => Provider("alice-generation").CompleteAsync(id, hash, default));
        }
        finally { Directory.Delete(root, true); }
    }

    public class Service : DispatchProxy
    {
        public byte[] Bytes = [1, 2, 3, 4];
        public byte[]? Published;
        public int Initiations, Completions, Publications;
        public int AbortFailures, Aborts;
        public bool Staged;
        private string? _key;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "ListMultipartUploadsAsync":
                    return Task.FromResult(new ListMultipartUploadsResponse { IsTruncated = false, MultipartUploads = _key is null ? [] : [new MultipartUpload { Key = _key, UploadId = "remote-id" }] });
                case "InitiateMultipartUploadAsync":
                    Initiations++; _key = ((InitiateMultipartUploadRequest)args![0]!).Key;
                    return Task.FromException<InitiateMultipartUploadResponse>(new IOException("Initiated; reply lost."));
                case "ListPartsAsync":
                    if (Completions > 0) return Task.FromException<ListPartsResponse>(new AmazonS3Exception("No such upload") { ErrorCode = "NoSuchUpload" });
                    return Task.FromResult(new ListPartsResponse { IsTruncated = false, Parts = [new PartDetail { PartNumber = 1, Size = 4, ETag = "opaque" }] });
                case "GetObjectMetadataAsync":
                    var available = args![1]!.ToString()!.StartsWith("objects/", StringComparison.Ordinal) ? Published is not null : Staged;
                    return available ? Task.FromResult(new GetObjectMetadataResponse { ContentLength = 4 }) : Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("Absent") { StatusCode = HttpStatusCode.NotFound });
                case "CompleteMultipartUploadAsync": Completions++; Staged = true; return Task.FromResult(new CompleteMultipartUploadResponse());
                case "GetObjectAsync": return Task.FromResult(new GetObjectResponse { ContentLength = 4, ResponseStream = new MemoryStream(Bytes, false) });
                case "PutObjectAsync":
                    Publications++;
                    var content = new MemoryStream(); ((PutObjectRequest)args![0]!).InputStream.CopyTo(content); Published = content.ToArray();
                    return Task.FromResult(new PutObjectResponse());
                case "DeleteObjectAsync": Staged = false; return Task.FromResult(new DeleteObjectResponse());
                case "AbortMultipartUploadAsync":
                    Aborts++;
                    if (AbortFailures-- > 0) return Task.FromException<AbortMultipartUploadResponse>(new IOException("Temporary cleanup failure."));
                    return Task.FromException<AbortMultipartUploadResponse>(new AmazonS3Exception("Gone") { ErrorCode = "NoSuchUpload" });
                case "Dispose": return null;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
