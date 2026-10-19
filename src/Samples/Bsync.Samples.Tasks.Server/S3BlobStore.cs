using System.Net;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using Bsync.Server.AspNetCore.Blobs;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// An <see cref="IBlobStore"/> in an S3-compatible bucket (task F1; a recipe, see docs/patterns/attachments.md: copy it
/// into the application, which then owns the AWS SDK dependency). Chunked uploads in progress stay on the server's
/// disk (<paramref name="partials"/>), because S3 multipart parts must be at least 5 MB and clients send smaller chunks; a
/// verified upload is written to the bucket once (deduplicated by hash) and its partial file removed. With
/// <see cref="PresignUploads"/>, clients instead send the whole content to a presigned URL under
/// <c>{prefix}uploads/</c>, which finishing reads into a private disk snapshot, verifies and uploads to its object. Downloads are
/// served by presigned URLs, so the bytes do not pass through the application server. Objects are stored under
/// <c>{prefix}objects/ab/&lt;sha256&gt;</c>.
/// </summary>
/// <param name="s3">The S3 client (for S3-compatible servers set <c>ServiceURL</c> and <c>ForcePathStyle</c>).</param>
/// <param name="bucket">The bucket.</param>
/// <param name="partials">Where partial uploads are kept.</param>
/// <param name="prefix">A key prefix, for sharing a bucket. Default none.</param>
public sealed class S3BlobStore(IAmazonS3 s3, string bucket, FileSystemBlobStore partials, string prefix = "") : IBlobStore
{
    private string Key(string sha256) => $"{prefix}objects/{sha256[..2]}/{sha256}";

    private string UploadKey(string upload) => $"{prefix}uploads/{upload}";

    /// <summary>
    /// Whether clients upload straight to the bucket by presigned URLs (<see cref="IBlobStore.PresignWriteAsync"/>).
    /// Browser clients need a CORS rule on the bucket allowing <c>PUT</c> from the app's origin. Default
    /// <see langword="false"/>.
    /// </summary>
    public bool PresignUploads { get; init; }

    /// <summary>Whether presigned URLs use <c>http</c> (local S3-compatible servers). Default <see langword="false"/>.</summary>
    public bool PresignHttp { get; init; }

