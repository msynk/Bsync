# Observability

## Redacted offline metadata export

`await OfflineDiagnosticExport.ExportJsonAsync(accountMetadata, recordLimit: 1000)` returns source-generated JSON
containing only allowlisted aggregate counts: transfer states, workflow states, background outcomes, cache reservations
and pins, and read-model build/active counts. It omits account/document/blob identities, endpoints, pin-owner strings,
payloads, source versions and error text. Unknown namespaces are not parsed; malformed/newer recognized records are
counted without copying their content. This is separate from the explicit, content-bearing work-export API.

The scan is bounded to 1..10000 records and reports `Truncated` when partial. Counts are a live metadata observation,
not a transactional snapshot, byte inventory, source freshness proof or package-readiness check. Read-model rows can
consume the scan budget even though their payloads are omitted. Capture a settled report with account work stopped
when needed. SQLite/browser storage access failures still surface to the caller; the exporter never wipes data or
contacts a server. Treat even aggregate business volume as application data when deciding where to send a report.

## Runtime telemetry

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
| `bsync.blob.bytes` | counter | By | `bsync.direction` (`upload`, `download`) | Attachment bytes sent or received by `HttpBlobTransfer` (task F1), including presigned uploads and redirected downloads. |
| `bsync.blob.transfers` | counter | transfers | `bsync.direction`, `bsync.result` (`complete`, `failed`, `corrupt`) | Attachment transfers by result. `corrupt` means the bytes did not match the hash and were discarded; `failed` covers network errors and refusals (retried by the caller). |

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
| `bsync.server.blob.bytes` | `bsync.direction` (`upload`, `download`) | Attachment bytes received in chunks and served by the blob routes. Presigned uploads and downloads (object storage) do not pass through the server and are not counted. |
| `bsync.server.blob.requests` | `bsync.endpoint` (`start`, `append`, `finish`, `read`), `bsync.result` (`ok`, `redirect`, `offset-mismatch`, a code such as `blob-hash-mismatch` or `blob-too-large`, or the status code) | Every blob request. |

Blob metrics carry no hashes, file names or scopes.

Logs:

- `SyncRequestRefused` (1) is logged for every problem response, with collection, endpoint, status, code and
  reset reason. Its level is Information for 4xx and Warning for 5xx.
- `SyncAuthorityFailed` (2) is logged at Error, with the exception, when the authority throws unexpectedly. The
  client receives only a generic `503 unavailable`, which it retries.

ASP.NET Core's own `http.server.*` metrics and request logs cover transport-level details.

## Replica acknowledgements (optional audit)

With `SyncEndpointOptions.ReplicaAudit` set, the endpoints record, for every pull that leaves nothing more to fetch,
which checkpoint a replica reached: replica (its clock node id, sent as the optional `replica` member of pull requests
by 0.2.0 replicas), caller name, collection, scope, checkpoint and time. A repeated checkpoint is recorded once.
`InMemorySyncReplicaAudit` keeps them in memory; `SqlServerReplicaAudit` in the table
`bsync.replica_acknowledgements` (created on first use; purge it by age like any log table). Query with
`GetAsync(replica)`. This proves which revision of the content a device held, and when. Replicas older than 0.2.0 send
no id and are not recorded. Tested: `ReplicaAuditTests`, `SqlServerAuthorityTests.ReplicaAudit`.

## What to alert on

Collection query counters `bsync.query.materialized` and `bsync.query.returned` use only the low-cardinality
`bsync.query.mode` tag (`id-page`, `index-page`, `full-sort`, `full-count`). Materialized includes residual predicate
misses and skipped records; a page fetch may materialize more than its returned limit. A high materialized/returned
ratio identifies scan-heavy UI queries. These counters do not report database-internal index entries visited, and
contain no account IDs, hashes, document IDs, predicate text or document content.

Custom `Order` reads/sorts the full collection. Default ID and declared-index paths read keyset pages of 200 and stop
after the requested number of matches, but a residual predicate or large Skip can still visit the entire range.
Pages are not a cross-call snapshot: inserts, deletes and index-key edits between reads can change page membership.
Use immutable package snapshots or a separately captured source generation when a stable read revision is required.

