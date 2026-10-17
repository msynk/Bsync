# Bsync Offline Capabilities Improvement Plan

Status: In progress — portable orchestration/content implementation and qualification, 2026-10-16
Prepared: 2026-10-16  
Baseline: Published Bsync 0.2.0 packages and repository source reviewed at `ad46c3b`  
Execution destination: The Bsync repository, independently of any consuming application

Current execution evidence and remaining implementation/release gates:
[offline implementation, 2026-10-16](review/offline-implementation-2026-10-16.md).
Unchecked work below remains open; partial APIs or a passing targeted test do not close an entire workstream.
Latest local verification (continuation through 2026-10-17): 740 core, 94 SQL Server, 45 PostgreSQL,
31 SQLCipher and 129 source browser/WPF checkpoint tests passed without skips. The final Tasks sample adds
12 passing targeted browser cases; exact preview `0.3.0-offline.20261016.8` passes 55 package-only tests,
75 packaged browser cases and web smoke. Four actual published-0.2.0/current HTTP process cases pass.
Native scheduling/download recipes compile against real platform references; physical-device delivery, supported
S3 service qualification and clean release/CI gates remain open. The plan is not marked complete.

## 1. Objective

Extend Bsync into a dependable foundation for applications that must start, read prepared content, accept durable local work, and recover synchronization after extended disconnection.

Deliver reusable capabilities for managed attachments, complete offline content packages, durable business intents, server publication, account lifecycle, and platform scheduling. Preserve the existing replication engine and its guarantees.

This plan defines library and adapter work. Applications remain responsible for their business models, authorization rules, approval semantics, offline-access policy, UI, and selection of data to download.

### Definition of offline support

For a declared dataset that was successfully prepared, a supported host must be able to:

1. Restart without contacting a server and open locally accessible content.
2. Read the complete active content revision, including required attachments.
3. Persist new work and accurately report its local durability and remote status.
4. Preserve work through supported crashes, account suspension, upgrades, and interrupted transfers.
5. Resume synchronization when connectivity and valid server credentials are available.
6. Report missing data, conflicts, rejections, unavailable keys, and exhausted storage explicitly.

Offline support does not promise access to data never downloaded, immediate remote revocation on disconnected devices, guaranteed server acceptance of delayed work, or continuous execution when an operating system suspends an application.

## 2. Baseline and implementation discipline

### 2.1 Existing capabilities to reuse

Verify these against the execution checkout before changing code. They were present in the reviewed implementation and must not be treated as missing features.

| Existing capability | Integration point |
|---|---|
| Durable local writes and pending operations | `SyncEngine<TDocument>`, local stores |
| SQLite and IndexedDB persistence | `Bsync.Storage.Sqlite`, `Bsync.Blazor` |
| Retry-safe operations and server receipts | Protocol, durable authorities |
| Retained conflicts and explicit resolution | Conflict handlers and collection API |
| Atomic groups within one collection | `WriteGroupAsync`, `SaveAllAsync` |
| Sessions, backoff, sync goals, and collection scheduling | `SyncSession<TDocument>`, `SyncCoordinator`, `SyncGoal` |
| SQL Server and PostgreSQL authorities | Existing server provider packages |
| Transactional domain write integration | `ISyncWriteHandler<TDocument>` where supported |
| Server-originated publication | `ISyncPublisher<TDocument>` |
| Read membership, removals, retention, and resets | Authority and replica implementations |
| Verified local blobs and resumable HTTP transfers | `IBlobCache`, `FileBlobCache`, `BrowserBlobStore`, `HttpBlobTransfer` |
| Server blob routes and authorization hooks | `MapSyncBlobs`, `IBlobStore`, `IBlobAccess` |
| Database encryption and replica deletion | Existing store/session APIs |
| Local indexes, observability, export/import, and recovery | Existing query, diagnostics, and recovery APIs |

Bundles, immutable intents, S3 storage, and MAUI lifecycle integration already have sample implementations or recipes. Extract and strengthen them instead of writing independent replacements.

### 2.2 Read first in the Bsync repository

All paths below are relative to the Bsync repository root:

- `README.md`, `CHANGELOG.md`, `docs/roadmap.md`, `docs/support-matrix.md`.
- `docs/compatibility.md`, `docs/protocol/v1.md`, `docs/architecture/invariants.md`.
- `docs/architecture/adr-009-server-integration.md`.
- `docs/architecture/adr-014-sql-server-authority.md`.
- `docs/architecture/adr-015-membership-and-removals.md`.
- `docs/architecture/adr-016-encryption-at-rest.md`.
- `docs/architecture/adr-018-local-secondary-indexes.md`.
- `docs/patterns/attachments.md`, `bundles.md`, `intents.md`, and `maui.md`.
- The Tasks, Notes, and Hybrid samples and corresponding tests.

Locate renamed files by symbols if paths differ. Existing documentation contains historical statements that may no longer match the code; reconcile claims with implementation and executed tests.

### 2.3 Rules for implementation

- Read applicable `AGENTS.md` instructions before editing.
- Map each task to current code and existing tests. Mark an already implemented task verified only after its acceptance tests pass.
- Record design decisions as new ADRs using the next available numbers; do not overwrite historical decisions.
- Treat every API name proposed here as provisional. Prefer extending an existing abstraction over introducing a parallel one.
- Keep domain terminology out of library contracts and samples.
- Preserve source-generated serialization and trimming/AOT compatibility.
- Never add a second independent outbox for ordinary document replication; the existing persisted pending operations remain authoritative.
- Do not perform external network calls while holding an authority's database transaction or feed lock.
- Keep protocol extensions optional and negotiated. Update fixtures and compatibility tests before enabling new wire behavior.
- Use disposable test databases and isolated object-store namespaces. Never run destructive tests against a consumer database.
- Missing devices or infrastructure must produce an explicit unverified support entry, not a passing qualification claim.

## 3. Scope and boundaries

### Required workstreams

