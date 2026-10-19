# Non-device continuation — 2026-10-19

The user explicitly deferred physical-device testing for this implementation pass. That deferral does not turn device
checks into passes or remove the separate service, build, cryptographic-review and release requirements. The preceding
[core qualification record](gap-closure-envelope-memory-2026-10-19.md) remains valid for immutable preview
`0.4.0-gapclosure.20261019.1`. This continuation changes the S3 sample, tests, qualification tooling and documentation;
the six package archives and library source files are unchanged.

## S3 publication integrity

The S3 sample previously hashed the uploaded object and then copied its staging key to the content-addressed key.
A still-valid presigned PUT could replace that staging object between the read and copy, publishing bytes that had
never been verified. Completion now downloads into a newly created, exclusively held disk snapshot, checks both
declared/actual length and SHA-256, and uploads that exact stream. It never reopens the untrusted staging key for
publication. Existing completed content is still deduplicated. Publication failure retains remote staging for retry.

The new `MaximumStagedBytes` property defaults to 512 MiB per completion. It bounds both the announced size and the
actual copied bytes. Hosts must also limit concurrent completions and provision protected temporary storage. The
snapshot is plaintext, uses delete-on-close and is removed on normal/error paths. Completion now consumes local disk
and a server-to-bucket upload, in addition to the existing verification download; this is documented rather than
claiming all completion traffic bypasses the server. The client PUT remains direct.

Five regressions cover staging replacement between verification/publication, interrupted publication, oversized
announced size, streamed bytes exceeding that declaration and truncation. They require exact published bytes, no
server-side mutable-key copy, appropriate staging retention and no remaining local plaintext files.

## Real-service qualification runner

The [runner](../../src/Tools/Bsync.ObjectStoreQualification/README.md) accepts an existing dedicated unversioned test
bucket, endpoint, region and credentials from environment variables. It changes only a fresh random run prefix and
fails if cleanup is incomplete. It never changes bucket configuration or deletes the bucket. Reports contain static
failure codes/types, HTTP status, assembly hashes and test identifiers, not credentials, presigned URLs or response bodies.

The runner uses actual `HttpBlobTransfer` requests against a service, with a synthetic in-process ticket authority.
It covers direct upload, lost completion acknowledgement/reconciliation, service-enforced expiry, one refresh of an
expired ticket while resuming a partial download, credential separation, correct range/hash content, orphan collection
and browser PUT/full GET/range/`Content-Range` exposure in Chromium, Firefox and WebKit. Tenant authorization and SQL
transaction semantics remain separately tested by the existing sample test; this runner does not impersonate a full
production authority.

The optional [workflow](../../.github/workflows/object-store.yml) builds unique local packages, runs against configured
service credentials and retains artifacts. It was inspected locally, not dispatched. Its existence is not qualification.

## Executed evidence

Evidence is retained under `artifacts/gap-closure/nondevice`; hashes and test counts are recorded in the
[validation manifest](gap-closure-nondevice-validation-2026-10-19.json).

| Check | Result |
|---|---|
| S3 snapshot unit regressions | Five passed |
| Current SQL Server 2025 LocalDB + moto sample path, including five regressions | Six passed, no skips; interrupted upload/download, tenant denial, reconciliation and orphan behavior covered |
| Exact-package runner build | Passed, no warnings/errors; loaded core/server DLL hashes compared with preserved `.20261019.1` archives |
| Configuration/evidence guards | Five passed; invalid arguments, insecure real endpoint, non-loopback emulator, missing credential and existing output rejected without exposing test secrets |
| Current moto 5.2.4 runner workload | Ten checks passed, two failed; process exit 1 and `ExternalServiceQualified=false` |
| Actual external S3 service | Not run: no configured endpoint/bucket/credentials supplied |
| Android SDK retrieval | Official command-line-tools URL still returned HTTP 404 from this environment |

The two emulator failures are **expected limitations, not passes**: moto accepted the expired signed URL, so there was
no 401/403 to trigger a refresh. Expiry enforcement and exactly-once refresh therefore failed. Native resumed-content
hash/ranges and credential separation passed independently, as did all three browser CORS checks and both cleanup
checks. Earlier harness iterations remain retained separately. These observations cannot qualify an external service.

The original full source/package/browser/memory qualification counts (919/208/6/15) belong to the preceding core
candidate. They were not rerun unnecessarily for this sample-only change and are not counted as a new designated run.

## Current disposition of the plan

| Remaining requirement | Disposition |
|---|---|
| Physical Android/iOS, native Safari/device lifecycle, battery/force-stop and device-only performance | Deferred by user instruction; no support claim added |
| Android full host build | Blocked by SDK retrieval; existing portable/reference compilation is not an APK build |
| Apple full host build, signed/AOT output and URLSession host integration | Mac/Xcode/build configuration still needed; the existing sample keeps URLSession opt-in and does not claim it enabled |
| Real object-store expiry/refresh/CORS | Runnable harness supplied; a configured dedicated service/bucket is still needed |
| Cache-only/native/browser memory attribution and non-device performance | 31 MiB whole-managed-heap matrix remains evidence for its declared workload, not a broader attribution proof |
| Independent cryptographic design review | Still required; this implementation pass is not independent review |
| Key retirement | Library journals supplied; host must account for v1, metadata, backups and every key user before deleting keys |
| Fixtures captured before implementation | Cannot be satisfied retroactively; actual published-binary fixtures exist, timing deviation still needs review disposition |
| Wire compatibility process matrix | Conditional on wire-affecting changes; this continuation changes no wire contract |
| Grouped packages | Conditional extension remains unselected; no declared workload exceeds the individual-package bound |
| Clean new-version tag, remote source verification and ten designated Windows/Linux/macOS runs | Still required for release; dirty previews and selective local tests do not substitute |

No package, release or tag was published. Physical-device deferral alone is insufficient to mark the entire plan complete.
