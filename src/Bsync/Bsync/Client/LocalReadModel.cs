using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Storage;

namespace Bsync.Client;

/// <summary>A derived row. Payload serialization and its schema belong to the host; credentials must not be stored.</summary>
/// <param name="Id">Unique identity within a generation, at most 256 characters.</param>
/// <param name="Payload">Host-serialized projected data, at most 262144 characters.</param>
public sealed record ReadModelRow(string Id, string Payload);

/// <summary>An immutable, completely applied generation. SourceVersion identifies a host-captured stable snapshot, not current freshness.</summary>
/// <param name="Generation">Opaque local generation.</param>
/// <param name="SchemaVersion">Host projection schema.</param>
/// <param name="SourceVersion">Host source epoch and snapshot boundary.</param>
/// <param name="RowCount">Number of committed rows.</param>
/// <param name="CompletedAt">Activation time, not a source cursor.</param>
public sealed record ReadModelGeneration(string Generation, string SchemaVersion, string SourceVersion, long RowCount, DateTimeOffset CompletedAt);

/// <summary>Durable progress; an interrupted rebuild remains visible and does not replace Active.</summary>
/// <param name="FormatVersion">Library metadata format.</param>
/// <param name="Active">Last complete generation, possibly stale.</param>
/// <param name="BuildingGeneration">Incomplete generation, if any.</param>
/// <param name="AppliedRows">Rows durably committed to the incomplete generation.</param>
public sealed record ReadModelStatus(int FormatVersion, ReadModelGeneration? Active, string? BuildingGeneration, long AppliedRows);

/// <summary>One bounded immutable-generation page. Pass Continuation to the same snapshot only.</summary>
/// <param name="Rows">Derived rows in opaque, deterministic key order.</param>
/// <param name="Continuation">Opaque continuation; null when enumeration is complete.</param>
public sealed record ReadModelPage(IReadOnlyList<ReadModelRow> Rows, string? Continuation);

/// <summary>
/// Full-reconciliation read model over account-scoped metadata. The host must supply every row from one stable source
/// snapshot (including a source epoch), obtained by a real snapshot transaction or by quiescing all source writers.
/// Ordinary live collection pagination is not a snapshot. Every rebuild starts a fresh generation; absent rows are
/// removed by activation. No transient notification or uncommitted checkpoint is used as correctness evidence.
/// Generations are retained until account deletion, allowing old readers to finish without mixing revisions.
/// </summary>
public sealed class LocalReadModel
{
    private readonly IOrchestrationStore _store;
    private readonly string _prefix;
    private readonly TimeProvider _time;

