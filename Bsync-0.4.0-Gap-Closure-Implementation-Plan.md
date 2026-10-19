# Bsync 0.4.0 gap-closure implementation plan

Prepared: 2026-10-17  
Status: Core/provider implementation and reference integrations implemented; external qualification and scoped follow-ups remain open  
Baseline: Published Bsync 0.4.0; source commit `e4286fe2141d0d1e836cfc88dbfa5dfd82154d37`  
Execution destination: the Bsync repository  
Scope: reusable library features, provider adapters, reference hosts, tests and release qualification

Scope update (2026-10-19): physical-device testing is **deferred by user instruction** for this implementation pass.
Unchecked physical-device and device-only performance items below are deferred, not failed or verified. This does not
waive non-device host builds, real object-store/CORS qualification, independent crypto review or release provenance.

The [non-device continuation](docs/review/gap-closure-nondevice-2026-10-19.md) closes the S3 sample's mutable-staging
verification/publication race and adds a real-service/CORS qualification runner. Its emulator tests explicitly fail
expiry qualification. The record separates user-deferred items, conditional extensions and unmet non-device gates.

Execution checkpoint (2026-10-17): the [continuation record](docs/review/gap-closure-continuation-2026-10-17.md)
supersedes the initial [repair-slice record](docs/review/gap-closure-2026-10-17.md). Decisions are in
[ADR-026](docs/architecture/adr-026-verified-content-repair.md), [ADR-027](docs/architecture/adr-027-account-ownership-and-protected-metadata.md)
and [ADR-028](docs/architecture/adr-028-experimental-chunked-encryption.md). Checked implementation items do not
close a workstream's separate qualification gates. This is not a qualified stable release.

The [maintenance continuation](docs/review/gap-closure-maintenance-2026-10-17.md) adds bounded quarantine reporting,
cached gauges, resumable native retired-file collection, v1 rotation lock reduction and read-model process-death evidence.

The [ownership continuation](docs/review/gap-closure-ownership-2026-10-17.md) adds the account maintenance proposal,
durable built-in pin reconciliation, foreground-write/network-owner process coverage and joined browser lock release. See
[ADR-029](docs/architecture/adr-029-account-maintenance.md) for its ownership and byte-accounting contract.

The [deadline continuation](docs/review/gap-closure-deadlines-2026-10-17.md) closes workflow-acquisition and late-transfer
acknowledgement gaps, adds bounded-invocation stop diagnostics and byte/time-limited full-content verification.
[ADR-030](docs/architecture/adr-030-execution-budgets-and-verification.md) records the remaining all-path and resumability limits.

The [publication continuation](docs/review/gap-closure-publication-2026-10-17.md) adds explicit dependent audience
publication to API, sync and intent writers, with SQL Server transaction/retry/concurrency coverage. It is a sample-only
continuation; packaged preview `.7` remains unchanged.

The [session continuation](docs/review/gap-closure-session-2026-10-17.md) fixes idle pause polling, adds monotonic
one-shot sync budgets and instruments session execution stops. Its package candidate is preview `.8`.

The [goal-wait continuation](docs/review/gap-closure-goals-2026-10-17.md) includes account resolution, opening and
observations in the original deadline, isolates wait failures from shared replication and joins observations before
replica shutdown. [ADR-031](docs/architecture/adr-031-goal-wait-budgets.md) defines unknown/last-observed results.
Its package candidate is preview `.9`; durable sub-object verification remains open.

The [listener continuation](docs/review/gap-closure-listeners-2026-10-17.md) rejects late/lost lease admission, joins
superseded hint providers before replacement/shutdown and ignores canceled listeners' late hints/capability responses.
Preview `.10` adds acquisition/listener stop diagnostics. [ADR-032](docs/architecture/adr-032-session-listener-lifetimes.md)
retains explicit limits for idle lease-loss signals, nested stop-cause propagation and callbacks that ignore cancellation.

The [idle-lease continuation](docs/review/gap-closure-idle-lease-2026-10-17.md) observes admitted lease loss between runs,
wakes ordinary idle waits and detaches before normal disposal. Preview `.11` preserves pause and server `Retry-After`;
[ADR-033](docs/architecture/adr-033-idle-lease-observation.md) distinguishes ownership signals from acquisition/run signals.

The [nested-stop continuation](docs/review/gap-closure-nested-stops-2026-10-17.md) preserves observed parent causes through
cooperative deadlines and registered workflow/background/read-model/transfer/goal aliases. Preview `.12` changes
diagnostics only; [ADR-034](docs/architecture/adr-034-nested-stop-causes.md) records scope cleanup and opaque-token limits.

The [2026-10-18 implementation record](docs/review/gap-closure-completion-2026-10-18.md) adds durable bounded chunk
scrubbing, generation key-retention journals, session transport/lifecycle cause propagation and native measurements.
[ADR-035](docs/architecture/adr-035-generation-integrity-maintenance.md) distinguishes authenticated chunk progress from
a fresh full hash and key obligations from host authorization to retire keys. External gates remain unchecked.

The [native memory continuation](docs/review/gap-closure-memory-2026-10-18.md) removes a redundant plaintext buffer from
aligned appends and adds isolated exact-package workloads, enforced GC caps, seeded ranges and rotation/retention checks.
The original 31 MiB whole-managed-heap cap failed during 1 GiB append; optimized appends finish but rotation failed in
the repeated exact-package profile despite a passing pilot. All fifteen `.3` workloads passed under the separate 64 MiB
cap. The cache-only working-buffer target, native allocations and browser/device measurements still require qualification.

The [2026-10-19 envelope memory continuation](docs/review/gap-closure-envelope-memory-2026-10-19.md) removes unnecessary
HTML escaping of encrypted Base64 in internal storage envelopes, with legacy-escape resume/rotation/recovery coverage.
Exact preview `0.4.0-gapclosure.20261019.1` passed all fifteen 31 MiB workloads, including three 1 GiB rotations,
with the previous failed candidates preserved separately.
This is native managed-heap evidence; cache-only attribution, native allocations and external gates remain distinct.

The [local qualification continuation](docs/review/gap-closure-local-qualification-2026-10-19.md) adds bounded browser
SHA-256, removes redundant unlimited-capacity inventory scans, exercises WASM/native AOT and Windows trimming, and runs a real
SeaweedFS service with expiry/refresh and three-engine CORS. Preview `0.4.0-gapclosure.20261019.3` has 928 passing source
tests and 210 passing package-consumer tests. Browser private-profile quota failures are retained separately from
temporary persistent-profile measurements. These results do not waive physical-device, external deployment or release gates.

| Workstream | Implemented continuation | Open acceptance boundary |
|---|---|---|
| B1 | Native/browser repair, promotion authorization hook, crash recovery, physical retained-file accounting, repair/quarantine metrics | Crypto recovery is explicit experimental v2 opt-in; measurements are bounded observations with age/partial indicators |
| B2 | Dedicated chunk providers, ranges, partials, rotation, v1 migration, explicit recovery, independent Node vectors | Independent crypto design review; measured native/mobile/browser memory, latency and amplification budgets |
| B3 | UI-independent durable bootstrap, Android/Apple application source and manual build workflow | Android SDK download failed; Mac/Xcode unavailable; full builds, signed/device runs and URLSession host qualification remain open |
| B4 | Account composition/scopes, cooperative execution/goal budgets, lease/listener lifetimes and idle-loss diagnostics, payload protection, downgrade barrier | Opaque host token causes, host resource/key/credential integration; no hard interruption of callbacks |
| B5 | Owned generation cleanup/limits, owner resolver, bounded inventory, resumable exclusive maintenance and durable built-in pin reconciliation | Unknown legacy staging remains protected; callbacks must honor declared budgets; legacy full-list API retained for compatibility |
| B6 | Capability-aware receiver policy, versioned-file protocol bridge, SQL Server conformance, real loopback SeaweedFS expiry/refresh/CORS/orphan checks | External deployment TLS/IAM and deployment-specific writer coverage |
| B7 | Conditional extension deliberately not selected | No declared workload requires grouped packages; individual package limit remains 256 |
| B8 | Published fixtures, actual old-reader rejection, API baselines, exact preview consumers and evidence | Clean immutable new-version tag, remote source checksums, ten designated runs and external/device matrix |

