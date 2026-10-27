**Bsync missing capabilities and production qualification — implementation plan**

Prepared: 24 October 2026. Status: experimental implementation continuation through B08; acceptance and qualification gates remain open.

Execution record: [B01 baseline and first corrections](docs/review/capabilities-b01-2026-10-24.md).
Current implementation audit: [remaining code and integrations addressed, 26 October](docs/review/capabilities-remaining-implementation-2026-10-26.md).
Qualification history: [26 October hardening and exact preview evidence](docs/review/capabilities-closure-2026-10-26.md).
Continuation: [B02–B08 implementation, validation and remaining gates](docs/review/capabilities-implementation-2026-10-24.md). No workstream is declared production-complete by this local run.

Repository: [msynk/Bsync](https://github.com/msynk/Bsync). Reviewed baseline: NuGet `0.5.0`, source commit `7a5169b3df212a99f128f4d55ecb80bc483ababc`. Recheck the current source, packages and open issues before execution; do not repeat work already completed after this baseline.

This is a standalone library development plan. All implementation, samples, tests, documentation and release artifacts belong in the Bsync repository. It introduces no dependency on a consuming application's entities, database, authentication provider, workflows or UI.

**Objective and scope**

Deliver a supported, verifiable foundation for durable offline document and file workflows across native .NET and browser hosts. Close qualification gaps in existing capabilities and implement the reusable extensions that are currently absent or available only as sample recipes.

The baseline already contains durable document replication, conflict handling, SQL Server and PostgreSQL authorities, SQLite and IndexedDB stores, account orchestration, managed content, prepared packages, intent contracts and recoverable workflows. Preserve and extend these implementations. Do not create competing queues, account identities or persistence mechanisms for the same work.

The plan separates foundational work from extensions. All eight workstreams below have implementation and acceptance requirements. Foundational work can ship independently; an extension is only complete when its own gates pass. New extension features are not prerequisites for every existing Bsync consumer.

| ID | Workstream | Kind | Priority | Main dependencies |
|---|---|---|---|---|
| B01 | Release baseline, failing CI and compatibility evidence | Qualification and defect correction | P0 | None |
| B02 | Stable large encrypted content | Existing experimental capability, hardening and qualification | P0 | B01; B03 for browser budget claims; B04 for mobile claims |
| B03 | Efficient bounded browser storage and capacity accounting | Performance/correctness enhancement | P1 | B01 |
| B04 | Supported native lifecycle and background execution integrations | Existing recipes, completion and device qualification | P0 | B01; exercise B02 where encrypted content is claimed |
| B05 | Durable resumable direct object-store uploads | New capability | P1 | B01; existing transfer and account ownership contracts |
| B06 | Atomic activation of related package sets | New capability | P1 | B01; existing package and managed-cache contracts |
| B07 | Supported relational publication, command ledger and effect outbox helpers | Extract and harden reusable sample infrastructure | P1 | B01 |
| B08 | SQL Server automatic change capture | New optional integration | P2 | B01; B07 publication/outbox foundations |

P0 means foundational release work. P1 means a reusable extension or improvement. P2 means a specialized provider integration with a larger operational burden. Priorities do not imply that an unqualified capability may be advertised as supported.

**Rules that apply throughout**

- Keep the portable core free of Android, Apple, MAUI, EF Core and object-store SDK dependencies. Put integrations in optional assemblies/packages where justified. Package names and public API shapes must be agreed in ADRs; names in this plan describe responsibilities rather than existing APIs.
- Preserve atomic local document/outbox writes, immutable operation identities, replay-safe outcomes, monotone checkpoint application and protection of edits made during network requests.
- Derive server authorization from authenticated context. Client account keys, content hashes, manifest fields and command actor fields are not authorization grants.
- Distinguish local persistence, network acceptance, business execution, verified content completeness and freshness. Cancellation of a wait does not undo durable work or recall a remote effect.
- Persist recovery state before acknowledging progress. Join admitted I/O before releasing ownership or disposing account resources. Enforce fencing where an expired lease could otherwise acknowledge another worker's work.
- Preserve unknown schema versions, unreadable encrypted data, pending work and quarantined evidence. Never replace them silently with empty/plaintext storage or delete them to make a test pass.
- Preserve per-account isolation, physical reader ownership and coordinated deletion. Do not use age-based expiry to evict content held by a live reader.
- Use versioned storage and additive protocol capability negotiation. Older clients must retain supported behavior or receive an explicit unsupported outcome without losing pending state.
- Use source-generated serializers for trimmed/AOT paths. Keep vulnerability auditing enabled. Do not suppress genuine test failures, package downgrades or audit failures to declare completion.
- Record observed limits. Browser persistence requests cannot guarantee retention, mobile schedulers cannot guarantee delivery time, and disconnected devices cannot observe remote revocation immediately.

**B01 — Establish a reliable release baseline**

At the reviewed commit, the full CI run failed in core tests on Windows/Linux/macOS and in browser-related jobs. Successful subsequent nightlies packed artifacts; they did not establish a passing full test matrix. Some documentation also described the published 0.5.0 release as unpublished. These are evidence and reliability gaps, not proof that every failed job represents a library defect.

Implementation:

1. Download and retain the failing jobs' logs and TRX artifacts. Classify individual failures as product defects, nondeterministic tests, infrastructure errors or unsupported configurations. Reproduce the important cases before changing behavior.
2. Correct product defects with focused regression coverage. Make timing tests deterministic where possible; preserve real process-death and lifecycle coverage. Do not replace observable invariants with arbitrary delays.
3. Inventory actual 0.5.0 packages and symbols, archive hashes, dependency minimums, source commit and static browser assets. Verify distributed binaries/PDBs against the declared source. Matching nuspec metadata alone is insufficient.
4. Reconcile README, changelog, support matrix, release procedures and historical qualification records. Separate facts about published bytes from later source changes and local preview measurements.
5. Produce a new immutable candidate from clean committed source. Apply the existing compatibility/versioning policy, protect its release tag and never republish changed bytes under an existing version.

Acceptance:

- Core, encrypted SQLite, PostgreSQL, SQL Server, browser/WPF and packaged-browser jobs pass on their declared targets.
- Tests consume the candidate from an isolated package feed/cache; bundled browser assets are executed, not merely found on disk.
- Complete the repository's ten-consecutive-green qualification gate for the required matrix. Preserve run links and exact commit/package identities; pack-only nightlies do not count.
- Source/symbol verification, old-to-new migration fixtures and required protocol compatibility tests pass. Document known historical gaps without rewriting their evidence.
- Release documentation agrees with actual publication status. Preparing a release does not itself authorize publishing it.

**B02 — Stabilize large encrypted content**

The v1 encrypted wrapper remains capped at 16 MiB. The existing opt-in v2 chunked format supports up to 1 GiB, but independent crypto review and parts of device/performance qualification remain open. This workstream hardens that implementation; it does not replace standard cryptographic primitives with a new scheme.

Implementation:

1. Review the format, nonce generation, associated-data binding, key separation, authenticated manifests, generation pointers, range reads and corruption/rollback threat model. Include the browser streaming SHA-256 implementation in the review scope.
2. Obtain independent cryptographic review. Track findings and resolutions against exact source revisions. Automated vectors and passing tests are supporting evidence, not substitutes for this gate.
3. Make key-use obligations enforceable or explicitly exposed through a supported host contract. Cover aggregate encryption invocations across chunks, manifests, retries, protected metadata, devices and key purposes. A changed key identifier must not reset the usage budget of the same actual key.
4. Finish bounded-memory import, append, verification, range-read, rotation, recovery and maintenance paths across native/browser providers. Audit hidden whole-object copies in JSON, interop and provider operations.
5. Preserve crash-safe migration from v1, reader-safe rotation, downgrade refusal, quarantine and durable key-retention obligations. Distinguish unavailable key access from a confirmed authentication failure; neither permits silent replacement.
6. Publish a stable support contract only for the object sizes, providers and platforms actually qualified. Retain explicit experimental status elsewhere.

Acceptance:

- Exercise zero length, chunk boundaries, 16/17 MiB, 256 MiB, 1 GiB and over-limit refusal where the platform has sufficient capacity. A constrained device/profile gets a documented lower supported ceiling, not an invented pass.
- Verify tampering, missing/reordered/substituted chunks, wrong keys, truncated manifests and unsupported versions. Unauthenticated content is never exposed as verified content.
- Kill/relaunch around chunk persistence, partial checkpoint, final activation, rotation and recovery publication. Acknowledged progress survives; incomplete work remains recoverable.
- Test migration with old readers and pending transfers; prevent old writers from modifying new-format storage. Prove retained-key reporting covers active, partial, retired, quarantined and migration-source generations.
- Measure bounded memory and I/O under both finite and unlimited capacity settings. Distinguish managed, JS, native and physical process measurements; sampled counters must not be labeled instantaneous peaks.
- Independent review findings are closed or explicitly prevent promotion to supported status. Required physical-device results are supplied by B04.

**B03 — Make browser storage budgets efficient and recoverable**

The finite-budget encrypted chunk path currently performs expensive physical inventory checks. Some raw browser operations also retain whole-object behavior. The objective is bounded operations without weakening conservative accounting after crashes or external interference.

Implementation:

1. Profile current inventory and payload reads across increasing object counts and sizes. Record the baseline before changing provider schema or accounting.
2. Design a versioned durable capacity ledger with reservations for admitted writes. Account for complete, partial, staging, retired-reader and quarantined bytes. Clearly state whether each budget includes metadata/index overhead; distinguish cache accounting from browser origin quota.
3. Atomically update accounting with stored payload mutations where the provider permits it. Where metadata and bytes span transaction boundaries, use a journal and recovery reconciliation; never rely on an in-memory counter as the authority.
4. Keep steady-state appends proportional to the new bounded write rather than the existing ciphertext inventory. Retain paged reconciliation for unexpected deletion, interruption, old schemas and detected accounting uncertainty.
5. Add bounded streaming/range operations where whole-object materialization prevents the declared workload. Preserve open-reader consistency across repair and replacement.
6. Make quota exhaustion, accounting uncertainty and unavailable persistence explicit. Do not evict pending work or protected readers to satisfy a new allocation.

Acceptance:

- Instrumentation shows ordinary finite-budget appends do not enumerate/read all existing payloads.
- Two tabs/workers racing for the last available capacity cannot both commit beyond the declared budget.
- Process/tab termination between reservation, write and accounting commit causes conservative recovery without double charging forever or undercounting retained bytes.
- Upgrade fixtures preserve objects, pending jobs and pins. Missing origin data produces an explicit incomplete state.
- Benchmarks cover many small objects and large encrypted objects with realistic finite budgets across Chromium, Firefox and WebKit. Report actual device/browser quota constraints separately.

**B04 — Qualify and support native lifecycle integrations**

Native background adapters and reference hosts already exist as recipes. Complete and qualify them rather than introducing a second scheduler abstraction. Decide through an ADR whether the supported deliverable is an optional package or a versioned maintained integration recipe.

Implementation:

1. Build complete Android and Apple hosts on appropriate SDK/signing infrastructure. Ensure platform-conditional code is compiled; a portable `net10.0` build is insufficient.
2. Integrate fresh account scopes, foreground/headless ownership, durable pending-work discovery, scheduler reconciliation, cancellation and joined disposal.
3. Test local key availability under each declared locked-device/accessibility policy. Headless work must report key unavailability and retain work without prompting or provisioning replacement keys.
4. Complete Android WorkManager and Apple processing-task lifecycle handling. Qualify background download callbacks and relaunch where supported, including durable receipt import and credential-isolated download tickets.
5. Reconcile account deletion and sign-out with OS tasks, callbacks, spooled files and key deletion. Late callbacks must not recreate a deleted account.
6. Keep foreground startup/resume recovery sufficient for correctness when the OS never schedules background execution.

Acceptance:

- Record physical device, OS, SDK/workload, host configuration, package hashes and logs for every supported combination. Include a declared iPadOS configuration if claiming iPad support.
- Cover app suspension, process death, user force-stop, reboot, lock/unlock, token expiry, key unavailability, network changes and OS budget expiration. State where the OS intentionally suppresses relaunch.
- Concurrent foreground/background executions do not duplicate accepted work or dispose each other's resources.
- Background file completion survives relaunch and is verified/pinned before exposure. Test duplicate, delayed and post-deletion callbacks.
- An unscheduled task remains pending and completes during permitted foreground execution. Documentation promises no fixed background deadline.

**B05 — Add resumable direct S3-compatible multipart uploads**

Current direct presigned uploads restart the entire object after interruption. Add multipart resume as a negotiated optional capability, preserving the existing upload path for older servers and small objects.

Implementation:

1. Define protocol operations for initiate, authorize parts, query/reconcile uploaded parts, complete and abort. Bind every session to authenticated scope, immutable content identity, expected size and compatible part layout.
2. Persist server upload/session identity and client transfer state before acknowledging progress. Store part number, byte range and returned receipt/checksum; reconcile with the service after restart rather than trusting stale local completion flags.
3. Refresh expired part URLs without losing valid completed parts. Keep application credentials out of presigned requests and redact all signed URLs/tokens from diagnostics.
4. Make completion retry-safe after a lost response. Verify final content length and content identity before making it available to document references. Multipart ETags are not general full-content SHA-256 checksums; use verified provider checksums where supported or an explicit bounded server verification path.
5. Implement abort, retention and orphan cleanup with ownership checks. Account deletion must invalidate pending work without allowing a late completion to publish into the wrong scope.
6. Keep object-store SDK dependencies in an optional provider integration. Define provider capabilities and fail explicitly when required multipart/checksum behavior is unsupported.

Acceptance:

- Interrupt after one or several completed parts, restart the process, and prove completed parts are not retransmitted unnecessarily.
- Exercise URL expiry, duplicate/out-of-order requests, lost completion replies, conflicting resume metadata and server restart.
- Verify cross-account/session access is refused and incomplete or corrupt content cannot be referenced as complete.
- Run against a real configured MinIO service and another explicitly supported S3-compatible target; label emulator evidence separately. Validate browser CORS and deployment TLS/authentication configuration.
- Old clients/servers retain documented behavior through negotiation. A transport-level completed upload remains distinct from execution of a referencing business action.

**B06 — Add atomic related-package activation**

Existing packages support at most 256 items per manifest. Add a bounded package-set abstraction for consumers requiring one coherent revision across several packages. This is local content activation, not a cross-collection document transaction or an atomic rollout across devices.

Implementation:

1. Specify an immutable root manifest binding exact child package identities/revisions and integrity identities, with bounded fan-out, total metadata size and dependency depth. Reject cycles, missing children and mutable revision substitution.
2. Stage and verify every required child before activating the root. Preserve the previous active set through failed or interrupted preparation.
3. Commit one durable active-root pointer. Readers acquire a snapshot of the root and open all child content through that revision, preventing mixed-version reads.
4. Integrate pins, reader lifetimes, authorization, shared-content accounting, repair and retirement with existing managed cache infrastructure. Count shared bytes consistently and avoid duplicate downloads.
5. Expose set-level active/candidate progress, missing required content and freshness observations. Optional children must have explicitly defined activation semantics.
6. Add compatible migration and negotiation. Do not reinterpret existing single-package manifests or merely raise their item cap without bounding metadata and traversal cost.

Acceptance:

- Prepare a set exceeding 256 total items while each child remains within existing limits.
- Interrupt any child download and terminate before/after root activation; readers observe either the complete old set or the complete new set.
- Concurrent readers keep their revision available through replacement and maintenance.
- Test shared files, quota pressure, corrupted child content, unauthorized children, duplicate IDs, excessive depth and dependency cycles.
- A complete local set is reported with its freshness boundary; activation does not imply that no newer server publication exists.

**B07 — Productize relational publication and action infrastructure**

The Tasks samples demonstrate transaction enlistment, projection outboxes, command ledgers and external-effect dispatch. Extract the reusable infrastructure into supported integrations while retaining application-owned domain rules and receiver policies.

Implementation:

1. Define explicit transaction ownership/enlistment APIs for EF Core and the SQL Server authority. Preserve execution-strategy retries, commit ambiguity handling and deterministic feed/domain lock ordering. Helpers must not silently open an independent transaction inside a caller's unit of work.
2. Provide durable coalesced projection work: enqueue affected IDs in the domain transaction, claim with fencing, reread canonical state, publish and acknowledge atomically. Support deletion and membership recalculation through application-supplied projection callbacks.
3. Extract immutable command-ledger storage with scope/actor binding, payload fingerprint, durable result, concurrent retry handling and identity retention independent of transport receipts.
4. Extract an external-effect outbox with bounded retries, poison isolation, current-authorization hooks, stable receiver identity and fenced result publication. Persist dispatch attempts before calling the receiver; perform no external network operation inside the domain transaction.
5. Expose receiver capabilities for idempotency and outcome lookup. Ambiguous delivery without either capability must yield reconciliation-required status instead of an automatic duplicate send. Do not advertise generic exactly-once external execution.
6. Supply explicit migrations, DI registration, worker lifecycle, metrics and package-only samples. Keep authorization, projection definitions, command handlers, credential acquisition and receiver semantics host-owned.

Acceptance:

- Rollback leaves no orphan publication, command result or effect request. Commit publishes canonical state and ledger/outbox records consistently.
- Concurrent retries and lost responses produce one committed domain execution for one command identity, including after transport receipt expiry.
- Test same identity with changed payload/actor, expired results, missing prerequisites, permanent refusal and credential unavailability.
- Exercise dependent projection/membership updates, multi-collection lock order, deadlocks/retries and crash recovery against real SQL Server.
- A lost external reply leads to safe replay, lookup or reconciliation according to declared receiver capabilities. Stale workers cannot acknowledge newer claims.
- A fresh consumer can use only packaged public APIs and migrations; no copy/paste dependency on sample internal classes remains for the supported path.

**B08 — Implement optional SQL Server Change Tracking capture**

Automatic capture is useful for applications with writers that cannot participate in explicit publication. Implement Change Tracking first as a current-state projection integration. Treat CDC as a separate future adapter unless its event/history requirements are explicitly selected; a Change Tracking implementation does not establish CDC support.

Implementation:

1. Define configured source tables/keys, reverse mappings from related changes to projection IDs, projection schema identity and application-owned reader-membership calculation. Capture does not infer domain relationships or authorization.
2. Validate Change Tracking and snapshot-isolation prerequisites under deployment privileges. Keep runtime permissions minimal. Do not silently enable invasive database features at application startup.
3. Capture initial data and its Change Tracking version within a valid snapshot boundary. For deltas, validate every contributing table's minimum valid version before enumerating changed keys/current data, including deleted keys.
4. Write a durable sealed staging batch with source epoch, schema identity and lower/upper boundaries. Abandon or resume unsealed work according to a documented protocol; never advance the authoritative cursor over incomplete publication.
5. Publish with an application marker atomically in each destination transaction. Preserve ordering or reread current state so an older staged projection cannot overwrite a newer one. Coordinate explicitly with any synchronous publication route.
6. Build a full resnapshot into an isolated generation with deletion/membership reconciliation, catch-up and a proven concurrent-writer handoff. A partial enumeration must never replace a complete scope.
7. Define source backup/restore detection and an operational epoch transition. A numerically plausible Change Tracking version is not sufficient evidence that history was not lost.
8. Expose capture lag, oldest unconsumed boundary, invalid cursors, failed projections and resnapshot progress. Retention expiry must trigger explicit recovery rather than silently skipping changes.

Acceptance:

- Concurrent insert/update/delete during initial capture and catch-up yields the correct final projection with no skipped committed changes.
- Related-row deletion and changed membership identify and republish affected parents correctly.
- Terminate before/after sealing, destination publication and cursor commit; replay is idempotent and does not regress versions.
- Test retention invalidation, table/key/schema changes, source restore, multiple capture workers and mixed publication strategies.
- Publish a compatibility matrix for supported SQL Server editions/versions/deployments and migration privileges.
- Unmapped relationships or an unsupported restore/capture boundary fail explicitly. Documentation states that captured current state is not an immutable business event history.

**Execution order and checkpoints**

1. **Baseline checkpoint:** complete B01 failure diagnosis and freeze fixtures/public API baselines before persistence/protocol changes. Preserve actual published 0.5.0 upgrade inputs.
2. **Foundation checkpoint:** develop B02 and B03 against shared accounting/format contracts; execute B04 device qualification against immutable candidates. An unavailable review/device environment leaves the relevant gate pending, even if portable work is complete.
3. **Transfer/content checkpoint:** implement B05 and B06 with versioned contracts and existing account, transfer and cache ownership. Validate old/new compatibility before promoting either extension.
4. **Relational integration checkpoint:** complete B07 and package-only samples, then B08 using those publication foundations. Do not create a second independent command delivery queue alongside replicated intent delivery.
5. **Release checkpoint:** repeat B01 qualification for the final candidate and every newly claimed target/capability. Documentation and support claims must reflect this exact candidate, not an earlier passing preview.

Each workstream should produce an ADR or ADR update, implementation, focused regressions/conformance coverage, migration/compatibility fixtures, a runnable neutral sample where useful, and a concise completion record listing remaining gates. Use generic notes, tasks, catalogs, documents and receivers in samples.

**Shared validation and evidence format**

Use existing tests and release tooling where possible. Add tests for new invariants and discovered failures rather than duplicating implementation details. Every release claim must identify:

| Evidence field | Required content |
|---|---|
| Identity | Source commit, clean/dirty state, exact package/archive hashes, host binary hash where applicable |
| Environment | SDK/runtime, OS/device/browser, provider version, relevant storage/network configuration |
| Execution | Reproducible command, scenario, expected invariant, actual outcome, duration and retained logs/results |
| Fault boundary | Injection point, persisted state before/after restart and observed recovery behavior |
| Performance | Dataset/object sizes, concurrency, capacity settings, measurement method and predeclared acceptance thresholds |
| Compatibility | Source/target package versions, stored fixture identity, migration/downgrade/protocol result |
| Limitations | Skipped, failed, unsupported and unexecuted cases; external prerequisites still missing |

Set measurable workload thresholds before evaluating optimized code. Include low-capacity storage, slow/interrupted networks, many small objects and representative large objects. Distinguish logical provider I/O from physical-media write amplification and sampled memory from process peak memory. Report failed attempts rather than replacing them with later successful runs.

**Completion criteria**

The foundational milestone is complete when B01–B04 meet their declared gates for the supported targets. B05–B08 may ship as separate optional integrations, each only after its acceptance criteria pass. The entire plan is complete only when all eight workstreams are delivered with accurate support claims; an omitted extension remains explicitly deferred, not completed.

Before a release is described as production-qualified:

- Required functional, fault-injection, provider, package-only and compatibility suites pass on the exact candidate.
- New storage formats have exercised migrations and explicit downgrade behavior; protocol extensions negotiate safely.
- Independent crypto review and named-device qualification are complete for any corresponding stable/support claims.
- Real object-store and SQL Server integration evidence supports the advertised providers and deployment modes.
- Release artifacts have immutable identities and verified source/symbol provenance, and the stability gate passes.
- Samples, API documentation, operational recovery instructions and support matrix agree on implemented features and limitations.

No part of this plan promises authoritative business approval while disconnected, guaranteed browser retention, guaranteed mobile background scheduling, automatic authorization inference, or exactly-once behavior from an arbitrary external receiver. Those boundaries remain explicit in the public contracts.

**Baseline references**

These links are pinned to the reviewed commit for reproducibility. They describe the starting point, not completion of the proposed work.

- [Release workflow and provenance policy](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/operations/releases.md)
- [Support matrix](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/support-matrix.md)
- [Latest local qualification record](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/review/gap-closure-local-qualification-2026-10-19.md)
- [Experimental chunked encryption design](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/architecture/adr-028-experimental-chunked-encryption.md)
- [Managed content and prepared packages](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/patterns/managed-content.md)
- [Native background execution recipes](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/patterns/native-background.md)
- [HTTP blob transfer contracts](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/src/Bsync/Bsync/Blobs/HttpBlobTransfer.cs)
- [Offline package implementation](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/src/Bsync/Bsync/Blobs/OfflinePackageManager.cs)
- [Intents, command ledger and external-effect recipes](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/patterns/intents.md)
- [SQL Server capture decision and experiment](https://github.com/msynk/Bsync/blob/7a5169b3df212a99f128f4d55ecb80bc483ababc/docs/architecture/adr-024-sql-server-capture-gate.md)
