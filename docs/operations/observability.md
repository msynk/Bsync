# Observability

Bsync emits traces and metrics through the .NET built-ins (`ActivitySource`, `Meter`). It emits logs
through `ILogger` in the packages that already depend on Microsoft.Extensions: the Blazor session and the
ASP.NET Core endpoints. The core package takes no logging dependency.

Nothing records document contents, document ids or account names. Tags carry counts, outcome kinds,
error codes, collection names and the engine's diagnostics name.

## Wiring it up

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(SyncDiagnostics.SourceName))                        // "Bsync"
    .WithMetrics(m => m.AddMeter(SyncDiagnostics.SourceName, SyncEndpoints.MeterName)); // + "Bsync.Server"
```

Give each engine a low-cardinality name with `SyncOptions<T>.DiagnosticsName`, for example the collection
name. The default is the document type name.

## Client engine (`Bsync` meter and activity source)

| Instrument | Type | Unit | Tags | Meaning |
|---|---|---|---|---|
| `bsync.push.operations` | counter | operations | `bsync.name`, `bsync.outcome` (`accepted`, `conflict`, `rejected`, `retry-later`, `missing`), `bsync.duplicate`, `error.type` for rejections | Every operation sent and what the server answered. `missing` means the response omitted it (resent later). |
| `bsync.pull.changes` | counter | changes | `bsync.name` | Server changes applied locally. |
| `bsync.conflicts` | counter | conflicts | `bsync.name`, `bsync.decision` (`use-master`, `use-resolved`, `keep-fork`, `defer`) | Conflict handler decisions. |
| `bsync.resets` | counter | resets | `bsync.name`, `bsync.reason` (`epoch`, `scope-changed`, `expired`) | Replica resets. |
| `bsync.run.duration` | histogram | s | `bsync.name`, `bsync.operation` (`sync`, `pull`, `push`), `bsync.result` (`complete`, `incomplete`, `error`), `error.type` | Duration of replication runs. |
| `bsync.queue.depth` | observable gauge | documents | `bsync.name` | Documents with unconfirmed local changes, as of the engine's last run. |
| `bsync.queue.oldest_age` | observable gauge | s | `bsync.name` | Age of the oldest change waiting for upload, by its authoring time, as of the last run. |
| `bsync.issues` | observable gauge | documents | `bsync.name`, `bsync.issue` (`conflict`, `rejected`, `blocked`) | Documents waiting for a person's decision, as of the last run: kept conflicts, rejected changes, and group members parked because another member failed (disjoint; their sum is the total). |

The queue gauges cost two small store reads per run. They are measured only while a listener subscribes to
them.

Spans: `bsync.sync`, `bsync.pull` and `bsync.push`, with the tags `bsync.pulled`,
`pushed`, `conflicts`, `rejected`, `deferred`, `reset` and `result`. A failed run has error status and
`error.type` (the transport error code, `cancelled`, or the exception type). A reset adds a
`bsync.reset` event with its reason.

## Session (`Bsync.SyncSession` log category)

| Event | Level | When |
|---|---|---|
| `SyncStateChanged` (1) | Warning for `Offline` and `AttentionRequired`; Debug for `Syncing` and `Synced`; Information otherwise | Every state change, with the pending count and the status detail (codes only). |
| `SyncProtocolError` (2) | Error | The server sent an invalid response. |
| `SyncUnexpectedError` (3) | Error | Any other failure. The session reports `AttentionRequired`, keeps local work, and retries after the maximum backoff or when asked. |

The DI recipes (`AddLocalSyncCollection`, `AddBrowserSyncCollection`) use the container's logger factory. Set
`SyncSessionOptions.Logger` to override it.

## Server endpoints (`Bsync.Server` meter and log category)

| Instrument | Tags | Meaning |
|---|---|---|
| `bsync.server.requests` | `bsync.collection`, `bsync.endpoint` (`pull`, `push`, `hints`), `bsync.result` (`ok` or a problem code) | Every protocol request. |
| `bsync.server.push.operations` | `bsync.collection`, `bsync.outcome`, `bsync.duplicate` | Operations decided by the authority. |

Logs:

- `SyncRequestRefused` (1) is logged for every problem response, with collection, endpoint, status, code and
  reset reason. Its level is Information for 4xx and Warning for 5xx.
- `SyncAuthorityFailed` (2) is logged at Error, with the exception, when the authority throws unexpectedly. The
  client receives only a generic `503 unavailable`, which it retries.

ASP.NET Core's own `http.server.*` metrics and request logs cover transport-level details.

## Replica acknowledgements (optional audit)

With `SyncEndpointOptions.ReplicaAudit` set, the endpoints record, for every pull that leaves nothing more to fetch,
which checkpoint a replica reached: replica (its clock node id, sent as the optional `replica` member of pull requests
by 0.3.0 replicas), caller name, collection, scope, checkpoint and time. A repeated checkpoint is recorded once.
`InMemorySyncReplicaAudit` keeps them in memory; `SqlServerReplicaAudit` in the table
`bsync.replica_acknowledgements` (created on first use; purge it by age like any log table). Query with
`GetAsync(replica)`. This proves which revision of the content a device held, and when. Replicas older than 0.3.0 send
no id and are not recorded. Tested: `ReplicaAuditTests`, `SqlServerAuthorityTests.ReplicaAudit`.

## What to alert on

- `bsync.queue.oldest_age` growing on devices you collect telemetry from: uploads are not getting through.
- `bsync.push.operations{outcome="rejected"}` by `error.type`: validation or permission problems, or
  `clock-skew` on devices with a wrong clock.
- `bsync.conflicts{decision="defer"}`: conflicts waiting for users.
- `bsync.resets` by reason, which is expected after a restore or a permission change and suspicious
  otherwise.
- `bsync.server.requests{result="unavailable"}` together with `SyncAuthorityFailed` logs.

## Evidence

- `DiagnosticsTests`: outcome, conflict, pull and reset counters, spans, queue gauges, error tagging, and the
  absence of document data in tags.
- `ServerObservabilityTests`: request and operation counters, refusal logs, and the retryable 503 without
  leaked details.
- `BlazorIntegrationTests.UnexpectedFailureIsReportedAndRecovers` and `RecipeUsesContainerLogging`.

- `OpenTelemetryExportTests`: the documented `AddSource`/`AddMeter` wiring exports the spans and the client and
  server metrics through the OpenTelemetry SDK (1.18.0, in-memory exporter).

Not verified: an OTLP collector over the network, and browser-side telemetry export.