Full-hash verification restarts after interruption rather than serializing a digest checkpoint. New v2 writes require
exclusive old-writer quiescence before the permanent provider feature upgrade. Fixtures were captured during the
continuation using isolated actual published 0.4.0 DLLs, not before the first source edit; their hashes/provenance are
recorded explicitly. No original v1 data or historical fixtures were replaced.

## 1. Objective and boundaries

Complete the remaining infrastructure needed to build dependable offline applications with Bsync: automatic content recovery, large encrypted objects, bounded resource use, safe account ownership, reliable platform integration, and reusable server integration examples.

This document is standalone. It requires no consuming application's repository, domain models or prior planning document. Core path shorthand (`Client/`, `Blobs/`, `Storage/`) resolves under `src/Bsync/Bsync/`; other paths are relative to the Bsync repository root. New API/type/package names are **proposals**, subject to an API design review; existing symbols are identified explicitly.

The work extends the existing replication engine. Do not introduce a parallel document queue, replace working conflict/receipt semantics, or redesign the wire protocol without demonstrated need.

### Library work versus application work

| Bsync should supply | Applications must still supply |
|---|---|
| Durable local state, replay-safe transport and clear execution status | Domain models, validation, permissions and business approval rules |
| Verified file storage, recovery, encryption and bounded transfers | Which content must be prepared, required freshness and offline access policy |
| Account resource ownership and key-provider contracts | Account provisioning, identity mapping and actual protected-key policy |
| Tested scheduler adapters or maintained integration recipes | App identifiers, entitlements, host bootstrap and platform deployment configuration |
| Transactional command/outbox/publication recipes and conformance scenarios | Actual handlers, external receiver integration and coverage of every domain writer |
| Explicit unknown/reconciliation outcomes for external effects | Receiver deduplication or a trustworthy external status lookup |
| Capability/support reporting | Product decisions when a platform cannot provide a requested capability |

Bsync must not claim immediate remote revocation while disconnected, guaranteed OS execution, server approval of offline work, or exactly-once effects at an arbitrary external service. These are not missing features that a generic library can manufacture.

## 2. Baseline: preserve what 0.4.0 already implements

The preceding 0.4.0 review executed 740 source core tests and 55 published-package consumer tests, all passing with zero skips, plus the package web smoke. All six downloaded NuGet packages identified the baseline commit; their consumed archive hashes matched the downloaded archives. A separate published-package regression reproduced the automatic repair limitation described in section 5.

These are inherited review observations, not new test runs performed while writing this plan. Provider/browser/native qualification must use fresh evidence for the implementation candidate. Repository records include additional source and preview-package tests; do not relabel them as production-device or published-release verification.

| Existing capability | Reuse / extend |
|---|---|
| Durable writes, immutable operation IDs, receipts, conflicts, membership/removals | Existing engine, stores, authorities and conformance suites |
| SQLite/IndexedDB orchestration with atomic CAS and execution fences | `IOrchestrationStore`, provider implementations, `ExecutionLease` |
| Offline account open, suspend, resume, journaled deletion | `AccountLifecycle.cs`, `AccountScope`, account resource locks |
| Durable transfer jobs, credentials resume, upload prerequisites | `BlobTransferManager`, `EnsureUploadsAsync`, `HttpBlobTransfer` |
| Pins, capacity accounting, verified manual reimport, reader lifetimes | `ManagedBlobCache`, native/browser blob locks |
| Manifest staging, active/candidate state, immutable readers | `OfflinePackageManager`, `PackageSnapshot` |
| Native/Web Crypto AES-GCM, partial encryption, rotation/migration | `EncryptedBlobCache`, `BlobEncryption`, `BrowserBlobEncryption` |
| Immutable intent registration and distinct execution results | `IntentRequest`, `IntentDefinition<TPayload>`, `IntentStatus` |
| Replayable local workflows | `RecoverableWorkflow` |
| Portable background coordination and native source recipes | `BackgroundWorkDispatcher`, `Bsync.Samples.NativeBackground` |
| Read-model generations | `LocalReadModel`; currently retains generations until account deletion |
| SQL Server command ledger, external-effect/projection outboxes, publication recipes | Tasks sample and SQL Server tests |
| Release checks, package consumers, historical migration fixtures | `scripts/release.py`, package tests, compatibility fixtures |

### Gap register

| ID | Gap | Evidence / classification | Priority |
|---|---|---|---|
| B1 | Package preparation does not automatically replace same-length corrupt completed content | Reproduced against published 0.4.0 | P0 correctness/recovery |
| B2 | Encrypted blobs are limited to 16 MiB and whole-object processing | Enforced constructor limit; open/append implementation | P1 capability |
| B3 | Background recipes lack complete native-host/device qualification | Explicit upstream support boundary | P1 platform |
| B4 | Account ownership, deadlines and protected metadata require substantial manual composition | Existing opt-in contracts and provider behavior | P1 integration hardening |
| B5 | Retired read models, abandoned owner pins and staging need bounded maintenance | Documented retention/conservative ownership behavior | P1 long-term reliability |
| B6 | External intent execution and file/publication integration remain application-owned samples | Existing recipes, not generic turnkey services | P1 reusable guidance/conformance |
| B7 | Independent bounded packages lack a generic grouped activation workflow | Each package activates independently; 256-item limit | P2 conditional extension |
| B8 | Release/support claims need exact-artifact and platform evidence | Version prefix matches 0.4.0, but reviewed changelog says unreleased and no tags were advertised | P0 release gate |

P0 means address before the relevant release is represented as dependable. P1 is required for the corresponding advertised capability. P2 is optional unless a declared supported workload needs it. Do not let optional grouped packages or general capture work delay a targeted repair release.

## 3. Invariants and design rules

1. An acknowledged local write remains recoverable within the documented provider durability boundary. Browser eviction/user clearing remains an explicit limit.
2. Content identity stays the plaintext SHA-256 and declared length. Local encrypted layout must not silently change server object identity or transfer semantics.
3. Downloaded replacement bytes are verified before publication. Existing ownership and readers remain protected through replacement.
4. Cryptographic authentication failure does not by itself distinguish a wrong key from damaged ciphertext. Preserve data and report an explicit outcome.
5. Account deletion needs exclusive physical ownership; metadata lease expiry is not proof that a process, tab or reader has stopped using bytes.
6. Every durable state transition survives replay. Persist intent before irreversible work and publish completion only after the required commit.
7. Metadata transactions and authority transactions contain no external network calls. Long I/O must not hold a global cache metadata lock.
8. Cancellation stops new work, joins started operations, and preserves truthful pending state. Document any non-interruptible atomic commit boundary.
9. Limits apply to bytes and allocations as well as item count. Verification of a few large files can still exceed a time or memory budget.
10. Wire compatibility, local-format compatibility, provider support and OS execution evidence are separate claims.
11. Domain command replay identity outlives transport receipts for the declared business replay horizon.
12. Plaintext tokens, approval secrets and credential-bearing URLs never enter general-purpose orchestration payloads or diagnostics.

Add new ADRs using the next available numbers; do not renumber historical ADRs. Read applicable repository instructions before implementation. Keep source-generated serialization, trimming and AOT support. Preserve public API baselines and versioned fixture coverage.