- `bsync.queue.oldest_age` growing on devices you collect telemetry from: uploads are not getting through.
- `bsync.push.operations{outcome="rejected"}` by `error.type`: validation or permission problems, or
  `clock-skew` on devices with a wrong clock.
- `bsync.conflicts{decision="defer"}`: conflicts waiting for users.
- `bsync.resets` by reason, which is expected after a restore or a permission change and suspicious
  otherwise.
- `bsync.server.requests{result="unavailable"}` together with `SyncAuthorityFailed` logs.
- `bsync.blob.transfers{result="corrupt"}` or `bsync.server.blob.requests{result="blob-hash-mismatch"}`: content
  damaged in transit or on a device's disk.

## Evidence

- `DiagnosticsTests`: outcome, conflict, pull and reset counters, spans, queue gauges, error tagging, and the
  absence of document data in tags.
- `ServerObservabilityTests`: request and operation counters, refusal logs, and the retryable 503 without
  leaked details.
- `BlazorIntegrationTests.UnexpectedFailureIsReportedAndRecovers` and `RecipeUsesContainerLogging`.
- `BlobRouteTests.TransfersAreMeasured`: client and server blob bytes and transfers by direction, endpoint and
  result (including a hash mismatch), with no content hash in any tag.

- `OpenTelemetryExportTests`: the documented `AddSource`/`AddMeter` wiring exports the spans and the client and
  server metrics through the OpenTelemetry SDK (1.18.0, in-memory exporter).

Not verified: an OTLP collector over the network, and browser-side telemetry export.

## Managed offline metrics (unreleased)

Create one `OfflineMetrics` per distinct account metadata store in a process and periodically call `RefreshAsync` with
a bounded record limit. Dispose on account close. Metric callbacks read cached aggregates and never access storage.
Multiple registrations for the same store double-count it; metadata in a separate application catalog needs its own
registration. No metric includes account IDs, content hashes, endpoints, package names or user-defined tags.

| Metric | Meaning |
|---|---|
| `bsync.transfer.backlog` | Nonterminal jobs, including paused/failed jobs requiring intervention |
| `bsync.transfer.pending_expected_bytes` | Full expected job sizes, not remaining bytes or deduplicated storage |
| `bsync.transfer.oldest_known_age`, `bsync.transfer.unknown_age` | Oldest timestamped pending job and number with unknown original enqueue time |
| `bsync.cache.reserved_bytes`, `bsync.cache.pinned_reserved_bytes` | Conservative durable metadata reservations |
| `bsync.cache.capacity_refusals` | Operations refused by the hard storage budget |
| `bsync.blob.verified_bytes` | Locally verified download bytes or authority-acknowledged upload bytes; repeated verification counts again |
| `bsync.package.last_verified{bsync.readiness}` | Last persisted verification observations, not proof of present completeness |
| `bsync.background.pending`, `bsync.background.last_outcome{bsync.outcome}` | Durable request count and last outcomes, not execution totals |
| `bsync.offline.snapshot.oldest_age`, `bsync.offline.snapshot.partial` | Snapshot staleness and count of truncated/unsupported snapshots |

Always collect the snapshot-age/partial and unknown-age metrics with backlog metrics. Old schema-1 transfer records
without `CreatedUtc` retain unknown age rather than inventing a timestamp. The redacted export contains the same
allowlisted aggregates. These are live bounded scans; concurrent changes can make counts approximate.

The Tasks outbox recipe emits `bsync.sample.publication.lag` on meter `Bsync.Samples.Tasks`, after commit, with no tags.
Coalescing preserves the oldest unconsumed request time, so repeated updates do not hide backlog age. The histogram
measures completed publications only: alert separately on retained/poisoned outbox rows. It cannot measure unregistered
domain writes or a dispatcher that never runs. Full database capture lag remains outside this recipe.
