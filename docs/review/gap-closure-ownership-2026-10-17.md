# Gap-closure ownership continuation — 2026-10-17

This continues the [maintenance checkpoint](gap-closure-maintenance-2026-10-17.md). The new development candidate is
`0.4.0-gapclosure.20261017.6`; `.4` and `.5` remain archived without byte/version reuse. This is not a stable release or the
complete external qualification of the improvement plan.

## Implementation

`AccountMaintenance` provides the opt-in proposal described in [ADR-029](../architecture/adr-029-account-maintenance.md).
It opens fresh resources under exclusive physical account ownership, checks urgent work before each page, shares a
single cooperative deadline, and persists replayable cursors after successful pages. Dry run changes neither data nor
progress. Item/byte limits carry across steps and each invocation has a 32-page ceiling. Incompatible step versions or
order are rejected. Callback limits are contracts validated on return, not a way to undo arbitrary host side effects.

Built-in page operations restore pins from package manifests and transfer jobs, then reconcile cache owners from
durable state and physical lifetime probes. Missing records can release orphan pins only under exclusive account
ownership. Unresolved repairs, unknown schemas/identities, legacy/application pins and live readers remain protected.
An absent manifest still referenced by active/latest pointers, or an unknown transfer-cleanup journal, also preserves pins.
Rotation/import temporary lifetimes can be reclaimed after process death without deleting encrypted generations,
quarantine or keys. CAS preserves unexpected concurrent cache changes. Byte charges describe accounted/expected
content examined per record; they are not physical I/O or freed disk bytes.

Native process coverage demonstrates foreground document reads/writes while another process holds a renewable
network lease and a blocked real engine push. Both maintenance and deletion are excluded. A second process test kills
a maintainer after a page's mutation but before its checkpoint; restart preserves the earlier checkpoint and safely
replays the interrupted page. Browser checks restore package/transfer pins and resume progress after a tab reload.
Browser account/cache disposal now joins physical Web Lock completion. Concurrent disposal callers await the same
request rather than returning after signaling the callback to release its lock.

## Validation and provenance

Commands/logs/TRX files are retained under `artifacts/gap-closure/ownership`; exact source/package hashes and final
counts are in the [validation manifest](gap-closure-ownership-validation-2026-10-17.json).

| Check | Result |
|---|---|
| Source core suite, including API baselines and native process death | 810 passed, zero failures/skips (`core-final-guard.trx`) |
| Exact `.6` package consumers | 101 passed, zero failures/skips (`package-preview6.trx`) |
| Published `.6` package web smoke | Blob round trip, package preparation, workflow and sync succeeded |
| Published `.6` repair/chunked/maintenance browser cases | Six passed across Chromium, Firefox and WebKit, zero failures/skips (`browser-preview6.trx`) |
| Published `.6` account deletion, dead-tab readers and cross-tab cache locks | Nine passed across all three engines, zero failures/skips (`browser-locks-preview6.trx`) |
| Symbols/source verification | Six assembly/PDB pairs; 211 embedded source checksums; zero remote source checks |
| Deterministic JavaScript release regression | Fails before fix; passes after fix (`node scripts/test_browser_lock_release.mjs`); registered in CI |
| Consumed artifacts | All six package archives match the isolated consumer cache; all three checked browser assets match both source and package |

The earlier 808- and 809-test core runs preceded the final preservation and callback-scope guards. The 810-test run
covers the final C# library source, including rejection of a callback that releases account ownership before checkpointing.
The subsequent JavaScript-only release fix is covered separately by the regression and packaged browser runs.

Environment-label correction: the embedded `.5`/`.6` pack provenance records SDK 10.0.402 and host runtime 10.0.13.
The manifest's summary labels originally inherited 10.0.401/10.0.12 from `.4`; they now match the pack provenance.
Individual test runs did not capture separate runtime snapshots. Package/source hashes and test results are unchanged.

The `.4` package consumer cannot compile the new conformance cases because maintenance APIs are absent; that expected
baseline failure is preserved in `preview4-missing-maintenance.log`. Initial process tests assumed native handle cleanup
was synchronous with process exit and observed `Busy`; they now wait for actual availability under the existing test
timeout, without treating elapsed age as proof. The failed TRX is retained. An initial test helper incorrectly used
asynchronous disposal on `SqliteLocalStore`, which owns no persistent connection; the compile error was corrected.

The first `.5` browser run passed five of six cases; a diagnostic rerun reproduced Firefox returning `Busy` immediately
after maintenance disposal. The Web Lock helper had signaled callback completion without awaiting the request's
physical-release completion. A deterministic delayed-release regression reproduced the defect and also covers
concurrent disposal. `.6` fixes the helper; both failed browser TRXs and the failing regression log are retained.

This adds no new SQL Server, SQLCipher, real object-store, mobile SDK, signing/AOT or physical-device qualification.
Those prior results and open gates remain separate. No stable tag, commit or remote publication is created here.

## Remaining work

The plan now marks its maintenance proposal, built-in owner reconciliation and foreground/network-owner coexistence
items implemented. Remaining work includes broader integrity-work byte/time bounds, consistent cancellation/lease-loss
diagnostics across all execution paths, key-retirement policy and independent crypto review, native/mobile/browser
performance measurements, complete mobile hosts/device lifecycle, real external-service coverage and release gates.
Conditional grouped preparation remains unselected; the individual package item limit remains unchanged.
