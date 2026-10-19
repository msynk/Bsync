# Local qualification continuation — 2026-10-19

This continuation executes work available on the Windows development machine. Physical-device testing remains
explicitly deferred by the user. It does not waive independent cryptographic review or release provenance requirements.

This continuation started from immutable core preview `0.4.0-gapclosure.20261019.1`. Final preview
`0.4.0-gapclosure.20261019.3` contains the streaming browser digest and unlimited-capacity inventory changes; results for earlier previews
must not be attributed to the new bytes. Detailed validation is recorded below.

## Findings and implementation

The first large browser matrix exposed a real memory failure: the .NET 10 browser incremental SHA-256 provider
buffers all input before one-shot managed hashing. The 1 GiB Chromium case exhausted memory while preparing its reference
digest. The same provider was used by full-content completion/verification in Bsync, so the fix covers those paths too.
See the [.NET browser provider](https://raw.githubusercontent.com/dotnet/runtime/v10.0.13/src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/SHAHashProvider.Browser.Managed.cs).
The explanatory source comment was corrected after qualification; its original snapshot and the exact comment-only
replacement are recorded in the validation inventory. Qualified package bytes and runtime code remain unchanged.

`StreamingSha256` uses bounded SHA-256 block state on browsers and preserves the platform implementation on native
targets. Compression and padding follow [FIPS 180-4](https://csrc.nist.gov/pubs/fips/180-4/upd1/final). It changes no
content identities, public APIs, wire messages or persisted formats. Native known-answer vectors, padding/fragmentation
comparison and a 513 MiB length-counter/allocation regression exercise the browser implementation independently.
This is functional verification, not an independent cryptographic review. The qualification driver computes expected
identities using native SHA-256 so browser verification does not certify itself.

The new browser qualification runner measures allocations, per-operation percentiles, sampled managed/JS memory,
WASM reserved capacity and visible crypto-buffer overlap with a fixed 1 MiB caller buffer. Those counters overlap and
are not a physical process peak or proof of <32 MiB cache-owned memory. Provider ciphertext write counts do not measure
IndexedDB metadata or physical-media write amplification.

The larger workloads also exposed a redundant physical inventory scan before each chunk write with the default
unlimited hard-budget setting. IndexedDB's inventory cursor reads each existing ciphertext value, so repeating it
on every append causes quadratic payload-read I/O. The unlimited path now skips that scan; finite configured budgets
retain serialized physical-capacity enforcement. A regression checks both behaviors, and the browser harness requires
zero inventory calls during unlimited appends. Finite-budget browser scans remain an explicit performance limitation;
no provider schema or accounting migration was introduced to conceal that cost.

The sample S3 completion path now sends a known-length verified snapshot with an explicit SHA-256 checksum, avoiding
stored `Content-Encoding: aws-chunked` metadata observed with SeaweedFS 4.48. Before the change, its response failed in
WebKit while Chromium and Firefox accepted it. All three passed after the change. The
[AWS request contract](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/S3/TPutObjectRequest.html) documents the
chunk-encoding switch and explicit checksum. No signature/checksum validation is disabled.

The object-store runner supports a separate `--local-service` mode. It requires loopback, records that it is a real
local service rather than an emulator, and never marks an external deployment qualified. The reproducible SeaweedFS
driver uses random ephemeral credentials, an isolated directory/bucket, loopback listeners and redacted logs.

The native benchmark's reports now use source-generated JSON serializers so the Windows self-contained trimmed
executable can run with reflection serialization disabled and linker warnings treated as errors.
Direct discovery also found MSVC 14.51.36231 and Windows SDK 10.0.26100.0, superseding the earlier incomplete
component inventory. Native AOT exposed an additional reporting bug: embedded assemblies have no DLL location.
The tool now records the executable hash for native AOT/single-file publications, leaves the unavailable managed-DLL
hash null, and retains exact input-package hashes separately. Ordinary managed runs still hash the loaded DLL.
The Windows native AOT publish passed with AOT/linker warnings treated as errors. The initial IL3000 failure is retained.

Browser profile mode is explicit. A private Chromium context rejected the 1 GiB workload with a storage-quota error
after the hashing fixes; it did not silently fall back or claim success. The runner can instead create a fresh temporary
on-disk profile, record the browser's storage estimate, and delete that profile after each case. The Chromium persistent
profile reported a 10 GiB quota. No quota override is applied. Private and persistent-profile results are separate;
storage estimates do not guarantee that a later write will fit.

## Evidence

Artifacts, retained failures, hashes and final results are under `artifacts/gap-closure/local-qualification/`.
The [validation inventory](gap-closure-local-qualification-validation-2026-10-19.json) records exact packages,
source snapshots, executable/host hashes, executed results and retained failures.

The machine is Windows 10 Enterprise x64, Intel Core Ultra 7 255H (16 logical processors), .NET SDK 10.0.402,
native runtime 10.0.13 and installed WASM toolchain 10.0.12. Browser versions are recorded per case.

| Final preview `.3` check | Executed result |
|---|---|
| Source tests | 928 passed, no failures/skips |
| Exact-package consumers | 210 passed, no failures/skips |
| Functional browser tests | Six WASM AOT and six interpreter checks passed across Chromium, Firefox and WebKit |
| Native memory workloads | All ten stages passed for each of 1, 16, 17, 256 and 1024 MiB in fresh processes with a 31 MiB whole-managed-heap cap |
| Windows self-contained trimming | Publish passed with linker warnings treated as errors; five size workloads and a 17 MiB/31 MiB-cap ten-stage workload passed |
| Windows native AOT | Publish passed with AOT/linker warnings treated as errors; all five size workloads and five fresh-process 31 MiB-cap ten-stage workloads passed, through 1 GiB |
| Real local SeaweedFS 4.48 | All twelve service checks passed, including expiry, refresh, three-engine CORS and cleanup |
| Object-store runner guards | All nine rejected-invalid-configuration checks passed |
| Package-only web smoke | Package initialization, workflow completion and synchronization passed |
| Package provenance | Six immutable `.3` archives match the isolated package cache; package README and browser assets match source/publications; six symbol pairs and 213 embedded-source checksums verified |

The sample's six LocalDB/S3 regression checks passed before the final inventory-scan optimization; its sample source
was unchanged by that optimization. Large browser-profile measurements are recorded separately below.

The five native capped cases sampled maximum managed bytes of 18.11, 28.68, 25.44, 26.46 and 28.41 MiB respectively.
The enforced cap covers the whole managed heap; it does not constrain native allocations or establish cache-only
ownership. Trimmed serialization uses generated metadata and runs with reflection serialization disabled.
The corresponding native AOT cap cases sampled maxima of 12.24, 29.33, 28.25, 24.35 and 28.99 MiB. Their reports
record a two-processor runtime setting; these are separate configurations from the ordinary native measurements.
The added manual CI workflows cover browser sizes, real local object storage and Windows native AOT. YAML and
PowerShell syntax were checked locally after substituting GitHub expressions; no remote workflow execution is claimed.

The initial browser driver used a ten-minute diagnostic deadline. Timeouts are retained as failed attempts, including
Chromium's persistent-profile 1 GiB interpreter case during the final hash pass. The current driver records its
twenty-minute deadline and can select engines for a focused retry. No deadline change alters the library's operation
budgets or establishes a latency SLO. Concurrent workloads and instrumentation affect these timings.
The overlapping matrices were interrupted after system memory pressure was observed; completed case files remain
unchanged and missing primary cases were scheduled sequentially. A subsequent Chromium startup failed to fetch
runtime assets; its owned browser was closed after a fresh startup probe passed. This is retained as a failed attempt,
not attributed to Bsync content verification. The current runner logs startup/page errors, bounds startup waits at
two minutes, and retries transient generated-profile cleanup failures before reporting cleanup unsuccessful.

Two generated persistent profiles are retained: automatic approval review rejected manual cleanup of
`C:\Users\msynk\AppData\Local\Temp\bsync-browser-qualification-3aadsny1.ugp` with “blocked by policy”; normal runner
cleanup of the interrupted Chromium startup also failed with access denied. Their paths and reasons are recorded in
[the retained-profile record](../../artifacts/gap-closure/local-qualification/retained-temporary-profiles.json).
No alternate deletion method was used for either retained profile.

## Browser workload results

The completed persistent-profile workloads are below. All use the final `.3` packages and 1 MiB encrypted-v2
chunks. Each completed workload passed all six stages, wrote exactly `sizeMiB * (1 MiB + 28 bytes)` of ciphertext,
performed zero unlimited-capacity inventory scans, and removed its generated profile. Detailed stage timing,
allocation deltas, browser versions, storage estimates, host manifests and driver hashes are in the validation JSON.

The columns are maxima across the passing sizes in each row, in MiB. These overlapping counters must not be added.

| Mode | Engine | Passed sizes (MiB) | Sampled managed | WASM reserved capacity | Sampled JS heap | Visible crypto buffers |
|---|---|---|---:|---:|---:|---:|
| Interpreter | chromium | 1, 16, 17, 256, 1024 | 31.01 | 115.00 | 80.99 | 2.0000 |
| Interpreter | firefox | 1, 16, 17, 256, 1024 | 28.99 | 115.00 | Unavailable | 2.0000 |
| Interpreter | webkit | 1, 16, 17, 256 | 27.49 | 115.00 | Unavailable | 2.0000 |
| WASM AOT | chromium | 1024 | 28.82 | 95.81 | 82.11 | 2.0000 |
| WASM AOT | firefox | 1024 | 28.80 | 95.81 | Unavailable | 2.0000 |
| WASM AOT | webkit | 256 | 26.14 | 79.81 | Unavailable | 2.0000 |

Managed baselines ranged from 5.20 to 5.22 MiB after provider setup and
the caller-buffer allocation. Subtracting these baselines does not establish cache-owned instantaneous memory.
Large workloads cover append, publication, sequential read, full hash, seeded ranges and cancellation before read
admission. Repair/reload and encrypted lifecycle behavior have separate functional tests; do not infer that every
operation was exercised at every large size. Raw `BrowserBlobStore` import/complete operations retain whole-object
steps; these measurements qualify the encrypted-v2 chunked workload.

WebKit's 1 GiB AOT attempt timed out during append and reported only 1000 MiB origin quota, below the minimum
ciphertext size for that case. Its declared workload ceiling here is 256 MiB. This is a measured setup/profile limit,
not a universal browser limit. No quota override or plaintext fallback was used.

The private-profile cohort completed the four sizes through 256 MiB in all three engines. Chromium's 1 GiB case
failed with an explicit storage-quota error; Firefox's hit the original ten-minute timeout; WebKit's 1 GiB attempt
was interrupted when the overlapping runs were stopped. These are retained separately from persistent-profile
results. The subsequent interpreter retries, WASM AOT results and profile-cleanup exceptions are not substituted
into an earlier cohort's failed or incomplete manifest.

## Remaining boundaries

| Open item | Why this local continuation does not close it |
|---|---|
| Physical devices, OS scheduling and physical-media performance | Explicitly deferred by the user; desktop browser engines cannot establish device behavior |
| Full Android host | Android reference/runtime packs and JDK are installed, but the Android SDK is absent; prior official SDK downloads failed |
| Apple host, signing and background URLSession relaunch | No Mac/Xcode/signing host is configured |
| Independent crypto review | Functional vectors and cross-implementation hashes are evidence for a reviewer, not an independent design review |
| Cache-owned instantaneous memory and physical write amplification | The tools measure sampled managed/JS bytes, WASM capacity, provider payload I/O and native logical I/O; these do not establish those stronger claims |
| External object-store deployment | Loopback SeaweedFS is real service evidence, but no external TLS/IAM deployment or credentials were supplied |
| Release provenance and stability gate | The tree is dirty and the new stable version is unassigned; no immutable release tag, remote source verification or ten-consecutive Windows/Linux/macOS qualification runs were produced |
| Host key deletion | Retention journals exist; deletion still requires the application's complete inventory across v1 stores, protected metadata and backups, plus host policy |
| Grouped packages | Conditional extension not selected: no declared supported workload requires more than 256 items per package |

The published-0.4.0 fixtures were captured during earlier implementation, so the historical instruction to capture
them before the first edit cannot be retroactively satisfied. Their actual binaries and provenance are retained.
Hashing changes no wire contract; existing cross-version protocol evidence remains separate.
