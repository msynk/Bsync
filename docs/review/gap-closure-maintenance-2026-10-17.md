# Gap-closure maintenance continuation — 2026-10-17

This continues the [preview `.3` implementation record](gap-closure-continuation-2026-10-17.md) with repair diagnostics
and bounded reclamation fixes. Candidate: `0.4.0-gapclosure.20261017.4`. This is a dirty development build from the
published 0.4.0 base, not a stable release or complete qualification of the improvement plan. Preview `.3` archives
remain under `artifacts/gap-closure/preview3-feed`; no published or earlier preview version was reused.

## Changes

- Repair journal transitions now emit metrics when revalidation queues a repair. Retry/error changes are counted;
  repeated acknowledgements of the same completed generation do not recount repaired bytes. Attempt duration covers
  replacement of damaged content and repair of missing content. Error tags use a fixed allowlist.
- `OfflineMetrics.RefreshAsync` and the redacted export report queued repairs, journaled chunk reservations and their
  quarantine-protected subset. Existing pinned-reservation gauges report protected cache capacity. Cached gauge
  callbacks perform no storage access; snapshot age, truncation and unsupported-record indicators remain required.
- `ChunkedEncryptedBlobCache.ObserveStoragePageAsync` provides an ordinal continuation for bounded journal scans.
  It works while keys are locked and ciphertext is damaged, without reading content. Invalid inventory fails explicitly.
  Reserved bytes can exceed stored bytes when writes were interrupted; observations are not a consistent physical
  snapshot and never authorize deletion or key retirement. Page histograms must not be summed repeatedly as cache size.
- `FileBlobCache.CollectRetiredPageAsync` advances across unknown and reader-protected files, allowing later retired
  files to be reclaimed. Hosts can persist its continuation across cache instances. The compatibility method retains
  progress within an instance. Memory and exclusive-open probes are bounded by the item limit; directory enumeration
  remains linear and cancellation-aware. Unknown filenames remain untouched and consume physical capacity.
- Managed v1 rotation reserves both generations and holds physical/durable lifetime ownership while crypto executes
  outside the cache metadata lock. The v1 encryption provider still serializes its operations; legacy reconciliation
  and provider integrity work are not thereby made uniformly bounded.
- A native SQLite process test holds a retired read-model snapshot in a separate live process, confirms collection
  cannot delete it, kills that process, and reclaims all 130 retired rows in pages of at most 17. The current generation
  and concurrent local metadata writes survive.

## Validation

Validation commands and logs are retained under `artifacts/gap-closure/maintenance`.
The final results and exact archive/source hashes are recorded in
[machine-readable evidence](gap-closure-maintenance-validation-2026-10-17.json).

| Final check | Result |
|---|---|
| Complete core suite, including API baselines and native process tests | 794 passed, 0 failed/skipped |
| Package-only native consumer, including 17/256/1024 MiB encrypted objects | 87 passed, 0 failed/skipped |
| Package-only browser repair/chunked tests across Chromium, Firefox and WebKit | 6 passed, 0 failed/skipped |
| Package-only web smoke | Upload/download, package readiness, workflow and synchronization passed |
| Package/symbol verification | Six assembly/PDB pairs; 205 embedded-source checksums; no remote source verification |
| Consumed archive/static asset identity | Six exact cached archives and two exact packaged JS assets |

The earlier `core-maintenance.trx` contains 792 passing cases before the cached-gauge additions; `core-final.trx`
covers the final 794 cases. Successful final build/test logs contain no compiler warnings or errors. These are
functional checks on Windows 10 x64 with SDK 10.0.401/runtime 10.0.12, not named-target performance qualification.

The first focused metric assertion mistakenly excluded existing direction/result tags; its failed TRX is retained.
The test was corrected. An initial process-test compile used the wrong positional argument for cancellation and was
corrected. A metrics-only rebuild overlapped this task's running core suite and encountered locked output DLLs;
the final validation script runs sequentially after both commands exited. Other user builds were left running.

The final browser consumer exercises paged quarantine observations in Chromium, Firefox and WebKit alongside partial
reload, rotation, stable old readers, repair, downgrade refusal, protected payload atomicity and the independent Node
format vector. Browser-engine coverage does not imply physical-device or native Safari qualification.

## Remaining boundaries

This section records preview `.4`. The subsequent [ownership continuation](gap-closure-ownership-2026-10-17.md)
implements the account maintenance proposal and durable built-in pin reconciliation with its own preview/test evidence.

This slice closes the plan's repair metrics item. It does not close independent cryptographic review/key-retirement
policy, selected-target memory/latency/write-amplification measurements, mobile builds/signing/device lifecycle,
URLSession host integration, real external-service ticket/CORS/orphan policy, general built-in owner reconciliation,
or the unified account-owned maintenance proposal. Legacy full-list paths retain their documented limitations.

Release qualification still requires a clean immutable version tag, remote source checksum verification, the advertised
provider/runtime matrix and ten consecutive designated qualification runs. SQL Server and SQLCipher tests from the
earlier continuation are historical evidence and were not rerun in this maintenance slice. B7 remains conditional and
unselected. No new stable release, commit, tag or remote publication was created.