    /// <summary>Creates an account-scoped projection. Use a separate model name for a separately authorized dataset.</summary>
    public LocalReadModel(IOrchestrationStore store, string name, TimeProvider? timeProvider = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ValidateLabel(name, nameof(name));
        _prefix = "read-model/" + Hash(name) + "/";
        _time = timeProvider ?? TimeProvider.System;
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(value)));
    private static void ValidateLabel(string value, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (value.Length > 256) throw new ArgumentException("Identity/version exceeds 256 characters.", parameter);
        _ = new UTF8Encoding(false, true).GetByteCount(value);
    }
    private static string Encode(ReadModelStatus value) => JsonSerializer.Serialize(value, ReadModelJson.Default.ReadModelStatus);
    private static ReadModelStatus Decode(OrchestrationRecord? record)
    {
        if (record is null) return new(1, null, null, 0);
        var value = JsonSerializer.Deserialize(record.Payload, ReadModelJson.Default.ReadModelStatus)
            ?? throw new InvalidDataException("Missing read-model metadata.");
        if (value.FormatVersion != 1) throw new LocalStoreUnavailableException("outdated", "Unsupported read-model format; data was retained.");
        if (value.AppliedRows < 0) throw new InvalidDataException("Invalid read-model progress.");
        return value;
    }

    /// <summary>Reports the last complete generation and any unfinished build. No generation is asserted to be current.</summary>
    public async Task<ReadModelStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Decode(await _store.ReadAsync(_prefix + "state", cancellationToken).ConfigureAwait(false));

    /// <summary>Opens the last complete generation without networking; returns null before first activation.</summary>
    public async Task<ReadModelSnapshot?> OpenActiveAsync(CancellationToken cancellationToken = default)
    {
        var active = (await GetStatusAsync(cancellationToken).ConfigureAwait(false)).Active;
        return active is null ? null : new ReadModelSnapshot(_store, _prefix + active.Generation + "/", active);
    }

    /// <summary>
    /// Rebuilds from a host-owned stable snapshot within a cooperative budget. Returns false on competing ownership,
    /// deadline or lease loss. Cancellation/errors leave the previous active generation readable and staging progress
    /// recoverable by restarting a full reconciliation. Source enumeration may be repeated and must have no side effects.
    /// Rows and their applied count commit together; only a fully enumerated generation can activate.
    /// </summary>
    public async Task<bool> RebuildAsync(string schemaVersion, string sourceVersion, IAsyncEnumerable<ReadModelRow> rows,
        TimeSpan budget, CancellationToken cancellationToken = default)
    {
        ValidateLabel(schemaVersion, nameof(schemaVersion)); ValidateLabel(sourceVersion, nameof(sourceVersion));
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(budget, TimeSpan.Zero);
        await using var lease = await ExecutionLease.TryAcquireAsync(_store, _prefix + "owner", TimeSpan.FromSeconds(30), _time, cancellationToken).ConfigureAwait(false);
        if (lease is null) return false;
        using var deadline = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, lease.Lost);
        var token = linked.Token;
        try
        {
            var stateKey = _prefix + "state";
            var record = await _store.ReadAsync(stateKey, token).ConfigureAwait(false);
            var generation = Guid.NewGuid().ToString("N");
            var status = Decode(record) with { BuildingGeneration = generation, AppliedRows = 0 };
            if (!await _store.TryWriteAsync([new(stateKey, record?.Revision, Encode(status))], lease.Fence, token).ConfigureAwait(false)) return false;
            record = (await _store.ReadAsync(stateKey, token).ConfigureAwait(false))!;
            var batch = new List<OrchestrationMutation>();
            var characters = 0;
            await foreach (var row in rows.WithCancellation(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                ArgumentNullException.ThrowIfNull(row);
                ValidateLabel(row.Id, nameof(rows));
                ArgumentNullException.ThrowIfNull(row.Payload);
                if (row.Payload.Length > 262144) throw new ArgumentException("Derived row exceeds 262144 characters.", nameof(rows));
                var payload = JsonSerializer.Serialize(row, ReadModelJson.Default.ReadModelRow);
                // JSON escaping can expand text. Enforce the actual provider payload limit before staging it.
                if (payload.Length > 524288) throw new ArgumentException("Serialized derived row exceeds the metadata record limit.", nameof(rows));
                var key = _prefix + generation + "/" + Hash(row.Id);
                if (batch.Any(m => m.Key == key)) throw new InvalidDataException("Duplicate read-model row identity.");
                batch.Add(new(key, null, payload)); characters += payload.Length;
                if (batch.Count == 128 || characters >= 4 * 1024 * 1024)
                {
                    if (!await FlushAsync().ConfigureAwait(false)) return false;
                }
            }
            if (batch.Count > 0 && !await FlushAsync().ConfigureAwait(false)) return false;
            token.ThrowIfCancellationRequested();
            status = status with { Active = new(generation, schemaVersion, sourceVersion, status.AppliedRows, _time.GetUtcNow()), BuildingGeneration = null, AppliedRows = 0 };
            return await _store.TryWriteAsync([new(stateKey, record.Revision, Encode(status))], lease.Fence, token).ConfigureAwait(false);

            async Task<bool> FlushAsync()
            {
                // An existing row means a duplicate source ID. Never silently merge an incomplete snapshot.
                foreach (var mutation in batch)
                    if (await _store.ReadAsync(mutation.Key, token).ConfigureAwait(false) is not null)
                        throw new InvalidDataException("Duplicate read-model row identity.");
                status = status with { AppliedRows = checked(status.AppliedRows + batch.Count) };
                batch.Add(new(stateKey, record.Revision, Encode(status)));
                if (!await _store.TryWriteAsync(batch, lease.Fence, token).ConfigureAwait(false)) return false;
                record = (await _store.ReadAsync(stateKey, token).ConfigureAwait(false))!;
                batch.Clear(); characters = 0;
                return true;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }
}

/// <summary>An immutable generation reader. Retained generations remain readable until coordinated account deletion.</summary>
public sealed class ReadModelSnapshot
{
    private readonly IOrchestrationStore _store;
    private readonly string _prefix;
    internal ReadModelSnapshot(IOrchestrationStore store, string prefix, ReadModelGeneration generation)
    { _store = store; _prefix = prefix; Generation = generation; }

    /// <summary>The schema and source boundary actually represented by these rows.</summary>
    public ReadModelGeneration Generation { get; }

    /// <summary>Reads at most 1000 rows in opaque key order; continuation must originate from this generation.</summary>
    public async Task<ReadModelPage> ReadPageAsync(int limit, string? continuation = null, CancellationToken cancellationToken = default)
    {
        OrchestrationValidation.Page(_prefix, continuation, limit);
        if (continuation is not null && !continuation.StartsWith(_prefix, StringComparison.Ordinal))
            throw new ArgumentException("Continuation belongs to another generation.", nameof(continuation));
        var records = await _store.ReadPageAsync(_prefix, continuation, limit, cancellationToken).ConfigureAwait(false);
        var rows = records.Select(r => JsonSerializer.Deserialize(r.Payload, ReadModelJson.Default.ReadModelRow)
            ?? throw new InvalidDataException("Missing derived row.")).ToArray();
        return new(rows, records.Count == limit ? records[^1].Key : null);
    }
}

[JsonSerializable(typeof(ReadModelStatus))]
[JsonSerializable(typeof(ReadModelRow))]
internal partial class ReadModelJson : JsonSerializerContext;
