using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Blobs;

/// <summary>Durable replacement progress. Promotion includes the provider's full hash verification and atomic switch.</summary>
public enum BlobRepairState
{
    /// <summary>Repairable damage was observed; bytes have not been replaced.</summary>
    Needed,
    /// <summary>Isolated partial content is resumable under physical content ownership.</summary>
    Downloading,
    /// <summary>Durable intent precedes verification and atomic provider promotion; reverify on restart.</summary>
    Promoting,
    /// <summary>Verified bytes are reachable. A lost transfer acknowledgement can be reconciled without downloading again.</summary>
    Completed,
    /// <summary>Capacity, credentials, keys or connectivity require a later retry.</summary>
    Deferred,
    /// <summary>Explicit host action is required; unreadable data is retained.</summary>
    RecoveryRequired,
}

/// <summary>Versioned repair journal, separate from document operations. No credentials or signed URLs are persisted.</summary>
/// <param name="SchemaVersion">Journal schema, currently 1.</param>
/// <param name="Sha256">Plaintext identity.</param>
/// <param name="Size">Expected plaintext length.</param>
/// <param name="SourceAuthority">Credential-free blob route identity.</param>
/// <param name="Generation">Unique repair attempt lineage; a physical lock fences the observed content through promotion.</param>
/// <param name="State">Recoverable progress.</param>
/// <param name="Attempts">Transfer attempts observed by this journal.</param>
/// <param name="NextAttemptUtc">Retry boundary, if applicable.</param>
/// <param name="ErrorCode">Content-free diagnostic.</param>
public sealed record BlobRepairJob(int SchemaVersion, string Sha256, long Size, string SourceAuthority, string Generation,
    BlobRepairState State, int Attempts, DateTimeOffset? NextAttemptUtc, string? ErrorCode);

