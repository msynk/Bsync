using System.Diagnostics.Metrics;
using System.Text.Json;
using Bsync.Diagnostics;
using Bsync.Storage;
using Xunit;

namespace Bsync.Tests;

[CollectionDefinition("Offline metrics", DisableParallelization = true)]
public sealed class OfflineMetricsCollection;

[Collection("Offline metrics")]
public sealed class OfflineMetricsTests
{
    [Fact]
    public async Task BoundedSnapshotsExposeAgeAndUnknownsWithoutIdentifiersAndUnregisterOnClose()
    {
        await using var store = new InMemoryOrchestrationStore();
        var created = JsonSerializer.Serialize(DateTimeOffset.UtcNow.AddMinutes(-10));
        await store.TryWriteAsync([
            new("transfers/private-one", null, "{\"SchemaVersion\":1,\"State\":0,\"Size\":100,\"CreatedUtc\":" + created + "}"),
            new("transfers/private-two", null, """{"SchemaVersion":1,"State":5,"Size":20}"""),
        ]);
        using var listener = new MeterListener();
        var observations = new Dictionary<string, long>();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name == "Bsync" && instrument.Name.StartsWith("bsync.transfer.", StringComparison.Ordinal)) observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            Assert.Empty(tags.ToArray());
            observations[instrument.Name] = value;
        });
        listener.Start();
        var metrics = new OfflineMetrics(store);
        try
        {
            var report = await metrics.RefreshAsync(1);
            Assert.True(report.Truncated);
            listener.RecordObservableInstruments();
            Assert.Equal(1, observations["bsync.transfer.backlog"]);
            report = await metrics.RefreshAsync(10);
            Assert.False(report.Truncated);
            Assert.Equal(1, report.Counts["transfer.unknown_age"]);
            // Reports are caller-owned: mutating the returned dictionary cannot change the metrics snapshot.
            ((Dictionary<string, long>)report.Counts)["transfer.pending"] = 999;
            listener.RecordObservableInstruments();
            Assert.Equal(2, observations["bsync.transfer.backlog"]);
            Assert.Equal(120, observations["bsync.transfer.pending_expected_bytes"]);
            Assert.True(observations["bsync.transfer.oldest_known_age"] >= 600);
            Assert.Equal(1, observations["bsync.transfer.unknown_age"]);
        }
        finally { metrics.Dispose(); }
        listener.RecordObservableInstruments();
        Assert.Equal(0, observations["bsync.transfer.backlog"]);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => metrics.RefreshAsync());
    }
}