## 4. Delivery sequence

| Milestone | Deliverables | Dependencies | Exit condition |
|---|---|---|---|
| M0: establish baseline | B8 inventory, regression fixtures, support/format matrix | None | Exact reviewed artifacts and known failures are reproducible |
| M1: recovery and ownership | B1 repair, B4 deadlines/account ownership, B5 basic maintenance | M0 | Damage, cancellation and process-death cases pass without data loss |
| M2: large encrypted storage | B2 format/provider implementation, B4 metadata/key integration, B5 generation cleanup | M1 replacement/lifetime contracts | Large-file integrity, memory, rotation and migration gates pass |
| M3: native platform integration | B3 host adapters and real-device evidence | M1; M2 for large encrypted workloads | Each claimed platform meets its own execution/key gates |
| M4: application integration toolkit | B6 reusable samples, conformance and external failure semantics | M1; can proceed alongside M2/M3 | Independent consumers integrate without copying undocumented assumptions |
| M5: grouped preparation, if needed | B7 | M1, B5 | All required child revisions are observed consistently |
| M6: release qualification | B8 exact-package matrix, docs, provenance, upgrade evidence | Required preceding milestones | Every advertised capability has executed evidence |

Prefer a narrowly scoped compatible repair release for M1 if API/behavior policy permits. Introduce new storage formats and optional platform packages in an explicitly versioned feature release. Version numbers are to be chosen by the maintainer, not assumed by this plan.

## 5. B1 — automatic, verified content repair

### Observed problem

`OfflinePackageManager.Queue` resumes a completed download only when `SizeAsync` differs from the manifest length. Same-length corruption passes this check. Subsequent package verification marks the revision incomplete, but preparation performs no download. Merely requeueing is insufficient: `HttpBlobTransfer.DownloadAsync` also verifies existing content and rejects corruption instead of replacing it.

Manual verified reimport already repairs suitable plaintext content while retaining pins. Extend this machinery into a durable repair workflow instead of deleting pinned bytes or weakening validation. The encrypted implementation deliberately refuses replacement when existing ciphertext cannot be authenticated; preserve that protection unless an explicit recovery policy authorizes a separately verified replacement.

Primary files: `Blobs/OfflinePackageManager.cs`, `BlobTransferManager.cs`, `HttpBlobTransfer.cs`, `ManagedBlobCache.cs`, `EncryptedBlobCache.cs`, provider caches and tests under `src/Tests`.

### Contract and state design

Add a structured verification result with at least: verified, missing, invalid length/hash, key locked/unavailable, authentication failed, unsupported format and I/O failure. Names are provisional. Avoid interpreting every `IOException` or `CryptographicException` as repairable corruption.

Add a durable repair record, separate from ordinary document pending operations, containing schema version, content identity/length, source authority identity, observed content generation, repair generation, state, attempts, retry time and stable error code. Never persist presigned credentials. Bind replacement to the observed generation so a stale repair cannot overwrite a newer rotation/import.

| State | Durable meaning | Recovery behavior |
|---|---|---|
| Needed | Verification established a repairable problem, or host explicitly requested recovery | Re-evaluate authorization/key policy and acquire ownership |
| Downloading | Isolated replacement staging exists or is being created | Resume verified staging progress |
| Verifying | Required bytes received, integrity not yet accepted | Verify full expected plaintext identity/length |
| ReadyToPromote | Verified replacement and durable promotion intent exist | CAS/lock-protected switch of logical generation |
| Completed | New generation is active and job state is reconciled | Reclaim retired bytes only when readers/policy allow |
| Deferred | Credentials, key, capacity or source temporarily unavailable | Preserve data and retry only after an appropriate trigger |
| Failed / RecoveryRequired | Permanent absence, repeated bad source data, unsupported format or ambiguous crypto failure | Surface stable diagnostics; no destructive automatic fallback |

### Implementation checklist

- [x] Add the published-package reproduction as a regression that expects successful repair. Cover missing, truncated and same-length damaged content separately.
- [x] Decide whether structured verification belongs on an optional cache capability interface or a new manager service; preserve compatibility for third-party `IBlobCache` implementations.
- [x] Feed failed verification into repair scheduling for both the active revision and staged candidates. Repairing only the latest candidate can strand a still-active older revision.
- [x] Add an explicit replacement-download path that bypasses the corrupt object without bypassing hash validation, authorization, quotas or transfer ownership.
- [x] Download into isolated, managed staging with reserved capacity for old bytes, partials and replacement. Network I/O must occur outside global metadata transactions/locks.
- [x] Hash and length-check the complete replacement. Recheck authorization and expected cache generation before promotion.
- [x] Publish replacement atomically per provider. Retain stable logical pins; native open handles and browser snapshot generations must remain valid until release.
- [x] Make the content switch and repair/transfer completion recoverable when they cannot share one physical transaction. Never mark a job complete before its bytes are durably reachable.
- [x] Fence stale repair workers and serialize competing import, rotation, eviction and repair on the same identity. Specify lock ordering and test for deadlocks.
- [x] Route key locked/unavailable to a deferred state. Route authentication failure to conservative recovery unless explicit policy authorizes verified replacement while preserving the unreadable generation.
- [x] Cap retries for repeated upstream hash mismatches; retain a stable permanent failure or reconciliation state rather than an endless redownload loop.
- [x] Invalidate cached readiness after damage or generation replacement; preserve verification timestamp semantics and distinguish active from candidate status.
- [x] Emit content-free repair metrics: queued count, repaired bytes, duration, retry/failure codes, protected/quarantined bytes. Cached gauges mark partial/stale observations; page histograms are not whole-cache totals.

Acceptance: after same-length corruption, preparation obtains a verified replacement and restores readiness automatically on supported plaintext paths. On encrypted paths, missing/wrong-key scenarios never delete or silently overwrite unreadable data; explicitly authorized recovery has its own passing test. Kill the process before and after promotion and before job acknowledgement: a valid committed generation or recoverable pending repair remains. Existing pins and readers survive.

## 6. B2 — bounded-memory encrypted storage for large objects

### Problem and architectural decision

`EncryptedBlobCache` v1 enforces a maximum of 16 MiB, authenticates the entire object on open, and rewrites the entire partial on append. Raising its limit would amplify memory and write costs without fixing the architecture.

Add a new versioned, chunk-based local format. Preserve v1 reading and explicitly select v2 writes. Reuse platform AES-GCM/Web Crypto primitives; do not implement cryptographic algorithms. Obtain a cryptographic design review of the envelope, nonce/key usage, associated data and rollback boundaries before declaring the format stable.

The existing `IBlobCache` is primarily an object abstraction. A v2 implementation must not pretend that a large plaintext object maps to one whole-object encrypted backing blob. Introduce an internal chunk/manifest storage abstraction or an optional provider capability with native and browser implementations, and adapt it to the existing public cache contract.

### Format requirements

| Component | Required design |
|---|---|
| Authenticated manifest | Format/algorithm version, account/cache boundary, object identity, generation ID, key reference, chunk size/count, total length and ordered chunk descriptors |
| Chunk identity | Immutable physical chunk record; associated data binds format, object generation, chunk index and plaintext length |
| Nonces and key usage | Define a reviewed uniqueness strategy across objects, retries and rotation; document limits and collision budget. Never derive nonce from offset alone under a reusable key |
| Completion | Seal only after all declared chunks exist and the full plaintext SHA-256 and length match |
| Partial state | Only authenticated, durably committed chunks count toward resumable progress; a short tail is separately versioned |
| Atomic publication | Switch a manifest/generation pointer only after every referenced chunk is durable; readers pin a generation |
| Range reads | Authenticate the manifest and each requested chunk before returning that chunk's plaintext; reject truncation, reordering, substitution and tag failure |
| Integrity claim | Full hash verified at ingest/repair; range reads authenticate requested chunks. Do not claim untouched chunks were freshly scrubbed |
| Metadata exposure | Document visible identities, lengths, access patterns and key references; encrypt sensitive manifest fields where the declared threat model requires it |

