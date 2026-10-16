using System.Text.Json;
using Bsync.Diagnostics;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

public sealed class OfflineDiagnosticExportTests
{
    [Fact]
    public async Task ExportAllowlistsCountsAndNeverCopiesIdentifiersPayloadsOrUnknownStates()
    {
        await using var store = new InMemoryOrchestrationStore();
        await store.TryWriteAsync([
            new("transfers/private-endpoint/private-hash", null, """{"SchemaVersion":1,"State":3,"ErrorCode":"Bearer secret","Sha256":"private"}"""),
            new("cache/private-hash", null, """{"SchemaVersion":1,"Bytes":42,"Owners":["private-owner"]}"""),
            new("workflow/private-action", null, """{"SchemaVersion":1,"State":"running","Steps":["private-business-data"]}"""),
            new("workflow/private-malformed", null, """{"SchemaVersion":1,"State":"secret"}"""),
            new("background/private-account", null, """{"SchemaVersion":99,"LastOutcome":0,"Pending":true,"secret":"secret"}"""),
            new("custom/private", null, "secret"),
        ]);
        var report = await OfflineDiagnosticExport.CaptureAsync(store);
        Assert.Equal(6, report.RecordsScanned); Assert.False(report.Truncated); Assert.Equal(2, report.UnsupportedRecords);
        Assert.Equal(1, report.Counts["transfer.WaitingForCredentials"]);
        Assert.Equal(42, report.Counts["cache.pinned_reserved_bytes"]);
        Assert.Equal(1, report.Counts["workflow.running"]);
        var json = await OfflineDiagnosticExport.ExportJsonAsync(store);
        Assert.DoesNotContain("private", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bearer", json, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("FormatVersion").GetInt32());
    }

    [Fact]
    public async Task BoundsExplicitlyLabelPartialCountsAndMalformedPayloadsAreContentFree()
    {
        await using var store = new InMemoryOrchestrationStore();
        await store.TryWriteAsync([new("cache/a", null, "not json secret"), new("cache/b", null, "{\"SchemaVersion\":1}")]);
        var partial = await OfflineDiagnosticExport.CaptureAsync(store, 1);
        Assert.True(partial.Truncated); Assert.Equal(1, partial.RecordsScanned); Assert.Equal(1, partial.UnsupportedRecords);
        var full = await OfflineDiagnosticExport.CaptureAsync(store, 2);
        Assert.False(full.Truncated); Assert.Equal(2, full.UnsupportedRecords); Assert.Empty(full.Counts);
    }
}
