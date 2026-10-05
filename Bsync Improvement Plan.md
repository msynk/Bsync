# Bsync improvement plan — agent brief

Status: execution brief for an AI coding agent. Date: 2026-10-05.
Repository: https://github.com/msynk/bsync. Baseline commit: `54047855b2edbedd55487f033f4206beb7d42a7a`.
Published packages at baseline (NuGet, `0.1.0`): `Bsync`, `Bsync.Blazor`, `Bsync.Storage.Sqlite`,
`Bsync.Server.AspNetCore`, `Bsync.Server.PostgreSql`, `Bsync.Testing`.

This brief is self-contained. Work only inside the Bsync repository. Do not look for, reference, or modify any
application that consumes Bsync.

---

## 1. Mission

Bsync is local-first document replication for .NET 10: durable client replicas (SQLite on native hosts,
IndexedDB in browsers), a single-flight sync session, an HTTP protocol, and an ASP.NET Core authority.

The client engine is strong. The gaps that stop a real line-of-business application from adopting it are:

1. **No durable authority for SQL Server**, and no way to run domain logic inside the authority's transaction.
   Applications that already own a relational system of record (EF Core, SQL Server) cannot adopt Bsync without a
   second database.
2. **No first-class way to publish server-originated changes** (projections, back-office edits, imports, jobs)
   into the feed.
3. **Authorization-shaped replication is weak**: `CanRead` filters after the SQL `LIMIT`, and the only way to
   remove documents from a replica is a full resnapshot.
4. **One session, loop, `HttpClient`, and SSE stream per collection.** An app with 10–20 collections overloads
   browser connection limits and has no aggregate status, ordering, or awaitable "sync now".
5. **No blobs/attachments**, and a single oversized document blocks a collection's push queue.
6. **No evidence on Android, iOS, Mac Catalyst, or native Safari.**
7. **Several robustness gaps** (clock skew, permanent rejections, tombstone growth, storage amplification,
   credential renewal semantics, status honesty).
8. **Trust gaps**: CI is failing, docs disagree with the code and with NuGet, the authority conformance suite is
   not publicly consumable.

Your job is to close these gaps in the order given in section 9, without weakening any existing guarantee.

---

## 2. Rules of engagement

1. **Read first.** Before changing behavior, read `docs/architecture/invariants.md`, `docs/protocol/v1.md`, and
   ADR-001, ADR-005, ADR-008, ADR-009, ADR-010, ADR-012, ADR-013.
2. **Invariants I01–I20 are sacred.** A new store or authority is unsupported until it passes the existing
   conformance suite plus the new cases named in its task.
3. **Fix bugs; never weaken tests.** If a test fails on a new OS or host, find out whether it is a product bug or
   an environment gap. Fix product bugs. Document environment gaps.
4. **Wire format.** Protocol stays v1. New behavior is negotiated through `features` in the pull response (see
   `Protocol/SyncFeatures.cs`; `groups` is the precedent). Unknown JSON members keep being ignored. Any change a v1
   peer cannot ignore needs an ADR first.
5. **Package boundaries (ADR-012).** `Bsync` depends only on DI and logging abstractions. Browser and server
   packages never flow into each other. Native SQLite never enters a WebAssembly graph. New provider, blob, and
   host-integration packages are optional and separate. No SignalR, Redis, MAUI, or cloud SDK reference in the
   core.
6. **Public API.** Pre-1.0 breaks are allowed only with an entry in `docs/compatibility.md` and a reviewed update
   of the public API baselines in `src/Tests/api`. Updating a baseline is a deliberate, reviewed step.
7. **Honest support matrix.** Change a row in `docs/support-matrix.md` to Verified only for runs you actually
   executed; record host, OS, versions, and the command. Compiling is "Build-only".
8. **AOT and trimming.** Every new public API stays `IsAotCompatible`; reflection-based paths carry
   `[RequiresUnreferencedCode]` and have a source-generated alternative.
9. **No publishing.** Do not push tags, publish NuGet packages, force-push, or rewrite history unless the
   maintainer asks. Pack locally and consume the local pack from a clean sample.
10. **Stop conditions.** Each task lists "Stop if". When one hits, stop that task, write down what you found, and
    move to the next independent task. Do not invent a parallel design.
11. **Every task ends with a PR note:** what changed, which invariant/conformance tests ran, on which hosts, what
    was not run, and which follow-up was intentionally left.

---

## 3. Baseline you must not rebuild

Already implemented and tested at the baseline commit. Reuse; extend only where a task says so.