| ID | Workstream | Principal outcome |
|---|---|---|
| R0 | Release integrity and baseline | Reproducible, auditable packages and accurate support claims |
| L1 | Account lifecycle and execution ownership | Safe startup, suspension, deletion, and foreground/background coordination |
| B2 | Managed blob lifecycle | Durable transfer scheduling, pinning, quota handling, and optional encryption |
| P3 | Offline packages and readiness | Complete versioned content sets with atomic activation |
| I4 | Durable intent helpers | Retry-safe action delivery with distinct execution outcomes |
| S5 | Server publication integration | Atomic publication, recoverable projections, and explicit capture boundaries |
| Q6 | Query and projection support | Bounded local queries and rebuildable local read models |
| H7 | Platform background adapters | Tested, capability-aware native scheduling |
| V8 | Qualification and release | Cross-platform evidence, migration coverage, and consumer documentation |

### Conditional extensions

- SQL Server CDC or Change Tracking capture: implement only after a bounded design spike establishes a need and a safe algorithm.
- A cross-collection local transaction API: implement only for stores that can share a real transaction, and only if aggregate documents and recoverable workflows cannot satisfy a demonstrated requirement.
- Closed-tab browser replication: experimental until runtime execution, credentials, key access, and lease ownership have a tested design.
- Specialized full-text search or relational query adapters: separate optional integrations, justified by measured workloads.

### Explicit non-goals

- Application-specific approvals, signatures, roles, or document schemas.
- A new identity provider, password store, or universal offline-login policy.
- A general distributed transaction coordinator.
- Guaranteed exactly-once external side effects.
- Guaranteed background execution frequency on every platform.
- A replacement for EF Core, OData, or a general-purpose local SQL engine.
- End-to-end encrypted server-blind synchronization in this roadmap. At-rest protection is a separate feature.

## 4. Target architecture and packaging

### 4.1 Responsibility map

| Concern | Suggested location | Dependency boundary |
|---|---|---|
| Readiness, intent, transfer, lifecycle contracts | Existing `Bsync` namespaces or a small optional orchestration assembly | No Blazor, MAUI, EF Core, or vendor SDK dependency |
| Native durable orchestration metadata | `Bsync.Storage.Sqlite` | Reuse SQLite ownership and migration conventions |
| Browser metadata, storage, and lifecycle | `Bsync.Blazor` | IndexedDB and JS interop remain browser-specific |
| Server endpoints and hosting helpers | `Bsync.Server.AspNetCore` | Preserve existing authentication/authorization integration |
| Provider-specific transactional support | Existing SQL Server/PostgreSQL packages | Explicitly state which features each provider supports |
| EF Core publication adapter | Optional package if a reusable implementation is justified | EF Core must not enter the core dependency graph |
| Android/iOS/MAUI schedulers | Optional platform adapter projects | Base library build must not require mobile workloads |
| S3-compatible object storage | Tested recipe first; optional provider package if justified | AWS SDK must not enter core or generic server packages |

Keep the number of packages small. Introduce a package when it isolates a substantial platform/vendor dependency, not merely to match a feature name.

### 4.2 Persistence design

Define a versioned persistence contract for new orchestration metadata before implementing managers. It must support atomic updates for records that share an ownership boundary, bounded enumeration, recovery, and account deletion.

Persist at least:

- Transfer jobs, retry eligibility, resumable transfer references, and pin ownership.
- Package manifests, candidate revisions, active revision pointers, and snapshot leases where needed.
- Recoverable workflow progress and intent execution bookkeeping where not already represented by replicated documents.
- Local read-model schema/version and rebuild progress.

Prefer native provider implementations and the existing transactional infrastructure. Do not serialize mutable orchestration state into an unversioned settings file. File bytes and metadata may occupy different stores; use write ordering and recovery records rather than pretending they share a transaction.

### 4.3 Additional guarantees

Assign stable new invariant IDs after checking the existing invariant catalogue:

1. Readiness never reports content complete when a required item is unavailable or unverified.
2. An interrupted preparation never replaces a usable active revision with an incomplete one.
3. Eviction never removes bytes protected by pending work, an active snapshot, or an active transfer.
4. An account transition cannot send one account's work using another account's credentials.
5. Transfer and workflow progress survive supported process termination.
6. A duplicate intent cannot execute again merely because a transport receipt expired.
7. A local read model never reports a checkpoint it has not actually applied.
8. An expired background execution lease cannot later commit scheduler-owned state as the current owner.

Document each guarantee's boundary: process crash, storage failure, browser eviction, and physical power loss are different failure classes.

## 5. R0 — Establish release integrity

Dependencies: none. This is the first release gate.

Implementation started 2026-10-16 ([evidence](review/release-baseline-2026-10-16.md),
[ADR-019](architecture/adr-019-release-provenance.md)). Delivered in the first slice:

- [x] Add tested release preflight for clean source, matching version tag and released changelog.
- [x] Verify all six package/symbol pairs and record checksums, commit, SDK/runtime and Source Link build maps.
- [x] Carry the patched Pkcs minimum through the SQL Server NuGet dependency graph.
- [x] Enable transitive auditing with findings/audit failures treated as errors, including package consumers.
- [x] Exercise central package management in the package-only web consumer.

R0 remains open: historical source equivalence, remote-source checksum verification, native distribution,
physical-device qualification and ten green workflow runs are not established. Current local provider/browser
runs and published-store fixtures are recorded in the continuation evidence. No release tag or publication was performed.

Later validation also reads the distributed PDBs, verifies DLL/PDB identity and embedded Source Link repository/commit,
and rejects missing/mismatched symbols. The old repository alias in Source Link is canonicalized during build.

### R0.1 Reconcile source, artifacts, and documentation

- [x] Inventory published package versions, source commit metadata, public API baselines, store schemas, and protocol features.
- [x] Investigate why reviewed 0.2.0 artifacts identify `5404785` while later source contains release additions. The inventory establishes the mismatch; the original build invocation/tree are unavailable, so its cause and exact binary/source equivalence remain unproven.
- [ ] Produce releases from a clean checkout at an immutable version tag.
- [x] Fail release builds if package version, tag, repository commit, or changelog status disagree.
- [x] Record package checksums, Source Link metadata, SDK/runtime versions, and test evidence.
- [x] Make README, changelog, roadmap, compatibility notes, and support matrix describe the same release. The development target is 0.4.0 (unreleased); published 0.2.0 provenance remains historical/unresolved.