Local metadata authentication cannot independently detect an attacker restoring a complete older valid snapshot. State this limitation unless a separately trusted monotonic anchor is added. Normal crash recovery still must reject stale concurrent writers through generations and CAS.

### Implementation checklist

- [x] Write an ADR with byte-level format, parser bounds, error taxonomy, tamper model, key lifecycle, wire compatibility and rollback rules.
- [x] Add provider operations for immutable chunk write/read, bounded inventory, durable manifest publication and generation-aware garbage collection.
- [x] Implement native and browser backends. Use bounded IndexedDB records/transactions; do not accumulate all chunk bytes or descriptors in one unbounded JS/.NET array.
- [x] Implement a seekable read stream or documented asynchronous range interface with memory proportional to bounded chunk buffers, not file size. Define concurrent-read limits and cancellation behavior.
- [x] Implement partial writes so each full chunk is encrypted/committed once. Rewriting a bounded partial tail is acceptable; rewriting all prior content per append is not.
- [x] Calculate resumed plaintext offsets from verified committed metadata/chunks. Do not equate ciphertext file length with plaintext transfer offset.
- [x] Verify the full plaintext hash incrementally before completion. If the hash state cannot be portably serialized, rehash committed chunks after restart rather than trusting an unverified digest checkpoint.
- [x] Integrate capacity reservation with physical ciphertext overhead, staging, retired generations, partial tails and garbage journals. Keep reservations conservative across interruption.
- [x] Implement verified B1 repair through a new generation. Repair never changes logical plaintext identity.
- [x] Implement resumable key rotation using new chunk generations, then a manifest switch. Avoid mixing old/new chunk keys through an ambiguous manifest.
- [ ] Retain old keys until all complete/partial/retired live-reader generations have migrated or been explicitly retired. Host key deletion must also account for v1, protected metadata and backups.
- [x] Persist and page generation key obligations through partials, rotation, retirement, readers and quarantine; legacy unknown references conservatively block retirement. Collection removes journals only after physical cleanup and reader release.
- [x] Migrate v1 to v2 with a durable journal and preserved original bytes. Unknown/newer formats must fail without mutation. Do not silently migrate on ordinary reads.
- [x] Reuse the existing plaintext HTTP blob protocol. If native encrypted-transfer features require a wire extension later, negotiate them separately.
- [x] Add independent format fixtures, malformed-input bounds tests and native/browser interoperability vectors; do not rely only on round trips through one implementation.

### Proposed qualification workloads

Ratify these targets before implementation: 1 MiB, 16 MiB, 17 MiB, 256 MiB and 1 GiB objects; configurable chunk sizes; random and sequential reads; restart after every chunk/publication boundary. Provider support can declare a lower tested ceiling, but never silently fall back to plaintext or raise a cap without evidence.

- [ ] Demonstrate less than 32 MiB incremental cache-managed working buffers for a single stream with chunks up to 1 MiB, excluding the viewer/caller and measured runtime baseline. Record actual browser JS/WASM duplication separately.
- [x] Supply a reproducible native exact-package memory harness with fresh processes, effective GC-cap checks, append/read/range/scrub/rotation/collection assertions, failure retention and configurable diagnostic profiles. This is tooling completion, not acceptance of the preceding memory target.
- [x] Supply a fresh-process browser measurement matrix with independent native reference hashes, bounded caller buffers, allocation-counter capability checks, sampled managed/JS memory, WASM capacity, crypto-buffer overlap, range I/O, cancellation and per-operation percentiles. Fix full-content browser SHA-256 buffering exposed by the 1 GiB workload; native platform hashing and storage formats remain unchanged.
- [x] A small range reads only its intersecting chunks plus bounded manifest metadata, not the entire object.
- [ ] Sequential appends produce approximately linear stored-byte I/O; record physical write amplification with a declared target and tolerance.
- [ ] Report p50/p95/p99, allocations, peak memory, physical bytes and cancellation latency on selected mobile/native/browser reference targets.

Acceptance: large files open, resume, repair, rotate and migrate within published bounds; every byte returned is authenticated according to the documented range contract. Tampered/reordered/truncated chunks and manifests fail safely, and process death never exposes an unsealed partial as complete.

## 7. B3 — supported native background integration

Primary files: `src/Samples/Bsync.Samples.NativeBackground/*`, `Client/BackgroundWork.cs`, platform test hosts, `.github/workflows/native-background.yml` and `docs/patterns/native-background.md`.

The existing common runner, Android WorkManager, iOS processing-task and durable download callback sources are the starting point. Their reference compilation is useful but does not qualify app startup, entitlements, key availability, lifecycle delivery or callbacks on physical devices.

### Shared host contract

- [x] Define a maintained integration boundary for durable account enumeration, fresh account scope creation, protected-key access, credential acquisition, scheduler registration and worker shutdown.
- [x] Require account identity verification before any worker reads stored work or imports a callback file. Use the same resource-lock namespace and catalog as foreground work.
- [x] Bound account enumeration and work per run. Return explicit completed, remaining, credential-required, key-unavailable, unsupported, expired/cancelled and failed outcomes.
- [x] Persist new work before requesting OS execution. Coalesce scheduler requests without losing a newer durable generation when a prior run finishes.
- [x] Apply a deadline to account opening, key lookup, execution and shutdown, not just the inner sync call. Record overruns by a noncooperative host callback honestly.
- [x] Supply a complete, minimal headless sample with no dependency on a page, WebView, UI-selected account or notification observer.
- [x] Expose capability discovery so unsupported charging/network/transfer constraints are not silently ignored.
- [x] Integrate foreground/resume reconciliation as the fallback; scheduled does not mean executed or complete.

### Android

- [ ] Complete a real MAUI/native test host build with declared SDK/workload/AndroidX dependencies, permissions, initializer and worker registration.
- [ ] Preserve unique work names and Java worker identities across upgrades. Test coexistence with a host-supplied WorkManager factory/configuration.
- [ ] Wire stop/cancellation into the same runtime token and join account resources before release. Verify retry/backoff does not spin on locked keys or invalid credentials.
- [ ] Test process termination, reboot, OS constraint changes, foreground overlap, task replacement/coalescing and account deletion.
- [ ] Record physical-device behavior under battery restrictions and force-stop separately. Do not imply that these conditions guarantee execution.

### iOS/iPadOS

- [ ] Provide complete AppDelegate/bootstrap, allowed-identifier and background-mode configuration in a real host; verify registration during all relevant launch paths.
- [ ] Wire expiration, completion reporting and rescheduling exactly once per callback. Return deferred outcomes if protected keys are inaccessible.
- [ ] Integrate stable background URLSession identifiers and durable download receipts at relaunch. Import only account-authorized, hash/length-verified files and keep receipts until durable acknowledgement.
- [ ] Make deleted-account and stale-callback handling idempotent. An OS callback must not recreate a removed account or key.
- [ ] Specify the OS plaintext spool boundary, file-protection settings and cleanup behavior. Test it on device; encrypted destination storage alone is not evidence of end-to-end at-rest encryption.
- [x] Either implement a separate durable background-upload recipe with file-based staging, completion reconciliation and idempotency, or explicitly declare it unsupported. Download callbacks do not prove upload support.
- [ ] Test locked-device behavior, process relaunch, cancellation, ticket expiry, callback duplication, account switch and interrupted verified import on physical devices.