    /// <summary>Per-completion plaintext snapshot disk ceiling; hosts must also bound concurrent completions. Default 512 MiB.</summary>
    public long MaximumStagedBytes { get; init; } = 512L * 1024 * 1024;

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, Key(sha256), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, Key(sha256), cancellationToken).ConfigureAwait(false);
            return new OwnedStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default)
    {
        var local = await partials.GetReceivedAsync(upload, cancellationToken).ConfigureAwait(false);
        return local > 0 || !PresignUploads ? local : await StagedLengthAsync(upload, cancellationToken).ConfigureAwait(false) ?? 0;
    }

    /// <inheritdoc />
    public Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default) =>
        partials.AppendAsync(upload, offset, content, limit, cancellationToken);

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default)
    {
        var path = partials.UploadFile(upload);
        if (!File.Exists(path) && PresignUploads && await StagedLengthAsync(upload, cancellationToken).ConfigureAwait(false) is not null)
        {
            return await CompleteStagedAsync(upload, sha256, cancellationToken).ConfigureAwait(false);
        }

        if (!await FileSystemBlobStore.VerifyAsync(path, sha256, cancellationToken).ConfigureAwait(false))
        {
            File.Delete(path);
            return false;
        }

        if (!await ExistsAsync(sha256, cancellationToken).ConfigureAwait(false))
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key(sha256), FilePath = path,
                ContentType = "application/octet-stream", UseChunkEncoding = false,
                ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)) }, cancellationToken).ConfigureAwait(false);
        }

        File.Delete(path);
        return true;
    }

    /// <inheritdoc />
    public async Task<Uri?> PresignWriteAsync(string upload, long size, TimeSpan validity, CancellationToken cancellationToken = default) =>
        PresignUploads
            ? new(await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
            {
                BucketName = bucket,
                Key = UploadKey(upload),
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.Add(validity),
                Protocol = PresignHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
            }).ConfigureAwait(false))
            : null;

    /// <inheritdoc />
    public async Task<Uri?> PresignReadAsync(string sha256, TimeSpan validity, CancellationToken cancellationToken = default) =>
        new(await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = Key(sha256),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validity),
            Protocol = PresignHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
        }).ConfigureAwait(false));

    /// <inheritdoc />
    public async IAsyncEnumerable<(string Sha256, DateTimeOffset Stored)> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = $"{prefix}objects/" };
        ListObjectsV2Response response;
        do
        {
            response = await s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                yield return (item.Key[(item.Key.LastIndexOf('/') + 1)..], new DateTimeOffset(item.LastModified ?? DateTime.UtcNow, TimeSpan.Zero));
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string sha256, CancellationToken cancellationToken = default) =>
        s3.DeleteObjectAsync(bucket, Key(sha256), cancellationToken);

    /// <inheritdoc />
    public async Task<int> PurgePartialsAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var removed = await partials.PurgePartialsAsync(before, cancellationToken).ConfigureAwait(false);
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = $"{prefix}uploads/" };
        ListObjectsV2Response response;
        do
        {
            response = await s3.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                if (item.LastModified is { } modified && new DateTimeOffset(modified.ToUniversalTime(), TimeSpan.Zero) < before)
                {
                    await s3.DeleteObjectAsync(bucket, item.Key, cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return removed;
    }

    /// <summary>The length of a presigned upload stored in the bucket, or <see langword="null"/>.</summary>
    private async Task<long?> StagedLengthAsync(string upload, CancellationToken cancellationToken)
    {
        try
        {
            return (await s3.GetObjectMetadataAsync(bucket, UploadKey(upload), cancellationToken).ConfigureAwait(false)).ContentLength;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Publishes the exact verified snapshot, never a later copy of the mutable presigned-upload key.</summary>
    private async Task<bool> CompleteStagedAsync(string upload, string sha256, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumStagedBytes, 1);
        // A still-valid PUT ticket can replace the staging object after verification. A server-side copy by key
        // would then publish bytes that were never hashed. Keep a private, exclusively opened snapshot instead.
        var snapshot = partials.UploadFile(upload) + ".verify-" + Guid.NewGuid().ToString("N");
        var created = false;
        try
        {
            await using var file = new FileStream(snapshot, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            created = true;
            using (var response = await s3.GetObjectAsync(bucket, UploadKey(upload), cancellationToken).ConfigureAwait(false))
            {
                if (response.ContentLength < 0 || response.ContentLength > MaximumStagedBytes)
                    throw new InvalidDataException("Staged object exceeds the completion snapshot limit.");
                var buffer = new byte[81920];
                long copied = 0;
                while (true)
                {
                    var count = await response.ResponseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (count == 0) break;
                    copied = checked(copied + count);
                    if (copied > MaximumStagedBytes || copied > response.ContentLength)
                        throw new InvalidDataException("Staged object exceeds its declared completion bounds.");
                    await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                }
                if (copied != response.ContentLength) throw new InvalidDataException("Staged object ended before its declared length.");
            }
            file.Position = 0;
            var actual = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
            var verified = string.Equals(actual, sha256, StringComparison.Ordinal);
            if (verified && !await ExistsAsync(sha256, cancellationToken).ConfigureAwait(false))
            {
                file.Position = 0;
                await s3.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = bucket, Key = Key(sha256), InputStream = file,
                    AutoCloseStream = false, ContentType = "application/octet-stream",
                    // The snapshot already has a verified digest and known length. Send its checksum in a header,
                    // avoiding aws-chunked encoding leaking into stored metadata on compatible services.
                    UseChunkEncoding = false, ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(actual))
                }, cancellationToken).ConfigureAwait(false);
            }
            await s3.DeleteObjectAsync(bucket, UploadKey(upload), cancellationToken).ConfigureAwait(false);
            return verified;
        }
        finally { if (created) File.Delete(snapshot); }
    }

    /// <summary>The object's stream, disposing the S3 response with it.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
