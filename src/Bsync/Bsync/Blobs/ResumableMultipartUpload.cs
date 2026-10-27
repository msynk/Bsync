using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>One service-confirmed part receipt. ETag is opaque and is never a full-object SHA-256.</summary>
public sealed record MultipartPartReceipt(int Number, long Offset, int Length, string Receipt);
/// <summary>Authenticated immutable session layout and reconciled service receipts, schema 1.</summary>
public sealed record MultipartUploadSession(int SchemaVersion, string SessionId, string Sha256, long Size,
    int PartSize, MultipartPartReceipt[] Parts, bool VerifiedComplete);
/// <summary>Ephemeral part authorization. Never persist or log its signed URL.</summary>
public sealed record MultipartPartTicket(Uri Url, int Number, long Offset, int Length);

/// <summary>Authenticated multipart protocol. Providers bind every operation to authenticated scope and immutable content.</summary>
public interface IMultipartUploadProtocol
{
    /// <summary>Stable authority identity, without credentials or signed query parameters; partitions durable resume state.</summary>
    Uri Endpoint { get; }
    /// <summary>
    /// Initiates/reconciles using a durable client operation id. Repeating it must return the same compatible session.
    /// Null means explicitly unsupported. Returned receipts must be reconciled with the backing service after restart.
    /// </summary>
    Task<MultipartUploadSession?> StartAsync(string operationId, string sha256, long size, CancellationToken cancellationToken);
    /// <summary>Authorizes only this session's exact part; supports refreshing expired URLs without resetting other parts.</summary>
    Task<MultipartPartTicket> AuthorizePartAsync(string sessionId, string sha256, int number, CancellationToken cancellationToken);
    /// <summary>Retry-safe completion; returns true only after final length/hash verification and scoped publication.</summary>
    Task<MultipartUploadSession> CompleteAsync(string sessionId, string sha256, CancellationToken cancellationToken);
    /// <summary>Idempotently aborts a scoped session; late completion must not grant content access.</summary>
    Task AbortAsync(string sessionId, string sha256, CancellationToken cancellationToken);
}