public sealed partial class BlobTransferManager
{
    private static readonly System.Diagnostics.Metrics.Counter<long> RepairOutcomes = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.blob.repairs", "{repair}", "Durable repair state transitions, without content or account identifiers.");
    private static readonly System.Diagnostics.Metrics.Counter<long> RepairedBytes = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateCounter<long>(
        "bsync.blob.repaired_bytes", "By", "Verified replacement bytes acknowledged by repair journals.");
    private static readonly System.Diagnostics.Metrics.Histogram<double> RepairDuration = Bsync.Diagnostics.SyncDiagnostics.Meter.CreateHistogram<double>(
        "bsync.blob.repair_attempt_duration", "s", "Time spent in replacement attempts, including failed or canceled attempts; excludes downtime between retries.");
    private string RepairKey(string hash) => "repair/v1/" + Key(hash, BlobTransferDirection.Download)["transfers/".Length..];
    private static string EncodeRepair(BlobRepairJob job) => JsonSerializer.Serialize(job, RepairJson.Default.BlobRepairJob);
    private static BlobRepairJob DecodeRepair(OrchestrationRecord record)
    {
        var job = JsonSerializer.Deserialize(record.Payload, RepairJson.Default.BlobRepairJob)!;
        if (job.SchemaVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported repair journal; bytes retained.");
        return job;
    }
    private BlobRepairJob NewRepair(BlobTransferJob job) => new(1, job.Sha256, job.Size, _transfer.Endpoint.AbsoluteUri,
        Guid.NewGuid().ToString("N"), BlobRepairState.Needed, job.Attempts, null, null);

    /// <summary>Reads the durable repair outcome for this authority/content identity, independently of transfer completion.</summary>
    public async Task<BlobRepairJob?> GetRepairAsync(string sha256, CancellationToken cancellationToken = default) =>
        await _store.ReadAsync(RepairKey(sha256), cancellationToken).ConfigureAwait(false) is { } record ? DecodeRepair(record) : null;

    // Revalidate under the same physical lock used by import, rotation and transfer. Only Complete can be reopened:
    // paused/canceled/failed jobs must never be implicitly resumed by repeated preparation.
    internal async Task RevalidateDownloadAsync(string hash, long size, CancellationToken ct)
    {
        if (_cache is not ManagedBlobCache managed) return;
        await using var held = await managed.AcquireTransferAsync(hash, ct).ConfigureAwait(false);
        var key = Key(hash, BlobTransferDirection.Download);
        var record = await _store.ReadAsync(key, ct).ConfigureAwait(false);
        if (record is null || Decode(record) is not { State: BlobTransferState.Complete } job) return;
        var result = await BlobVerification.VerifyAsync(_cache, hash, size, ct).ConfigureAwait(false);
        if (result == BlobVerificationResult.Verified) return;
        var repairKey = RepairKey(hash);
        var previous = await _store.ReadAsync(repairKey, ct).ConfigureAwait(false);
        if (previous is not null) _ = DecodeRepair(previous);
        var repair = NewRepair(job);
        var repairable = result == BlobVerificationResult.Missing || result == BlobVerificationResult.InvalidContent && managed.SupportsVerifiedReplacement;
        var code = VerificationCode(result);
        repair = repair with { State = repairable ? BlobRepairState.Needed : Deferred(result) ? BlobRepairState.Deferred : BlobRepairState.RecoveryRequired, ErrorCode = code };
        if (await _store.TryWriteAsync([
            new(key, record.Revision, Encode(job with { State = repairable ? BlobTransferState.Queued : BlobTransferState.Failed, ErrorCode = code, NextAttemptUtc = null })),
            new(repairKey, previous?.Revision, EncodeRepair(repair))], cancellationToken: ct).ConfigureAwait(false))
        {
            RecordRepairTransition(repair, previous is null ? null : DecodeRepair(previous));
            Notify();
        }
    }

    private async Task SaveRepairAsync(BlobRepairJob job, OrchestrationFence fence, CancellationToken ct)
    {
        var key = RepairKey(job.Sha256);
        var previous = await _store.ReadAsync(key, ct).ConfigureAwait(false);
        if (previous is not null) _ = DecodeRepair(previous);
        if (!await _store.TryWriteAsync([new(key, previous?.Revision, EncodeRepair(job))], fence, ct).ConfigureAwait(false))
            throw new OperationCanceledException("Repair execution ownership was lost.");
        RecordRepairTransition(job, previous is null ? null : DecodeRepair(previous));
    }

    private static void RecordRepairTransition(BlobRepairJob job, BlobRepairJob? previous)
    {
        if (previous is null || previous.Generation != job.Generation || previous.State != job.State || previous.Attempts != job.Attempts || previous.ErrorCode != job.ErrorCode)
        {
            RepairOutcomes.Add(1, new KeyValuePair<string, object?>("bsync.state", job.State.ToString()), new KeyValuePair<string, object?>("bsync.error", RepairMetricCode(job.ErrorCode)));
            if (job.State == BlobRepairState.Completed && (previous?.State != BlobRepairState.Completed || previous.Generation != job.Generation)) RepairedBytes.Add(job.Size);
        }
    }

    // Durable journals may be host supplied. Keep metric cardinality and content independent of their strings.
    private static string RepairMetricCode(string? code) => code switch
    {
        null => "none",
        "missing-content" or "invalid-content" or "key-locked" or "key-unavailable" or "authentication-failed" or
        "unsupported-format" or "io-failure" or "capacity-required" or "authorization-required" or
        "hash-or-length-mismatch" or "interrupted" or "invalid-transfer-data" or "missing-local-content" or
        "credentials-required" or "not-found" or "too-large" or "unavailable" => code,
        _ => "other",
    };

    private async Task DownloadManagedAsync(BlobTransferJob job, OrchestrationFence fence, CancellationToken ct)
    {
        if (_cache is not ManagedBlobCache managed)
        {
            await _transfer.DownloadAsync(_cache, job.Sha256, job.Size, ct).ConfigureAwait(false);
            return;
        }
        var result = await BlobVerification.VerifyAsync(_cache, job.Sha256, job.Size, ct).ConfigureAwait(false);
        if (result == BlobVerificationResult.Verified) return;
        var repair = await GetRepairAsync(job.Sha256, ct).ConfigureAwait(false);
        if (result != BlobVerificationResult.Missing && (result != BlobVerificationResult.InvalidContent || !managed.SupportsVerifiedReplacement))
        {
            repair = (repair ?? NewRepair(job)) with { State = Deferred(result) ? BlobRepairState.Deferred : BlobRepairState.RecoveryRequired,
                Attempts = job.Attempts, ErrorCode = VerificationCode(result) };
            await SaveRepairAsync(repair, fence, ct).ConfigureAwait(false);
            throw new BlobVerificationException(repair.ErrorCode!);
        }
        if (result == BlobVerificationResult.InvalidContent || repair is not null)
        {
            repair = (repair is null || repair.State == BlobRepairState.Completed ? NewRepair(job) : repair)
                with { State = BlobRepairState.Downloading, Attempts = job.Attempts, ErrorCode = null, NextAttemptUtc = null };
            await SaveRepairAsync(repair, fence, ct).ConfigureAwait(false);
        }
        var started = _options.TimeProvider.GetTimestamp();
        try
        {
            if (result == BlobVerificationResult.InvalidContent)
            {
                await _transfer.DownloadReplacementAsync(managed, job.Sha256, job.Size,
                    async token =>
                    {
                        if (_options.AuthorizePromotionAsync is { } authorize && !await authorize(job.Sha256, job.Size, token).ConfigureAwait(false))
                            throw new BlobVerificationException("authorization-required");
                        await SaveRepairAsync(repair! with { State = BlobRepairState.Promoting }, fence, token).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
            }
            else await _transfer.DownloadAsync(_cache, job.Sha256, job.Size, ct).ConfigureAwait(false);
        }
        finally { if (repair is not null) RepairDuration.Record(_options.TimeProvider.GetElapsedTime(started).TotalSeconds); }
    }

    private async Task ReconcileRepairAsync(BlobTransferJob job, OrchestrationFence fence)
    {
        if (job.Direction != BlobTransferDirection.Download) return;
        var repair = await GetRepairAsync(job.Sha256).ConfigureAwait(false);
        if (repair is null) return;
        var state = job.State == BlobTransferState.Complete ? BlobRepairState.Completed
            : job.State != BlobTransferState.Failed || job.ErrorCode is "capacity-required" or "key-locked" or "key-unavailable" or "io-failure" or "authorization-required"
                ? BlobRepairState.Deferred : BlobRepairState.RecoveryRequired;
        await SaveRepairAsync(repair with { State = state, Attempts = job.Attempts, NextAttemptUtc = job.NextAttemptUtc, ErrorCode = job.ErrorCode }, fence, CancellationToken.None).ConfigureAwait(false);
    }

    private static bool Deferred(BlobVerificationResult result) => result is BlobVerificationResult.KeyLocked or BlobVerificationResult.KeyUnavailable or BlobVerificationResult.IoFailure;
    private static string VerificationCode(BlobVerificationResult result) => result switch
    {
        BlobVerificationResult.Missing => "missing-content",
        BlobVerificationResult.InvalidContent => "invalid-content",
        BlobVerificationResult.KeyLocked => "key-locked",
        BlobVerificationResult.KeyUnavailable => "key-unavailable",
        BlobVerificationResult.AuthenticationFailed => "authentication-failed",
        BlobVerificationResult.UnsupportedFormat => "unsupported-format",
        _ => "io-failure",
    };
    private sealed class BlobVerificationException(string code) : Exception("Content verification requires recovery.")
    { public string Code { get; } = code; }
}

[JsonSerializable(typeof(BlobRepairJob))]
internal partial class RepairJson : JsonSerializerContext;