### Distribution and qualification

- [x] Decide whether to ship optional platform packages or maintained source recipes after the API stabilizes. Names such as `Bsync.Hosting.Android` / `Bsync.Hosting.Apple` are proposals, not existing packages.
- [x] Keep platform SDK dependencies out of core/browser assemblies; execute exact-package browser WASM AOT, Windows native AOT and Windows self-contained trimming. The Windows publications treat AOT/linker warnings as errors. See the local qualification record for executed checks and the native benchmark's executable/input-package identity distinction.
- [ ] Build and execute signed native release hosts with their platform SDKs. Browser AOT and the Windows benchmark consumer do not satisfy Android/Apple host qualification or production application signing.
- [x] Publish a support table distinguishing source-tested, reference-compiled, full-app-built, simulator-executed and device-executed capabilities.
- [x] Include deterministic scheduler doubles and injected-time portable conformance for durable generation/coalescing, unsupported constraints, key deferral, cancellation, joined account scopes, foreground reconciliation and deleted-account safety (`BackgroundWorkTests`, `NativeBackgroundRuntimeTests`, `HeadlessSampleHostTests`, `BackgroundDownloadInboxTests`).
- [ ] Execute actual OS scheduler/lifecycle tests on device. Portable conformance cannot satisfy this gate; physical-device testing is explicitly deferred.

Acceptance: each advertised platform has a complete buildable integration host and executed lifecycle/key/ownership tests. Device scheduling delay is reported as OS behavior; work loss, cross-account import, double completion and resource-disposal races fail qualification. A core release may ship without an unqualified adapter if that adapter remains explicitly experimental.

## 8. B4 — safe composition, complete budgets and metadata protection

Primary files: `Client/AccountLifecycle.cs`, `AccountResourceLock.cs`, `LocalKeyAccess.cs`, `BackgroundWork.cs`, `Blobs/OfflinePackageManager.cs`, provider orchestration stores and dependency-injection extensions.

### Account resource composition

- [x] Add a proposed high-level account registration/builder that registers document stores, managed caches, package managers, workflows, background jobs, maintenance and keys as named resources.
- [x] Validate resource names, account boundaries and lock/catalog configuration at startup. Require safe ownership for destructive operations through the new high-level entry point.
- [x] Provide a common account work scope for foreground/headless callers, including cancellation and resource joining. Keep lower-level APIs compatible while documenting their ownership requirements.
- [x] Define a total lock acquisition order covering account lifetime, execution owner, per-content operation, package/group pointer and provider metadata. Add race/deadlock tests that exercise the order.
- [x] Expose pending work without depending on a live UI. Unknown pending counts must conservatively block default deletion.
- [x] Ensure deletion journals survive deletion of registered resources. Delete keys only after encrypted resources and required recovery/export actions are resolved.
- [x] Demonstrate local reads/writes while another process owns network synchronization, without permitting a second unsafe deletion/maintenance owner. Native SQLite process test blocks a real engine push under a renewable network lease and preserves foreground local writes.

### End-to-end budgets

- [x] Make `OfflinePackageManager.PrepareAsync` respect one cooperative end-to-end budget covering queueing, lock acquisition, transfer, verification and activation. Preserve the old active revision on timeout.
- [x] Apply equivalent deadline semantics to upload prerequisites, repair, rotation, migration and maintenance. Document whether budget exhaustion returns resumable status or throws cancellation.
- [x] Use `TimeProvider` and linked cancellation for bounded feature execution; distinguish caller, budget and lease signals through library-owned deadline, lifecycle and transport aliases. Opaque application-linked tokens remain caller-relative; provider-internal timeouts are not an inferred parent cause.
- [x] Include workflow ownership acquisition in the original budget; check late transfer callbacks before acknowledging completion. Instrument bounded invocations with explicit stop reasons, excluding normal lease disposal. General session-loop diagnostics remain outside this coverage.
- [x] Extend deadline enforcement to one-shot sync with an injectable monotonic clock, zero-I/O zero budgets and honest provider cancellation. Classify individual session run stops and prevent paused sessions polling storage.
- [x] Apply one original deadline to session goal account resolution, opening, observations and synchronous progress; return explicit unknown/last-observed results on expiry, isolate waits from shared replication and join observations before replica disposal. Add goal stop diagnostics and exact-package regression coverage.
- [x] Reject late/lost lease admission before status reads/replication; join superseded hint providers before replacement and shutdown; ignore late hint/capability responses. Instrument acquisition and listener lifetimes, including injected-time reconnect backoff.
- [x] Observe admitted lease loss between runs and while paused; wake ordinary idle waits without bypassing pause or server Retry-After, and detach/join before normal lease disposal. Keep ownership and run diagnostics distinct.
- [x] Preserve nested stop causes through cooperative deadlines and registered library aliases, including zero-budget sync/goal waits; retain caller/lease/budget precedence and remove scope associations. Opaque host tokens remain caller-relative; lifecycle and session transport aliases are now registered too.
- [x] Bound new maintenance enumeration/integrity operations by items/bytes/time and persist chunk authentication progress across permitted runs. Retain legacy full-list APIs for compatibility; a fresh full-SHA-256 pass still restarts and requires a sufficient budget.
- [x] Add a byte/time-limited full-content verification entry point that rejects oversized work before opening storage and bounds reads to the declared size plus an EOF probe. Interrupted full hashes restart; durable chunk authentication now uses generation-bound encrypted checkpoints. Legacy full-list APIs remain available for compatibility.
- [x] Do not start a fresh full-size inner budget after most of the outer budget has elapsed. Pass remaining budget/deadline through nested work.
- [x] Test callbacks that honor cancellation and callbacks that do not. The library can stop scheduling new work and preserve ownership; it cannot forcibly interrupt arbitrary host code safely.

### Protected orchestration payloads

Native `SqliteOrchestrationStore` already accepts an encryption key with SQLCipher. Browser orchestration payloads are not encrypted by the blob encryption layer. Do not describe encrypted blobs as whole-account encryption.

- [x] Inventory metadata record kinds: opaque coordination state, content descriptors, workflow payloads and derived rows. Document which may contain application data.
- [x] Design optional payload protection using platform cryptography and `ILocalKeyProvider`, with envelope version/key identity and associated data binding account, logical record key, kind and format.
- [x] Preserve original CAS revisions and atomic multi-mutation/fence semantics. Do not encrypt an execution-fence token and then compare it to plaintext in the provider.
- [x] Choose and document a viable fence design: keep declared opaque control records clear while encrypting application payloads, or implement provider-integrated protected comparisons inside the atomic transaction. Test mixed mutations and stale fences.
- [x] Protect bounded page reads without destroying ordinal keyset pagination. Document metadata leaked by clear keys, sizes, expiration, control records and access patterns.
- [x] Account for ciphertext/base64 expansion in per-record and batch limits. Reject oversize mutations before partial writes.
- [x] Add restartable plaintext-to-protected migration and key rotation. Never downgrade to plaintext after decryption/key failure.
- [x] Avoid placing business payloads in clear control records as a workaround. Use typed registration and validation rather than fragile prefix assumptions.
- [x] Preserve credentials as host-owned secrets; payload encryption is not permission to store transient bearer tokens in durable work records.

Acceptance: the high-level sample cannot delete an account while a managed reader/worker is active; complete preparation honors its declared cooperative budget; protected payloads remain confidential within the documented boundary while CAS/fences still reject stale owners on SQLite and IndexedDB. Unsupported key/schema states leave bytes unchanged.

## 9. B5 — bounded maintenance and safe reclamation