Acceptance: a maintainer can identify and rebuild the source behind every released package; a consumer smoke test restores the exact produced artifacts without project references.

### R0.2 Resolve dependency and native-library issues

- [x] Update the dependency chain implicated by the reviewed `System.Security.Cryptography.Pkcs` advisories; re-evaluate advisories at execution time.
- [x] Keep NuGet auditing enabled in release validation. Do not treat disabling auditing as remediation.
- [x] Define compatible Microsoft package minimums and verify consumers with central package management.
- [ ] Qualify a maintained SQLite/SQLCipher distribution for each supported native target.
- [x] Document and test mutually exclusive plain/encrypted native SQLite bundles.

Acceptance: package consumers restore/build under the documented warning policy; the security audit has no unaddressed release-blocking findings; native bundle conflicts produce understandable guidance.

### R0.3 Stabilize baseline evidence

- [x] Run core, provider, browser, package-consumer, and available native suites independently.
- [x] Identify flaky tests using recorded seeds, traces, and environment details; fix race conditions rather than merely increasing timeouts.
- [ ] Require ten consecutive green runs of the designated release qualification workflow; separate infrastructure failures from product failures in the evidence record.
- [x] Preserve fixtures made by published 0.2.0 stores for future migration testing. SQLite and IndexedDB fixtures and preservation tests are now preserved ([evidence](review/published-020-store-fixture.md)).

Review evidence only: 637 source tests and 53 published core-package conformance tests passed, plus a published-package SQLite/HTTP/blob smoke test. These numbers are historical, not a substitute for execution-checkout qualification. The source test run required auditing to be disabled because restore failed on dependency advisories; that workaround is not release acceptance.

## 6. L1 — Account lifecycle, offline opening, and execution ownership

Dependencies: R0 baseline inventory. Complete before relying on background adapters or encrypted managed blobs.

Implementation update (2026-10-16): account-scoped CAS metadata is implemented in SQLite and IndexedDB;
`AccountScope`, coordinator open/suspend/resume/close/delete, resumable deletion journals, no-network cold opening,
pending-work policy, and renewable fenced execution leases are implemented. See
[ADR-020](architecture/adr-020-account-orchestration.md). Targeted lifecycle/store/coordinator tests pass;
metadata reload passes in Chromium, Firefox and WebKit. Process-kill metadata/lock recovery and tab-termination locks
also pass on the recorded desktop environment. Credential-required and deferred-migration outcomes now preserve
local data. Shared native/browser account-resource locks now exclude deletion while another lifetime is open;
deletion rechecks pending work after acquiring exclusive access. Explicit key/version lookup and a browser recovery-key
unlock example are implemented; physical-device protected-key qualification remains open.

### L1.1 Introduce one account-scoped lifecycle owner

- [x] Extend existing sessions/coordinator with a single owner for opening, suspending, closing, and deleting an account's replicas and associated resources.
- [x] Make replica identity include stable application/environment/account boundaries; allow an additional organization scope without trusting client-selected scope for server authorization.
- [x] Define explicit lifecycle outcomes: opened, locked/key unavailable, credentials required for network access, migrating, storage unavailable, and stopped.
- [x] Make repeated close/delete operations idempotent and recoverable after interruption.
- [x] Stop active work before credential replacement, account switching, or deletion. Registered participants use shared account-resource access; unmanaged/legacy participants remain host-owned.
- [x] Offer a pending-work summary and explicit preserve/export/discard options. Never erase data solely because token refresh failed.
- [x] Cover database files, IndexedDB databases, blob stores, scheduler jobs, orchestration records, and account keys in coordinated deletion through registered idempotent resource callbacks. Each host must register its actual resources; unmanaged resources are not discovered automatically.

Tests: account A writes cannot upload under account B; an offline cold open performs no network dependency; token expiry preserves local reads and pending work; interrupted deletion resumes; another account's data remains untouched.

### L1.2 Define execution leases

- [x] Reuse `IndexedDbReplicaLease` and existing store concurrency rules where applicable.
- [x] Add native cross-process ownership for synchronization and managed transfer work where foreground and OS workers can overlap.
- [x] Specify ownership scope, bounded lease duration, renewal, cancellation, and generation/fencing behavior.
- [x] Keep local reads and supported local writes available while another process owns network replication.
- [x] Cancel work on lease loss and reject stale scheduler metadata updates. In-flight network requests may still complete; existing operation IDs and receipts must make replay safe.

Tests: competing processes/tabs elect one scheduler owner; process death allows takeover; a stale owner cannot erase the successor's progress; no operation is lost during a foreground/background handover.

### L1.3 Provide key and credential integration contracts

- [x] Document or add host-provided key access with explicit available/locked/unavailable outcomes and a key identifier/version.
- [x] Keep local unlock separate from server credential renewal.
- [x] Provide a native protected-store sample and a browser offline-unlock sample with an explicit threat model. See [local unlock](patterns/local-unlock.md); native lookup is build-only and physical-device acceptance remains open.
- [x] Do not persist raw keys beside ciphertext as a default recipe or claim that browser-origin code cannot access unlocked data.
- [x] Explain that remote revocation cannot take effect on a disconnected device until new information arrives.
- [x] Reuse `CredentialRenewal` and renewal coalescing; do not build a second authentication framework.

Acceptance: an encrypted replica can reopen offline using the sample's supported unlock mechanism, while unavailable keys leave the data intact and inaccessible. Automated tests must not log keys, tokens, or document bodies.

## 7. B2 — Managed blob lifecycle

Dependencies: L1 persistence and ownership contracts. Reuse existing blob stores and transfer protocol.

Implementation update (2026-10-16): durable transfer scheduling, managed pin ownership, conservative reservations,
native exclusive locks and browser Web Locks are implemented. Native failure tests cover resumed offsets,
credential/network failures, cancellation, deduplication, bounded scan rotation, corruption, missing uploads,
capacity pressure and reader pins. See [ADR-021](architecture/adr-021-managed-content.md) and
[composition guide](patterns/managed-content.md). Native/browser inventory includes partial/staging payloads; unknown
content consumes protected capacity. Presigned upload refresh is tested with a fake bucket. New reader/snapshot lifetime
pins are reclaimed after process/tab death releases their locks; custom/legacy owners remain conservative. Download
redirect credential qualification, encryption and real S3 qualification remain open. Transfer progress reports durable
partial offsets separately from verified completion; observer failures cannot interrupt the transfer.

