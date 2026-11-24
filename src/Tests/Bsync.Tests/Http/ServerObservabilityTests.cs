using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Bsync.Protocol;
using Bsync.Server;
using Bsync.Server.AspNetCore;
using Bsync.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Bsync.Tests.Http;

/// <summary>Phase 10: server-side logs and metrics (docs/guide.md#observability).</summary>
public sealed class ServerObservabilityTests
{
    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Category, string Message, Exception? Error)> Entries { get; } = new();

        private string _category = string.Empty;

        public ILogger CreateLogger(string categoryName) => new Logs { _category = categoryName, Sink = Entries };

        private ConcurrentQueue<(LogLevel, string, string, Exception?)>? Sink { get; init; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Sink?.Enqueue((logLevel, _category, formatter(state, exception), exception));

        public void Dispose()
        {
        }
    }

    /// <summary>An authority whose push fails unexpectedly (a database outage, a bug).</summary>
    private sealed class Broken(InMemorySyncServerRef inner) : ISyncAuthority<Note>
    {
        public AuthorityLimits Limits => inner.Server.Limits;

        public Task<PullResult<Note>> PullAsync(SyncCallContext context, PullRequest request, CancellationToken cancellationToken = default) =>
            inner.Server.PullAsync(context, request, cancellationToken);

        public Task<PushResult<Note>> PushAsync(SyncCallContext context, PushRequest<Note> request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("connection to the database lost: secret connection string");
    }

    [Fact(DisplayName = "I08: an unexpected authority failure is logged and answered as a retryable 503, without leaking details")]
    public async Task AuthorityFailureIsRetryable()
    {
        var logs = new Logs();
        await using var host = await SyncTestHost.StartAsync(new Broken(InMemorySyncServerRef.Create()), logs: logs);

        var error = await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport().PushAsync(new PushRequest<Note>(
            [new PushOperation<Note>(Guid.NewGuid().ToString("N"), "n1", null, new Note { Id = "n1", Title = "body text" })])));

        Assert.Equal((SyncErrorCodes.Unavailable, true, 503), (error.ErrorCode, error.IsTransient, error.StatusCode));
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        var failure = Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal("Bsync.Server", failure.Category);
        Assert.IsType<InvalidOperationException>(failure.Error);
        Assert.DoesNotContain(logs.Entries, e => e.Message.Contains("body text", StringComparison.Ordinal) || e.Message.Contains("n1", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Requests and push outcomes are counted by collection, endpoint and code; refusals are logged with their code")]
    public async Task RequestsAreCounted()
    {
        var measurements = new ConcurrentQueue<(string Instrument, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SyncEndpoints.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                copy[tag.Key] = tag.Value;
            }

            measurements.Enqueue((instrument.Name, copy));
        });
        listener.Start();

        var logs = new Logs();
        var collection = "notes";
        await using var host = await SyncTestHost.StartAsync(new RefAuthority(InMemorySyncServerRef.Create(Clocks.SystemPhysicalClock.Instance)), logs: logs);
        var engine = new SyncEngine<Note>(new Storage.InMemoryLocalStore<Note>(NoteJson.Clone), host.Transport(), new Clocks.HybridLogicalClock("c"), NoteJson.Clone);
        await engine.WriteAsync(new Note { Id = "n1" });
        await engine.SyncAsync();
        await Assert.ThrowsAsync<SyncTransportException>(() => host.Transport(schemaId: "notes-v0").PullAsync(new PullRequest(Checkpoint.Start, 1)));

        // The listener is process-wide: other tests' hosts also map "notes", so look for at least these measurements.
        Assert.Contains(measurements, m => m.Instrument == "bsync.server.requests" && Equals(m.Tags["bsync.endpoint"], "pull") && Equals(m.Tags["bsync.result"], "ok") && Equals(m.Tags["bsync.collection"], collection));
        Assert.Contains(measurements, m => m.Instrument == "bsync.server.requests" && Equals(m.Tags["bsync.endpoint"], "push") && Equals(m.Tags["bsync.result"], "ok"));
        Assert.Contains(measurements, m => m.Instrument == "bsync.server.requests" && Equals(m.Tags["bsync.result"], SyncErrorCodes.UpgradeRequired));
        Assert.Contains(measurements, m => m.Instrument == "bsync.server.push.operations" && Equals(m.Tags["bsync.outcome"], nameof(PushOutcomeKind.Accepted)));
        Assert.Contains(logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains(SyncErrorCodes.UpgradeRequired, StringComparison.Ordinal));
    }
}
