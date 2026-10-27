# Capabilities plan: B01 baseline and first corrections

Observed 2026-10-24. B01 is **in progress**, not qualified. B02–B08 have not started.
Source baseline: `7a5169b3df212a99f128f4d55ecb80bc483ababc`; the working tree initially contained the
untracked capabilities plan. This record describes local, uncommitted changes on that baseline.
No stable candidate, tag, publication or ten-run gate is claimed.

## Rechecked release and CI evidence

The public GitHub API reported no open issues and no later source revision in the latest 15 workflow runs.
[Full CI run 39261201009](https://github.com/msynk/Bsync/actions/runs/39261201009) failed on the baseline:

| Job | Observed result |
|---|---|
| Core Windows, Ubuntu, macOS | Failed at the core test step; SQLCipher was subsequently skipped |
| Browser/WPF | Failed at the browser test step |
| Packaged WebAssembly | Failed at the packaged browser test step |
| PostgreSQL, SQL Server | Passed |
| Release tooling, pack/package consumers | Passed |

The five newer successful workflows in that response were pack-only nightlies at the same commit.
They do not establish full-matrix qualification. Job/check annotations expose only exit codes, not test identities.
`gh` is unauthenticated here. Anonymous run-log download returned HTTP 403; the Windows TRX artifact endpoint
returned HTTP 401. Raw jobs, artifact listings, annotations and download errors are retained locally under
`artifacts/capabilities-b01/remote/`. Individual remote failures remain **unclassified** until authenticated logs
and TRX archives are obtained; the local reproduction below is not proof of the remote failure's cause.

All six 0.5.0 nupkg archives were downloaded directly from NuGet's flat container. Their declared source is
the baseline commit. The [machine-readable inventory](capabilities-b01-baseline-2026-10-24.json) records
download URLs, archive SHA-256 hashes, sizes, dependency minimums, DLL/browser-asset hashes, CI job identities,
and 39 exact-commit API/protocol/store/browser fixture hashes. Fixture hashes use Git blob bytes, avoiding
checkout line-ending normalization. Those baselines were not regenerated or changed.
All five distributed `staticwebassets/*.js` files match the corresponding Git blobs at the declared commit byte
for byte; the inventory includes these comparisons. This verifies those script assets, not DLL/PDB provenance.

Archives are retained at `artifacts/capabilities-b01/published-0.5.0/`. Attempts to fetch symbol archives from
NuGet's v2 symbolpackage endpoint returned 404 for all six packages. This does not establish whether individual
PDBs are available from NuGet's symbol server. Distributed DLL/PDB/source correspondence remains unverified;
matching nuspec commits alone is insufficient. Existing published 0.1/0.2/0.4 fixtures remain intact; new
stored-state fixtures produced by the actual 0.5.0 archives are still required.

## Corrections

1. The 513 MiB streaming SHA-256 regression included platform `IncrementalHash.AppendData` calls inside
   its managed-compression allocation measurement. A full run with `DOTNET_PROCESSOR_COUNT=2` failed at
   8,144 allocated bytes against the unchanged 4,096-byte ceiling; the same baseline test passed in isolation.
   The measurement now contains only Bsync's warmed managed appends. The platform digest runs separately
   and still validates all 513 MiB, including the length beyond a 32-bit bit count. This corrects test attribution;
   it does not change hashing, relax the limit or establish the origin of every allocation in the failed run.
2. CI now executes SQLCipher after an attempted core test step even if that step failed, unless cancelled.
   The core failure remains a job failure. Both core and SQLCipher TRX directories are uploaded together.
3. README, support matrix, compatibility notes, release instructions and changelog now distinguish published
   0.5.0 from later working-tree changes and incomplete qualification. Historical evidence is preserved.

## Local validation

Environment: Windows 10 x64 (`10.0.19045`), .NET SDK `10.0.402`, .NET runtime `10.0.13`.
Restore auditing remains enabled. TRX files, including failed attempts, are retained in
`artifacts/capabilities-b01/tests/`; exact outcomes and hashes are recorded alongside the final validation below.

Initial commands:

```powershell
dotnet test src/Tests/Bsync.Tests -c Release --logger 'trx;LogFileName=core-baseline.trx' --results-directory artifacts/capabilities-b01/tests
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet test src/Tests/Bsync.Tests -c Release --no-build --logger 'trx;LogFileName=core-cpu2.trx' --results-directory artifacts/capabilities-b01/tests
dotnet test src/Tests/Bsync.Tests -c Release --no-build --filter FullyQualifiedName~StreamingSha256Tests.LengthBeyond --logger 'trx;LogFileName=sha-baseline.trx' --results-directory artifacts/capabilities-b01/tests
```

Initial results: unrestricted core 928 passed; two-CPU core 927 passed/1 failed; isolated baseline SHA test passed.
The two-CPU run overlapped the browser suite's build. This is a local scheduling condition, not a claimed replica
of a hosted runner. The original failed TRX remains retained.

After the correction:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet test src/Tests/Bsync.Tests -c Release --no-restore -p:BuildProjectReferences=false --logger 'trx;LogFileName=core-cpu2-corrected.trx' --results-directory artifacts/capabilities-b01/tests
Remove-Item Env:DOTNET_PROCESSOR_COUNT
dotnet test src/Tests/Bsync.Tests.SqliteEncrypted -c Release --no-restore -p:BuildProjectReferences=false --logger 'trx;LogFileName=sqlite-encrypted.trx' --results-directory artifacts/capabilities-b01/tests
python -m unittest discover -s scripts -p 'test_*.py' -v
node scripts/test_browser_lock_release.mjs
git diff --check
```

Core: **928 passed**, zero failures/skips (54 seconds reported test duration). A further full run with
`DOTNET_PROCESSOR_COUNT=1`, `--no-build` and `LogFileName=core-cpu1-corrected.trx` also passed all **928**
tests (66 seconds), checking the intermittent measurement under another constrained schedule. SQLCipher: **31 passed**,
zero failures/skips (2 seconds). Release-tooling tests: **9 passed**. Browser physical-lock release checks passed.
The tests reused baseline project references while the browser build was active; only the corrected core test
project needed recompilation. No runtime library code changed. TRX counters and SHA-256 hashes are in the
machine-readable inventory. Package archives, all 39 baseline-file hashes and edited-document local links were
independently checked against the inventory. `git diff --check` passed.

Browser baseline command (source-built hosts and assets):

```powershell
dotnet test src/Tests/Bsync.Tests.Browser -c Release --logger 'trx;LogFileName=browser-baseline.trx' --results-directory artifacts/capabilities-b01/tests --blame-hang-timeout 3m --blame-hang-dump-type none
```

No `BSYNC_POSTGRES` or `BSYNC_SQLSERVER` connection was configured for this run, so database-dependent browser
sample cases skip. This run does not exercise the published-package browser harness or establish the full declared
browser/WPF qualification matrix.

Browser/WPF result: **126 passed, 5 skipped, zero failures** (8 minutes 45 seconds). The five skips are the
PostgreSQL multi-server scenario and four SQL Server Tasks sample cases. Chromium, Firefox, WebKit and the WPF
tests executed in this source run; its passing checks do not classify the historical remote browser failures.

## Remaining B01 gates

- Retrieve authenticated CI logs/TRX; classify and reproduce individual remote failures, including other OSes.
- Verify distributed 0.5.0 PDBs/source and capture published-0.5.0 migration inputs.
- Complete browser and packaged-browser diagnosis, then the full declared provider/host matrix.
- Produce a new immutable candidate from clean committed source under the compatibility policy; run isolated
  package consumers, bundled browser assets, migrations and protocol compatibility against that exact candidate.
- Preserve ten consecutive green full qualification runs, immutable identities and protected release-tag evidence.

Independent crypto review, browser capacity work, physical devices and the optional extensions remain in their
respective B02–B08 workstreams. Nothing in this first B01 slice promotes those capabilities to supported status.