### B2.1 Persist and schedule transfers

- [x] Introduce a transfer manager over `HttpBlobTransfer`, with durable jobs scoped by account, endpoint, content hash, and direction.
- [x] Persist state transitions: queued, running, waiting for connectivity/credentials, paused, failed, complete, and canceled.
- [x] Deduplicate equivalent jobs without merging authorization across accounts or server endpoints.
- [x] Support priorities, bounded concurrency, retry/backoff, cancellation, and a time-budgeted drain operation.
- [x] Resume from verified local/server progress; never trust an obsolete in-memory byte offset.
- [x] Refresh expired presigned URLs and keep application credentials off third-party upload/download requests. Downloads use opt-in `DownloadTickets`; legacy automatic redirect mode retains its documented limitation.
- [x] Coordinate document upload readiness through `ReadyToPush`; server handlers must independently validate blob availability.
- [x] Report byte progress, retry reason, and verification state through subscriptions and diagnostics.

Tests: interruption and restart at several transfer offsets; lost completion response; expired URL; hash mismatch; authentication failure; duplicate jobs; cancellation; missing file; oversized content; a blocked large transfer does not starve small urgent work.

### B2.2 Add pinning and storage budgets

- [x] Implement durable pin ownership for pending uploads, referenced local work, active package revisions, active readers, and in-progress preparation.
- [x] Use owner identities and transactional reference records so a crash cannot incorrectly decrement a shared reference count.
- [x] Add configurable soft/hard budgets and a deterministic eviction policy for unpinned content.
- [x] Account for partial transfers and staging revisions as well as complete blobs.
- [x] Surface an explicit insufficient-capacity result when only protected content remains.
- [x] Integrate browser quota estimates/persistent-storage requests as hints, not guarantees against eviction.
- [x] On startup and on access, detect externally missing content and invalidate readiness accordingly.
- [x] Add a repair pass for orphaned metadata/partials without deleting unrecognized pending work.

Acceptance: pressure evicts eligible cached content only; pending uploads and reader-held snapshots remain usable; deletion races cannot remove newly pinned content; a browser storage-loss event is never reported as an empty-but-synchronized success.

### B2.3 Qualify S3-compatible integration

- [x] Extract the existing `S3BlobStore` recipe only as far as needed for a supported adapter.
- [x] Cover authorization/ownership records, presigned URL expiry, range downloads, canceled uploads, and garbage collection. Moto integration covers ownership/ranges/interrupted PUT/abandoned staging/grace-period collection; deterministic HTTP tests cover expiry refresh and credential isolation. Actual service signature expiry and browser CORS remain unqualified below.
- [x] Explicitly document whether uploads resume by chunk/multipart or restart as a whole presigned PUT. Do not advertise resumable object-store uploads without implementing them.
- [ ] Test browser CORS against an actual supported S3-compatible service and verify credentials are not forwarded to the bucket.
- [x] Preserve the grace period between upload completion and document publication.

Acceptance: the same transfer contract passes against the filesystem store and a qualified S3-compatible implementation; service-specific limitations are documented.

### B2.4 Add optional blob encryption at rest

Version 1 is bounded to 16 MiB per object and authenticates whole envelopes; chunk encryption is not required.
See [ADR-025](architecture/adr-025-encrypted-blob-envelopes.md) for nonce limits, leakage, rotation and migration.
Native process-kill tests cover both sides of rotation commit. Browser interoperability/reload tests cover all three engines.

- [x] Write an ADR describing content hashes, metadata leakage, key ownership, random access, resumable partials, and rotation.
- [x] Use reviewed platform cryptography and authenticated encryption; do not design new cryptographic primitives.
- [x] If chunk encryption is required, specify nonce uniqueness, chunk ordering, authenticated lengths, key/version metadata, and truncation detection.
- [x] Encrypt partial and final local content, including browser storage, without exposing plaintext temporary files in the promised protection boundary.
- [x] Preserve plaintext content identity for transfer compatibility unless a separate protocol change is explicitly designed; document the information leaked by hashes and sizes.
- [x] Keep key rotation crash-recoverable. Wrong keys or corrupted ciphertext must not trigger destructive repair.

Acceptance: wrong-key, tamper, truncation, reordered-chunk, interrupted rotation, and ranged-read tests pass; pending uploads survive migration from an existing supported cache. Database encryption alone must not be presented as blob encryption.

## 8. P3 — Offline content packages and readiness

Dependencies: B2 transfer/pinning and L1 lifecycle. Start from the existing bundle sample.

Implementation update (2026-10-16): bounded immutable manifests, dependency validation, durable staging, conditional
atomic activation, revision-pinned readers, explicit retirement and content-verified readiness are implemented.
SQLite tests cover incomplete updates, restart, unchanged reader revisions, immutable revision identities,
required dependencies and external loss. Browser package reload and fixed-reader-revision tests passed in Chromium,
Firefox and WebKit. Fresh status rehashes the bounded manifest; a durable, age-labelled last-verification read supports
UI polling without content I/O and invalidates on pointer changes. The bounded whole-manifest implementation passes
the declared desktop performance budgets; incremental verification is not required for its supported bounds.
Physical-device/storage-eviction qualification remains open; no broader device qualification is inferred.

### P3.1 Define package manifests and versioned snapshots

- [x] Propose reusable package contracts, for example `OfflinePackageManifest`, `OfflinePackageStatus`, and `IOfflinePackageManager`.
- [x] A manifest identifies a package, revision, required/optional items, exact content identities, sizes, and dependencies.
- [x] Define document items as immutable versioned snapshots or pinned serialized content. A reference to a mutable live collection record cannot guarantee a stable package revision.
- [x] Never combine checkpoints from separate collections into a fictitious global snapshot. The publisher must construct a coherent manifest or declare weaker consistency.
- [x] Validate dependency cycles, missing references, unsupported schema versions, manifest size limits, and authorization.
- [x] Separate latest-known revision, preparing revision, and active revision.
- [x] Treat server-issued content freshness information separately from local completeness. A device cannot know about an unseen server revision.

