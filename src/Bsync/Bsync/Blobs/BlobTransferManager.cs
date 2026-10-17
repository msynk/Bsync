using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>Direction of durable blob work.</summary>
public enum BlobTransferDirection
{
    /// <summary>Device to authority.</summary>
    Upload,
    /// <summary>Authority to device.</summary>
    Download,
}

/// <summary>Persisted transfer state, independent of document replication status.</summary>
public enum BlobTransferState
{
    /// <summary>Eligible for execution.</summary>
    Queued,
    /// <summary>Owned by a worker; recovered by the next owner after interruption.</summary>
    Running,
    /// <summary>Retry after connectivity/backoff allows.</summary>
    WaitingForConnectivity,
    /// <summary>Host must restore credentials and resume the job.</summary>
    WaitingForCredentials,
    /// <summary>Explicitly paused.</summary>
    Paused,
    /// <summary>Requires explicit retry or repair.</summary>
    Failed,
    /// <summary>Verified local download or authority-confirmed upload.</summary>
    Complete,
    /// <summary>Explicitly canceled; an already committed remote upload cannot be recalled.</summary>
    Canceled,
}

/// <summary>Durable transfer record, schema 1. It contains no credentials, file paths or presigned URLs.</summary>
/// <param name="SchemaVersion">Payload schema.</param>
/// <param name="Sha256">Content identity.</param>
/// <param name="Size">Expected verified length.</param>
/// <param name="Direction">Upload or download.</param>
/// <param name="State">Persisted state.</param>
/// <param name="Priority">Higher numbers run first.</param>
/// <param name="Attempts">Number of execution attempts.</param>
/// <param name="NextAttemptUtc">Backoff boundary.</param>
/// <param name="ErrorCode">Content-free failure classification.</param>
public sealed record BlobTransferJob(int SchemaVersion, string Sha256, long Size, BlobTransferDirection Direction,
    BlobTransferState State, int Priority, int Attempts, DateTimeOffset? NextAttemptUtc, string? ErrorCode)
{
    /// <summary>Initial enqueue time. Null for older schema-1 records whose original age is unknown.</summary>
    public DateTimeOffset? CreatedUtc { get; init; }
}

/// <summary>Outcome of one bounded drain. Remaining work includes paused/failed jobs requiring host action.</summary>
/// <param name="Completed">Jobs completed by this drain.</param>
/// <param name="HasRemainingWork">Nonterminal work remains, or the bounded scan cannot establish that the queue is empty.</param>
/// <param name="Owned">Whether this caller acquired execution ownership.</param>
public sealed record BlobDrainResult(int Completed, bool HasRemainingWork, bool Owned);

/// <summary>Bounds for a managed transfer drain.</summary>
public sealed class BlobTransferManagerOptions
{
    /// <summary>Maximum simultaneous jobs, 1..16. Default 2 leaves room for small urgent work.</summary>
    public int Concurrency { get; init; } = 2;
    /// <summary>Maximum attempt duration before rotating to another job. Partial progress is retained.</summary>
    public TimeSpan AttemptBudget { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Maximum jobs read during one drain; pagination exposes the remainder to a later drain.</summary>
    public int ScanLimit { get; init; } = 1000;
    /// <summary>Retry base delay.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Time source for deterministic scheduling tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Durable, account-scoped jobs over the existing resumable HTTP blob protocol. Use metadata/cache/transport bound to
/// one account. Endpoint identity partitions jobs; credentials and presigned URLs are never persisted. The host must
/// keep pending-upload bytes pinned until completion/cancellation. Managed caches pin jobs until ReleaseTerminalAsync;
/// other cache implementations require host-owned pinning.
/// </summary>
public sealed class BlobTransferManager
{
    private readonly IOrchestrationStore _store;
    private readonly IBlobCache _cache;
    private readonly HttpBlobTransfer _transfer;
    private readonly BlobTransferManagerOptions _options;
    private readonly string _prefix;
    private readonly string _leaseKey;
    private readonly string _cursorKey;
    private readonly SemaphoreSlim _drain = new(1, 1);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);

    /// <summary>Creates a manager. Opening does not execute network work.</summary>
    public BlobTransferManager(IOrchestrationStore store, IBlobCache cache, HttpBlobTransfer transfer, BlobTransferManagerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(transfer);
        _store = store;
        _cache = cache;
        _transfer = transfer;
        _options = options ?? new();
        if (_options.Concurrency is < 1 or > 16 || _options.ScanLimit is < 1 or > 10000
            || _options.AttemptBudget <= TimeSpan.Zero || _options.RetryDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        var endpoint = transfer.Endpoint;
        if (endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new ArgumentException("Blob endpoint must be an HTTP(S) route without embedded credentials, query or fragment.", nameof(transfer));
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint.AbsoluteUri.TrimEnd('/'))));
        _prefix = "transfers/" + key + "/";
        _leaseKey = "transfer-owner/" + key;
        _cursorKey = "transfer-scan/" + key;
    }

