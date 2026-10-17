# Building and validating release artifacts

Release status and historical provenance: [README](../../README.md#status),
[baseline inventory](../review/release-baseline-2026-10-16.md), [ADR-019](../architecture/adr-019-release-provenance.md).

## Candidate workflow

1. Choose a new, unpublished version under the compatibility policy. Update `VersionPrefix`,
   the release documentation and the changelog heading to `## <version> - YYYY-MM-DD`.
2. Commit the changes, complete the required qualification runs, and create a protected `v<version>`
   tag on that commit. Do not reuse or move a release tag.
3. Run `.github/workflows/release.yml` on that tag (tag push or manual dispatch selecting the tag).
   It checks clean source and version metadata, tests core and encrypted SQLite, packs all six
   libraries, verifies provenance, and runs package-only conformance and web smoke tests.
4. Review `release-candidate` (packages, symbols, `provenance.json`) and `release-test-evidence`
   (TRX files). The workflow log records commands and smoke-test output. Keep these with the
   remaining provider/browser/device qualification evidence.

This workflow never publishes. It does not replace the full support matrix or ten-run qualification gate.
The current 0.4.0 development checkout requires a clean, matching version tag before stable preflight can pass.
The historical 0.2.0 published-source mismatch remains unresolved.

## Local preview

Use an empty `artifacts/packages` feed and a new suffix for every pack, including reruns. The
consumer cache is isolated from the global NuGet cache, but an already cached version is still cached;
changing a package's bytes without changing its preview version is not a valid test.

```powershell
python -m unittest discover -s scripts -p 'test_*.py' -v
# Repeat for each of the six packages listed in scripts/release.py:
dotnet pack src/Bsync/Bsync -c Release -o artifacts/packages -p:VersionSuffix=local.1 -p:ContinuousIntegrationBuild=true
python scripts/release.py manifest --version 0.4.0-local.1
$packageCommit = git rev-parse HEAD
dotnet run --project src/Tools/Bsync.PackageVerifier -c Release -- artifacts/packages $packageCommit
python scripts/verify_symbols_test.py artifacts/packages -v
dotnet test src/Tests/PackageConsumers/Bsync.PackageConsumer.Tests -c Release -p:BsyncPackageVersion=0.4.0-local.1
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Web -c Release -p:BsyncPackageVersion=0.4.0-local.1 -o artifacts/consumer-web
dotnet artifacts/consumer-web/Bsync.PackageConsumer.Web.dll --smoke
python scripts/check_http_compatibility.py --version 0.4.0-local.1 --packages artifacts/packages
```

The HTTP fixture builds isolated processes against published 0.2.0 and the exact local candidate. It verifies legacy
uploads/resumed downloads in both version directions, new ticket downloads against the candidate and explicit 404
with retained partial bytes when ticket mode contacts the old server. Logs and results are retained under
`artifacts/http-compatibility-*`; CI and release-candidate jobs upload them. This does not qualify object-store CORS.

For package-only WebAssembly execution, publish `src/Tests/PackageConsumers/Bsync.PackageConsumer.Browser` with the
same `BsyncPackageVersion` to an isolated output directory. Build `Bsync.Tests.Browser`, then set `BSYNC_BROWSER_HOST`
to that published `wwwroot` and run it with `--no-build --filter FullyQualifiedName~IndexedDbBrowserTests`. The normal
Playwright Chromium/Firefox/WebKit harness then runs against packaged client libraries and packaged static assets;
the test authority remains the source-built HTTP server. Unset the override for ordinary source qualification. This
checks package integration without claiming native Safari, mobile-browser or WebAssembly AOT qualification.

The manifest verifies all six `.nupkg` and `.snupkg` pairs, rejects an old/mixed feed, and records
whether the tree was dirty. `Bsync.PackageVerifier` separately opens distributed DLL/PDB pairs, checks their
CodeView/content identity, reads the PDB's module-level Source Link JSON and validates repository/commit and document
coverage (embedded source or mapping), and verifies embedded-source checksums using the
[portable PDB specification](https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md).
It writes `symbols-verification.json`; integration tests reject wrong commits, missing PDBs, mismatched assemblies
and altered embedded checksums. A failed verification removes an earlier success report.

Release candidates additionally pass `--verify-sources`: download non-embedded source from the exact canonical commit
and compare each PDB checksum, with bounded response sizes and no redirects. This deliberately fails for a dirty
working copy whose tracked source differs from its claimed commit. Release qualification still requires a successful
clean tagged run of this mode; a default local verification is not remote-source evidence. Preserve byte-for-byte source
line endings in release checkouts (the Linux candidate workflow does this); do not normalize checksum inputs to hide a
mismatch. The known pre-rename `msynk/BlazorSync` source mapping is canonicalized to `msynk/Bsync` during build.

## Dependency policy

Auditing stays enabled for the full dependency graph. NU1900–NU1905 fail validation, including audit
service failures; retry infrastructure failures after resolving connectivity rather than suppressing
the audit. This policy is shared by source projects and package-only consumers.

The existing Microsoft DI/logging, Blazor/JS interop and SQLite Core references have minimum 10.0.12.
SQL Client is 7.1.1; `Bsync.Server.SqlServer` additionally requires Pkcs >= 9.0.21 to fix
[GHSA-846f-f764-76mx](https://github.com/dotnet/runtime/security/advisories/GHSA-846f-f764-76mx) and
[GHSA-2fmv-rv2c-59x7](https://github.com/dotnet/runtime/security/advisories/GHSA-2fmv-rv2c-59x7).
Consumers using central package management must not pin lower transitive versions. See the web
consumer's `Directory.Packages.props` for the exercised configuration. These are package minimums,
not a statement that an older installed runtime is fully patched.

Sample WebAssembly server hosts use `Microsoft.AspNetCore.Components.WebAssembly.Server` 10.0.13,
addressing [GHSA-g27p-qf6j-c96c](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-g27p-qf6j-c96c)
and [GHSA-qjpx-jqm5-h2c4](https://github.com/dotnet/aspnetcore/security/advisories/GHSA-qjpx-jqm5-h2c4).

Plain SQLite and SQLCipher bundles remain mutually exclusive in a process. The core tests use
`SQLitePCLRaw.bundle_e_sqlite3` 2.1.12; the separate encrypted tests use
`SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11. Passing these tests on Windows does not qualify their
maintenance/security status or availability on other native targets.