/// <summary>
/// Optional upload transport used by the existing durable transfer manager through HttpBlobTransfer. This is not a
/// second work queue. Uses account metadata and shared physical ownership; no URLs or credentials enter recovery state.
/// </summary>
public sealed class ResumableMultipartUpload
{
    private readonly IOrchestrationStore _store;
    private readonly IBlobCacheLock _locks;
    private readonly IMultipartUploadProtocol _protocol;
    private readonly HttpClient _direct;
    private readonly string _prefix;
    /// <summary>
    /// Requires a credential-free direct client (no application auth/cookie handlers) and account-scoped metadata/locks.
    /// The authenticated protocol client is separate. Whole-origin deletion/revocation follows existing account ownership.
    /// </summary>
    public ResumableMultipartUpload(IOrchestrationStore store, IBlobCacheLock locks, IMultipartUploadProtocol protocol, HttpClient directClient)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store)); _locks = locks ?? throw new ArgumentNullException(nameof(locks));
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol)); _direct = directClient ?? throw new ArgumentNullException(nameof(directClient));
        if (!protocol.Endpoint.IsAbsoluteUri || protocol.Endpoint.UserInfo.Length != 0 || protocol.Endpoint.Query.Length != 0 || protocol.Endpoint.Fragment.Length != 0)
            throw new ArgumentException("Multipart authority identity must be absolute and credential-free.");
        if (_direct.DefaultRequestHeaders.Contains("Authorization") || _direct.DefaultRequestHeaders.Contains("Cookie")) throw new ArgumentException("Direct uploads require a credential-free client.");
        _prefix = "multipart/v1/" + Hash(protocol.Endpoint.AbsoluteUri) + "/";
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Validate(MultipartUploadSession session, string hash, long size)
    {
        if (session.SchemaVersion != 1) throw new NotSupportedException("Unsupported multipart session version; pending state retained.");
        if (session.Sha256 != hash || session.Size != size || string.IsNullOrWhiteSpace(session.SessionId) || session.SessionId.Length > 256
            || session.PartSize is < 5242880 or > 16777216 || session.Parts is null || session.Parts.Length > 1000
            || size < 0 || size > 1000L * session.PartSize) throw new InvalidDataException("Incompatible multipart session.");
        var numbers = new HashSet<int>();
        foreach (var part in session.Parts)
            if (part is null || part.Number < 1 || !numbers.Add(part.Number) || part.Offset != (part.Number - 1L) * session.PartSize
                || part.Offset >= size || part.Length != Math.Min(session.PartSize, size - part.Offset)
                || string.IsNullOrEmpty(part.Receipt) || part.Receipt.Length > 256) throw new InvalidDataException("Invalid multipart receipt layout.");
    }
    private async Task<OrchestrationRecord> Save(string key, OrchestrationRecord? before, MultipartResumeState state)
    {
        var payload = JsonSerializer.Serialize(state, MultipartJson.Default.MultipartResumeState);
        if (payload.Length > 524288) throw new InvalidDataException("Multipart resume state exceeds metadata limits.");
        if (!await _store.TryWriteAsync([new(key, before?.Revision, payload)], cancellationToken: CancellationToken.None).ConfigureAwait(false))
            throw new IOException("Multipart state changed outside account ownership.");
        return await _store.ReadAsync(key, CancellationToken.None).ConfigureAwait(false) ?? throw new IOException("Multipart state disappeared after commit.");
    }

    /// <summary>
    /// Returns bytes sent, or null when the authority explicitly lacks multipart support and no remote session exists.
    /// Persists operation/session/receipts before reporting progress; service reconciliation overrides stale local flags.
    /// Direct URL failures never include the URL in generated exception messages. Final verification belongs to the authority.
    /// </summary>
    public async Task<long?> UploadAsync(string sha256, IBlobSource source, Action<long, bool>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (sha256 is null || sha256.Length != 64 || sha256.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) throw new ArgumentException("Expected lowercase SHA-256.");
        ArgumentOutOfRangeException.ThrowIfNegative(source.Length);
        var key = _prefix + sha256;
        await using var held = await _locks.AcquireAsync(Hash(key), cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        var state = record is null ? new MultipartResumeState(1, Guid.NewGuid().ToString("N"), sha256, source.Length, null)
            : JsonSerializer.Deserialize(record.Payload, MultipartJson.Default.MultipartResumeState) ?? throw new InvalidDataException("Missing multipart state.");
        if (state.Version != 1 || state.Sha256 != sha256 || state.Size != source.Length || !Guid.TryParseExact(state.OperationId, "N", out _))
            throw new InvalidDataException("Incompatible multipart recovery state; retained.");
        if (record is null) record = await Save(key, record, state).ConfigureAwait(false);
        var session = await _protocol.StartAsync(state.OperationId, sha256, source.Length, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            if (state.Session is not null) throw new NotSupportedException("Multipart resume became unsupported; durable state retained.");
            return null;
        }
        Validate(session, sha256, source.Length);
        if (state.Session is { } old && (old.SessionId != session.SessionId || old.PartSize != session.PartSize)) throw new InvalidDataException("Multipart session/layout changed during resume.");
        state = state with { Session = session };
        record = await Save(key, record, state).ConfigureAwait(false);
        if (session.VerifiedComplete) { progress?.Invoke(source.Length, true); return 0; }
        var receipts = session.Parts.ToDictionary(p => p.Number);
        long sent = 0, available = session.Parts.Sum(p => (long)p.Length);
        progress?.Invoke(available, false);
        var buffer = new byte[session.PartSize];
        for (var number = 1; (number - 1L) * session.PartSize < source.Length; number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receipts.ContainsKey(number)) continue;
            var offset = (number - 1L) * session.PartSize;
            var length = (int)Math.Min(buffer.Length, source.Length - offset);
            await using (var stream = await source.OpenAsync(offset, cancellationToken).ConfigureAwait(false))
                await stream.ReadExactlyAsync(buffer.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            string? receipt = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var ticket = await _protocol.AuthorizePartAsync(session.SessionId, sha256, number, cancellationToken).ConfigureAwait(false);
                if (ticket.Number != number || ticket.Offset != offset || ticket.Length != length || !ticket.Url.IsAbsoluteUri
                    || ticket.Url.UserInfo.Length != 0 || ticket.Url.Scheme is not ("https" or "http")) throw new InvalidDataException("Invalid multipart part authorization.");
                using var content = new ByteArrayContent(buffer, 0, length);
                using var request = new HttpRequestMessage(HttpMethod.Put, ticket.Url) { Content = content };
                // Browser fetch must omit ambient cookies even when a provider URL happens to share the app origin.
                request.Options.Set(new HttpRequestOptionsKey<IDictionary<string, object>>("WebAssemblyFetchOptions"), new Dictionary<string, object> { ["credentials"] = "omit", ["redirect"] = "error" });
                HttpResponseMessage response;
                try { response = await _direct.SendAsync(request, cancellationToken).ConfigureAwait(false); }
                catch (HttpRequestException error) { throw new HttpRequestException("Direct multipart request failed.", null, error.StatusCode); }
                using (response)
                {
                    if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden && attempt == 0) continue;
                    if (!response.IsSuccessStatusCode) throw new HttpRequestException("Direct multipart request refused.", null, response.StatusCode);
                    receipt = response.Headers.ETag?.ToString();
                    if (string.IsNullOrEmpty(receipt) || receipt.Length > 256) throw new InvalidDataException("Missing or oversized multipart receipt; configure provider CORS to expose ETag.");
                }
                break;
            }
            if (receipt is null) throw new IOException("Refreshed multipart authorization was refused.");
            receipts.Add(number, new(number, offset, length, receipt));
            session = session with { Parts = receipts.Values.OrderBy(p => p.Number).ToArray() };
            state = state with { Session = session };
            record = await Save(key, record, state).ConfigureAwait(false);
            sent += length; available += length;
            progress?.Invoke(available, false);
        }
        var completed = await _protocol.CompleteAsync(session.SessionId, sha256, cancellationToken).ConfigureAwait(false);
        Validate(completed, sha256, source.Length);
        if (!completed.VerifiedComplete || completed.SessionId != session.SessionId || completed.PartSize != session.PartSize)
            throw new InvalidDataException("Authority did not verify multipart completion.");
        await Save(key, record, state with { Session = completed }).ConfigureAwait(false);
        progress?.Invoke(source.Length, true);
        return sent;
    }

    /// <summary>Aborts remote work before deleting resume state. A failed abort retains local recovery evidence.</summary>
    public async Task AbortAsync(string sha256, CancellationToken cancellationToken = default)
    {
        var key = _prefix + sha256;
        await using var held = await _locks.AcquireAsync(Hash(key), cancellationToken).ConfigureAwait(false);
        var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null) return;
        var state = JsonSerializer.Deserialize(record.Payload, MultipartJson.Default.MultipartResumeState) ?? throw new InvalidDataException("Missing multipart state.");
        if (state.Version != 1 || state.Sha256 != sha256) throw new InvalidDataException("Unknown multipart state; retained.");
        // Start may have created remote state before its reply was lost. Reconcile the durable operation id first.
        var session = state.Session ?? await _protocol.StartAsync(state.OperationId, sha256, state.Size, cancellationToken).ConfigureAwait(false);
        if (session is null && state.Session is not null) throw new NotSupportedException("Cannot reconcile the remote session; recovery state retained.");
        if (session is not null)
        {
            Validate(session, sha256, state.Size);
            if (state.Session is { } old && old.SessionId != session.SessionId) throw new InvalidDataException("Remote session changed; retained.");
            if (state.Session is null) record = await Save(key, record, state with { Session = session }).ConfigureAwait(false);
            await _protocol.AbortAsync(session.SessionId, sha256, cancellationToken).ConfigureAwait(false);
        }
        if (!await _store.TryWriteAsync([new(key, record.Revision, null)], cancellationToken: CancellationToken.None).ConfigureAwait(false)) throw new IOException("Multipart ownership changed.");
    }
}

internal sealed record MultipartResumeState(int Version, string OperationId, string Sha256, long Size, MultipartUploadSession? Session);
[JsonSerializable(typeof(MultipartResumeState))]
[JsonSerializable(typeof(MultipartUploadSession))]
[JsonSerializable(typeof(MultipartPartTicket))]
internal partial class MultipartJson : JsonSerializerContext;