- Atomic local write plus pending operation (SQLite transaction, WAL, `synchronous=FULL`; IndexedDB transaction).
- Immutable operation ids, server receipts with replay, per-document optimistic concurrency, checkpointed atomic
  pull, epochs and resets (`epoch`, `scope-changed`, `expired`), scope fingerprints.
- Conflict handlers: Defer (default), ThreeWayMerge (with set/counter merges via `ThreeWayMergeOptions`),
  ClientWins, ServerWins, LastWriteWins, Delegate. Durable kept conflicts with list/resolve/discard.
- Same-collection dependency groups (`WriteGroupAsync`, `SaveAllAsync`).
- `DocumentUpgrade`, `[JsonExtensionData]` preservation, store schema migrations (SQLite and IndexedDB).
- `ISyncAuthority<T>`, `MapSyncCollection<T>`, `HttpSyncTransport<T>`, `InProcessTransport<T>`,
  `InMemorySyncServer<T>`, `PostgreSqlSyncAuthority<T>` (714 lines; the model for new providers).
- `ISyncCollection<T>` for all Blazor render modes and native hosts; recipes `AddLocalSyncCollection`,
  `AddBrowserSyncCollection`, `AddServerSyncCollection`; multi-account replicas; Web Locks lease for multi-tab.
- `SyncSession` loop: interval (30 s), write trigger, request trigger, SSE hints, browser `online`/visibility,
  exponential backoff with jitter, `Retry-After`, pause/resume, `RenewCredentials`, `AttachLifecycle`.
- `PushOutcome.Accepted` already carries a `Document`, so a server-canonical document can reach the client.
- OpenTelemetry traces and metrics, session and server logs, SQLite check-and-rebuild, export/import of local work.
- 486 core tests pass locally on Windows in about 3 s (`dotnet test src/Tests/Bsync.Tests -c Release`).

---

## 4. Known facts from the review (verify, then act)

Treat these as leads with evidence, not as truth. Re-verify each before you change code.

### 4.1 Documentation drift (task A1)
- README says "pre-1.0, not published"; six packages at `0.1.0` are on NuGet.
- README says the in-memory authority is the only authority in one section and documents the PostgreSQL
  authority in another.
- README and support matrix mention "seven packages" and `0.1.0-preview`; there are six, versioned `0.1.0`.
- `docs/protocol/v1.md` introduction says no database-backed authority exists and scopes are not part of the
  protocol.
- ADR-011 says protocol headers are not implemented; `Bsync-Protocol` and `Bsync-Schema` are implemented.
- ADR-009 names a nonexistent `Bsync.Server.EntityFrameworkCore` package.
- `docs/operations/disaster-recovery.md` says there is no upcasting hook; `DocumentUpgrade` exists.
- `Transport/ISyncTransport.cs` comment says hints are not consumed; `SyncSession` consumes them.
- Support matrix says 487 tests; the suite has 486.
- Roadmap and support matrix say CI has never run. It has run: at the baseline, Ubuntu and macOS unit tests,
  PostgreSQL (Linux) and Pack pass; Windows unit tests and the Browser/WPF job fail. Five of the last seven runs
  failed.

