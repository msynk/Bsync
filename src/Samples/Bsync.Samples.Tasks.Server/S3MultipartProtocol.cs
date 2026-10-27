using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using Bsync.Blobs;
using Bsync.Storage;

namespace Bsync.Samples.Tasks.Server;

/// <summary>
/// Experimental S3 multipart authority recipe. Construct only after authenticating and authorizing the current account.
/// Metadata and locks must be durable, shared by every server and coordinated with account revocation. ScopeGeneration
/// is a server-owned random account generation, never a request parameter. Configure bucket lifecycle abort rules for
/// incomplete multipart uploads; retain metadata tombstones. SDK dependencies remain outside the portable library.
/// </summary>
public sealed class S3MultipartProtocol(IAmazonS3 s3, string bucket, string prefix, string scopeGeneration,
    IOrchestrationStore metadata, IBlobCacheLock locks, Uri endpoint,
    Func<string, long, CancellationToken, Task> grantVerifiedAccess) : IMultipartUploadProtocol
{
    public Uri Endpoint => endpoint;
    public bool AllowHttp { get; init; }
    private const int PartSize = 8 * 1024 * 1024;
    private const long MaximumSize = 1024L * 1024 * 1024;
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private string StateKey(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid session identity.");
        return "s3-multipart/v1/" + Hash(scopeGeneration) + "/" + id;
    }
    private string ObjectKey(string id) => prefix + "multipart/" + Hash(scopeGeneration) + "/" + id;
    private static bool ValidState(S3MultipartState? state)
    {
        if (state is null || state.Version != 1 || !Guid.TryParseExact(state.Id, "N", out _)
            || state.Hash is null || state.Hash.Length != 64 || state.Hash.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))
            || state.Size is <= 0 or > MaximumSize || state.Parts is null || state.Parts.Length > 128) return false;
        var numbers = new HashSet<int>();
        return state.Parts.All(p => p is not null && p.Number > 0 && numbers.Add(p.Number)
            && p.Offset == (p.Number - 1L) * PartSize && p.Offset < state.Size
            && p.Length == Math.Min(PartSize, state.Size - p.Offset) && !string.IsNullOrEmpty(p.Receipt) && p.Receipt.Length <= 256);
    }
    private async Task<S3MultipartState> Load(string id, string hash, CancellationToken ct)
    {
        var record = await metadata.ReadAsync(StateKey(id), ct) ?? throw new KeyNotFoundException("Unknown scoped multipart session.");
        var state = JsonSerializer.Deserialize(record.Payload, S3MultipartJson.Default.S3MultipartState) ?? throw new InvalidDataException("Missing multipart state.");
        if (!ValidState(state) || state.Id != id || state.Hash != hash || state.Aborted)
            throw new InvalidDataException("Incompatible or aborted scoped multipart session; state retained.");
        return state;
    }
    private async Task Save(S3MultipartState state)
    {
        var key = StateKey(state.Id);
        var record = await metadata.ReadAsync(key, CancellationToken.None);
        if (!await metadata.TryWriteAsync([new(key, record?.Revision, JsonSerializer.Serialize(state, S3MultipartJson.Default.S3MultipartState))], cancellationToken: CancellationToken.None))
            throw new IOException("Multipart ownership changed.");
    }
    private static MultipartUploadSession View(S3MultipartState state) => new(1, state.Id, state.Hash, state.Size, PartSize, state.Parts, state.Verified);
    private async Task Grant(S3MultipartState state, CancellationToken ct)
    {
        var stored = await s3.GetObjectMetadataAsync(bucket, prefix + "objects/" + state.Hash[..2] + "/" + state.Hash, ct);
        if (stored.ContentLength != state.Size) throw new InvalidDataException("Previously verified object is unavailable; retained session requires repair.");
        await grantVerifiedAccess(state.Hash, state.Size, ct);
    }
    private async Task<bool> Staged(string id, CancellationToken ct)
    {
        try { await s3.GetObjectMetadataAsync(bucket, ObjectKey(id), ct); return true; }
        catch (AmazonS3Exception error) when (error.StatusCode == HttpStatusCode.NotFound) { return false; }
    }
    private async Task<S3MultipartState> Reconcile(S3MultipartState state, CancellationToken ct)
    {
        if (state.Verified || state.UploadId is null) return state;
        var parts = new List<MultipartPartReceipt>();
        try
        {
            var request = new ListPartsRequest { BucketName = bucket, Key = ObjectKey(state.Id), UploadId = state.UploadId, MaxParts = 1000 };
            var response = await s3.ListPartsAsync(request, ct);
            if (response.IsTruncated == true) throw new InvalidDataException("Multipart part inventory exceeds bounds.");
            foreach (var part in response.Parts ?? [])
            {
                var number = part.PartNumber ?? 0;
                var offset = (number - 1L) * PartSize;
                if (number < 1 || offset >= state.Size || part.Size != Math.Min(PartSize, state.Size - offset)
                    || string.IsNullOrEmpty(part.ETag) || part.ETag.Length > 256 || parts.Any(p => p.Number == number))
                    throw new InvalidDataException("Multipart service receipt does not match immutable layout.");
                parts.Add(new(number, offset, (int)part.Size!.Value, part.ETag));
            }
        }
        catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload")
        {
            // Completion may have committed before its reply or local checkpoint. Parts were saved before completion.
            if (!await Staged(state.Id, ct)) throw new IOException("Remote upload disappeared; session evidence retained.");
            return state;
        }
        state = state with { Parts = parts.OrderBy(p => p.Number).ToArray() };
        await Save(state); return state;
    }
    public async Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken)
    {
        if (size == 0) return null; // Legacy upload verifies the empty object.
        if (size is < 0 or > MaximumSize || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) throw new ArgumentException("Invalid multipart content bounds.");
        var key = StateKey(operationId);
        await using var held = await locks.AcquireAsync(Hash(key), cancellationToken);
        S3MultipartState state;
        if (await metadata.ReadAsync(key, cancellationToken) is null)
        {
            state = new(1, operationId, sha256, size, null, [], false, false, DateTimeOffset.UtcNow);
            await Save(state);
        }
        else state = await Load(operationId, sha256, cancellationToken);
        if (state.Size != size) throw new InvalidDataException("Operation identity was reused for different content.");
        state = state with { LastActivityUtc = DateTimeOffset.UtcNow }; await Save(state);
        if (state.UploadId is null)
        {
            // Discover an initiation that committed before its response/checkpoint. Never silently choose between duplicates.
            var pending = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = ObjectKey(operationId), MaxUploads = 2 }, cancellationToken);
            var matches = (pending.MultipartUploads ?? []).Where(x => x.Key == ObjectKey(operationId)).ToArray();
            if (pending.IsTruncated == true || matches.Length > 1) throw new IOException("Ambiguous remote initiation requires operator reconciliation.");
            var uploadId = matches.Length == 1 ? matches[0].UploadId : (await s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = ObjectKey(operationId), ContentType = "application/octet-stream" }, cancellationToken)).UploadId;
            state = state with { UploadId = uploadId }; await Save(state);
        }
        state = await Reconcile(state, cancellationToken);
        if (state.Verified)
        {
            // A previous completion may have persisted verification before the scoped grant or its response.
            await Grant(state, cancellationToken);
            await s3.DeleteObjectAsync(bucket, ObjectKey(operationId), cancellationToken);
        }
        return View(state);
    }
    public async Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken)
    {
        await using var held = await locks.AcquireAsync(Hash(StateKey(sessionId)), cancellationToken);
        var state = await Load(sessionId, sha256, cancellationToken);
        var offset = (number - 1L) * PartSize;
        if (state.Verified || state.UploadId is null || number < 1 || offset >= state.Size) throw new InvalidOperationException("Part is outside an active session.");
        state = state with { LastActivityUtc = DateTimeOffset.UtcNow }; await Save(state);
        var expires = state.LastActivityUtc!.Value.AddMinutes(5);
        var url = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest { BucketName = bucket, Key = ObjectKey(sessionId), UploadId = state.UploadId,
            PartNumber = number, Verb = HttpVerb.PUT, Expires = expires.UtcDateTime, Protocol = AllowHttp ? Amazon.S3.Protocol.HTTP : Amazon.S3.Protocol.HTTPS });
        return new(new Uri(url), number, offset, (int)Math.Min(PartSize, state.Size - offset));
    }
    public async Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken)
    {
        await using var held = await locks.AcquireAsync(Hash(StateKey(sessionId)), cancellationToken);
        var state = await Reconcile(await Load(sessionId, sha256, cancellationToken), cancellationToken);
        if (!state.Verified)
        {
            if (state.Parts.Sum(p => (long)p.Length) != state.Size) throw new InvalidDataException("Multipart content is incomplete.");
            if (!await Staged(sessionId, cancellationToken))
                await s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = ObjectKey(sessionId), UploadId = state.UploadId,
                    PartETags = state.Parts.Select(p => new PartETag(p.Number, p.Receipt)).ToList() }, cancellationToken);
            // Stream into a private snapshot: an ETag is not a content hash, and a subsequent copy could race replacement.
            var path = Path.Combine(Path.GetTempPath(), "bsync-multipart-" + Guid.NewGuid().ToString("N"));
            try
            {
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous))
                using (var response = await s3.GetObjectAsync(bucket, ObjectKey(sessionId), cancellationToken))
                using (var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    if (response.ContentLength != state.Size) throw new InvalidDataException("Completed length mismatch.");
                    var buffer = new byte[65536]; long total = 0; int count;
                    while ((count = await response.ResponseStream.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        total += count; if (total > state.Size) throw new InvalidDataException("Completed object exceeds bound.");
                        digest.AppendData(buffer, 0, count); await file.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    if (total != state.Size || Convert.ToHexStringLower(digest.GetHashAndReset()) != state.Hash) throw new InvalidDataException("Completed content hash mismatch.");
                    file.Position = 0;
                    await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = prefix + "objects/" + sha256[..2] + "/" + sha256,
                        InputStream = file, AutoCloseStream = false, UseChunkEncoding = false, ChecksumSHA256 = Convert.ToBase64String(Convert.FromHexString(sha256)) }, cancellationToken);
                }
                state = state with { Verified = true }; await Save(state);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        await Grant(state, cancellationToken); // retry-safe scoped publication, never hash-only access
        await s3.DeleteObjectAsync(bucket, ObjectKey(sessionId), cancellationToken);
        return View(state);
    }
    public async Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken)
    {
        await using var held = await locks.AcquireAsync(Hash(StateKey(sessionId)), cancellationToken);
        var record = await metadata.ReadAsync(StateKey(sessionId), cancellationToken);
        if (record is null) throw new KeyNotFoundException("Unknown scoped session.");
        var state = JsonSerializer.Deserialize(record.Payload, S3MultipartJson.Default.S3MultipartState)!;
        if (!ValidState(state) || state.Id != sessionId || state.Hash != sha256) throw new InvalidDataException("Scoped session mismatch.");
        await AbortOwned(state, cancellationToken);
    }

    private async Task AbortOwned(S3MultipartState state, CancellationToken cancellationToken)
    {
        if (state.Aborted && state.CleanupComplete) return;
        var sessionId = state.Id;
        state = state with { Aborted = true }; await Save(state); // never allow a late completion to grant access
        var uploadIds = new List<string>(); bool more = false;
        if (state.UploadId is not null)
            uploadIds.Add(state.UploadId);
        // Even a lost initiation response has durable intent but may not have an UploadId. Reconcile all exact-key
        // orphans; never abort a different operation that merely shares the prefix. Each sweep remains bounded.
        var orphanPage = await s3.ListMultipartUploadsAsync(new ListMultipartUploadsRequest { BucketName = bucket, Prefix = ObjectKey(sessionId), MaxUploads = 1000 }, cancellationToken);
        uploadIds.AddRange((orphanPage.MultipartUploads ?? []).Where(u => u.Key == ObjectKey(sessionId)).Select(u => u.UploadId));
        more = orphanPage.IsTruncated == true;
        foreach (var uploadId in uploadIds.Distinct(StringComparer.Ordinal))
            try { await s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = ObjectKey(sessionId), UploadId = uploadId }, cancellationToken); }
            catch (AmazonS3Exception error) when (error.ErrorCode == "NoSuchUpload") { }
        if (more) throw new IOException("More orphan uploads remain; retry bounded cleanup. Tombstone retained.");
        await s3.DeleteObjectAsync(bucket, ObjectKey(sessionId), cancellationToken);
        await Save(state with { CleanupComplete = true, Parts = [] });
    }

    /// <summary>
    /// Sweeps one bounded metadata page under the same per-session ownership as uploads. Refuses live five-minute
    /// authorizations, retains unknown/legacy records without timestamps, and never deletes replay tombstones.
    /// Call from a host worker with a deliberately chosen inactivity horizon; continuation is an opaque metadata key.
    /// Failed remote cleanup remains tombstoned and is retried on a later sweep. No credentials enter this result.
    /// </summary>
    public async Task<S3MultipartSweepResult> SweepAsync(DateTimeOffset inactiveBefore, int limit = 100, string? afterKey = null, CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (inactiveBefore > DateTimeOffset.UtcNow.AddMinutes(-5)) throw new ArgumentException("The inactivity horizon must exceed the five-minute ticket lifetime.", nameof(inactiveBefore));
        var records = await metadata.ReadPageAsync("s3-multipart/v1/" + Hash(scopeGeneration) + "/", afterKey, limit, cancellationToken);
        var aborted = 0; var retained = 0; var failed = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var held = await locks.AcquireAsync(Hash(record.Key), cancellationToken);
            var current = await metadata.ReadAsync(record.Key, cancellationToken);
            if (current is null) continue;
            S3MultipartState? state;
            try { state = JsonSerializer.Deserialize(current.Payload, S3MultipartJson.Default.S3MultipartState); }
            catch (JsonException) { retained++; continue; }
            if (!ValidState(state) || StateKey(state!.Id) != record.Key)
            { retained++; continue; }
            if (state.CleanupComplete || (!state.Aborted && (state.LastActivityUtc is null || state.LastActivityUtc >= inactiveBefore))) { retained++; continue; }
            try
            {
                if (state.Verified && !state.Aborted)
                {
                    // A lost completion reply may be retried after the inactivity horizon. Cleanup must retain
                    // the verified replay result; converting it to an abort would strand a valid durable job.
                    await s3.DeleteObjectAsync(bucket, ObjectKey(state.Id), cancellationToken);
                    await Save(state with { CleanupComplete = true }); retained++;
                }
                else { await AbortOwned(state, cancellationToken); aborted++; }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { failed++; }
        }
        return new(records.Count, aborted, retained, failed, records.Count == limit ? records[^1].Key : null);
    }
}

/// <summary>One bounded cleanup page. Retained includes live, legacy and unknown state; failures retain retry evidence.</summary>
public sealed record S3MultipartSweepResult(int Inspected, int Aborted, int Retained, int Failed, string? Continuation);
internal sealed record S3MultipartState(int Version, string Id, string Hash, long Size, string? UploadId, MultipartPartReceipt[] Parts, bool Verified, bool Aborted,
    DateTimeOffset? LastActivityUtc = null, bool CleanupComplete = false);
[JsonSerializable(typeof(S3MultipartState))]
internal partial class S3MultipartJson : JsonSerializerContext;
