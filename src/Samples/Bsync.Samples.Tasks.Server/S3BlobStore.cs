using System.Net;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Blob storage in an S3-compatible bucket (task F1). Uploads in progress stay on the server's disk, because S3
/// multipart parts must be at least 5 MB and clients send smaller chunks; a verified upload is written to the bucket
/// once (deduplicated by hash) and its partial file removed. Reads can be served by a short-lived presigned URL, so the
/// bytes do not pass through the application server.
/// </summary>
public sealed class S3BlobStore(IAmazonS3 s3, string bucket, FileSystemBlobStore partials, bool useHttp = false) : IBlobStore
{
    private static string Key(string sha256) => $"objects/{sha256[..2]}/{sha256}";

    public async Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default)
    {
        try
        {
            await s3.GetObjectMetadataAsync(bucket, Key(sha256), cancellationToken);
            return true;
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await s3.GetObjectAsync(bucket, Key(sha256), cancellationToken);
            return new OwnedStream(response.ResponseStream, response);
        }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default) => partials.GetReceivedAsync(upload, cancellationToken);

    public Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default) =>
        partials.AppendAsync(upload, offset, content, limit, cancellationToken);

    public async Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default)
    {
        var path = partials.UploadFile(upload);
        bool matches;
        await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
        {
            matches = string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken)), sha256, StringComparison.Ordinal);
        }

        if (!matches)
        {
            File.Delete(path);
            return false;
        }

        if (!await ExistsAsync(sha256, cancellationToken))
        {
            await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = Key(sha256), FilePath = path, ContentType = "application/octet-stream" }, cancellationToken);
        }

        File.Delete(path);
        return true;
    }

    public Task<Uri?> PresignReadAsync(string sha256, TimeSpan validity, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(new Uri(s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = Key(sha256),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validity),
            Protocol = useHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS,
        })));

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