### 4.2 Code-level findings
| # | Finding | Evidence |
|---|---|---|
| F1 | Only durable authority is PostgreSQL with its own `bs_*` document tables; writes that bypass it are unsupported | `Bsync.Server.PostgreSql/PostgreSqlSchema.cs:19-52`, ADR-009 |
| F2 | `CanRead` is applied in memory **after** `LIMIT`, so a page can be sparse or empty while `hasMore` is true, and cost scales with all documents in the scope | `PostgreSqlSyncAuthority.cs:105-135` |
| F3 | Removing a document from one caller's view requires a scope-fingerprint change, which forces a full resnapshot of the collection | ADR-010, `SyncEngine.cs:711-760` |
| F4 | `ScopeHash` concatenates collection, scope and fingerprint without separators (ambiguous) | `PostgreSqlSyncAuthority.cs:660-662` |
| F5 | Server `Validator` receives no `SyncCallContext` | `PostgreSqlSyncAuthorityOptions.cs:47` |
| F6 | Spec says limits are advertised to clients; they are not, and client batch size is independent | `docs/protocol/v1.md` §8, `SyncOptions.cs` |
| F7 | HTTP 413 maps to non-transient `PayloadTooLarge`; batches are never split, so one oversized document blocks the collection's push queue | `Transport/HttpSyncTransport.cs:205-221` |
| F8 | `clock-skew` rejection (device clock > 5 min ahead, `MaxClockSkew`) parks the record permanently | `PostgreSqlSyncAuthorityOptions.cs:29` |
| F9 | All rejections require manual `RetryAsync`/`RevertAsync`, including ones that would succeed later (e.g. a dependency that is not yet on the server) | `SyncEngine.cs` around line 313, protocol §5 |
| F10 | Clients never compact acknowledged tombstones; `PurgeAsync` only runs during resnapshot | `SyncEngine.cs:729` |
| F11 | Each record stores 2–7 JSON copies (current, base, pending payload, observed, conflict server/local/base) | `Bsync.Storage.Sqlite/SqliteLocalStore.cs:522-570` |
| F12 | Custom `SyncQuery.Order` loads and deserializes the whole collection; no field indexes; no skip/count | `Client/LocalSyncCollection.cs:59-86`, `SqliteLocalStore.cs:297-313`, `wwwroot/bsync-indexeddb.js:333-338` |
| F13 | One `SyncSession`, loop, transport, and SSE stream per collection | `Client/ClientServiceCollectionExtensions.cs:21-46`, `Client/SyncSession.cs:68-121` |
| F14 | `Changed` fires on every status publish, even when nothing changed | `Client/SyncSession.cs:446-459` |
| F15 | `RenewCredentials` returns `bool`; offline and "sign-in required" are indistinguishable | `Client/SyncSessionOptions.cs:68` |
| F16 | No native connectivity watcher; native hosts must wire `AttachLifecycle` themselves; no background execution | `Client/SyncSessionOptions.cs:62`, MAUI sample `App.xaml.cs:21-22` |
| F17 | Push allocates about 64 KB per document (JSON clones) | `docs/benchmarks.md` |
| F18 | Receipt retention is manual; purging receipts earlier than the longest offline period turns a replayed success into a conflict | `docs/operations/disaster-recovery.md:55-68` |
| F19 | `AuthorityConformanceTests` (26 cases) live in the xUnit project, not in `Bsync.Testing` | `src/Tests/api/Bsync.Testing.txt` |
| F20 | Default `Defer` switches the visible record to the server state and parks the local edit; apps without conflict UI appear to lose the user's edit | `SyncEngine.cs:1252-1258` |
| F21 | MAUI sample targets Windows only; SQLite never run on Android/iOS; IndexedDB never run in native Safari; no quota-pressure test | `docs/support-matrix.md` |
| F22 | No encryption at rest; no API to wipe a replica (database, keys, files) on sign-out | ADR-010 |

---

## 5. Non-goals

Do not add these, even if a sample would be shorter:

- Cross-collection transactions or a global snapshot across collections.
- Arbitrary LINQ, OData, or SQL translation on the client.
- A second browser database (EF Core over OPFS/SQLite) unless task E1 measurements prove IndexedDB cannot meet the
  query bar.
- CRDTs as the default conflict policy. Defer stays the default.
- Treating SQL Server `rowversion`, `IDENTITY`, timestamps, or an in-process lock as a feed position.
- Vendor-specific object storage inside the core.
- SignalR, Redis, or a cloud SDK as a dependency of any existing package.
- Moving an application's validation, authorization policy, or UI into this repository.

---

## 6. Workstreams and tasks

Each task: **Goal**, **Work**, **Done when**, **Stop if**. IDs are referenced in section 9.

### A — Trust baseline (target release `0.1.1`)

#### A1. Docs match code and NuGet
- **Goal:** a reader of the README can restore `0.1.0` from NuGet and finds exactly the package set the solution
  packs.
- **Work:** fix every item in 4.1. Generate test counts from a real `dotnet test` run. Label anything not run.
  Add a short "Status" section that is the single source of truth and link to it from the other docs.
- **Done when:** no document contradicts another or the code, checked by a reviewer pass over README, roadmap,
  support matrix, compatibility, protocol introduction, and every ADR's status line.
- **Stop if:** correcting a sentence requires a behavior change. Record it as a later task.

#### A2. CI green and consuming the pack
- **Goal:** CI is trustworthy evidence.
- **Work:** find out why Windows unit tests and the Browser/WPF job fail (read the failing logs; reproduce
  locally; look for timing-sensitive waits — one `BlazorIntegrationTests` wait timeout is already on the watch
  list). Fix root causes. Add a job that packs to a local feed and builds a sample using only
  `PackageReference` (core, browser assets, SQLite store, ASP.NET Core endpoints).
- **Done when:** ten consecutive green runs on Windows, Linux, and macOS, linked from the support matrix.
- **Stop if:** a failure is caused by the runner image and cannot be fixed in the repository; document it and
  quarantine only that test with an issue link.