Primary files: `Client/LocalReadModel.cs`, `Blobs/ManagedBlobCache.cs`, `BlobTransferManager.cs`, `OfflinePackageManager.cs`, encrypted storage journals and provider stores.

### Read-model generations

- [x] Introduce explicit generation-reader lifetime ownership before deleting retired data. Current `ReadModelSnapshot` retention semantics must not be broken by a new background cleanup pass.
- [x] Choose an additive disposable snapshot API or a documented versioned API change. Old clients/snapshots that do not participate in ownership must remain protected until a safe upgrade/quiescence boundary.
- [x] Track generation state, active pointer, build owner, creation/retirement and cleanup journal separately. Make abandoned incomplete builds discoverable.
- [x] Reclaim in bounded batches only after proving the generation is neither active nor owned by any live reader/builder. Use physical lifetime locks where required; wall-clock age alone is insufficient.
- [x] Resume cleanup after process death without deleting a newly activated generation. CAS/fence the transition to reclaiming and revalidate ownership under the correct lock.
- [x] Report retained/staging bytes and generations, not just row count. Add configurable retention limits that defer/refuse rebuild rather than evicting protected readers.
- [x] Document stable-source snapshot requirements; cleanup does not make ordinary live pagination a consistent rebuild source.

### Cache owners and jobs

- [x] Add a bounded owner-resolution contract for application-defined pins with outcomes live, released, unknown and temporarily unavailable. Unknown/unavailable means preserve.
- [x] Reconcile built-in package, transfer, reader, snapshot, repair and rotation owners from durable state; never rely only on event notifications. Exclusive-account pin scans restore package/transfer ownership and reclaim proven abandoned lifetimes; unresolved repair and encrypted generation/quarantine state remain protected under their separate protocols.
- [x] Close the crash window where a terminal job record is removed but its pin remains. Use a cleanup journal or equivalent idempotent reconciliation.
- [x] Reclaim abandoned staging only with proven ownership/generation state. Keep a quarantine inventory for unrecognized legacy files and ciphertext.
- [x] Add bounded inventory pagination for providers where current full listings become expensive. Do not load an entire large cache into memory to enforce a budget.
- [x] Expose `MaintainAsync`-style functionality as a proposal with byte/item/time limits, dry-run observations and durable continuation. It must acquire account ownership and never outrank urgent pending work indefinitely. See ADR-029 for the implemented opt-in proposal and precise callback/byte-unit limits.
- [x] Preserve content shared by packages, pending drafts and uploads when any one owner retires. Deduplication must not become premature deletion.

Acceptance: repeated prepare/update/read-model rebuild cycles stabilize within the declared retention budget when readers finish. Process/tab death allows supported dead-owner reclamation; paused live readers keep their data. Unknown owners remain reported/protected. Cancelling or restarting cleanup cannot delete active content.

## 10. B6 — reusable intent, publication and external-file integration

This work converts existing recipes into documented, executable integration contracts. It does not introduce domain approval rules or promise that every arbitrary external API is safely retryable.

Primary files: `Client/IntentRequest.cs`, Tasks sample `IntentLedger.cs`, `ExternalEffectOutbox.cs`, `TaskProjectionOutbox.cs`, `TaskProjectionReconciler.cs`, `TaskApiPublication.cs`, `TaskAudiencePublication.cs`, ASP.NET Core blob endpoints and integration tests.

### Intent execution and external effects

- [x] Publish a minimal generic task/action sample with immutable request, distinct canonical result, expected domain revision and authorized caller derived on the server.
- [x] Document ledger uniqueness/fingerprint scope, duplicate behavior, payload mismatch rejection and retained command identity after result/transport receipt expiry.
- [x] Provide transaction helpers only where they remove repeated plumbing without hiding connection ownership, EF retry boundaries or domain validation. The sample's opt-in `TaskAudienceProjection` shares dependent locking/publication; callers retain transaction, retry and validation boundaries.
- [x] Demonstrate database-only execution committing domain effects, ledger and result publication together. Test rollback and lost-commit response.
- [x] For external effects, persist an outbox before delivery and execute network work outside the transaction. Demonstrate stable receiver keys, fenced acknowledgements, bounded retries and poison isolation.
- [x] Supply two receiver test doubles: one deduplicates by key; one lacks deduplication but supports status lookup. Include a receiver with neither capability to demonstrate explicit reconciliation-required behavior.
- [x] Separate not-attempted, queued, delivery-attempted/unknown, confirmed success, business rejection and cancellation observation. A timeout after send is not proof of non-execution.
- [x] Demonstrate credential acquisition at dispatch time, with unavailable authorization producing a preserved deferred state. Never persist access tokens as intent payloads.
- [x] Provide host hooks for cancellation/precondition revalidation; cancellation of a client wait cannot recall an external operation already accepted.
- [x] Add public integration conformance scenarios or a reusable test harness. Keep command/receiver implementations application-owned unless a provider-specific package has a justified stable API.

### Publication and authorization

- [x] Demonstrate shared transaction publication, coalesced outbox publication, and their different consistency guarantees.
- [x] Cover EF execution-strategy retry, delete, related-table change, reader grant/removal and concurrent REST/sync writes with consistent lock ordering. SQL Server dependent-publication tests cover both holding orders and ledger/rollback/replay; authenticated HTTP races cover the default API/sync endpoints. Deployment-specific writer inventory remains required.
- [x] Provide an explicit projection dependency registry in the sample and a checklist for all contributing writers. A general interceptor must not claim to discover arbitrary projection dependencies or raw SQL writes.
- [x] Keep bounded complete-snapshot reconciliation with fail-before-truncation behavior. Test missed notifications, abandoned snapshots and external catalog failures.
- [x] Document safe initial snapshot/catch-up boundaries and source epochs. Do not use timestamps or bare identity/rowversion values as proof of committed feed completeness.
- [x] Keep generic automatic capture/online large-feed rebuild as separately justified work; it is not required to close the reviewed portable gaps.

### External file-service bridge

- [x] Add a generic versioned-file-service example: logical file ID + immutable external version + hash/length/media type + authorized domain owner.
- [x] Persist upload/complete/mapping state so a lost completion reply cannot silently create duplicate external files or leave dangling references.
- [x] Demonstrate actual Bsync blob protocol adaptation rather than passing an unrelated presign endpoint to `HttpBlobTransfer`.
- [x] Derive read/write authorization server-side, including hash lookups and shared-file ownership; content-addressed identifiers are not access tokens.
- [x] Publish the exact verified S3 staging snapshot, with bounded temporary disk and failure cleanup; reject mutable-key substitution between verification and publication.
- [x] Supply an opt-in real-service qualification runner for signed URL expiry/refresh, credentials, ranges, reconciliation, orphan cleanup and browser CORS, with explicit emulator exclusion.
- [x] Execute that qualification against a named real object-store implementation: isolated loopback SeaweedFS 4.48 passed expiry enforcement, one ticket refresh, resumed ranges, credential isolation, lost completion reconciliation, all three browser engines and cleanup. Fix stored `aws-chunked` content encoding via known-length checksum-bearing sample uploads. This closes the real-implementation contract check for the named local service; it does not qualify an external deployment's TLS/IAM policies. Moto's expired-signature behavior remains insufficient evidence.
- [x] Declare when an external service cannot supply immutable versions, trustworthy hashes or completion lookup. Expose the missing capability instead of inventing integrity or idempotency.

Acceptance: an independent sample consumer can wire an application transaction, external receiver and file service using documented contracts. Tests prove one business effect on replay where the receiver supports it, and an honest unresolved outcome where it does not. Approval and domain permission logic stay in the sample/host.

## 11. B7 — optional grouped package preparation

Implement only if supported workloads need an atomic usable set spanning more than one bounded package. Preserve the 256-item individual manifest limit unless a separate scalability design justifies changing it.

