using System.Text.Json;
using System.Text.Json.Serialization;
using Bsync.Blobs;
using Bsync.Client;
using Bsync.Storage;

namespace Bsync.Diagnostics;

/// <summary>Redacted aggregate metadata snapshot. Counts may change during enumeration and never prove package readiness.</summary>
/// <param name="FormatVersion">Diagnostic format.</param>
/// <param name="CapturedAt">Report creation time.</param>
/// <param name="RecordsScanned">Bounded number of inspected records.</param>
/// <param name="Truncated">More records existed than the requested limit; counts are partial.</param>
/// <param name="UnsupportedRecords">Recognized feature records that were malformed or used an unsupported schema.</param>
/// <param name="Counts">Fixed, library-defined names and aggregate integer values; never record IDs or payloads.</param>
public sealed record OfflineDiagnosticReport(int FormatVersion, DateTimeOffset CapturedAt, int RecordsScanned,
    bool Truncated, int UnsupportedRecords, IReadOnlyDictionary<string, long> Counts);

/// <summary>Exports only allowlisted aggregate counts. This is deliberately separate from content-bearing work export.</summary>
public static class OfflineDiagnosticExport
{
    /// <summary>
    /// Scans at most 10000 records from the supplied account store/catalog. Omits account/content identities, endpoint
    /// URLs, pin owners, source versions, payloads, error text and credentials, including unknown feature records.
    /// Enumeration is live; stop writers for a settled report. It performs no network or blob verification.
    /// </summary>
    public static async Task<OfflineDiagnosticReport> CaptureAsync(IOrchestrationStore store, int recordLimit = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (recordLimit is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(recordLimit));
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var capturedAt = DateTimeOffset.UtcNow;
        var scanned = 0; var unsupported = 0; string? after = null; var exhausted = false;
        void Add(string name, long value = 1) => counts[name] = checked(counts.GetValueOrDefault(name) + value);
        while (scanned < recordLimit)
        {
            var limit = Math.Min(1000, recordLimit - scanned);
            var page = await store.ReadPageAsync("", after, limit, cancellationToken).ConfigureAwait(false);
            foreach (var record in page)
            {
                scanned++; after = record.Key;
                // Unknown namespaces are not parsed: even their names may contain application data.
                var kind = record.Key.StartsWith("transfers/", StringComparison.Ordinal) ? "transfer"
                    : record.Key.StartsWith("cache/", StringComparison.Ordinal) ? "cache"
                    : record.Key.StartsWith("workflow/", StringComparison.Ordinal) ? "workflow"
                    : record.Key.StartsWith("background/", StringComparison.Ordinal) ? "background"
                    : record.Key.StartsWith("read-model/", StringComparison.Ordinal) && record.Key.EndsWith("/state", StringComparison.Ordinal) ? "read-model"
                    : record.Key.StartsWith("packages/", StringComparison.Ordinal) && record.Key.EndsWith("/verification", StringComparison.Ordinal) ? "package"
                    : null;
                if (kind is null) continue;
                try
                {
                    using var document = JsonDocument.Parse(record.Payload);
                    var root = document.RootElement;
                    if (root.GetProperty(kind == "read-model" ? "FormatVersion" : "SchemaVersion").GetInt32() != 1)
                    { unsupported++; continue; }
                    switch (kind)
                    {
                        case "transfer":
                            var state = (BlobTransferState)root.GetProperty("State").GetInt32();
                            if (!Enum.IsDefined(state)) { unsupported++; break; }
                            Add("transfer." + state);
                            if (state is not (BlobTransferState.Complete or BlobTransferState.Canceled))
                            {
                                Add("transfer.pending");
                                if (root.TryGetProperty("Size", out var size) && size.TryGetInt64(out var length) && length >= 0)
                                    Add("transfer.pending_expected_bytes", length);
                                else Add("transfer.unknown_size");
                                if (root.TryGetProperty("CreatedUtc", out var created) && created.ValueKind == JsonValueKind.String && created.TryGetDateTimeOffset(out var at))
                                    counts["transfer.oldest_known_age_seconds"] = Math.Max(counts.GetValueOrDefault("transfer.oldest_known_age_seconds"), Math.Max(0, (long)(capturedAt - at).TotalSeconds));
                                else Add("transfer.unknown_age");
                            }
                            break;
                        case "cache":
                            var bytes = root.GetProperty("Bytes").GetInt64();
                            var owners = root.GetProperty("Owners").GetArrayLength();
                            if (bytes < 0) { unsupported++; break; }
                            Add("cache.entries"); Add("cache.reserved_bytes", bytes);
                            if (owners > 0) { Add("cache.pinned_entries"); Add("cache.pinned_reserved_bytes", bytes); }
                            break;
                        case "workflow":
                            var workflow = root.GetProperty("State").GetString();
                            if (workflow is not ("pending" or "running" or "failed" or "complete")) { unsupported++; break; }
                            Add("workflow." + workflow);
                            break;
                        case "background":
                            var outcome = (BackgroundWorkOutcome)root.GetProperty("LastOutcome").GetInt32();
                            if (!Enum.IsDefined(outcome)) { unsupported++; break; }
                            var pending = root.GetProperty("Pending").GetBoolean();
                            Add("background." + outcome);
                            if (pending) Add("background.pending");
                            break;
                        case "read-model":
                            var active = root.GetProperty("Active");
                            var building = root.GetProperty("BuildingGeneration");
                            Add("read-model.models");
                            if (active.ValueKind != JsonValueKind.Null) Add("read-model.active");
                            if (building.ValueKind != JsonValueKind.Null) Add("read-model.building");
                            break;
                        case "package":
                            var readiness = (OfflinePackageReadiness)root.GetProperty("Status").GetProperty("Readiness").GetInt32();
                            if (!Enum.IsDefined(readiness)) { unsupported++; break; }
                            Add("package.last_verified." + readiness);
                            break;
                    }
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
                { unsupported++; }
            }
            if (page.Count < limit) { exhausted = true; break; }
        }
        var truncated = !exhausted && (await store.ReadPageAsync("", after, 1, cancellationToken).ConfigureAwait(false)).Count != 0;
        return new(1, capturedAt, scanned, truncated, unsupported, counts);
    }

    /// <summary>Captures and serializes a redacted report using source-generated JSON. Never serializes raw store records.</summary>
    public static async Task<string> ExportJsonAsync(IOrchestrationStore store, int recordLimit = 1000, CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await CaptureAsync(store, recordLimit, cancellationToken).ConfigureAwait(false), OfflineDiagnosticJson.Default.OfflineDiagnosticReport);
}

[JsonSerializable(typeof(OfflineDiagnosticReport))]
internal partial class OfflineDiagnosticJson : JsonSerializerContext;