#### A3. Public authority conformance
- **Goal:** any out-of-repo authority can prove it is correct.
- **Work:** move the authority conformance cases into `Bsync.Testing` (receipt replay, changed payload under a
  reused operation id, conflict, filtered read, groups, scope change, paging, retention, reset, clock skew).
  Provide drivers for an in-process authority and for an HTTP authority. Keep provider-specific drills (delayed
  commit, crash, restore) in the provider's test project. In-memory and PostgreSQL suites must call the same
  public cases.
- **Done when:** a separate test assembly that references only the `Bsync.Testing` package runs the suite
  against `InMemorySyncServer<T>`.
- **Stop if:** making cases public forces internal types into the public surface; wrap them instead.

#### A4. Release discipline
- **Work:** `CHANGELOG.md`; 0.x policy in `docs/compatibility.md` (minor may break, patch never breaks);
  a nightly `-preview` pack job (pack only, no publish).
- **Done when:** the next release's notes can be produced from the changelog alone.

### B — Relational authority and server programming model (target `0.2.0`)

#### B1. ADR-014: authority on the caller's SQL Server database
Write the ADR before the package API freezes. Decide and record:
- Protocol metadata lives in a `bsync` schema on the **same database** as the application's tables: meta, feeds,
  documents (canonical JSON, version, tombstone, HLC, collection, scope), receipts. The application's relational
  tables remain the system of record; the `bsync` tables are the replication projection.
- Feed position is the protocol version allocated under a per-feed lock that preserves ADR-005 (a version is
  visible only after every lower version in that feed has committed or rolled back). Document the mechanism
  (`UPDLOCK, HOLDLOCK` on the feed head row, or an equivalent you prove) and why `rowversion` / `IDENTITY` alone
  fail this test (assigned at write time, not commit time).
- **Transaction enlistment:** every write API accepts an optional caller `DbConnection` + `DbTransaction`. When
  given, the authority must enlist and must not commit. This is how an EF Core `SaveChanges` and the feed append
  commit atomically.
- **Write handler** `ISyncWriteHandler<TDocument>`: runs inside the authority's transaction; receives
  `SyncCallContext`, the submitted document, the base version, and the transaction. Returns accept (with the
  canonical document, which may differ from the submission), conflict (with the current document), reject (with
  a stable `errorCode` and optional arguments), or retry-later.
- Receipt replay returns the stored outcome and never calls the handler again (I04, I11).
- Reject rolls back the handler's work, then persists the rejection receipt so the replica parks.
- **Feed publisher** for server-originated writes (API endpoints, jobs, imports, projection rebuilds) — see B3.
- Tombstones: the handler performs the application's delete (usually soft) and returns `Deleted = true`; the
  feed stores a tombstone; the authority never hard-deletes application rows.
- Handlers needing an outbound call return retry-later; the feed lock is never held across network I/O.
- Restore: `BeginNewEpochAsync(versionFloor)` mirrors the PostgreSQL authority; document it in
  `docs/operations/disaster-recovery.md`.
- Shared versus per-principal documents (see C1): writable documents shared by several principals live once in a
  group/tenant scope so concurrent editors conflict; per-principal copies are allowed only for read-only
  projections.
- **Stop if:** the design needs a client-visible message that v1 cannot express. Prefer existing types.

#### B2. Package `Bsync.Server.SqlServer`
- **Work:** `SqlServerSyncAuthority<TDocument>` on `Microsoft.Data.SqlClient` only (no EF Core in the provider,
  same choice as PostgreSQL). Port the PostgreSQL authority's behavior: versioned schema created under
  `sp_getapplock`, newer schema refused, unique indexes on `(collection, scope, operation_id)` and
  `(collection, scope, id)`, conditional `UPDATE … WHERE version = @base`, retention
  (`PurgeTombstonesAsync`, `PurgeReceiptsAsync`), epochs, dependency groups, clock-skew check, in-process
  `ISyncCommitNotifier`. Implement B1's enlistment and handler. A pass-through handler (store canonical JSON,
  no domain work) is used by conformance so the suite stays generic.