- [ ] Define a versioned group manifest that references immutable child package IDs/revisions and required/optional membership, with strict count/depth/cycle limits.
- [ ] Stage child revisions independently, with durable group ownership pins acquired before reporting them protected.
- [ ] Verify all required child revisions and publish one group active pointer under a documented ownership boundary. Do not modify each child active pointer and call that a transaction.
- [ ] Return a group snapshot whose reads resolve through its recorded child revisions, never through independently changing child “latest” pointers.
- [ ] Hold child generations and group reader lifetimes until snapshot disposal; integrate retirement and dead-reader reclamation with B5.
- [ ] Expose group candidate progress separately from the existing usable revision, including optional failures, missing required bytes and observed verification age.
- [ ] Recover interruption between child pin creation, pointer commit and retired-group cleanup. Release only the group's ownership when a child is shared elsewhere.

Acceptance: interruption or one failed child never exposes a partially prepared group. Old readers keep the prior complete group. A group adds local composition semantics; it does not promise global server transactions or automatically consistent live collections.

## 12. B8 — compatibility, release integrity and qualification

Primary files: `src/Directory.Build.props`, `src/Tests/api`, `src/Tests/PackageConsumers`, `src/Tools/Bsync.PackageVerifier`, `scripts/release.py`, `.github/workflows`, `docs/compatibility.md`, `docs/support-matrix.md` and release documentation.

### Preserve and extend compatibility evidence

- [ ] Capture fixtures using actual published 0.4.0 binaries before implementation: pending document operations, conflicts, rejections, groups, transfer jobs, package pointers/pins, orchestration fences, encrypted v1 partial/complete objects and read-model generations.
- [x] Keep existing published 0.2.0 fixtures. A source-generated fixture is not proof of upgrade from a published package.
- [x] Version repair journals, encrypted v2 manifests, protected payload envelopes and grouped preparation independently. Define readers/writers and migration support in a format matrix.
- [x] Specify downgrade behavior before shipping a new writer. Older versions must not silently reinterpret or erase v2 data; reject unsupported formats and retain originals where migration rollback is promised.
- [x] Preserve third-party provider compatibility with optional capabilities or explicit breaking-change notes. Do not add mandatory interface members without a migration path.
- [x] Update public API baselines, XML documentation, source-generated serializers and package-only consumer examples together.
- [ ] Run old-client/new-server and new-client/old-server process fixtures for every wire-affecting change. Local encryption should require no server protocol change by default.
- [x] Verify new features are usable from published/packed packages, including JS/static assets and native runtime assets, without project-reference leakage. Executed Windows and Playwright preview evidence is recorded; mobile/native target qualification remains separate.

### Release provenance and support claims

- [x] Reconcile README, changelog, version prefix, compatibility notes and support matrix. Record historical publication accurately rather than retroactively inventing a tagged build.
- [ ] Produce new releases from a clean immutable version tag using existing preflight/manifest tooling. Do not reuse a published package version with different bytes.
- [ ] Record archive checksums, assembly/PDB pairing, source-link mappings and remote source checksum verification against the release commit.
- [x] Keep dependency auditing enabled, including transitive dependencies and audit-service failures. Resolve findings rather than suppressing them to obtain a green run.
- [x] Retain exact commands, SDK/runtime/platform versions, logs/TRX, package hashes and failure traces. Separate infrastructure failures from product failures without deleting the failure history.
- [ ] Satisfy the repository's existing ten-consecutive-designated-qualification-run gate, or change that policy explicitly in a separate reviewed decision. A collection of selectively rerun tests is not equivalent.
- [ ] Add physical-device, native library and real object-store/CORS evidence for advertised support. Mock storage and browser-engine emulation cannot close those gates.
- [x] Update platform-package artifact inventories and verification if B3 introduces optional packages. The current six-package release script must not silently omit them.

Acceptance: a reviewer can trace each advertised capability to the exact released artifacts and executed tests. A source-only success, old preview result or skipped required device test cannot qualify a new release.

## 13. Validation matrix

Use deterministic fault injection for state transitions plus actual process/tab termination for ownership/durability cases. Prefer representative concurrency/failure tests over tests that simply mirror methods. Once a required suite passes, repeat it only for a meaningful new change or the explicitly designated release qualification gate.

| ID | Required scenario | Expected result | Workstream |
|---|---|---|---|
| V01 | Completed file overwritten with same-length bytes | Replacement downloaded, verified and promoted; readiness restored | B1 |
| V02 | Missing/truncated content in active revision while newer candidate exists | Active content remains repairable independently | B1 |
| V03 | Crash after replacement promotion before job completion | Restart reconciles to valid bytes without duplicate unsafe promotion | B1 |
| V04 | Rotation/import/repair race | Stale generation cannot replace newer state; no deadlock | B1/B2 |
| V05 | Locked/missing/wrong key or corrupt authentication tag | Distinct conservative outcome; no automatic wipe or plaintext fallback | B1/B2/B4 |
| V06 | Full disk/quota with old bytes + staged replacement | Capacity refusal retains work and reports accounted bytes | B1/B2 |
| V07 | 17 MiB, 256 MiB and supported maximum encrypted file | Bounded memory and correct resumable storage | B2 |
| V08 | Random ranges across chunk boundaries | Only intersecting chunks read; all returned plaintext authenticated | B2 |
| V09 | Reordered/truncated/substituted chunk or manifest | Rejected before unauthenticated data is returned | B2 |
| V10 | Restart after each partial chunk/manifest commit | Resume from committed authenticated progress, never mark partial complete | B2 |
| V11 | Key rotation with old reader and interrupted migration | Old reader preserved; key retirement waits for dependencies | B2/B5 |
| V12 | Mixed native/browser format fixtures | Same format semantics and integrity decisions | B2 |
| V13 | Work scheduled while prior worker finishes | New pending generation survives completion race | B3 |
| V14 | OS expiration during account open/verification | Cancellation reaches full operation; resources joined; work remains pending | B3/B4 |
| V15 | Headless relaunch with unavailable protected keys | No UI dependency or replacement-key creation; deferred outcome | B3/B4 |
| V16 | Duplicate/stale download callback after account deletion | No account resurrection, duplicate import or ownership leak | B3 |
| V17 | Foreground/tab/process/OS overlap with deletion | Exclusive deletion waits/refuses; reads remain safe | B3/B4 |
| V18 | Encrypted payload batch with stale execution fence | Entire batch refused atomically; ciphertext does not bypass fencing | B4 |
| V19 | Payload encryption expansion exceeds provider limit | Refused before mutation; documented usable limits | B4 |
| V20 | Verification exceeds preparation budget | Resumable pending state and old active revision retained | B4 |
| V21 | Repeated read-model rebuild/aborted build | Retired garbage reclaimed after readers finish; bounded growth | B5 |
| V22 | Suspended live reader versus dead process/tab | Live reader retained; dead owner reclaimed only through safe ownership proof | B5 |
| V23 | Crash between terminal job removal and unpin | Maintenance resolves leak without deleting other owners' data | B5 |
| V24 | Unknown legacy owner or partial file | Preserved and reported, never reclaimed by age alone | B5 |
| V25 | Domain commit succeeds but response lost | One command effect; result replay or retained identity | B6 |
| V26 | Receiver succeeds but acknowledgement is lost | Receiver deduplication/status reconciliation or explicit unknown outcome | B6 |
| V27 | Related-table permission change and dropped hints | Grants/removals converge; direct reads enforce membership | B6 |
| V28 | External upload completes but file-ID mapping is interrupted | Stable mapping recovered without duplicate upload completion effects | B6 |
| V29 | Child package fails or group activation crashes | Prior complete group remains usable; no partial group visibility | B7 |
| V30 | Published-0.4.0 upgrade and unsupported downgrade | Pending work preserved; newer formats refused without mutation | B8 |
| V31 | Package-only WASM/native consumer | Distributed assets implement the tested API and behavior | B8 |
| V32 | Dirty source, wrong tag, mismatched symbols/source checksums | Release verification fails with no stale success record | B8 |

