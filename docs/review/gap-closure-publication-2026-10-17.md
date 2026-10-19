# Gap-closure publication continuation — 2026-10-17

This continues the [deadline checkpoint](gap-closure-deadlines-2026-10-17.md) with sample-only B6 changes. The six
immutable `0.4.0-gapclosure.20261017.7` package artifacts remain unchanged. No wire, persisted schema, packaged API,
release tag or publication changes were made. This is not a qualified stable release.

## Change

The optional reader-specific Tasks feed previously had an ACL/repair publisher but required applications to duplicate
publication plumbing in their domain writers. `TaskAudienceProjection` now provides explicit dependent-lock and
publication methods on the caller's transaction. API creation, sync update/delete and intent completion/rename accept
the opt-in projection. ACL publication reuses the helper. Each writer locks the Tasks feed, then audience feed, before
domain/attachment/ACL access; an intent starts with its own feed before those locks.

The helper neither commits nor retries transactions, and validates connection/transaction pairing. EF creation keeps
its fresh-context transaction delegate and limited creation replay contract. Sync and intent handlers stay within the
authority transaction. Domain validation, ledger retention and caller-derived authorization remain application-owned.
The default server does not enable the separate feed. The [sample registration guide and writer inventory](../../src/Samples/Bsync.Samples.Tasks.Server/README.md#opt-in-to-the-reader-specific-projection)
cover backfill, contributing writers, endpoint access policies and post-commit hints. An audience projection does not
restrict the original tenant-wide Tasks endpoint or blob permissions.

## Validation

Windows 10 x64; .NET SDK 10.0.402, installed default host runtime 10.0.13; dedicated LocalDB instance
`BsyncGapClosure20261017`, SQL Server engine 17.0.4025.3. Tests create/drop individual `bs_test_*` databases. This is
local integration evidence, not Azure SQL or production deployment qualification.

Commands and TRXs are under `artifacts/gap-closure/publication`; hashes and counters are in the
[validation manifest](gap-closure-publication-validation-2026-10-17.json).

- Focused final run: 10 passed, zero failed/skipped (`publication-fixed.trx`).
- Full SQL Server suite: 107 passed, zero failed, one unconfigured S3 skip (`sqlserver-final.trx`); the dedicated
  LocalDB instance was stopped after validation.
- Nine dependent-publication cases cover before/after-commit execution-strategy retry, permanent rollback, sync
  update/delete/replay, caller-owned rollback, both competing API/sync holding orders with reader changes/repair,
  intent ledger rollback/replay, and rejection of mismatched connections without taking their ownership.
- The tenth case runs twelve authenticated API/sync creation races over Kestrel HTTP, verifies accepted/conflicting
  outcomes and receipt replay, then compares all canonical feed documents to domain rows with revision one.
- Existing API, ACL, projection outbox and reconciliation tests supply related-row, reader removal/grant and complete
  snapshot coverage in the full SQL Server run.

The first focused run had eight passes and one fixture failure (`dependent-publication.trx`). The intent replay
factory regenerated `CreatedAt`, so the request correctly failed the immutable-payload replay check. The fixed test
preserves the original timestamp and also proves rollback of the dependent feed and ledger before retry. The initial
TRX is retained rather than relabeled as a product regression or hidden.

Reproduce the suite in PowerShell with a disposable local SQL Server instance:

```powershell
$env:BSYNC_SQLSERVER='Server=(localdb)\BsyncGapClosure20261017;Integrated Security=true;TrustServerCertificate=true'
dotnet test src/Tests/Bsync.Tests.SqlServer/Bsync.Tests.SqlServer.csproj -c Release --logger 'trx;LogFileName=sqlserver-final.trx' --results-directory artifacts/gap-closure/publication
```

## Limits and remaining work

Core, package-consumer, browser, native and AOT suites were not rerun for this sample-only slice. Their earlier results
remain attached to their original candidates. The S3 test requires `BSYNC_S3`; no object-store ticket/CORS/orphan
qualification is claimed. Real service, physical-device, crypto/performance and release qualification gates remain
open, as do the broader B4 diagnostics and resumable integrity-inventory items. Optional grouped packages remain
unselected. Every consuming deployment must register and audit its own raw SQL, import and job writers; the helper
does not automatically discover dependencies or enforce domain authorization.