### P3.2 Implement staged preparation and atomic activation

- [x] Persist preparation before beginning transfers and resume it after restart.
- [x] Verify every required item and pin it before activation.
- [x] Commit a durable active-revision pointer only after the candidate is complete.
- [x] Implement native and IndexedDB activation using each provider's actual transaction/commit boundary.
- [x] Provide snapshot handles so a reader resolves all items through one revision and holds their pins until disposal or recoverable lease expiry. Lifetime locks are non-expiring and recover after process/tab termination.
- [x] Keep the previous revision usable until activation; retire it only after active readers release it.
- [x] Use an explicit state for no complete revision rather than displaying a partially prepared package.
- [x] Preserve readiness information across process termination and detect content loss on subsequent reads.

### P3.3 Expose readiness as a separate contract

Suggested operations, subject to API review:

```csharp
Task<PackagePreparationResult> PrepareAsync(string packageId, PreparationOptions options, CancellationToken ct);
Task<OfflinePackageStatus> GetStatusAsync(string packageId, CancellationToken ct);
Task<PackageSnapshot> OpenActiveAsync(string packageId, CancellationToken ct);
Task RemoveAsync(string packageId, RemovalOptions options, CancellationToken ct);
```

- [x] Return missing required items, optional failures, bytes remaining, active/latest-known revision, last verification, and a resumable failure reason.
- [x] Distinguish locally ready, locally incomplete, known outdated, and freshness unknown.
- [x] Document that `SyncGoal.Complete` is not package readiness, and package readiness is not server approval of local actions.
- [x] Provide bounded status aggregation without rescanning every blob on each UI refresh. Cached verification is explicitly historical; reads/fresh verification still detect loss.

Acceptance scenarios: interrupt preparation before/after every activation boundary; restart while a second revision is incomplete; update while readers hold the first revision; duplicate downloads; revoked access; missing dependency; quota exhaustion; loss of a previously verified blob; concurrent tabs preparing the same package. Run against native and browser persistence.

## 9. I4 — Durable intents and recoverable workflows

Dependencies: L1; B2 when an intent references files. Reuse replicated documents and write handlers.

Implementation update (2026-10-16): `RecoverableWorkflow` persists versioned step progress before idempotent effects;
tests cover lost replies, concurrent execution, cancellation, dependency deferral and explicit failed-step retry.
Immutable request/registration and separate execution/status contracts are implemented, with explicit generated
payload metadata, a Tasks completion/rename adapter preserving existing hashes, and completion/acknowledgment
requests surviving SQLite restart. The Tasks sample command ledger persists identity/results in the domain/publication transaction and retains
compact identities beyond result expiry. SQL Server tests cover new transport IDs, rollback, concurrency, forged
results, actor binding and expiration. This does not complete an intent execution framework, historical-command
migration or application-specific external receivers. Completion and rename execute in the sample; core restart tests also cover document-revision acknowledgments. An optional external-effect outbox recipe now covers transactional enqueue,
stable receiver keys, fenced result publication, lost replies and poison isolation; host/receiver integration remains
explicit. See [ADR-022](architecture/adr-022-workflow-publication.md).

### I4.1 Extract intent helpers from two generic workflows

- [x] Use at least two examples, such as completing a work item and acknowledging a document revision, to validate the abstraction.
- [x] Define an immutable client request with a stable command ID, command kind/schema, payload, target identity, expected target revision, and optional prerequisite references.
- [x] Store server-produced execution state separately from client-controlled request fields, either within a canonical envelope or a separate result document. Separate result contract; Tasks retains canonical server-owned result fields for wire compatibility.
- [x] Expose local persistence, transport acceptance, and business execution as distinct dimensions.
- [x] Use source-generated payload registration; reject unknown kinds/schema versions without reflection-based arbitrary type activation.
- [x] Derive caller identity and scope from authenticated context, never from submitted actor fields.
- [x] Revalidate domain permissions and preconditions when executing, regardless of offline UI checks.

### I4.2 Define execution and deduplication guarantees

- [x] Persist execution identity/result under a unique scoped command ID in the same transaction as domain mutations and result publication.
- [x] Reuse operation receipts for transport retries, but retain command-level deduplication independently for the declared business replay horizon.
- [x] Specify behavior after command/result retention expires. An old command must be rejected or require explicit recreation, not silently execute again.
- [x] For database-only handlers, execute atomically and publish the outcome in the accepting transaction.
- [x] For external work, atomically enqueue an outbox entry and return a queued execution state; dispatch outside the transaction. Optional SQL Server recipe; application wires the queued result and host worker.
- [x] Require idempotency keys at external receivers where supported. Document at-least-once delivery where exactly-once effects cannot be guaranteed.
- [x] Specify cancellation as a separate request with an outcome; canceling a client wait cannot recall an already accepted action.
- [x] Separate protocol rejection, business rejection, and retryable dependency failure.

Tests: timeout after commit; repeated command with a new transport operation ID; immutable-request mutation; concurrent execution; stale target revision; missing target later arriving; permission revoked before execution; expired receipt; expired execution ledger; external dispatcher crash after send; forged result fields.

### I4.3 Support recoverable multi-step workflows

- [x] Document aggregate documents as the default for a single atomic editable unit.
- [x] For work spanning collections and blobs, provide a persisted workflow state machine with idempotent steps and observable incomplete/failed states.
- [x] Persist the workflow record before side effects, and resume incomplete steps after restart. SQLite process-kill tests now cover both effect/progress gaps in a two-step workflow.
- [x] Keep dependency ordering separate from atomicity. Server validation remains mandatory if prerequisites are unavailable or rejected.
- [x] Define compensation explicitly where useful; never imply automatic distributed rollback.
- [x] Defer a universal cross-collection transaction API. If a concrete case requires one, add a capability-gated store extension with conformance tests and no false fallback on unsupported stores.

Acceptance: process termination between any two steps leaves recoverable progress; no silently partial success; duplicate recovery does not duplicate effects; optional unsupported transaction capabilities fail explicitly.

