# Bsync architecture

How Bsync works, why it is built this way, what it guarantees and where its limits are. This page holds the design
decisions (ADR-001 to ADR-041), the invariants (I01–I29) and the compatibility policy. Source comments cite ADR and
invariant numbers; they keep their meaning here.

Related: [protocol specification](protocol.md), [guide](guide.md) (using and operating Bsync),
[implementation plan](implementation-plan.md) (open work, item ids such as `A1`, `P3`, `C1`), [README](../README.md).

1. [Overview](#overview)
2. [Core model](#core-model)
3. Decisions: [replication core](#replication-core), [local storage](#local-storage), [server](#server),
   [security and identity](#security-and-identity), [offline orchestration and content](#offline-orchestration-and-content),
   [lifecycle and execution](#lifecycle-and-execution), [compatibility and release](#compatibility-and-release)
4. [Invariants](#invariants)
5. [Compatibility policy](#compatibility-policy)

---

## Overview

Bsync replicates JSON documents between local replicas on devices and an authoritative server. Applications write to
the local replica first; a sync loop pushes pending operations and pulls committed changes. The same application model
and conflict policy run in Blazor WebAssembly (IndexedDB), native hosts (SQLite) and server-connected hosts.

```
 component ─► ISyncCollection<T> ─┬─► LocalSyncCollection ─► SyncSession (loop, triggers, backoff, lease, hints)
                                  │                              │     ▲ SyncCoordinator (all collections of an account)
                                  │   local write (atomic) ─► SyncEngine ─► ILocalStore.UpdateAsync (compare-and-transform)
                                  │                              │          InMemory | SQLite | IndexedDB
                                  │                              ▼
                                  │                       ISyncTransport: HTTP (pull, push, SSE hints) | in-process
                                  └─► ServerSyncCollection ──────┤ (server-rendered hosts call the authority in-process)
                                                                 ▼
                                  endpoints (scope from auth) ─► ISyncAuthority: InMemory (tests) | PostgreSQL | SQL Server
                                                                 feeds, versions, receipts, membership, retention, epochs
```

| Component | Role |
|---|---|
| `SyncEngine<T>` | Protocol logic: prepares operations, correlates outcomes, applies pull pages, runs conflict policies, resets. Single-flight per store. |
| `ILocalStore<T>` | Durable replica: one atomic multi-record compare-and-transform plus bounded reads. Records hold current, base, base version, local revision, pending operation, rejection, observed state, kept conflict and generation. |
| `SyncSession<T>`, `SyncCoordinator` | Own the loop of an account: triggers (writes, interval, hints, online/visibility), backoff, execution lease, account switch, pause/resume, goal waits. |
| `ISyncTransport<T>` | `HttpSyncTransport` (with batched pull and multiplexed hints) or `InProcessTransport`. |
| `ISyncAuthority<T>` | Controlled write service: per-document compare-and-set, receipts, feed, read membership, retention, epochs. |
| Endpoints (`Bsync.Server.AspNetCore`) | HTTP binding; scope from the authenticated principal; problem-details codes; blob and multipart routes. |
| Blobs (`Bsync.Blobs`) | Content-addressed (SHA-256) attachments: transfers, durable jobs, managed cache with pins and capacity, optional encryption. |
| Orchestration | Account-scoped metadata beside the replica: orchestration store, account composition, execution leases, offline packages, workflows, read models, maintenance. |

**Data flow.** A local write commits the new state and its pending operation in one store transaction and returns a
`LocalWriteReceipt`. A push sends operations (id, base version, payload); the authority answers one outcome per
operation (`accepted`, `conflict`, `rejected`, `retry-later`) and replays duplicates from receipts. A pull sends an
opaque checkpoint and receives a page of `(document, version)` and removals; page and checkpoint commit together.
Hints carry no data; they only trigger a pull, so losing them never loses data.

**Packages** (ADR-012): `Bsync`, `Bsync.Blazor`, `Bsync.Storage.Sqlite`, `Bsync.Server.AspNetCore`,
`Bsync.Server.PostgreSql`, `Bsync.Server.SqlServer`; `net10.0` only.

---

## Core model

### Replication metadata (ADR-003)

| Metadata | Owner | Purpose |
|---|---|---|
| Local revision | Replica, local only | Increments on every local write; decides whether an acknowledgement still describes the latest edit. |
| Operation id (GUIDv7) | Replica; wire; receipts | Identifies one immutable logical write for deduplication. |
| Server version (`long ≥ 1`) | Server | Per-document concurrency token, strictly increasing per document. |
| Checkpoint (opaque) | Server | Feed position with epoch and a hash of collection, scope and scope fingerprint. Never parsed by clients. |
| Origin HLC (`UpdatedAt`) | Authoring replica | When the state was authored; input to time-based policies only. Never re-stamped by the server. |

Replication metadata lives in the `SyncRecord` envelope, not on the entity. 64-bit numbers are decimal strings on the
wire. The hybrid logical clock is bounded, validated and strictly monotonic per node; it is seeded from the store's
clock high-water mark after a restart and corrected, never moved backwards, when the device is ahead (ADR-017).

### Feed, checkpoints and epochs (ADR-005)

- A checkpoint denotes a **committed, gap-free prefix** of a feed (one feed per collection and scope). A page holds at
  most one change per document, in feed order; `hasMore` requires the checkpoint to advance.
- An **epoch** names a feed history. After a restore the authority starts a new epoch above a **version floor**, so an
  old base version can never match a different state.
- On `reset-required` the replica starts a new **generation** and resnapshots. Reason `epoch` hides records the server no
  longer has; `scope-changed` and `expired` purge clean records that are no longer visible (locally only). Pending edits,
  operations and kept conflicts always survive.
- Purging tombstones raises a **retention horizon**: older checkpoints get `expired`, and an edit on a purged version is
  rejected with `base-expired` instead of resurrecting the document.

### Consistency model (ADR-002)

Guaranteed: atomic local writes (state and pending intent together); per-document optimistic concurrency (accept only if
base version equals current; independent documents, partial batch acceptance is normal); all-or-nothing dependency
groups where the authority advertises `groups`; a committed-prefix feed; resumable eventual convergence under the I20
assumptions; truthful confirmation levels (saved locally, pending, accepted by the server, rejected, conflicted).
`SyncResult.IsComplete` means only that the queue was drained and the pull reached the server's checkpoint at that moment.

Not guaranteed: cross-document ordering on other replicas, read-your-writes across replicas before a pull, causal
consistency, bounded staleness, consistent multi-collection snapshots (C4), field-level changes on the wire (C6).

---

## Replication core

### ADR-001: Build a native protocol rather than adopt an existing system

Build a portable C# protocol and engine, borrowing concepts, not code: document + checkpoint pull and assumed-master
conflicts (RxDB/CouchDB), persisted operation identity and server-authoritative outcomes (Replicache/Zero), checkpoints
that never skip pending uploads (PowerSync), version/ETag concurrency (Datasync). In 2026 no option combined offline
Blazor WebAssembly, native .NET and a document conflict model. Consequence: Bsync owns protocol correctness and every
provider, mitigated by numbered invariants, randomized and fault-injection tests and public conformance suites; a new
provider is supported only after it passes them. Optional capabilities are added on evidence. **Status:** Accepted.

### ADR-002: Consistency model and scope

Per-document optimistic concurrency with an authoritative server and eventual convergence, as described in
[Consistency model](#consistency-model-adr-002); global serializability and causal consistency were rejected (impossible
offline, or need cross-document dependency tracking). Invariants spanning documents must live in one document or use
dependency groups. Cross-collection groups and snapshots are tracked in [implementation plan](implementation-plan.md)
(C4). **Status:** Accepted.

### ADR-003: Separation of replication metadata

Five separate values replace the prototype's server-stamped HLC, which made last-writer-wins depend on upload order (see
[Replication metadata](#replication-metadata-adr-003)). Rejected: version fields on entities (protocol state in domain
models) and content hashes as versions (unordered, costly). An authority may use one global sequence or per-feed
sequences as long as per-document versions increase. **Status:** Accepted.

### ADR-005: Committed-prefix feed ordering and epochs

Checkpoints are opaque, carry an epoch and cover a committed, gap-free prefix. Values assigned before commit
(timestamps, `rowversion`, `IDENTITY`) are never feed positions: a slower transaction could commit a lower value after a
reader passed it. PostgreSQL and SQL Server lock the feed row for the whole push, so version `n+1` becomes visible only
after `n` committed or rolled back (verified with a real uncommitted lower version and with 12 writers on two instances).
Cost: one writer per feed; a `pg_snapshot_xmin` watermark is the fallback if throughput requires it. A new epoch's version
floor also applies to feeds created afterwards, on every authority (A2). Remaining: set-based push (P1), tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted; in-memory, PostgreSQL, SQL Server.

### ADR-006: Conflict model, policies and defaults

The server detects conflicts; an `IConflictHandler` on the client sees the server state, the edit's base and the latest
local state and returns `AcceptMaster`, `KeepFork`, `Resolve(doc)` or `Defer`. Handlers are pure and never re-run for
replays; a resolution commits only if the local revision is unchanged. The default `Defer` adopts the server state and
keeps the local change durably as a conflict until the app resolves or discards it. Alternatives: `ClientWins`,
`ServerWins`, `LastWriteWins`, delegate, and `ThreeWayMergeConflictHandler` (field-level JSON merge; sets and counters
only when declared). Delete versus update is an ordinary conflict; server rejections are parked, not conflicts.
Dependency groups (`SaveAllAsync`) are committed, sent and applied all or nothing; a failing member parks its group.
Consequence: with `Defer`, apps must surface conflicts to converge. Field-level wire changes, server mutators and CRDTs
(C6, C7, C11) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted.

### ADR-013: Recovery of local work and damaged replicas

Never lose or duplicate unsynchronized work silently. Parked rejections can be retried as a **new** operation or
reverted to the newest known server state; export/import moves local work between stores with operation ids
preserved, so a write that already reached the server replays from its receipt. `SqliteStoreRecovery` moves damaged
files aside (never deletes them), salvages rows with local work past damaged pages and resnapshots clean data. Domain
upgrades use `upgrade-required`; documents upgrade on read (`DocumentUpgrade.TryTake`). Limits: no salvage for IndexedDB
(evicted data is lost); an upload in flight across a shape change gets `operation-id-reused`. **Status:** Accepted.

### ADR-017: Correcting a device clock that is ahead of the server

Pull responses carry `serverTime`; a device ahead by a second or more applies `HybridLogicalClock.PhysicalOffset`
(timestamps never go backwards; a device behind is left alone). Writes stamped before the first pull and rejected with
`clock-skew` are re-stamped once, in order, as new operations, and the clock high-water mark is reset. This is safe
because timestamps above the server bound were never accepted anywhere. Opt out with
`SyncOptions.RestampSkewedWrites` if the app copied `UpdatedAt` elsewhere before upload. **Status:** Accepted.

---

## Local storage

### ADR-004: Atomic store operations, ownership and queue coalescing

One primitive, `UpdateAsync(updates, checkpoint?)`, applies pure transforms to the committed state of several records
and optionally the checkpoint, atomically; transforms get copies and may run more than once, which allows optimistic
implementations. A generic transaction object was rejected (it cannot span .NET/JS awaits in IndexedDB). One engine
replicates a store at a time; local writes never wait for the network. A record has at most one pending operation:
unsent edits coalesce, a sent operation is immutable. Stores never alias application objects. In-memory filtering and
counting costs (P11, P12) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted; three
stores pass `LocalStoreConformance`.

### ADR-008: Browser storage: IndexedDB baseline

IndexedDB through a narrow JS bridge: read records with write stamps, run transforms in .NET, then commit in one
strict-durability transaction that aborts if a stamp changed, and retry. Documents are stored as JSON strings to keep
64-bit numbers and unknown members exact. A Web Locks lease elects one replicating tab, but correctness relies on the
stamps. Quota, eviction and private-mode failures are errors, never success. SQLite-WASM on OPFS is deferred until a
benchmark needs it. Not tested: native Safari, mobile browsers, real quota pressure. Lease leak (A7), connection edge
cases (A14), interop cost (P9, P10), cross-tab messaging (E1) and worker/OPFS (E2) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted.

### ADR-018: Declared secondary indexes in local stores

Index keys are computed in .NET from the typed document (`SyncIndex<T>.Create(name, d => d.Field, version)`) with one
order-preserving string encoding and stored with the record in the same transaction: SQLite table `bs_index`, one
IndexedDB `multiEntry` index over `ixKeys` (new indexes need no database upgrade). Why: a custom order loaded the whole
collection (p95 above 300 ms for a page of 50,000 documents; now under 0.5 ms), and `json_extract` would sort
differently from IndexedDB. Extractors must be pure; change `version` when one changes. Strings sort ordinally. A writer
with another index set marks keys unusable and queries fall back to memory; background rebuild is P11 in the
[implementation plan](implementation-plan.md). **Status:** Accepted.

### ADR-023: Full-reconciliation local read-model generations

`LocalReadModel` builds a derived view from an explicitly captured stable snapshot into a new immutable generation, in
fenced batches, then switches an active pointer atomically; failures keep the previous generation and recovery rebuilds
from scratch. Why: observer callbacks are transient and live pagination is not a snapshot. The host must supply a real
snapshot (database snapshot, immutable export, or a gate quiescing **all** writers and replication). For small and
medium views; generation cleanup comes with the owned variant (ADR-027). **Status:** Accepted.

---

## Server

### ADR-009: Controlled write service and change capture

All writes to synchronized collections go through one authoritative service that does the conditional write, the
receipt and the feed append in one transaction; HTTP and in-process callers share it (I18). Concurrency comes from the
database, never a process lock. PostgreSQL first, on Npgsql directly; SQL Server second (ADR-014). Writes that bypass the
service are unsupported unless published explicitly (ADR-014, ADR-040) or captured (ADR-041). PostgreSQL write handler,
EF Core integration and logical decoding (D8, D9, C9) are tracked in [implementation plan](implementation-plan.md).
**Status:** Accepted.

### ADR-014: SQL Server authority on the application's database

Protocol tables live in a `bsync` schema of the application's database; application tables stay the system of record.
The feed row is locked (`UPDLOCK, HOLDLOCK`) for the push (ADR-005). Write APIs accept a caller `DbTransaction`, so EF
Core `SaveChanges` and a feed append commit together. `ISyncWriteHandler<T>` runs in the transaction after protocol
checks and returns `Accept(canonical)`, `Conflict`, `Reject` (receipt kept) or `RetryLater` (no receipt); it must not
commit or do network I/O and never runs for replays. `ISyncPublisher<T>` adds server-originated changes under the same
lock. Shared writable documents live once in a group scope. After a restore call `BeginNewEpochAsync(versionFloor)`.
Set-based push (P1), hint polling (P3) and deterministic failures as rejections (A3) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted; verified on SQL Server 2025 LocalDB.

### ADR-015: Read membership in the feed and removals without a resnapshot

With `Readers` and `PrincipalKey`, the authority computes a document's readers at every write and keeps one access row
per document and principal, versioned under the feed lock. A pull reads only the caller's rows, so pages are full and
cost follows what the caller sees. Revocation returns the id in `removals`: clean copies are purged, dirty ones hidden and
parked as `forbidden`. Peers without the `removals` feature get `reset-required` (`scope-changed`) instead. Writes and
direct reads require current membership. A separate grant log was rejected (second allocator, dangling grants). Own-access
removal reported as `forbidden` (A4), membership derived from client JSON (A5) and declarative sync rules (C1) are
tracked in [implementation plan](implementation-plan.md). **Status:** Accepted; all three authorities.

### ADR-024: SQL Server capture spike

Kept caller-transaction publication and an explicit projection outbox as the supported routes; automatic capture is
justified only for uncontrolled writers. A LocalDB spike validated a Change Tracking snapshot boundary and recorded the
candidate algorithm (validate minimum valid version, capture in one snapshot, resnapshot on invalid boundary, external
restore epoch, track every related table). CDC was not adopted (high privileges, Agent jobs, unproven snapshot/log
handoff). **Status:** Historical; superseded by ADR-041.

### ADR-040: Relational workflow helpers with caller-owned transactions

`Bsync.Server.SqlServer.Workflows` provides a command ledger (command id bound to actor and payload fingerprint; result
committed with the domain change; expired results never re-execute), a coalesced projection outbox rebuilt from canonical
state under the feed lock, and an external-effect outbox (durable attempts, claims, receiver deduplication or status
lookup, otherwise `reconciliation-required`), plus `AddBsyncWorkflowWorker`. Helpers use the caller's transaction and
never commit it; feed locks are taken in one fixed order before domain rows; migrations are explicit deployment steps.
Broader qualification and PostgreSQL equivalents (D8, O5) are tracked in [implementation plan](implementation-plan.md).
**Status:** Experimental.

### ADR-041: Bounded SQL Server Change Tracking capture

`SqlServerChangeTrackingCapture` stages changed keys (including deletes) from 1–32 tables in one snapshot transaction, at
most 10,000 keys per sealed batch, then publishes current state, applied marker and cursor in one transaction. Callbacks
reread canonical data and derive membership; initial and resnapshot callbacks replace the **complete** scope. Large
resnapshots build isolated generations (up to 1,000,000 keys) and activate them under locks with a bounded final delta.
Limits: one database, single-column keys, no CDC; a restore must change the external epoch; an expired boundary forces a
resnapshot. Promotion from experimental (C9) is tracked in [implementation plan](implementation-plan.md).
**Status:** Experimental; supersedes ADR-024.

---

## Security and identity

### ADR-010: Authentication, scope identity and revocation

Authority comes from the authenticated context, never request bodies. A scope is (tenant, principal or group,
collection, filter, schema version); its fingerprint is bound into checkpoints, and a changed scope gets
`reset-required` (`scope-changed`). Receipts are per scope with a request fingerprint; replays and conflicts are filtered
by read authorization. Replicas have an id and incarnation (new after restore or cloning). Revocation removes local
copies without global deletion. No database credentials or client secrets in WebAssembly; logs redact tokens and
documents. Every route fails closed (A1): `ResolveScope` is required, anonymous callers get 401 unless the application sets
`AllowAnonymous`, and a scope shared by every caller (`SharedScope`) is logged at startup. Blob routes answer with
`nosniff` and attachment downloads, and can require `Bsync-Protocol` so browsers always preflight (A12). **Status:** Accepted.

### ADR-016: Encryption at rest and replica wipe

Threat model: a lost device or copied file, and the next user of a shared device; not malware running as the user, XSS,
the server, or metadata (ids, sizes, index values, timing). Wipe: `DeleteReplicaAsync` stops replication and deletes the
account's replica; the browser recipe requests persistent storage. SQLite uses SQLCipher (the app adds
`bundle_e_sqlcipher` instead of `bundle_e_sqlite3`; raw 32-byte key, rekey, encrypt in place); IndexedDB uses an AES-GCM
envelope per value with the record key as associated data (keys and index values stay plain). The app supplies keys; a
wrong or missing key fails explicitly, never as an empty replica. SQLite connection strings never carry the key (A10):
each physical connection is keyed with `PRAGMA key` and tagged with a one-way fingerprint, so pools (Bsync's and
Microsoft.Data.Sqlite's) hold no key and a pooled connection is reused only for the same key. Encrypting in place folds
and removes the plain write-ahead log before the swap and closes only that file's pool (A11); deleted plain blocks may
survive on flash storage. Unmaintained SQLCipher bundle (R4) and plaintext accepted by encrypted IndexedDB (A8) are
tracked in [implementation plan](implementation-plan.md). **Status:** Accepted; opt-in.

### ADR-025: Encrypted blob envelopes (v1)

`EncryptedBlobCache` encrypts partial and complete blobs with AES-256-GCM (`nonce | ciphertext | tag`), binding account,
plaintext hash, mode, key and length as associated data, and checks digest, length, tag and plaintext hash before
exposing bytes. Platform and Web Crypto only. Objects are capped at 16 MiB; appends rewrite and opens authenticate the
whole object. Rotation and migration (copy-and-verify, source kept) commit through metadata CAS; key or data failures
never trigger repair or empty fallback. Hashes, sizes and timing stay visible; random nonces need a per-key budget.
Whole-object cost (B2), subkeys instead of a nonce ledger (B12) and cryptographic review (R5) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted; opt-in.

### ADR-027: Account composition, owned read models and protected metadata

`AccountComposition` opens registered resources under shared physical account ownership and deletes them with the same
registry (lock order: account, execution, content, short metadata transactions). Orchestration feature versions are
permanent barriers (2 for protected payloads and chunked v2, 3 for package sets); older readers refuse them, so close old
processes first (`UpgradeFeatureStoreAsync`). `ProtectedOrchestrationStore` encrypts selected record kinds with an
AES-GCM envelope (fence records stay clear; credentials never belong there; no plaintext fallback). Owned read models
(`read-model-v2/`) add reader lifetimes and bounded collection. Rollback to an older valid snapshot is not detected.
**Status:** Accepted; opt-in; cryptographic review open (R5).

### ADR-028: Experimental chunked encrypted blobs (v2)

`ChunkedEncryptedBlobCache` stores objects up to 1 GiB as immutable AES-256-GCM chunks (64 KiB–1 MiB) plus an
encrypted manifest, binding account, hash, generation, key, index and length; fresh random nonces on every seal.
Journals precede physical writes, so crashes leave accounted orphans, never progress pointing at missing data. Readers
are asynchronous and hold one decrypted chunk. Recovery quarantines old state, which is never collected automatically.
Proposed ceiling: 2^24 seals per actual key across all uses. Manifest sealing 2 (A9) binds the manifest's role
(`manifest/partial` or `manifest/complete`) and length into the associated data, and completion reseals the manifest as
complete, so an earlier partial manifest cannot authenticate as a truncated object. Pre-0.6 manifests (sealing 0) stay
readable: the first `OpenReadAsync` verifies the whole plaintext hash and reseals them; sequential reads to the end also
check the hash. Nonce strategy (B12) and independent review (R5) are tracked in
[implementation plan](implementation-plan.md). **Status:** Experimental.

### ADR-035: Chunk scrubbing and key-retention observations

`ScrubAsync` authenticates a bounded number of chunks per call against the sealed manifest and advances an encrypted
CAS checkpoint, so scans resume; `Complete` means every chunk authenticated over the scan, not at one instant.
A per-generation key journal makes key obligations visible to `GetKeyRetentionPageAsync`. The page is an observation,
not permission: hosts delete keys only after a full scan under exclusive ownership, counting v1 data, metadata and
backups. **Status:** Experimental (with ADR-028).

### ADR-036: Encryption invocation admission and browser capacity ledger

`BudgetedBlobEncryption` reserves one invocation per seal, keyed by a hash of the actual key so aliases cannot reset it;
reservations are never refunded and the persisted ceiling can only be lowered. Multi-device keys need a shared or
partitioned budget. IndexedDB blob storage (schema 3) keeps a capacity ledger in the same transaction as payload writes,
uses Blob-backed objects with bounded range reads, and reconciles in pages after external changes. Replacing the ledger
with per-generation subkeys (B12) is tracked in [implementation plan](implementation-plan.md). **Status:** Accepted;
qualification open.

---

## Offline orchestration and content

### ADR-020: Account lifecycle and versioned orchestration persistence

Document pending operations stay authoritative; orchestration metadata never duplicates the outbox. `AccountScope`
is a local partition key, not authorization. `IOrchestrationStore` offers bounded keyset pages and atomic
compare-and-write batches with independent schemas. `ExecutionLease` fences every scheduler commit inside the storage
transaction, so a stale worker cannot overwrite a successor. `SyncCoordinator` opens, suspends and deletes accounts;
deletion is journaled outside the deleted resources and refuses pending or unknown work by default. Optional physical
account locks block deletion while another participant is live. Locked keys or missing credentials keep local data and
never produce an empty store. **Status:** Accepted; implementation in progress (browser and device gates open).

### ADR-021: Managed content and immutable package activation

`BlobTransferManager` persists transfer jobs with deadlines and backoff (never credentials or signed URLs). Completion
means verified bytes, not business acceptance. `ManagedBlobCache` records pin owners, reserves capacity before writes and
guards deletion with a **non-expiring** physical lock, because an expiring fence could let a delayed worker delete newly
pinned content. Package manifests are immutable (≤ 256 items); activation verifies every required item and switches one
pointer atomically, so an incomplete candidate never replaces a complete revision. Download and cache cost (B1, B2),
automatic pin release (B4), job failure handling (B5) and staging sweep (B6) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted; implementation in progress.

### ADR-022: Recoverable workflows, command identity and controlled publication

`RecoverableWorkflow` persists step definition and progress before effects, fenced by the execution lease; effects get a
stable identity and must be idempotent (crash after effect means replay, not exactly once). `BackgroundWorkDispatcher`
persists requests before asking the OS scheduler. Intent contracts separate local persistence, transport and execution
status; command ledgers and outboxes became package helpers in ADR-040. Writers lock feeds before domain rows in one
total order; never call `ReplaceScopeAsync` with an incomplete snapshot. A generic command ledger (C7, I25) and supported
mobile schedulers (E4) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted;
implementation in progress.

### ADR-026: Verified content repair and cooperative preparation deadlines

`BlobVerification.VerifyAsync` separates missing, damaged, key, cryptographic, format and I/O outcomes. Providers with
`IBlobCacheReplacement` stage a replacement and promote it atomically only after full-hash verification, so open readers
keep their bytes. The transfer manager repairs under continuous content ownership with a durable repair journal.
Preparation runs under one cooperative deadline; started work is joined, never abandoned. Limits: no repair without the
capability (encrypted v1, third-party caches); interrupted verification restarts; do not mix versions on one cache.
**Status:** Accepted.

### ADR-029: Exclusive account maintenance and owner reconciliation

`AccountMaintenance` runs an ordered plan of 1–32 versioned steps under exclusive account ownership (`Busy` while any
participant is live, never takeover), within one deadline and at most 32 pages per call, resuming from a checkpoint bound
to the step list. Steps must be replay-safe and free of network effects; dry runs change nothing. Built-in steps restore
pins from manifests and jobs and remove orphan pins only when every owner is provably gone. Safety is favoured over
availability. **Status:** Accepted; opt-in.

### ADR-038: Resumable multipart upload

`ResumableMultipartUpload` plugs into the existing transfer manager. An operation id is committed before initiation and
every restart reconciles with server receipts. Parts are 5–16 MiB, at most 1,000, sent by a credential-free client
without redirects; completion counts only after server-side verification. Routes require authentication and a host
resolver; a 501 at initiation is the only fallback. An S3 recipe ships in the Tasks sample. Real-provider evidence (R6)
and large direct uploads (B3) are tracked in [implementation plan](implementation-plan.md). **Status:** Experimental.

### ADR-039: Atomic package sets

One immutable root binds 1–32 child packages by id, revision and manifest hash; activation switches one active-root
pointer and snapshots open every child through it. Flat only; schema 2 allows optional children reported as unavailable
rather than failing. First use raises the orchestration feature version to 3, which published 0.5.0 refuses; stop old
processes under exclusive ownership first. **Status:** Experimental.

---

## Lifecycle and execution

### ADR-007: Hosting profiles, application API and lifecycle

| Profile | Replica | Offline writes | Confirmation |
|---|---|---|---|
| Browser (WASM, PWA, Auto after activation) | IndexedDB per account | Yes | Local commit, then server |
| Native (MAUI, WPF, WinForms, headless) | SQLite | Yes | Local commit, then server |
| Server-connected (Interactive Server) | None; authority in-process | No | Server commit |
| Request (static SSR, prerender) | None | No | Server commit |

One facade, `ISyncCollection<T>`, over `LocalSyncCollection` or `ServerSyncCollection`; explicit DI recipes per profile,
no runtime guessing. Prerendered state is never imported into a replica, so it cannot overwrite unsynced edits. The
session owns its background work; account switch is a barrier. Server collection scans (P15), builders, live queries and
components (D2, D3, D5, D6) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted; every
profile has a tested sample.

### ADR-030: Execution stop diagnostics and bounded verification

Workflows, read-model rebuilds, transfers and `SyncForAsync` share one monotonic `TimeProvider` deadline including lease
acquisition and joined callbacks; after a late callback they return the last observed state instead of reading again;
a zero budget does no I/O; caller cancellation throws. `bsync.execution.stops` counts one stop per invocation with fixed
`bsync.operation` and `bsync.reason` tags (precedence: caller, lease loss, budget); it is not a business-outcome counter.
Verification reads at most the declared size plus one byte. Deadlines are cooperative. **Status:** Accepted.

### ADR-031: Goal-wait deadlines and observation ownership

Each `SyncAsync(goal, budget)` wait owns one deadline that also covers account resolution and opening. The shared loop
only signals; each wait does its own reads and callbacks, so one wait's expiry or exception never affects replication.
On expiry the result is the last observation with `Reached = false`; `HasObservation = false` means `Pending = 0` is
unknown. Shutdown joins goal observations; a callback that blocks on its own session's shutdown deadlocks.
**Status:** Accepted.

### ADR-032: Lease admission and joined hint-listener lifetimes

A session checks cancellation around lease acquisition; a late lease is kept and disposed at shutdown, a lost one means
follower behaviour. Hint listeners ignore items after cancellation. A coordinator switching accounts chains the new hint
listener behind the old one, and shutdown joins the chain. Callbacks that ignore cancellation can delay shutdown.
**Status:** Accepted.

### ADR-033: Lease loss between session runs

The session observes an admitted lease's `Lost` token; loss triggers a sync through the normal trigger (reacquire or
become follower) and records `session_ownership`. Pause and server `Retry-After` still win; normal disposal is not
counted as loss. **Status:** Accepted.

### ADR-034: Nested execution stop causes

A private registry maps Bsync deadline tokens and library aliases to their observation scope, so nested invocations
report the parent's real cause (`budget_expired`, `lease_lost`) rather than `caller_canceled`. Diagnostics only;
application-created linked tokens stay caller-relative; do not sum parent and child counts. **Status:** Accepted.

### ADR-037: Maintained native background recipes

Keep the NativeBackground runtime and Android/Apple hosts as versioned recipes, not a second scheduler and not platform
dependencies in the portable package. Consumers own OS registration, signing, credentials and key accessibility.
Foreground recovery is mandatory; late OS callbacks pass the same deletion and ownership checks; no delivery deadline is
promised. Platform builds, device runs and a `Bsync.Maui` package (E4, E6, R7) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted as recipes; platform gates open.

---

## Compatibility and release

### ADR-011: Wire, store and domain compatibility

Three independent versions: protocol, store schema per provider, and domain schema. `Bsync-Protocol` and `Bsync-Schema`
headers on every request; unsupported combinations get `upgrade-required` before anything is read or written. Canonical
JSON (64-bit numbers as strings, canonical HLC, ordinal ids, `null` distinct from absent) enforced by strict converters
and fixtures; source-generated serialization for trim/AOT. Pending operations are never rewritten after they may have
been sent. Unknown document fields survive only with `[JsonExtensionData]`; client migrations (C5) and cross-version
fixtures (R3) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted; see
[Compatibility policy](#compatibility-policy).

### ADR-012: Packaging, support tiers, target framework

`net10.0` only. Packages split only at real dependency boundaries (six packages); single-SDK integrations (S3, MAUI) are
sample recipes. Native database binaries never enter the WebAssembly graph and server code never enters the client
bundle. Support tiers: *Verified* (executed tests with recorded versions), *Build-only*, *Planned*; nothing is called
supported without Verified evidence. Publishing is a separate, explicitly authorized action. API reduction (D1) and
performance budgets (R8) are tracked in [implementation plan](implementation-plan.md). **Status:** Accepted.

### ADR-019: Release provenance and audited package consumers

Releases build from a clean checkout at a protected `v<version>` tag with a dated changelog heading. Tooling verifies
nuspec metadata, records checksums, toolchain, API baseline hashes and Source Link maps, and `Bsync.PackageVerifier`
checks PDB/DLL pairing and sources. NuGet audit findings are errors. A green candidate is not permission to publish, and
published versions are never reused with different bytes. The 0.2.0 packages name an earlier commit, so their
provenance is unresolved; green CI and tagged qualification (R1, R2) are tracked in
[implementation plan](implementation-plan.md). **Status:** Accepted.

---

## Invariants

Tests name the invariants they check in their display names (`dotnet test --filter "DisplayName~I04"`). Durable
providers must pass the public conformance suites in `Bsync.Testing` (`LocalStoreConformance`,
`LocalStoreIndexConformance`, `AuthorityConformance`) before claiming an invariant. Status: **Enforced** (tested; the
qualifier says where), **Partial** (gap stated), **Open**.

| ID | Invariant | Status | Key evidence or limitation |
|---|---|---|---|
| I01 | A local write and its pending intent survive supported crashes together. | Enforced (SQLite process kill, IndexedDB reload) | `ProcessKillTests`. Power loss untested; in-memory store not durable. |
| I02 | Acknowledging operation N never clears or overwrites a later edit. | Enforced (reference) | Local revision checks, also across tabs. |
| I03 | Page and checkpoint commit together; never advance past unrepresented data. | Enforced (reference) | `CrashDuringPageApplyIsAtomic`. |
| I04 | Retries reuse id and payload; duplicates replay; a reused id with other content fails. | Enforced (reference) | Receipts with fingerprints. A replay after receipt purge becomes a conflict, so receipts must outlive offline periods (C10). |
| I05 | Base-version compare and write are one atomic server operation. | Enforced (reference, PostgreSQL) | Feed row locked for the push. |
| I06 | A cursor covers a committed, gap-free prefix in an explicit epoch. | Enforced (reference, PostgreSQL) | `DelayedCommitIsNeverSkipped`; conformance "a feed first written after a new epoch starts above the version floor" (A2). |
| I07 | Isolation across tenant, principal, collection, filter, replica. | Enforced (reference) | Scope from principal, fingerprint in checkpoints. Fail-closed default open (A1). |
| I08 | Work is bounded; exhaustion reports resumable remaining work. | Partial | Custom query order reads the whole collection (P11); no pull byte budget (P2). |
| I09 | No regression of versions or cursors; bad outcomes never mark data clean. | Enforced (reference) | `CorrelateOutcomes`, `ValidatePullPage`, invalid fixtures. |
| I10 | Tombstones propagate; revocation removes projections without global deletion; purge is distinguishable. | Enforced (reference) | Reset reasons, removals, `base-expired`. Local work and conflicts are never purged. |
| I11 | Deterministic conflicts, no repeated side effects, forks preserved. | Enforced (reference) | No re-run on replay; durable kept conflicts. |
| I12 | Bounded, validated HLC; origin time separate from version and cursor. | Enforced (reference) | Strict parser, high-water seeding, skew bound. |
| I13 | Repair after notification loss. | Enforced (reference, browser) | `LostHintsStillConverge` drops every hint. |
| I14 | Recovery and reset preserve pending edits. | Enforced (reference, SQLite) | `RandomSchedulesWithRestoresConverge`; SQLite rebuild. |
| I15 | No partial logical writes on cancellation; post-commit cancellation is an unknown outcome. | Partial | Cooperative only; ignored cancellation delays shutdown. |
| I16 | Truthful confirmation levels. | Partial | No server "current up to" watermark exposed. |
| I17 | Schema compatibility; old clients cannot erase unknown fields. | Partial | Only with `[JsonExtensionData]` (C5, C6). Store schemas migrate forward; newer refused. |
| I18 | Equal authorization and business rules on every host. | Enforced (reference) | All hosts call the same authority (`HostEquivalence`). |
| I19 | Rejected or conflicting records do not block independent work. | Enforced (reference, PostgreSQL) | Parking, conflict budget, groups. Deterministic server exceptions still loop as 503 (A3). |
| I20 | Eventual convergence under stated assumptions after quiescence. | Partial | 60 seeded schedules, four policies, restores, groups; assumptions below. |
| I21 | Readiness requires available, verified required content. | Partial | External deletion can race an already-open stream. |
| I22 | An incomplete candidate never replaces a complete active package. | Enforced | One conditional pointer transaction. |
| I23 | Eviction preserves pending-work, package, reader and transfer pins. | Enforced (managed participants) | Custom and legacy owners stay pinned. |
| I24 | An account switch cannot reuse another account's transport or identity. | Partial | Hosts must bind factories and credentials to the account and quiesce writers. |
| I25 | Local persistence, transport acceptance and business execution are distinct. | Partial | Generic command ledger open (C7). |
| I26 | Lease loss cancels work; stale owners cannot commit progress. | Enforced | Fences in the storage transaction; remote effects need idempotency. |
| I27 | Unknown newer orchestration schemas are refused without destructive fallback. | Partial | Full migration and browser newer-schema qualification open. |
| I28 | Background scheduling claims match demonstrated host capabilities. | Open | Upheld by making no native background claim; device runs open (E6, R7). |
| I29 | A read model never advertises rows or a boundary it has not fully applied. | Enforced (given a host snapshot) | Atomic batches and fenced activation. |

**Assumptions behind I20.** The randomized suite covers the in-memory authority and store, one collection without
scopes, policy-based whole-document resolution, server restores and a finite schedule followed by reliable connectivity
(SQLite in one seeded run). It assumes one available authority, no schema change, unbounded receipt retention,
terminating conflict handlers and, with `Defer`, that every kept conflict is eventually decided. Revocation, filter
changes, receipt expiry and tombstone purge have targeted tests only; clocks beyond the skew bound are not covered.
Passing a finite suite is evidence, not proof.

---

## Compatibility policy

### Versioning

- **Before 1.0:** a *minor* release (`0.x` → `0.(x+1)`) may break the public API, behaviour, store schemas or optional
  wire features; every break is recorded with a migration path in the [CHANGELOG](../CHANGELOG.md). A *patch* release
  never breaks: fixes, additive API and documentation only, no store schema change, and no wire change that an older
  peer of the same minor version cannot ignore.
- **From 1.0:** semantic versioning; public API compatibility checked in CI; protocol, store schema and domain schema
  versioned independently (ADR-011); a documented client/server compatibility window.
- Behavioural changes count as breaking even when signatures do not change. A published version is immutable.

### Wire protocol

- `Bsync-Protocol` and `Bsync-Schema` headers on every request; unsupported combinations get `upgrade-required` and
  nothing is applied.
- New capabilities are additive and negotiated through `features` (for example `groups`, `removals`, `server-time`,
  `retention`, `limits`, `pull-batch`, `hints-multiplex`). Receivers ignore unknown members. New clients fall back with
  old servers; old clients receive only what they understand (for example `reset-required` instead of `removals`). Only
  explicit refusals (such as 501 at multipart initiation) trigger a fallback, never authentication failures.
- Every wire change gets a feature flag, fixtures, an update to the [protocol](protocol.md) and old/new peer tests (R3).

### Local and server storage schemas

- Each store versions its schema (SQLite `user_version`, IndexedDB version, server `meta` tables). Upgrades migrate
  forward in place under a lock, keeping records, pending operations, groups, conflicts and rejections. Newer schemas are
  refused and left untouched; there is no automatic downgrade.
- Upgrade every process that opens a database together (all server instances; all processes and tabs of a replica; old
  tabs are closed as `outdated`).
- Orchestration records and encrypted formats carry their own versions, independent of the document schema; unknown
  versions are retained and refused (I27). Feature barriers require closing older processes under exclusive account
  ownership first, because a version check cannot revoke an already-open connection. New formats are opt-in, and
  rollback preserves new metadata and bytes.

### Domain schema

- Declare `[JsonExtensionData]` so older writers keep unknown fields; upgrade on read with `IJsonOnDeserialized` and
  `DocumentUpgrade.TryTake`.
- Rolling upgrade: the server accepts both schema ids for a window; afterwards old clients stop with `upgrade-required`
  and keep their work. Drain queues before changing a document's shape. Sent operations are never rewritten.

### Public API baselines

1. Baselines live in `src/Tests/api/*.txt` and are checked by `PublicApiTests`; any public surface change fails it.
2. After review, regenerate on purpose with
   `BSYNC_UPDATE_API=1 dotnet test src/Tests/Bsync.Tests --filter PublicApiTests` and review the diff.
3. Record every non-additive change with a migration path in the [CHANGELOG](../CHANGELOG.md).

### Release rules

- Each user-visible change adds a line under *Unreleased* in `CHANGELOG.md` in the same change.
- A release sets `VersionPrefix` in `src/Directory.Build.props`, adds `## <version> - YYYY-MM-DD`, and packs from a clean
  tree at the protected `v<version>` tag with green qualification runs and package consumers (ADR-019).
- Publishing to NuGet is a separate, explicitly authorized step; nightly builds produce preview packages as artifacts only.
- New decisions get a new `ADR-NNN` subsection here; superseded ones are marked, never deleted.