    /// <summary>Notification after durable state transitions. Observers must re-read; notifications can be lost across restarts.</summary>
    public event Action? Changed;

    /// <summary>
    /// A ReadyToPush helper for at most 256 application-selected local prerequisites. Enqueues idempotently, drains within
    /// budget and returns true only when each upload is authority-confirmed complete. Server handlers must still validate
    /// blob availability/authorization. Pass only prerequisites this device must upload, not every remote reference.
    /// </summary>
    public async ValueTask<bool> EnsureUploadsAsync(IReadOnlyList<BlobReference> prerequisites, TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prerequisites);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        if (prerequisites.Count > 256) throw new ArgumentException("Upload prerequisites are bounded to 256 items.", nameof(prerequisites));
        var items = prerequisites.ToArray();
        // Validate the complete request before creating durable jobs.
        foreach (var item in items)
        { ArgumentNullException.ThrowIfNull(item); _ = Key(item.Sha256, BlobTransferDirection.Upload); ArgumentOutOfRangeException.ThrowIfNegative(item.Size); }
        if (items.GroupBy(item => item.Sha256).Any(group => group.Select(item => item.Size).Distinct().Count() != 1))
            throw new ArgumentException("One hash cannot have different prerequisite lengths.", nameof(prerequisites));
        foreach (var item in items.DistinctBy(item => item.Sha256))
        {
            await EnqueueAsync(item.Sha256, item.Size, BlobTransferDirection.Upload, priority: 1, cancellationToken).ConfigureAwait(false);
            // Complete is historical. The server may have collected an unpublished upload during a long offline period.
            var key = Key(item.Sha256, BlobTransferDirection.Upload);
            while (await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false) is { } record)
            {
                var job = Decode(record);
                if (job.State != BlobTransferState.Complete) break; // Never override a concurrent pause/cancel/failure.
                if (await _store.TryWriteAsync([new(key, record.Revision, Encode(job with { State = BlobTransferState.Queued, NextAttemptUtc = null, ErrorCode = null }))], cancellationToken: cancellationToken).ConfigureAwait(false))
                { Notify(); break; }
            }
        }
        if (items.Length == 0) return true;
        await DrainAsync(budget, cancellationToken).ConfigureAwait(false);
        foreach (var item in items)
            if ((await GetAsync(item.Sha256, BlobTransferDirection.Upload, cancellationToken).ConfigureAwait(false))?.State != BlobTransferState.Complete) return false;
        return true;
    }

    private string Key(string hash, BlobTransferDirection direction)
    {
        ArgumentNullException.ThrowIfNull(hash);
        if (hash.Length != 64 || hash.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("Expected a lowercase SHA-256.", nameof(hash));
        if (!Enum.IsDefined(direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        return _prefix + (direction == BlobTransferDirection.Upload ? "up/" : "down/") + hash;
    }

    private static string Encode(BlobTransferJob job) => JsonSerializer.Serialize(job, TransferJson.Default.BlobTransferJob);
    private static BlobTransferJob Decode(OrchestrationRecord record)
    {
        var job = JsonSerializer.Deserialize(record.Payload, TransferJson.Default.BlobTransferJob)!;
        if (job.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported transfer job schema; nothing was changed.");
        return job;
    }

    /// <summary>Queues/deduplicates an exact endpoint/hash/direction job. Existing terminal jobs remain terminal until explicitly resumed.</summary>
    public async Task<BlobTransferJob> EnqueueAsync(string sha256, long size, BlobTransferDirection direction, int priority = 0, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        var key = Key(sha256, direction);
        await using var contentLock = _cache is ManagedBlobCache locked
            ? await locked.AcquireTransferAsync(sha256, cancellationToken).ConfigureAwait(false) : null;
        while (true)
        {
            var existing = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                var job = Decode(existing);
                if (job.Size != size) throw new ArgumentException("The same content identity was queued with a different length.", nameof(size));
                return job;
            }
            var created = new BlobTransferJob(1, sha256, size, direction, BlobTransferState.Queued, priority, 0, null, null)
                { CreatedUtc = _options.TimeProvider.GetUtcNow() };
            if (_cache is ManagedBlobCache managed) await managed.PinAsync(sha256, key, cancellationToken).ConfigureAwait(false);
            if (await _store.TryWriteAsync([new(key, null, Encode(created))], cancellationToken: cancellationToken).ConfigureAwait(false)) { Notify(); return created; }
        }
    }

    /// <summary>Reads one persisted job.</summary>
    public async Task<BlobTransferJob?> GetAsync(string sha256, BlobTransferDirection direction, CancellationToken cancellationToken = default) =>
        await _store.ReadAsync(Key(sha256, direction), cancellationToken).ConfigureAwait(false) is { } record ? Decode(record) : null;

    /// <summary>
    /// Removes a complete/canceled job and releases its managed-cache pin. Pending documents/packages must own separate
    /// pins before calling. Crash after record removal can leave a conservative pin for host reconciliation.
    /// </summary>
    public async Task<bool> ReleaseTerminalAsync(string sha256, BlobTransferDirection direction, CancellationToken cancellationToken = default)
    {
        var key = Key(sha256, direction);
        await using var contentLock = _cache is ManagedBlobCache managed
            ? await managed.AcquireTransferAsync(sha256, cancellationToken).ConfigureAwait(false) : null;
        var record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null) return true;
        if (Decode(record).State is not (BlobTransferState.Complete or BlobTransferState.Canceled)) return false;
        if (!await _store.TryWriteAsync([new(key, record.Revision, null)], cancellationToken: cancellationToken).ConfigureAwait(false)) return false;
        if (_cache is ManagedBlobCache pinned) await pinned.UnpinAsync(sha256, key, cancellationToken).ConfigureAwait(false);
        Notify();
        return true;
    }

    /// <summary>After a host restores credentials, resumes only a credential-waiting job. A concurrent pause/cancel is preserved.</summary>
    public async Task<bool> ResumeCredentialWaitAsync(string sha256, BlobTransferDirection direction, CancellationToken cancellationToken = default)
    {
        var key = Key(sha256, direction);
        while (await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false) is { } record)
        {
            var job = Decode(record);
            if (job.State != BlobTransferState.WaitingForCredentials) return false;
            if (await _store.TryWriteAsync([new(key, record.Revision, Encode(job with { State = BlobTransferState.Queued, NextAttemptUtc = null, ErrorCode = null }))], cancellationToken: cancellationToken).ConfigureAwait(false))
            { Notify(); return true; }
        }
        return false;
    }

    /// <summary>Pauses, cancels or resumes a job. Cancellation never claims to undo a committed upload.</summary>
    public async Task SetStateAsync(string sha256, BlobTransferDirection direction, BlobTransferState state, CancellationToken cancellationToken = default)
    {
        if (state is not (BlobTransferState.Queued or BlobTransferState.Paused or BlobTransferState.Canceled)) throw new ArgumentOutOfRangeException(nameof(state));
        var key = Key(sha256, direction);
        while (await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false) is { } record)
        {
            var job = Decode(record);
            if (await _store.TryWriteAsync([new(key, record.Revision, Encode(job with { State = state, NextAttemptUtc = null, ErrorCode = null }))], cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                lock (_running) if (_running.TryGetValue(key, out var running)) running.Cancel();
                Notify();
                return;
            }
        }
        throw new KeyNotFoundException("Transfer job not found.");
    }

    /// <summary>Drains one bounded queue window. A restarted worker resumes Running jobs from verified cache/server offsets.</summary>
    public async Task<BlobDrainResult> DrainAsync(TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        await _drain.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completed = 0;
        var owned = false;
        try
        {
            await using var lease = await ExecutionLease.TryAcquireAsync(_store, _leaseKey, TimeSpan.FromSeconds(30), _options.TimeProvider, cancellationToken).ConfigureAwait(false);
            if (lease is null) return new(0, true, false);
            owned = true;
            using var expiry = new CancellationTokenSource(budget, _options.TimeProvider);
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, lease.Lost);
            var records = new List<OrchestrationRecord>();
            var cursor = await _store.ReadAsync(_cursorKey, stopping.Token).ConfigureAwait(false);
            string? after = cursor?.Payload;
            var startedAfter = after;
            var exhausted = false;
            while (records.Count < _options.ScanLimit)
            {
                var count = Math.Min(1000, _options.ScanLimit - records.Count);
                var page = await _store.ReadPageAsync(_prefix, after, count, stopping.Token).ConfigureAwait(false);
                records.AddRange(page);
                if (page.Count > 0) after = page[^1].Key;
                if (page.Count < count) { exhausted = true; break; }
            }
            await _store.TryWriteAsync([new(_cursorKey, cursor?.Revision, exhausted ? null : after)], lease.Fence, stopping.Token).ConfigureAwait(false);
            var eligible = records.Select(r => (Record: r, Job: Decode(r))).Where(r =>
                r.Job.State is BlobTransferState.Queued or BlobTransferState.Running or BlobTransferState.WaitingForConnectivity
                && (r.Job.NextAttemptUtc is null || r.Job.NextAttemptUtc <= _options.TimeProvider.GetUtcNow()))
                .OrderByDescending(r => r.Job.Priority).ThenBy(r => r.Job.Attempts).ThenBy(r => r.Record.Key, StringComparer.Ordinal).ToArray();
            using var slots = new SemaphoreSlim(_options.Concurrency);
            var tasks = eligible.Select(async item =>
            {
                try { await slots.WaitAsync(stopping.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                try { if (await RunAsync(item.Record, item.Job, lease.Fence, stopping.Token).ConfigureAwait(false)) Interlocked.Increment(ref completed); }
                finally { slots.Release(); }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Scan the same bounded window again; if a full window was read there may also be work beyond it.
            var remaining = !exhausted || startedAfter is not null;
            foreach (var record in records)
            {
                var current = await _store.ReadAsync(record.Key, stopping.Token).ConfigureAwait(false);
                if (current is not null && Decode(current).State is not (BlobTransferState.Complete or BlobTransferState.Canceled)) remaining = true;
            }
            return new(completed, remaining, true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new(completed, true, owned); }
        finally { _drain.Release(); }
    }

    private async Task<bool> RunAsync(OrchestrationRecord record, BlobTransferJob job, OrchestrationFence fence, CancellationToken stopping)
    {
        using var attempt = new CancellationTokenSource(_options.AttemptBudget, _options.TimeProvider);
        using var token = CancellationTokenSource.CreateLinkedTokenSource(stopping, attempt.Token);
        lock (_running) _running[record.Key] = token;
        try
        {
            await using var contentLock = _cache is ManagedBlobCache managed
                ? await managed.AcquireTransferAsync(job.Sha256, token.Token).ConfigureAwait(false) : null;
            job = job with { State = BlobTransferState.Running, Attempts = job.Attempts + 1, ErrorCode = null };
            if (!await _store.TryWriteAsync([new(record.Key, record.Revision, Encode(job))], fence, stopping).ConfigureAwait(false)) return false;
            record = (await _store.ReadAsync(record.Key, stopping).ConfigureAwait(false))!;
            if (record.Payload != Encode(job)) return false;
            Notify();
            try
            {
                if (job.Direction == BlobTransferDirection.Download)
                {
                    await _transfer.DownloadAsync(_cache, job.Sha256, job.Size, token.Token).ConfigureAwait(false);
                    if (await _cache.SizeAsync(job.Sha256, token.Token).ConfigureAwait(false) != job.Size) throw new BlobCorruptedException("Verified content has an unexpected length.");
                }
                else
                {
                    if (await _cache.SizeAsync(job.Sha256, token.Token).ConfigureAwait(false) != job.Size) throw new FileNotFoundException("Verified upload content is unavailable.");
                    await _transfer.UploadAsync(job.Sha256, new CacheSource(_cache, job.Sha256, job.Size), token.Token).ConfigureAwait(false);
                }
                job = job with { State = BlobTransferState.Complete, NextAttemptUtc = null };
            }
            catch (OperationCanceledException) { job = job with { State = BlobTransferState.Queued, ErrorCode = "interrupted", NextAttemptUtc = null }; }
            catch (BlobCorruptedException) { job = job with { State = BlobTransferState.Failed, ErrorCode = "hash-or-length-mismatch" }; }
            catch (FileNotFoundException) { job = job with { State = BlobTransferState.Failed, ErrorCode = "missing-local-content" }; }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            { job = job with { State = BlobTransferState.WaitingForCredentials, ErrorCode = "credentials-required" }; }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.RequestEntityTooLarge)
            { job = job with { State = BlobTransferState.Failed, ErrorCode = error.StatusCode == HttpStatusCode.NotFound ? "not-found" : "too-large" }; }
            catch (Exception error) when (error is HttpRequestException or IOException)
            { job = job with { State = BlobTransferState.WaitingForConnectivity, ErrorCode = "unavailable", NextAttemptUtc = _options.TimeProvider.GetUtcNow() + _options.RetryDelay * Math.Min(128, Math.Pow(2, Math.Min(job.Attempts - 1, 7))) }; }
            // A concurrent pause/cancel/resume changes the revision and wins over this worker's completion.
            var saved = await _store.TryWriteAsync([new(record.Key, record.Revision, Encode(job))], fence, CancellationToken.None).ConfigureAwait(false);
            if (saved) Notify();
            return saved && job.State == BlobTransferState.Complete;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; }
        finally { lock (_running) _running.Remove(record.Key); }
    }

    private void Notify()
    {
        foreach (var observer in Changed?.GetInvocationList() ?? [])
            try { ((Action)observer)(); } catch { /* A UI observer cannot undo a durable transition. */ }
    }

    private sealed class CacheSource(IBlobCache cache, string hash, long length) : IBlobSource
    {
        public long Length => length;
        public async ValueTask<Stream> OpenAsync(long offset, CancellationToken cancellationToken = default)
        {
            var stream = await cache.OpenReadAsync(hash, cancellationToken).ConfigureAwait(false) ?? throw new FileNotFoundException("Verified upload content is unavailable.");
            stream.Position = offset;
            return stream;
        }
    }
}

[JsonSerializable(typeof(BlobTransferJob))]
internal partial class TransferJson : JsonSerializerContext;