## 10. S5 — Server publication and change capture

Dependencies: R0; use current publisher/provider contracts. Can proceed independently of client features.

Implementation update (2026-10-16): SQL Server exposes caller-transaction publication locking; the Tasks API and
intent handler use feed-before-domain ordering. The optional sample projection outbox coalesces current-state
refreshes and atomically consumes request generations with publication. Tests cover commit/rollback, competing
connections, coalescing, restart, deletes and poison isolation. Bounded repair holds an exclusive publication boundary
through replacement, includes attachments/deletions and rolls back partial failure. An optional related-ACL projection and whole-transaction EF execution-strategy retry recipe are now implemented and tested. Online large-feed rebuild remains open. The bounded Change Tracking spike passed; [ADR-024](architecture/adr-024-sql-server-capture-gate.md)
retains controlled publication and explicitly defers a capture adapter pending its full qualification gate.

### S5.1 Publish domain changes in the caller transaction

- [x] Strengthen the EF Core example into a documented, tested adapter or recipe using the same database connection and transaction as the authority.
- [x] Cover `SaveChanges`, explicit transactions, execution-strategy retries, rollback, canonical projection creation, and deletes.
- [x] Define consistent lock ordering between domain rows and sync feed rows; test competing API and replication writes for deadlocks and retry safety.
- [x] Notify listeners only after commit; missed notifications must remain harmless.
- [x] Require explicit projection registration. A generic interceptor must not claim to infer every affected projection or capture raw SQL/bulk operations automatically.
- [x] Enforce server-derived membership and republish when changes in related data affect visibility.

Acceptance: a committed domain mutation has its corresponding feed update; rollback has neither; API and sync writes apply identical domain rules; changes in authorization produce the expected grant/removal behavior.

### S5.2 Add optional outbox publication and reconciliation

- [x] Use an application-transaction outbox when synchronous projection publication is impractical. Document its eventual consistency separately from atomic feed publication.
- [x] Choose either immutable ordered projection events or coalesced current-state rebuilds; specify how stale events are prevented from overwriting newer projections.
- [x] Add durable dispatcher checkpoints, bounded batches, retries, poison-event handling, and reconciliation lag metrics.
- [x] If duplicates have side effects, record event consumption atomically with projection publication. Do not rely on content equality alone for arbitrary event processing.
- [x] Implement bounded repair/rebuild with a protected snapshot boundary and explicit deletion/membership reconciliation. Registered writers are excluded through commit, so catch-up is empty; online large-feed rebuild is not claimed.
- [x] Never run `ReplaceScopeAsync` against an incomplete snapshot without protection against concurrent changes. The bounded Tasks repair holds the publication boundary through commit and refuses overflow; online catch-up and membership remain open.
- [x] Keep external-side-effect outboxes and projection-publication outboxes conceptually distinct, even when sharing infrastructure.

Tests: crash before commit, after commit, and before dispatcher acknowledgement; duplicate/out-of-order events; concurrent rebuild and writes; deleted records; changed readers; one poison event while unrelated scopes progress.

### S5.3 Gate automatic SQL Server capture on a design spike

- [x] Compare controlled publication with SQL Server Change Tracking and CDC for one generic projection.
- [x] Document deployment prerequisites, required privileges, supported editions, schema changes, retention, and restore behavior.
- [x] Define and test a bounded Change Tracking snapshot/version handoff in ADR-024. CDC handoff and production capture remain gated; timestamps, bare `IDENTITY` and bare `rowversion` are not committed cursors.
- [x] Specify capture checkpoint persistence and transaction boundaries with Bsync publication so crashes cannot skip changes. ADR-024 specifies a candidate design; shipping automatic capture remains gated.
- [x] Include deletes, membership-affecting related-table changes, capture lag, retention expiry, and resnapshot behavior in the ADR-024 design; production capture qualification remains open.
- [x] Keep inbound writes routed through normal authorization/domain logic; CDC is not an alternative write authorization mechanism.

Decision gate: ship an optional adapter only after these invariants pass on a real SQL Server instance. Otherwise publish a clear integration recipe and retain controlled publication as the supported route. Do not delay the portable client milestone for this optional adapter.

## 11. Q6 — Local querying and read models

Dependencies: baseline index APIs; L1 for account lifecycle.

Implementation update (2026-10-16): low-cardinality materialized/returned counters distinguish index pages from
full sorting, with a telemetry test and documented paging mutation semantics. Indexed page benchmarks ran at 10k,
50k and 100k records in memory/SQLite. `LocalReadModel` adds atomic batch/progress, immutable generations and full
reconciliation with SQLite restart and three-browser reload tests. The host must provide a real source snapshot;
automatic generation reclamation, incremental journal integration and representative device budgets remain open.

### Q6.1 Make bounded query behavior explicit

- [x] Document which queries use indexes and which scan the collection.
- [x] Add diagnostics for scan-heavy queries and record the number of visited/materialized records without exposing content.
- [x] Demonstrate indexed pagination/filtering for representative 10k, 50k, and 100k-document datasets.
- [x] Specify mutation semantics for paging; do not claim snapshot-stable pagination if results may change between pages.
- [x] Gate compound indexes/new continuation APIs on measured need and provider semantics. Current bounded/indexed APIs meet the declared desktop probe budgets; no speculative API was added.

### Q6.2 Provide rebuildable read-model integration

- [x] Define read models as derived, account-scoped state with their own schema version and progress marker.
- [x] Do not build correctness solely on transient observer callbacks; they can be missed while an app is stopped.
- [x] Use a durable change journal, a transactional projection hook, or a documented full reconciliation algorithm.
- [x] Commit projected changes and their progress marker together when using one store; otherwise use replayable/idempotent processing with an honest lag status.
- [x] Rebuild into a staging generation and activate only after catch-up, preserving a usable prior generation where practical.
- [x] Handle resets, removals, conflicts, account changes, and document upgrades. Full reconciliation uses a host-captured immutable snapshot and explicit source/schema generation; it does not infer conflict policy or a global collection checkpoint.
- [x] Keep rich SQL/full-text engines optional and outside the portable core.

