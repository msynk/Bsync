using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Server.AspNetCore.Blobs;
using Bsync.Storage;

namespace Bsync.Samples.Tasks.Server;

/// <summary>Immutable service identity and verified bytes. Logical IDs alone are not stable blob versions.</summary>
public sealed record ExternalFileVersion(string FileId, string Version, string Sha256, long Size, string ContentType);

/// <summary>
/// Host adapter contract: immutable versions, trustworthy full SHA-256/length, idempotent completion by operation ID,
/// and completion lookup are mandatory for this recipe. Implementations acquire service credentials at call time.
/// Neither version IDs nor hashes authorize a reader. Configure SyncBlobEndpoints with a server-side IBlobAccess.
/// </summary>
public interface IVersionedFileService
{
    Task<ExternalFileVersion?> FindCompletionAsync(string operationId, CancellationToken cancellationToken);
    Task<ExternalFileVersion> PutAsync(string operationId, Stream verifiedContent, string sha256, long size, CancellationToken cancellationToken);
    Task<Stream?> OpenReadAsync(ExternalFileVersion version, CancellationToken cancellationToken);
    Task DeleteAsync(ExternalFileVersion version, CancellationToken cancellationToken);
}

/// <summary>
/// Actual Bsync blob-protocol adapter: resumable server staging, full verification, durable upload operation identity,
/// then immutable external mapping. It exposes no external presign endpoint as a Bsync endpoint. The metadata provider
/// and staging directory are server-owned and must be shared by all adapter users. Production integrations must qualify
/// their real service/CORS/credential implementation; this recipe deliberately uses the protocol's streamed fallback.
/// </summary>
public sealed class VersionedFileBlobStore(FileSystemBlobStore staging, IOrchestrationStore metadata, IVersionedFileService service) : IBlobStore
{
    private const string Prefix = "external-file/v1/";
    private static string ProofKey(string upload) => "external-file-proof/v1/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(upload)));
    private static string Key(string hash)
    {
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Expected a SHA-256.", nameof(hash));
        return Prefix + hash;
    }
    private static ExternalFileMapping? Decode(OrchestrationRecord? record)
    {
        if (record is null) return null;
        var mapping = JsonSerializer.Deserialize(record.Payload, ExternalFileJson.Default.ExternalFileMapping) ?? throw new InvalidDataException("Missing file mapping.");
        if (mapping.SchemaVersion != 1) throw new InvalidDataException("Unsupported file mapping; data retained.");
        return mapping;
    }
    private static string Encode(ExternalFileMapping mapping) => JsonSerializer.Serialize(mapping, ExternalFileJson.Default.ExternalFileMapping);
    public async Task<bool> ExistsAsync(string sha256, CancellationToken cancellationToken = default) =>
        Decode(await metadata.ReadAsync(Key(sha256), cancellationToken)) is { File: not null, Deleting: false };
    public async Task<Stream?> OpenReadAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var mapping = Decode(await metadata.ReadAsync(Key(sha256), cancellationToken));
        return mapping is { File: { } file, Deleting: false } ? await service.OpenReadAsync(file, cancellationToken) : null;
    }
    public async Task<long> GetReceivedAsync(string upload, CancellationToken cancellationToken = default)
    {
        var record = await metadata.ReadAsync(ProofKey(upload), cancellationToken);
        if (record is null) return await staging.GetReceivedAsync(upload, cancellationToken);
        var proof = JsonSerializer.Deserialize(record.Payload, ExternalFileJson.Default.ExternalUploadProof);
        if (proof is null || proof.Version != 1 || proof.Size < 0 || proof.Sha256 is null || proof.Sha256.Length != 64 || !proof.Sha256.All(char.IsAsciiHexDigitLower))
            throw new InvalidDataException("Unsupported or invalid uploader proof; data retained.");
        return proof.Size;
    }
    public Task<long?> AppendAsync(string upload, long offset, Stream content, long limit, CancellationToken cancellationToken = default) => staging.AppendAsync(upload, offset, content, limit, cancellationToken);

    public async Task<bool> CompleteAsync(string upload, string sha256, CancellationToken cancellationToken = default)
    {
        var key = Key(sha256);
        await using var lease = await ExecutionLease.TryAcquireAsync(metadata, "external-file-owner/" + sha256, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        if (lease is null) throw new IOException("External mapping is owned by another worker.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
        var token = stop.Token;
        var record = await metadata.ReadAsync(key, token); var mapping = Decode(record);
        if (mapping is { Deleting: true }) throw new IOException("External version deletion requires reconciliation.");
        var proofKey = ProofKey(upload);
        var proofRecord = await metadata.ReadAsync(proofKey, token);
        if (proofRecord is null)
        {
            // Deduplication must not let a different uploader claim bytes merely by knowing their hash.
            // Retain the verified uploader receipt before Complete removes this uploader's staging file.
            var path = staging.UploadFile(upload);
            await using var candidate = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(candidate, token)) != sha256)
            {
                await candidate.DisposeAsync();
                await staging.CompleteAsync(upload, sha256, token); // Provider discards the rejected upload under its own lock.
                return false;
            }
            var proof = new ExternalUploadProof(1, sha256, candidate.Length);
            if (!await metadata.TryWriteAsync([new(proofKey, null, JsonSerializer.Serialize(proof, ExternalFileJson.Default.ExternalUploadProof))], lease.Fence, token)) throw new IOException("Uploader verification changed.");
        }
        else
        {
            var proof = JsonSerializer.Deserialize(proofRecord.Payload, ExternalFileJson.Default.ExternalUploadProof)!;
            if (proof.Version != 1 || proof.Sha256 != sha256) throw new InvalidDataException("Uploader proof mismatch.");
        }
        if (File.Exists(staging.UploadFile(upload)) && !await staging.CompleteAsync(upload, sha256, token)) return false;
        if (mapping?.File is not null) { await staging.DeleteAsync(sha256, token); return true; }
        await using var source = await staging.OpenReadAsync(sha256, token) ?? throw new IOException("Verified staging disappeared.");
        if (mapping is null)
        {
            mapping = new(1, Guid.NewGuid().ToString("N"), source.Length, DateTimeOffset.UtcNow, null);
            if (!await metadata.TryWriteAsync([new(key, null, Encode(mapping))], lease.Fence, token)) throw new IOException("File mapping changed.");
            record = (await metadata.ReadAsync(key, token))!;
        }
        if (source.Length != mapping.Size) throw new InvalidDataException("Staging length changed.");
        // There is no database transaction across these calls. A lost reply resumes by operation ID; repeating Put
        // must return the same external version and must never create a second logical file.
        var file = await service.FindCompletionAsync(mapping.OperationId, token)
            ?? await service.PutAsync(mapping.OperationId, source, sha256, mapping.Size, token);
        if (file.Sha256 != sha256 || file.Size != mapping.Size || string.IsNullOrWhiteSpace(file.FileId) || string.IsNullOrWhiteSpace(file.Version) ||
            file.FileId.Length > 256 || file.Version.Length > 256 || file.ContentType.Length > 256)
            throw new InvalidDataException("External service returned an unverified or unbounded version.");
        if (!await metadata.TryWriteAsync([new(key, record!.Revision, Encode(mapping with { File = file }))], lease.Fence, token))
            throw new IOException("File mapping acknowledgement lost ownership; reconcile by operation ID.");
        await staging.DeleteAsync(sha256, token);
        return true;
    }
    public async IAsyncEnumerable<(string Sha256, DateTimeOffset Stored)> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string? after = null;
        while (true)
        {
            var page = await metadata.ReadPageAsync(Prefix, after, 100, cancellationToken);
            foreach (var record in page)
                if (Decode(record) is { File: not null, Deleting: false } mapping) yield return (record.Key[Prefix.Length..], mapping.CreatedAt);
            if (page.Count < 100) yield break;
            after = page[^1].Key;
        }
    }
    public async Task DeleteAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var key = Key(sha256);
        await using var lease = await ExecutionLease.TryAcquireAsync(metadata, "external-file-owner/" + sha256, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);
        if (lease is null) throw new IOException("External mapping is in use.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lost);
        var token = stop.Token;
        var record = await metadata.ReadAsync(key, token); var mapping = Decode(record);
        if (mapping is null) return;
        if (mapping.File is null) throw new IOException("Resolve unfinished external completion before orphan deletion.");
        if (!mapping.Deleting)
        {
            if (!await metadata.TryWriteAsync([new(key, record!.Revision, Encode(mapping with { Deleting = true }))], lease.Fence, token)) throw new IOException("File mapping changed.");
            record = (await metadata.ReadAsync(key, token))!;
        }
        await service.DeleteAsync(mapping.File, token);
        await staging.DeleteAsync(sha256, token);
        if (!await metadata.TryWriteAsync([new(key, record!.Revision, null)], lease.Fence, token)) throw new IOException("Deletion acknowledgement requires retry.");
    }
    public Task<int> PurgePartialsAsync(DateTimeOffset before, CancellationToken cancellationToken = default) => staging.PurgePartialsAsync(before, cancellationToken);
}

internal sealed record ExternalFileMapping(int SchemaVersion, string OperationId, long Size, DateTimeOffset CreatedAt, ExternalFileVersion? File, bool Deleting = false);
internal sealed record ExternalUploadProof(int Version, string Sha256, long Size);
[JsonSerializable(typeof(ExternalFileMapping))]
[JsonSerializable(typeof(ExternalUploadProof))]
internal partial class ExternalFileJson : JsonSerializerContext;