- **Cross-process hints:** ship a polling notifier inside this package (watch the feed head every N seconds; no
  new dependency) and document a host bridge (a callback the host's own bus can call). No Service Broker, no
  Redis dependency in this package.
- **Tests (all required):** the public authority suite (A3); a delayed lower version is never skipped (T27/T28
  analogue); two processes, twelve writers, one reader, committed-prefix order (T30 analogue); rollback or kill
  after the handler writes and before commit leaves no feed row, receipt, or domain write; a duplicate operation
  id replays without calling the handler; an accepted document that differs from the submission reaches the
  replica while a local edit made during the round trip stays pending on the new base; a handler `forbidden`
  rejection parks only that record (I19); scope, retention, epoch restore, groups.
- **Done when:** the tests pass on a real SQL Server (2019 or 2022 container, LocalDB, or a `BSYNC_SQLSERVER`
  connection string) and on Azure SQL if available; the support matrix says exactly which.
- **Stop if:** no SQL Server instance is available. Leave the package compiling, the matrix row "Planned", and
  report.

#### B3. Feed publisher for server-originated writes
- **Goal:** applications can project relational data into replicated documents and push back-office edits to
  replicas without fake operation ids or base versions.
- **Work:** `ISyncPublisher<TDocument>` with `UpsertAsync(scope, document)`, `DeleteAsync(scope, id)`, and
  `ReplaceScopeAsync(scope, documents)` (diffs the scope and tombstones documents that disappeared). Every
  method accepts the caller's transaction (B1).
  - **Upsert-if-changed:** compare a canonical JSON fingerprint and skip the version bump when the content is
    identical, so a full projection rebuild adds zero feed entries.
  - **Fan-out:** `PublishAsync(document, scopes)` writes per-scope copies of a read-only projection in one batch.
  - Implement for SQL Server, PostgreSQL, and in-memory.
- **Done when:** rebuilding 10,000 unchanged documents appends 0 feed entries; a document removed from a scope by
  `ReplaceScopeAsync` reaches the replica as a tombstone (no reset); a publisher write and a domain write in one
  transaction both roll back together.
- **Stop if:** fingerprint comparison cannot be made deterministic for a source-generated JSON context; document
  the constraint.

#### B4. Server ergonomics
- `MapSyncCollections(group => group.Add<A>("a").Add<B>("b"))` sharing scope resolver, authorization policy,
  and options.
- Pass `SyncCallContext` to `Validator` (F5).
- Fix `ScopeHash` (F4) with an unambiguous encoding (length-prefixed or separator plus escaping). Keep old
  checkpoints valid or reset them deliberately with reason `scope-changed`; record the choice in
  `docs/compatibility.md`.
- Advertise server limits in the pull response under a feature flag, and make the client clamp its batch sizes
  to them (F6).
- **Done when:** tests cover each item and the public API baselines are updated deliberately.

#### B5. Sample: relational system of record
- Add a new sample (not a rewrite of the notes PWA): ASP.NET Core, SQL Server, one EF Core entity, a write
  handler that recomputes one field and rejects one rule with a stable code, a publisher used by an ordinary API
  endpoint, bearer-token auth, a WebAssembly client on IndexedDB, and a WPF or console client on SQLite.
- Document that a general-purpose HTTP pipeline that rewrites error bodies hides `Retry-After` and protocol
  errors; the sync `HttpClient` should be dedicated.
- **Done when:** the sample's automated test shows offline write → restart → upload → server-rewritten field
  visible → rejection code visible via `GetItemStatusAsync` → a second client receives a publisher write.

### C — Authorization-shaped replication (target `0.3.0`)

#### C1. Read filters pushed into storage, and removals without resnapshot
- **Goal:** fix F2 and F3. Pages are full; cost is proportional to what the caller can see; a document leaving
  one caller's view does not reset the whole collection.
- **Work:**
  - Write ADR-015 first.
  - Add an optional membership index on the server: `bsync.document_access(collection, scope, id, principal_key)`
    maintained by the publisher and write handler. When present, pull joins it in SQL instead of filtering in
    memory. `CanRead` remains as a final guard.
  - Add a negotiated pull feature `removals`: the response may list ids that left the caller's view since the
    checkpoint. The client purges clean copies of those ids; dirty documents and kept conflicts stay, hidden from
    ordinary queries and not uploaded while `CanWrite` is false. Peers without the feature keep today's
    fingerprint/reset behavior.
  - Document the recipe: scope = tenant or group; membership = who may read; time windows expressed through
    retention plus explicit pins, so a moving window never resets every replica every day.
- **Done when:** two principals edit one shared document and get a real conflict; revoking access removes clean
  copies via `removals` without a reset and quarantines a dirty draft; a filtered pull over 100,000 documents
  where the caller sees 500 returns full pages; a moving window test does not emit `scope-changed`.
- **Stop if:** the removal log cannot be made consistent with ADR-005 ordering. Fall back to fingerprint resets
  and report.

#### C2. Pull-only collections
- **Goal:** cut storage for read-only projections (F11) and make intent explicit.
- **Work:** `SyncMode.PullOnly` on client recipes. The store keeps only `current` (no base, pending, or conflict
  copies). `SaveAsync`/`DeleteAsync` fail fast and enqueue nothing. The server still enforces `CanWrite`.
- **Done when:** a pull-only SQLite collection of 10,000 documents uses at most 55% of the bytes of the same
  collection in two-way mode, and local writes throw a documented exception.

#### C3. Coordinator for many collections
- **Goal:** fix F13 and F14; give apps one place to schedule and observe sync.
- **Work:**
  - `SyncCoordinator` (thin layer over existing typed sessions, not a second engine): registration with
    priority and `DependsOn` (parents push before children), concurrency limit, shared backoff, pause/resume,
    aggregate status, per-collection status.
  - **Multiplexed hints:** one SSE stream per account whose events carry collection names (negotiated feature
    `hints-multiplex`). Old servers keep per-collection streams.
  - Optional negotiated `pull-batch` endpoint that pulls pages for several collections in one request.
  - `Changed` fires only when data or status actually changes.
  - **Hint bridge:** a host-supplied callback that triggers sync, with a ten-line sample forwarding a message
    from an application's own real-time hub. Losing every hint still converges on the interval (I13).
- **Done when:** 15 collections in a browser use one SSE connection; a child created before its parent is never
  rejected for a missing dependency; one failing collection does not starve the others and status names it;
  UI subscribers are not notified on idle cycles.
- **Stop if:** the browser lease model cannot elect one leader for the coordinator; keep per-collection leases
  and report.

#### C4. Awaitable sync goals and profiles
- **Work:** keep background triggers; add `SyncAsync(goal, budget)` where the goal is "pull caught up to now",
  "local revision N of document X accepted", "all pending accepted", or a combination, across one or more
  collections. Compatible waiters coalesce; single-flight stays; a waiter for revision N is never completed by an
  older acknowledgement; cancelling a wait never rolls back a local commit; budget expiry returns an incomplete
  `SyncResult`. Add named profiles (for example `Background` and `Complete`) with progress callbacks. Add a
  one-shot headless helper (time budget, batch budget, resumable checkpoint).
- **Done when:** tests prove each sentence above, including an account switch where an old in-flight callback
  cannot write status into the new account.

#### C5. Honest status and staleness
- **Work:** status must never read "synced" while unresolved conflicts, parked rejections, or blocked group
  members exist (verify with a test first). Expose counts (pending, conflicts, rejections, blocked), last
  successful pull and push instants per collection, and a `Staleness` helper the UI can compare to a threshold.
  Issue enumeration is paged with a total. Rejections carry `errorCode` plus an optional string dictionary of
  arguments so UIs never parse `Message`.
- **Done when:** a kept conflict cannot hide behind a synced status across restart; a parked rejection is still
  listed on a later run with no new outcomes.

### D — Robustness (target `0.3.0`, in parallel with C)

| ID | Fix | Done when |
|---|---|---|
| D1 | **413 handling (F7):** split the batch in halves down to one operation; a single oversized operation is parked locally as `payload-too-large`; the rest of the queue continues | A test with one 5 MiB document among 99 small ones uploads the 99 and parks the one |
| D2 | **Clock skew (F8):** server returns its time (HTTP `Date` and/or a server HLC field under a feature flag); client keeps an offset and stamps HLCs with it; a `clock-skew` rejection is re-stamped and retried automatically once | A client with a clock 2 hours ahead converges without user action |
| D3 | **Rejection classes (F9):** classify codes as transient (auto-retry with backoff: `clock-skew`, `dependency-missing`, `retry-after`-like) or terminal (park). Make the class part of the outcome, not a client-side string list | A child pushed before its parent's acceptance is retried and accepted automatically |
| D4 | **Client tombstone compaction (F10):** purge acknowledged tombstones older than the server's retention horizon (advertised under a feature) | Tombstone count stays bounded in a 30-day soak simulation |
| D5 | **Retention service (F18):** hosted service that purges tombstones and receipts using a configured `MaxOfflineHorizon` (document a default, for example 45 days) and refuses a receipt horizon shorter than it | Configuration below the horizon fails at startup with a clear message |
| D6 | **Credential result (F15):** replace `bool` with `Renewed`, `Offline`, `SignInRequired`; one renewal at a time per account; only `SignInRequired` reports attention/sign-out | A connection failure during renewal keeps the queue and local saves; an auth failure stops uploads |
| D7 | **Conflict visibility (F20):** add an option to keep the local edit visible (marked conflicted) instead of switching to the server state; document when to use each | A test shows the local edit visible after a deferred conflict with the option on |
| D8 | **Storage amplification (F11):** drop `observed` and conflict copies once resolved; consider compressing large JSON columns behind a store option | Bytes per clean record in two-way mode measured before and after in `docs/benchmarks.md` |
| D9 | **Push allocations (F17):** remove redundant JSON clones on the push path | Allocation per pushed document drops below 16 KB in the benchmark |

### E — Local query performance (target `0.4.0`)

#### E1. Measure first
- Benchmark on a mid-range Android device (if available), an iPad Safari, and a Windows desktop: 10,000 and
  50,000 documents of about 1 KB; a query ordered by a `DateTimeOffset` field; a selective filter; first sync;
  peak memory. Record in `docs/benchmarks.md`.
- **Bar (ADR-012 style):** p95 under 50 ms for a 50-item ordered page on a named device.

#### E2. Declared secondary indexes (only if E1 misses the bar)
- **Work:** `options.Index("name", d => d.Field)`. SQLite: generated column via `json_extract` plus an index,
  added by a store schema migration that keeps pending work. IndexedDB: an index on the key path inside the stored
  record. Query API additions: index range, order by index, `Skip`/`Take`, `CountAsync`. The `Func` predicate
  stays for residual filtering. Same contract and conformance cases for both stores.
- **Done when:** E1's bar is met on the named devices and both stores pass the new conformance cases.
- **Stop if:** the IndexedDB key-path approach cannot index values inside the stored envelope; propose a store
  schema change in an ADR instead.

### F — Blobs, bundles, and intents (target `0.4.0`)

Do not extract a framework from imagination. First extend the B5 sample with one attachment, one bundle, and one
intent. Extract package code only when the sample would otherwise copy logic.

#### F1. Blob replica beside the document
- Documents carry references `{ id, sha256, size, contentType, fileName }`. Bytes never enter document JSON.
- **Client:** content-addressed store (file system next to the SQLite replica; Cache Storage or OPFS in the
  browser — quota failures must surface, never silently succeed). `OpenRead` works offline once bytes are
  verified. A missing blob leaves the document readable with the reference marked unavailable.
- **Durability order:** bytes are written before the document save reports success; upload completes before the
  parent document's push is sent; a lost finalization response replays by hash and never stores a second object.
- **Transfers:** resumable chunked upload and download; `IBlobSource` (stream plus length) with a file-system
  source and a resumable HTTP source; presigned URLs, if the host uses them, are fetched at transfer time and
  refreshed on expiry. The service worker must not proxy the blob route (same rule as the hint stream).
- **Pinning and eviction:** blobs referenced by pinned documents are prefetched and never evicted; others are
  evicted least-recently-used under a quota; eviction never removes a blob that a pending document names.
- **Server:** `IBlobStore` abstraction plus a file-system implementation; an S3-compatible adapter in a separate
  optional package; dedupe by hash; read permission derived from document access (a caller may read a blob if
  they may read a document that references it).
- **Done when:** kill during upload and during download both resume; a corrupted file is never opened as
  complete; a 50 MB file survives a network drop at 60%.

#### F2. Bundles (atomic content sets)
- A bundle is a manifest document listing blob references and a revision. On the client it becomes "available"
  only after every referenced blob is present and verified; until then the previous revision stays active.
  Expose `GetBundleStateAsync` (revision, availability, missing items, bytes remaining).
- **Done when:** a test interrupts a bundle update midway and the previous revision remains readable and
  reported; completion switches revision atomically.

#### F3. Immutable intents
- An intent is an action request (id, kind, target document id, target revision, payload, created time) that
  never coalesces and is executed once. The server ignores caller identity inside the payload and uses
  `SyncCallContext`. Acceptance of an intent document is not execution: execution state (pending, executed,
  rejected, waiting on dependency) is separately visible.
- Ship as a documented pattern on the existing engine first; add types only after the sample has two intents
  sharing them.
- **Done when:** a lost response executes once; restart still shows pending; a rejection keeps its code.

### G — Hosts, platforms, and device security (target `0.5.0`)

#### G1. Native host integration package (`Bsync.Maui`, optional)
- Connectivity watcher (`Connectivity.ConnectivityChanged`) triggering sync, app lifecycle pause/resume wired
  through `AttachLifecycle`, optional background sync hooks (Android `WorkManager`, iOS `BGTaskScheduler`) with
  budgets, and helpers to keep replica identity and keys in `SecureStorage`.
- **Done when:** on an Android emulator, toggling network triggers sync within 5 s; a background task drains a
  small queue within its budget.

#### G2. Platform qualification
- Build and run the MAUI sample on Android and, if a workload/device is available, iOS and Mac Catalyst, with the
  same smoke as WPF: UI write → offline kill → restart → upload. Run the SQLite conformance and process-kill tests
  on Android. Zero trim/AOT warnings in a published iOS build.
- Run the IndexedDB conformance, offline reload, and a quota-pressure test in native Safari (macOS and iPadOS).
- Also cover: suspend/resume, two tabs, SQLite file contention, backup restored as a new incarnation, account
  switch during pull, migration from the published `0.1.0` store schema with pending, conflict, rejection, group,
  and unknown JSON fields.
- **Done when:** each host you ran is recorded in the support matrix with evidence; every host you could not run
  is labeled honestly.
- **Stop if:** no device or workload is available; never mark a host verified from a compile.

#### G3. Encryption at rest and wipe (ADR-016 first)
- Write ADR-016 covering key source, rotation, and threat model.
- SQLite: optional SQLCipher bundle via a connection factory or key option in `SqliteLocalStoreOptions`.
- IndexedDB: optional WebCrypto AES-GCM envelope around stored documents with a caller-supplied key provider.
- `DeleteReplicaAsync(account)`: removes database, blobs, keys, and leases; safe to call while a session runs.
- Request persistent browser storage (`navigator.storage.persist()`) automatically and report the result in
  status.
- **Done when:** an encrypted SQLite file cannot be opened without the key; wipe leaves no files for the account;
  encryption never becomes a default or a claim without the ADR.

### H — Operations and audit (target `0.5.0`)

- **Replica acknowledgement audit (optional server feature):** record replica id, account, collection,
  checkpoint/version reached, and time when a replica completes a pull; queryable per replica. Useful for
  regulated environments that must prove which content revision a device held.
- Metrics: add counts from C5 (conflicts, rejections, blocked, oldest pending age) and blob transfer metrics to
  the existing `Bsync` and `Bsync.Server` meters. Logs never contain document bodies, ids, or tokens.
- Runbook updates for SQL Server: restore drill (new epoch, version floor, replicas resnapshot, pending local work
  kept), retention jobs, capacity guidance.
- Throughput benchmark on SQL Server and PostgreSQL with concurrent sessions.

---

## 7. Releases (suggested milestones)

| Release | Contents |
|---|---|
| `0.1.1` | A1–A4 |
| `0.2.0` | B1–B5 |
| `0.3.0` | C1–C5, D1–D9 |
| `0.4.0` | E1–E2, F1–F3 |
| `0.5.0` | G1–G3, H |
| `1.0.0` | Protocol v1 frozen, public API frozen, after sustained real-world use with no data-loss incidents |

Land the acceptance tests, then cut the release. Release numbers are milestones, not commitments.

---

## 8. Definition of done for the whole plan

A host application can, using only published packages and the samples as a guide:

1. Save locally and survive process death, on Windows, Android, iOS, and in browsers including Safari.
2. Upload through a SQL Server authority whose write handler runs in the same transaction as the application's
   own tables, and receive a server-computed field or a stable rejection code with arguments.
3. Publish server-originated changes (API, job, projection rebuild) into the feed transactionally, with no feed
   churn for unchanged content.
4. Share one document between two principals and get a real conflict on concurrent edits; lose access and have
   clean copies removed without a full resnapshot and without losing a dirty draft.
5. Run 15+ collections with one coordinator, one hint stream, ordered dependencies, aggregate status, and an
   awaitable "sync until goal" call.
6. Keep syncing through token expiry and network loss without discarding the queue; stop only when sign-in is
   truly required.
7. Attach verified blobs, distribute bundles that switch revision atomically, and execute intents exactly once.
8. Query sorted, paged views of 50,000 local documents within the stated bar.
9. Encrypt replicas at rest and wipe them on sign-out.
10. Read a support matrix that matches the machines the tests actually used.

---

## 9. Order of execution

1. **A1, A2, A3, A4.** Nothing else starts until CI is green and conformance is public.
2. **B1 spike:** one handler, one transaction, a lost-response test, a delayed-commit test against a real SQL
   Server. Stop if the lock cannot satisfy ADR-005.
3. **B2, B3, B4, B5.**
4. **D1, D2, D3, D6** (they unblock real-world use), then **C1–C5**, then **D4, D5, D7, D8, D9**.
5. **E1**, then **E2** only if E1 misses the bar.
6. Extend the B5 sample with one blob, one bundle, one intent; then **F1, F2, F3**.
7. **G1, G2, G3, H.**

## 10. PR note template

```
Task: <ID>
Changed: <summary>
Invariants/conformance run: <suites and counts>
Hosts executed: <OS, runtime, browser/device versions>
Hosts not executed: <list>
Compatibility: <entry in docs/compatibility.md or "none">
Public API baseline: <updated deliberately / unchanged>
Left for later: <task IDs and reason>
```