Acceptance: restart during projection application/rebuild; lost notification; source reset; index migration; removal/regrant; concurrent changes. Reads must be correct for the advertised generation or explicitly stale/rebuilding, never silently incomplete.

## 12. H7 — Platform background scheduling

Dependencies: L1 ownership, B2 time-budgeted transfers, existing session/coordinator budgets.

Implementation update (2026-10-16): portable scheduling requests, capabilities/outcomes, durable coalescing and
fenced execution are implemented in `BackgroundWorkDispatcher`; tests prove deferred/unsupported/canceled requests
remain pending and old runs cannot clear new requests. Optional WorkManager/BGProcessingTask recipes and fresh-scope
runtime are implemented and reference-compiled; physical locked-device/OS delivery tests remain unqualified. This
environment now also has the Android workload, but full SDK bootstrap failed with HTTP 404.

### H7.1 Define a portable execution request

- [x] Expose an idempotent request for bounded work for an account, with priority and host-supported constraints.
- [x] Report capabilities and outcomes: completed, remaining work, deferred by host, credentials/key unavailable, unsupported, canceled, and failed.
- [x] Preserve queued work when a scheduler declines or delays execution.
- [x] Reconcile on foreground/resume as the dependable fallback.
- [x] Document that scheduling a task is not evidence that it ran.

The optional Android/iOS scheduler recipes now compile against their real C# platform references, and the common
fresh-account/deadline/fencing runtime is tested. Checkmarks below denote implemented recipe behavior, not OS delivery
qualification. Full Android SDK setup was blocked by an HTTP 404 download; Apple execution requires an equipped host.
The opt-in background URLSession download recipe now has durable per-account receipts, a bounded plaintext OS
spool, fresh-scope verified import and relaunch delegate handling. Five portable failure/recovery tests pass;
OS-delivered callbacks and locked-device file/key access still require actual Apple hardware. See [native background](patterns/native-background.md).

### H7.2 Android adapter

- [x] Use WorkManager with unique work names and appropriate connectivity/battery constraints.
- [x] Open a fresh account-scoped service lifetime in a headless worker; do not depend on a live page or WebView.
- [x] Acquire execution ownership, obtain available keys/credentials, and run within the granted budget.
- [x] Handle cancellation, retries, process restart, and foreground overlap without busy loops.
- [x] Validate current platform restrictions and document periodic scheduling precision honestly.

### H7.3 iOS/iPadOS adapter

- [x] Register supported background tasks and provide complete host configuration instructions.
- [x] Wire expiration cancellation, bounded work, completion reporting, and rescheduling.
- [x] Separate document synchronization from large background file transfer where native transfer APIs are appropriate.
- [ ] Test protected-key availability when the device is locked; defer work safely if keys are unavailable.
- [x] Handle transfer callbacks after process relaunch without requiring the original UI instance. Implemented by the optional URLSession download/inbox recipe; portable recovery tests and binding compilation pass, OS relaunch delivery remains unverified.

### H7.4 Desktop and browser behavior

- [x] Provide desktop startup/resume/minimize/shutdown integration and an optional process/service example where appropriate.
- [x] Preserve browser online/visibility/multi-tab behavior and document storage persistence limitations.
- [x] Provide a PWA sample with app-shell precaching, viewer/static asset coverage, and sync/blob/hint route exclusions from incompatible service-worker caching.
- [x] Treat closed-tab browser synchronization as unsupported by default. An experimental implementation must prove how code, keys, credentials, protocol handling, and leases work in the worker context.
- [x] Never introduce an untested parallel JavaScript replication implementation merely to claim closed-tab support.

Acceptance: real-device tests for OS-delivered runs, foreground handover, process termination, deadline expiration, locked-device key access, and reconnect. Simulator/emulator and desktop browser tests must be labeled separately from physical-device evidence.

## 13. V8 — Qualification, observability, and delivery

Dependencies: each feature's own tests; final gate after required workstreams.

### V8.1 Generic end-to-end samples

- [x] A content-library sample prepares two package revisions, opens locally after restart, survives an interrupted update, and reports readiness.
- [x] A work-item sample edits offline, attaches a file, submits an immutable action, and displays local/transport/execution outcomes separately.
- [x] Run the same application-facing contracts through browser and native storage adapters.
- [x] Include account switching, offline unlock, expired credentials, and a server-originated projection update.
- [x] Keep sample domain types separate from library internals.

### V8.2 Failure qualification matrix

| Failure | Required outcome |
|---|---|
| Process dies immediately after a reported local save | Acknowledged local work survives within the documented storage guarantee |
| Response lost after server commit | Retry returns the existing outcome without a second effect |
| Device reconnects after retention expires | Supported reset/recovery; preserved local work; no silent resurrection or intent re-execution |
| File transfer interrupted or corrupted | Resumption or explicit repair; unverified bytes never exposed as complete |
| Storage quota exhausted | Actionable failure; protected work retained |
| Package activation interrupted | Previous complete revision or new complete revision; no mixed package |
| Account changes during sync | No cross-account writes, reads, credentials, or blob exposure |
| Key unavailable or wrong | Data retained and access refused; no destructive fallback |
| Concurrent tabs/processes/workers | Correct ownership and durable state under takeover |
| Server restored from backup | New epoch procedure; safe reconciliation with explicit recovery states |
| App/store schema upgraded with pending work | Work, IDs, conflicts, and rejection context preserved |
| Old client contacts new server | Negotiated supported behavior or explicit incompatibility |
| Access revoked while work is pending | Defined removal/retention policy; no unauthorized subsequent upload |
| Publication dispatcher or projection builder crashes | Replay/recovery without skipped source changes |

### V8.3 Diagnostics and support evidence

