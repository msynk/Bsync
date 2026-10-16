# R0 baseline and first implementation slice — 2026-10-16

Scope: [Improvement Plan](../Improvement-Plan.md), M0/R0. Source baseline:
`ad46c3b61f17d2304fd6c580d916a5242aca52d8`, plus the uncommitted R0 changes described here.
This is development evidence, not a qualified release or a claim of clean-source reproducibility.

## Published artifacts and source

Downloaded all six 0.2.0 packages from NuGet's v3 flat-container endpoint on 2026-10-16.
Their IDs, versions, repository metadata and SHA-256 checksums are preserved in
[`published-0.2.0-inventory.json`](published-0.2.0-inventory.json). Packages:
`Bsync`, `Bsync.Blazor`, `Bsync.Server.AspNetCore`, `Bsync.Server.PostgreSql`,
`Bsync.Server.SqlServer`, `Bsync.Storage.Sqlite`.

Every published nuspec identifies `54047855b2edbedd55487f033f4206beb7d42a7a`. The existing local
`artifacts/*.0.2.0.nupkg` files identify the same commit. Their archive hashes differ from the NuGet
downloads; these hashes alone do not establish different assembly contents (NuGet adds signing data).

`git show 5404785:src/Directory.Build.props` declares 0.1.0. Later commit `2537abe` introduces the
0.2.0 additions; `e182879` and `ad46c3b` correct tests. There are no version tags in this checkout.
Thus the package commit field does not identify a demonstrated clean build of these artifacts.
Building with uncommitted additions or overridden metadata could explain it, but the original
invocation and working tree are unavailable; neither explanation is asserted as fact. No existing
commit is retroactively tagged and no historical package is declared reproducible.

## Source inventory

| Contract | Current checkout | Verification entry point |
|---|---|---|
| Package version prefix / target | 0.2.0 / net10.0 | `src/Directory.Build.props`, six library projects |
| Public API | Six `src/Tests/api/*.txt` baselines | `PublicApiTests` in the executed core suite |
| SQLite schema | 5 | `SqliteSchema.CurrentVersion` |
| IndexedDB document schema | 4 | `bsync-indexeddb.js`, `SCHEMA_VERSION` |
| SQL Server schema | 3 | `SqlServerSchema.CurrentVersion` |
| PostgreSQL schema | 2 | `PostgreSqlSchema.CurrentVersion` |
| HTTP protocol | 1 | `HttpSyncTransport.ProtocolVersion`, `SyncEndpoints.ProtocolVersion` |
| Optional features | groups, limits, server-time, removals, hints-multiplex, pull-batch, retention | `SyncFeatures` and protocol fixtures |

These schema values describe source, not independently extracted published-store fixtures.
The core test run includes wire fixtures, API baselines, SQLite migrations and conformance.
Provider schemas are inventoried only; no provider database was opened for this change.

## Dependency finding and remediation

Audited restore initially failed with NU1902/NU1903 for Pkcs 9.0.18 through SQL Client 7.1.1.
The SQL Server project now has a public Pkcs 9.0.21 reference, the patched 9.x minimum in
[Microsoft's high-severity advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-846f-f764-76mx)
and [moderate-severity advisory](https://github.com/dotnet/runtime/security/advisories/GHSA-2fmv-rv2c-59x7).
`dotnet nuget why` on the centrally managed package-only web consumer confirms both its direct
SQL Server dependency and SQL Client's transitive dependency resolve 9.0.21. Auditing was not disabled.

The subsequent full-solution audit also found the browser test project's independent SQL Client
path to Pkcs 9.0.18 and the three sample hosts' WebAssembly server package at 10.0.12. The browser
test project now pins Pkcs 9.0.21; Notes, Tasks and WebApp hosts use WebAssembly.Server 10.0.13 for
[GHSA-g27p-qf6j-c96c](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-g27p-qf6j-c96c)
and [GHSA-qjpx-jqm5-h2c4](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-qjpx-jqm5-h2c4).

## Executed evidence

Environment: Windows 10.0.19045 x64, .NET SDK 10.0.401, .NET/ASP.NET Core runtime 10.0.12,
Python 3.14.6. Native test bundles: plain SQLitePCLRaw 2.1.12, SQLCipher 2.1.11, separate processes.
Preview version: `0.2.0-r0.20261016.1`. Artifacts: `artifacts/packages`, with `provenance.json` recording
the dirty source state, full `dotnet --info`, six API baseline hashes, twelve archive hashes and
SDK-generated Source Link maps. These local artifacts are ignored by Git.

| Command/check | Outcome |
|---|---|
| `dotnet build src/Bsync.slnx -c Release` after dependency remediation | Passed, 0 warnings/errors; includes trimmed browser harness and sample publishes, not browser execution |
| `python -m unittest discover -s scripts -p 'test_*.py' -v` | 9 passed; disposable Git repositories and package fixtures |
| `dotnet test src/Tests/Bsync.Tests -c Release -p:NuGetAudit=true -p:NuGetAuditMode=all -p:TreatWarningsAsErrors=true --logger 'trx;LogFileName=r0-core.trx'` | 637 passed, 0 skipped |
| `dotnet test src/Tests/Bsync.Tests.SqliteEncrypted -c Release --logger 'trx;LogFileName=r0-encrypted.trx'` | 30 passed, 0 skipped |
| `dotnet pack src/Bsync/<package> -c Release -o artifacts/packages -p:VersionSuffix=r0.20261016.1 -p:ContinuousIntegrationBuild=true` for all six packages | 12 package/symbol artifacts produced |
| `python scripts/release.py manifest --version 0.2.0-r0.20261016.1` | All 12 artifacts verified; provenance recorded |
| `dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.2.0-r0.20261016.1 --logger 'trx;LogFileName=r0-consumer.trx'` | 53 passed, 0 skipped; exact package version assertion included |
| `dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release -p:BsyncPackageVersion=0.2.0-r0.20261016.1 -o artifacts/r0-consumer-web` | Passed with central package management; all six Bsync packages restored |
| `dotnet artifacts/r0-consumer-web/Bsync.PackageConsumer.Web.dll --smoke` | Blob upload/download and SQLite/HTTP sync succeeded; packaged IndexedDB/SQL Server types loaded |
| Check `wwwroot/_content/Bsync.Blazor/bsync-indexeddb.js` in published consumer | Present; no browser execution claim |
| `python scripts/release.py preflight --tag v0.2.0` in this checkout | Correctly refused dirty source |

The first consumer restore stalled downloading external dependencies into its empty private cache and
was canceled. The retry seeded 65 **non-Bsync** package versions from the existing NuGet cache, then
restored normally with auditing enabled and unchanged source mapping. No Bsync package was copied
from the global cache; Bsync packages came from the isolated produced feed. This is a warm external
dependency restore, not evidence of a successful cold network restore.

## Remaining gates

- Original 0.2.0 build provenance and clean-source reproducibility remain unresolved.
- PDB-level Source Link verification and remote source retrieval remain unimplemented; the tool
  currently checks generated build maps and records symbol-package checksums.
- Release workflow execution in GitHub Actions and ten consecutive full qualification runs remain pending.
- Provider, browser, WPF/MAUI and physical-device suites were not rerun in this slice.
- Native distribution maintenance/security qualification is still open; these Windows tests do not close it.
- Published 0.2.0 store fixtures with pending work, conflicts, rejections and groups remain to be preserved.
- No public API, protocol or store format changed. No release tag, commit or publication was performed.
