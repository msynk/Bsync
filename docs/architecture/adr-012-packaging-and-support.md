# ADR-012: Packaging, supported platforms, support tiers and workloads

- **Status:** Accepted (2026-09-27)

## Decision

- **Target framework:** `net10.0` only. .NET 10 is the current LTS release (see ADR-001 sources for the
  lifecycle dates checked). Adding older targets requires a demonstrated user need and CI coverage.
- **Packages are split only when a real boundary exists** (a dependency that must not flow into another
  host). Packages: `Bsync` (core, the UI-independent client in namespace `Bsync.Client`, the HTTP
  transport in `Bsync.Transport` and the provider conformance cases in `Bsync.Testing`; depends only on the DI
  and logging abstractions), `Bsync.Blazor` (browser store in `Bsync.Blazor.IndexedDb` and the server-connected
  profile; JS interop and the Blazor authentication state), `Bsync.Storage.Sqlite` (`Microsoft.Data.Sqlite.Core`;
  the application adds the plain or SQLCipher native bundle, ADR-016), `Bsync.Server.AspNetCore` (the ASP.NET Core
  shared framework), `Bsync.Server.PostgreSql` (Npgsql) and `Bsync.Server.SqlServer` (Microsoft.Data.SqlClient).
  Integrations with one third-party SDK or app model and a few files of public-API code (S3 blob storage, MAUI
  lifecycle) are recipes in the samples, not packages (2026-10-11: six packages; `Bsync.Testing`,
  `Bsync.Storage.Sqlite.Encrypted`, `Bsync.Server.Blobs.S3` and `Bsync.Maui` merged or turned into recipes;
  `Bsync.Testing` adds no dependency, so it lives in the core). The client and HTTP transport live in the core because they add no dependency that
  must be kept out of any host; `Microsoft.AspNetCore.Components` still never flows into native hosts
  without Blazor. The browser store and the server-connected collection share `Bsync.Blazor` because both
  are Blazor-only and an Auto-mode app uses both (2026-09-28: consolidated from nine packages).
  Native database binaries must never enter the WebAssembly dependency graph; server code must never
  enter the client bundle.
- **Support tiers** published in `docs/support-matrix.md`:
  - *Verified*: executed smoke and recovery tests in CI or a recorded manual run with versions.
  - *Build-only*: compiles/publishes; not executed.
  - *Planned*: not implemented.
  No host is described as supported without *Verified* evidence.
- **Public API:** pre-1.0 releases may break with migration notes (`docs/compatibility.md`). From 1.0,
  API compatibility is checked in CI (package validation baseline).
- **Releases:** publishing packages is a separate, explicitly authorized action and is not performed by
  development work.

## Provisional workloads (targets to calibrate, not measurements)

| Dimension | Workload |
|---|---|
| Functional | 10,000 documents of ~1 KiB, 10% updates and tombstones |
| Scale | 100,000 documents, large backlog, skewed hot keys, bounded pages |
| Local UX | p95 durable write and small indexed query < 50 ms on a named reference device |
| Reconnect | 10,000 queued writes converge without manual repair or duplicate effects |
| Concurrency | multiple server processes; 100, then 1,000 simulated sessions |
| Memory | steady-state sync memory proportional to batch/window size |

Supported offline duration and document size limits will be set together with retention (Phase 8); until
then there is no retention limit and no size limit beyond the batch budgets.