- [x] Add low-cardinality metrics for transfer backlog/age, verified bytes, storage pressure, package completeness, publication lag, and background outcomes. Bounded snapshots report age/truncation; package observations are historical and outbox lag covers completed publications. See [metric semantics](operations/observability.md#managed-offline-metrics-unreleased).
- [x] Distinguish protocol issues from intent business outcomes in status and telemetry.
- [x] Add diagnostic export with sensitive content redacted by default; existing work export remains an explicit separate operation.
- [x] Record OS, architecture, browser/runtime, native SQLite distribution, device type, commit, package versions, and test command with every qualification result. See the [continuation validation manifest](review/offline-validation-2026-10-17.json); historical records keep their original scope.
- [x] Define performance budgets on selected reference devices before feature completion; measure p50/p95/p99 and memory using bounded, reproducible workloads.
- [x] Benchmark local-save latency under bounded managed transfer staging pressure, indexed queries, package activation, encrypted ranged reads and reconnect of a prepared backlog. The desktop probe records p50/p95/p99 and allocations; real network/mobile workloads remain unqualified.
- [x] Document limits and unverified combinations rather than broadening support claims from one successful environment.

### V8.4 Migration and compatibility

- [x] Test upgrades from databases created by published 0.2.0 packages, including pending operations, conflicts, rejections, groups, and unknown fields.
- [x] Version orchestration metadata, blob encryption envelopes, package manifests, and intent payload/result schemas independently where needed.
- [x] Refuse unsupported newer local schemas without modification.
- [x] Define rollback limitations before deployment; preserve original data during destructive-format migrations or recovery.
- [x] Run new-client/old-server and old-client/new-server fixtures for every wire change. Actual published 0.2.0/current processes verify legacy transfers both ways and the additive download-ticket success/explicit-incompatibility paths; core replication wire format is unchanged.
- [x] Update public API baselines, XML documentation, compatibility notes, and package consumer projects together.

## 14. Delivery sequence and gates

Use milestone names rather than promising a specific release number. Map them onto the current compatibility policy during implementation.

| Milestone | Includes | Exit gate |
|---|---|---|
| M0: Trustworthy baseline | R0 | Clean tagged artifact production; dependency issues addressed; baseline evidence reconciled |
| M1: Safe local foundation | L1, orchestration persistence | Offline cold open, account isolation, key access, and ownership takeover verified |
| M2: Complete local content | B2, P3 | Native and browser package preparation/activation/recovery verified |
| M3: Reliable application workflows | I4, S5.1–S5.2, Q6 | Generic intents and projections survive retries, crashes, and upgrades |
| M4: Qualified host scheduling | H7 | Actual supported-device background behavior recorded; fallback behavior verified |
| M5: Supported release | V8 | Published-artifact consumers, compatibility, documentation, and operational runbooks complete |
| Optional: Capture/query extensions | S5.3 and evidence-selected adapters | Independent ADR and provider qualification; no implied support before completion |

Server publication and query work can be implemented independently after M0, but dependent client work must use the settled persistence/lifecycle contracts. Do not bundle every new API into one unreviewable change.

### Suggested pull-request breakdown

1. Baseline/release provenance, dependency remediation, and evidence cleanup.
2. ADRs and conformance contracts for account ownership and orchestration persistence.
3. Native and browser lifecycle/persistence implementations with migration tests.
4. Durable transfer scheduler.
5. Pinning, quotas, repair, and supported object-store integration.
6. Optional encrypted blob implementations and key lifecycle tests.
7. Package manifests, native/browser activation, and readiness API.
8. Generic intent execution/deduplication helpers and samples.
9. Recoverable workflow guidance and any demonstrated helper extraction.
10. Transactional publication, outbox/reconciliation samples or adapters.
11. Query diagnostics and recoverable read-model integration.
12. Platform adapters, each with its own build and device evidence.
13. End-to-end qualification, published-package consumer coverage, and release documentation.

Each implementation PR should include the behavior it enables, changed persistence/protocol contracts, meaningful failure tests, compatibility impact, and the support claims its evidence actually establishes.

## 15. Verification entry points

Run commands from the Bsync repository root. Confirm project paths and test-runner conventions against the execution checkout. These commands describe existing entry points; extend the matrix for new projects.

```text
dotnet build src/Bsync.slnx -c Release
dotnet test src/Tests/Bsync.Tests/Bsync.Tests.csproj -c Release
dotnet test src/Tests/Bsync.Tests.SqlServer/Bsync.Tests.SqlServer.csproj -c Release
dotnet test src/Tests/Bsync.Tests.PostgreSql/Bsync.Tests.PostgreSql.csproj -c Release
dotnet test src/Tests/Bsync.Tests.Browser/Bsync.Tests.Browser.csproj -c Release
dotnet test src/Tests/Bsync.Tests.SqliteEncrypted/Bsync.Tests.SqliteEncrypted.csproj -c Release
```

Provider suites require their documented isolated database configuration. Browser/native suites require their documented runtimes and workloads. Record skipped prerequisites explicitly.

Pack every affected library into an isolated feed, then run `src/Tests/PackageConsumers` with `BsyncPackageVersion` set to that exact version. Preserve source mapping so Bsync dependencies cannot silently resolve to a different artifact. Run both the conformance consumer and the web consumer's `--smoke` path. Add a browser consumer that exercises packaged static assets, and native consumers for claimed targets.

Do not disable auditing, trimming checks, or meaningful tests to obtain a green release. An unsupported platform may remain outside the release's support matrix, provided its limitation is stated accurately.

## 16. Definition of done

A workstream is complete only when its public API, durable format, failure behavior, sample usage, migration path, and support evidence agree.

The supported release is complete when:

- [ ] All required workstreams have passed their acceptance gates on the declared supported targets.
- [x] Prepared content can be reopened offline with no hidden network prerequisite.
- [x] Readiness is stronger than an idle synchronization status and remains accurate after recovery.
- [x] Local acceptance, server acceptance, and business execution are separately observable.
- [x] No acknowledged supported local write is lost in the tested crash/restart scenarios.
- [x] Account transitions, key failures, revocation, retention, and storage exhaustion have documented behavior.
- [x] Existing 0.2.0 pending work survives the supported upgrade path.
- [ ] Platform scheduling claims match actual device evidence.
- [ ] NuGet artifacts have verified provenance and pass package-only consumer tests.
- [x] Applications can adopt the capabilities through documented contracts without copying domain-specific logic into Bsync.

Optional capture, query, and browser-worker extensions remain separately tracked until their own qualification gates pass. They must not be presented as completed merely because the portable offline foundation ships.