### Execution levels

| Level | Required evidence |
|---|---|
| Core | Unit, property/seeded concurrency, format fixtures, fault injection and real process-kill cases |
| Providers | SQLite/SQLCipher, IndexedDB across supported engines, SQL Server and any other advertised authority/provider |
| Packages | Exact-artifact conformance, web smoke, published WASM assets and native consumer builds/execution |
| Integration | Transactional application sample, receiver/file-service simulations, real supported object store and CORS |
| Devices | Android/iOS physical host execution and key behavior; native Safari/installed PWA if advertised; actual desktop hosts |
| Performance | Fixed workloads/seeds and reference hardware; p50/p95/p99, peak memory, I/O amplification, storage growth and cancellation |

Record unsupported/unverified targets explicitly. Keep a platform feature disabled or experimental when its required infrastructure is unavailable; do not mark it complete because the portable tests pass.

## 14. Suggested pull-request sequence

Each PR includes its targeted tests and documentation. Split further when review size warrants it; do not combine all work into one change.

| PR | Reviewable result |
|---|---|
| 01 | Baseline fixture capture, repair regression, capability/format inventory |
| 02 | Structured verification and repair journal; no destructive recovery behavior |
| 03 | Native/browser verified replacement, package/job integration, process/tab failure coverage |
| 04 | Shared deadline/account scope composition and complete-budget regression coverage |
| 05 | Terminal owner/staging reconciliation and read-model lifetime/retention contracts |
| 06 | Encrypted v2 ADR, reviewed threat model, parser bounds and independent fixture design |
| 07 | Native chunk provider, range reader and partial resume |
| 08 | Browser chunk provider and cross-provider vectors |
| 09 | Managed capacity, v1 migration, rotation, repair and reader-safe reclamation |
| 10 | Optional orchestration payload protection and atomic fence/CAS conformance |
| 11 | Android complete integration host, adapter contract and device evidence |
| 12 | Apple integration host, callback recovery/protected spool policy and device evidence |
| 13 | Generic intent/external-effect/publication/file-bridge samples and conformance harness |
| 14 | Optional grouped package snapshots and retirement |
| 15 | Exact-package qualification, support matrix, migration/recovery documentation and release provenance |

Do not postpone all device setup until PR 11: provision reference hardware/build agents during M0. PR 13 can proceed alongside encrypted storage work once lifecycle/transaction contracts are stable. PR 14 is optional. PR 15 is a release gate, not a substitute for tests in preceding PRs.

### Definition of done for every workstream

- [ ] Baseline failure or missing capability documented, with an executable reproducer where applicable.
- [ ] API/storage/protocol decisions recorded, including limits and backwards compatibility.
- [ ] Implementation works through supported provider/package boundaries.
- [ ] Failure, cancellation, ownership and migration tests pass; all required external checks have evidence.
- [ ] Performance/storage budgets declared before final qualification and measured on named targets.
- [ ] Samples and operator diagnostics explain recovery without disclosing content or credentials.
- [ ] Public API baselines, source-generated contexts, docs and support matrix agree.
- [x] No claim of support exceeds the executed evidence.

## 15. Execution handoff

When this document is copied into the Bsync repository:

1. Read repository instructions and compare the current checkout with the baseline commit. Classify each task as still missing, partially implemented or verified; do not recreate completed work.
2. Run the baseline suites and save their result files. Reproduce B1 before changing it.
3. Capture published-version fixtures and settle M0 capability/format decisions.
4. Implement the smallest repair slice first, including native and browser behavior. Then follow the dependency sequence.
5. Keep an evidence log per workstream with commit, package version, commands, environment, results, failed attempts and open gates.
6. Stop at a documented support boundary when real device/external evidence is unavailable. Continue independent library work without claiming the blocked capability is qualified.
7. Select a release only after the relevant completion criteria and repository release policy are satisfied.

### Baseline commands

From the Bsync repository root, with the reviewed SDK/workload setup:

```powershell
dotnet test src/Tests/Bsync.Tests -c Release --nologo --verbosity minimal

# Put the actual published 0.4.0 archives in artifacts/packages first.
# Use the repository's isolated package-cache configuration.
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release `
  -p:BsyncPackageVersion=0.4.0

dotnet run --project src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release `
  -p:BsyncPackageVersion=0.4.0 -- --smoke
```

Use `docs/operations/releases.md` for candidate packing, symbol/source verification, HTTP compatibility and package-only browser execution. Use a unique version for every different preview artifact. Required provider/device suites need their own isolated infrastructure; never point destructive tests at consumer databases or production buckets.

### Standalone repair reproducer

In a disposable package-consumer test referencing `Bsync` 0.4.0:

1. Construct in-memory orchestration metadata, native file/blob locks, a managed file cache, transfer manager and package manager. Supply a test-only authorization callback.
2. Import `[1, 2, 3, 4]`, stage one required item with its exact hash and length, and prepare the package.
3. Assert the active package is `ReadyFreshnessUnknown` and its download job is `Complete`.
4. Fault-inject `[9, 9, 9, 9]` into the disposable backing object's bytes, preserving length. Application code must never bypass the managed cache this way.
5. Prepare again with a test HTTP handler capable of serving the correct bytes.
6. Baseline observation: package `Incomplete`, transfer still `Complete`, zero replacement HTTP requests.
7. Fix acceptance: verified replacement downloaded and promoted, package ready, pins retained, restart safe. Add separate key/authentication and browser-provider cases.

## Appendix: pinned implementation references

These references document the reviewed baseline, not the proposed final design.

- [Package preparation/verification](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Blobs/OfflinePackageManager.cs), [HTTP transfer](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Blobs/HttpBlobTransfer.cs), [managed cache replacement and pins](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Blobs/ManagedBlobCache.cs).
- [Encrypted v1 implementation](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Blobs/EncryptedBlobCache.cs) and [ADR-025 protection/format boundaries](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/architecture/adr-025-encrypted-blob-envelopes.md).
- [Atomic orchestration/fence contract](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Storage/OrchestrationStore.cs), [browser orchestration provider](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync.Blazor/IndexedDb/IndexedDbOrchestrationStore.cs), [read-model lifetime/retention](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Bsync/Bsync/Client/LocalReadModel.cs).
- [Native host contract and qualification boundaries](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/patterns/native-background.md), [support matrix](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/support-matrix.md), [recorded validation](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/review/offline-validation-2026-10-17.json).
- [Command ledger](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Samples/Bsync.Samples.Tasks.Server/IntentLedger.cs), [external-effect outbox](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/src/Samples/Bsync.Samples.Tasks.Server/ExternalEffectOutbox.cs), [publication/capture boundary](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/architecture/adr-024-sql-server-capture-gate.md).
- [Release operations and exact-artifact verification](https://github.com/msynk/Bsync/blob/e4286fe2141d0d1e836cfc88dbfa5dfd82154d37/docs/operations/releases.md).

Use existing platform primitives for the proposed encryption format: [.NET AesGcm](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm?view=net-10.0) and [Web Crypto AES-GCM](https://www.w3.org/TR/webcrypto/#aes-gcm). Their availability does not validate a new chunk/manifest design; that design still needs review and independent vectors. For scheduling integration, consult [Android unique work and cancellation](https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/manage-work) alongside the pinned native-host contract and the platform documentation it references.


