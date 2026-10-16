# ADR-023: Full-reconciliation local read-model generations

Status: Implemented portable primitive; host snapshot integration required

## Context

Collection observer notifications are transient. A projection built only from callbacks misses changes while its
process is stopped. Existing live collection pagination is not a snapshot across concurrent writes. Its last page
cannot establish a checkpoint that represents every source mutation.

## Decision

`LocalReadModel` consumes an explicitly captured stable source snapshot and writes a new immutable generation to
the account's orchestration store. The host supplies a projection schema and a source version containing its epoch
and snapshot boundary. Timestamps are diagnostic information, never source cursors.

Each bounded batch commits projected rows and its applied-row count in one CAS transaction, guarded by an execution
fence. Full enumeration is followed by an atomic active-pointer change. Exceptions, deadlines, lease loss and
process interruption preserve the previous active generation. Recovery begins a new full reconciliation rather than
resuming a cursor into a source snapshot that may no longer exist. Duplicate source IDs refuse activation.

Readers bind to one immutable generation. A rebuild replaces the entire dataset, naturally reconciling removals,
regrants, source resets and projection-schema changes. Source conflicts are projected according to the host's explicit
choice of current/base/conflict data; no business resolution is inferred. A generation reports its actual source
version, never an assertion that it is current. An interrupted build remains visible in durable status.

The source must be captured using a real database snapshot transaction, an immutable exported dataset, or a host
gate that quiesces **all** local writers and replication appliers. Merely pausing one UI or a session timer is not
enough. Hosts without such a boundary cannot use this primitive to claim a consistent source projection. Multi-store
and multi-collection capture needs a shared real boundary or explicitly separate read models.

## Consequences and limits

- No durable change-journal dependency or second replication outbox is introduced. Reconcile on startup/resume and
  after relevant changes; notifications can request an earlier reconciliation but are not its correctness mechanism.
- Rows are host-serialized strings (source-generated JSON recommended), with bounded payload and batch sizes.
  This is a small/medium derived-view primitive, not a full-text engine or relational query replacement.
- All generations, including interrupted staging, are retained until coordinated account deletion. This makes
  immutable readers safe without assuming a reader process survived. Hosts must budget storage and limit rebuild
  frequency; automatic generation GC and incremental durable-journal integration are future work. Do not use this
  default for an unbounded/high-frequency projection workload.
- Access revocation must stop readers and remove the account-scoped derived store according to host policy. Activating
  a new generation alone is not secure erasure of old generations. Orchestration encryption must match the source's
  confidentiality requirements; browser orchestration is currently plaintext.
- Unsupported metadata formats are refused. A new host projection schema builds beside the previous generation;
  rollback can reopen the previous compatible generation only while the host retains that supported code/data.

## Evidence

`LocalReadModelTests` covers SQLite reopen after a failed batch sequence, old-reader stability, empty snapshots,
source epoch/schema changes, competing executors, cancellation, duplicate IDs and newer-format refusal.
`ReadModelRebuildSurvivesReloadAndKeepsPriorGeneration` runs the same recovery behavior against IndexedDB in the
trimmed browser harness. See the dated execution record for actual results and platform boundaries.
