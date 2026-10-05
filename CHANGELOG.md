# Changelog

User-visible changes per release. Pre-1.0: a minor release may break, a patch release never does
([compatibility policy](docs/compatibility.md#policy)); every breaking change also has a migration note in
[docs/compatibility.md](docs/compatibility.md). Release status: [README](README.md#status).

## Unreleased (0.1.1)

### Added

- `Bsync.Testing`: the authority conformance suite is public. `AuthorityConformance.Cases` (21 cases: one outcome
  per operation, version and conflict rules, receipt replay, operation id reuse, malformed operations, origin
  timestamps and the clock-skew bound, validation, feed coverage and paging, resume, foreign checkpoints, filtered
  reads, scope fingerprints, scope isolation, dependency groups, tombstone and receipt retention, new epochs), run
  through an `IAuthorityConformanceDriver`. `InMemoryAuthorityDriver` is the reference driver; `HttpAuthorityDriver`
  runs the same cases through the HTTP binding. The in-memory, JSON-wire, HTTP and PostgreSQL suites in this
  repository all run these cases.
- CI builds and tests projects that reference only the packed packages (`src/PackageConsumers`): the public
  conformance suites from the `Bsync.Testing` package, and an ASP.NET Core host with the browser store's static
  assets and a SQLite replica that syncs over HTTP.
- A nightly workflow packs `-preview` packages as build artifacts (never published).
- `CHANGELOG.md`, the 0.x compatibility policy and the release process (`docs/compatibility.md`).

### Fixed

- Documentation now matches the code and NuGet: six packages at `0.1.0` are published; PostgreSQL is a durable
  authority; protocol headers are implemented; document upgrade on read (`DocumentUpgrade`) is documented in the
  runbook; the live stream is consumed by `SyncSession` as hints; CI status and test counts come from real runs.
  The README has a single [Status](README.md#status) section the other documents link to.
- Test reliability: `HttpBindingTests.TimeoutAfterCommitIsRetrySafe` retried with the same 300 ms timeout it
  used to provoke the first timeout, so on a loaded machine the retry could time out too (1 failure in 15 local
  runs; a likely cause of the intermittent Windows CI failures). The retry now uses a normal timeout.

## 0.1.0 - 2026-09-28

First published release: `Bsync`, `Bsync.Blazor`, `Bsync.Storage.Sqlite`, `Bsync.Server.AspNetCore`,
`Bsync.Server.PostgreSql` and `Bsync.Testing` on NuGet.org.

- Local-first engine: atomic local write plus pending operation, immutable operation ids with server receipts and
  replay, per-document optimistic concurrency, checkpointed atomic pull, epochs and resets (`epoch`,
  `scope-changed`, `expired`), dependency groups.
- Conflict handlers: Defer (default, durable kept conflicts), ThreeWayMerge (with set and counter merges),
  ClientWins, ServerWins, LastWriteWins, Delegate.
- Stores: SQLite (native hosts), IndexedDB (browsers, multi-tab lease), in-memory; schema migrations that keep
  pending work; SQLite check and rebuild; export and import of local work.
- Authorities: in-memory reference and PostgreSQL; ASP.NET Core endpoints with a Server-Sent Events hint stream;
  `HttpSyncTransport`; scopes, read and write authorization, retention.
- `ISyncCollection<T>` for every Blazor render mode and native hosts, with the `SyncSession` loop (triggers,
  backoff, `Retry-After`, pause and resume, credential renewal).
- OpenTelemetry traces and metrics; session and server logs.

Details of every change before this release: [docs/compatibility.md](docs/compatibility.md#before-010).

