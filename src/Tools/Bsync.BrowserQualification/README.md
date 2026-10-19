# Browser chunk qualification

Publish the package consumer into an isolated output/intermediate directory first. This runner never builds or changes
the supplied host. Install Playwright's Chromium, Firefox and WebKit, then run:

```powershell
dotnet publish src/Tests/PackageConsumers/Bsync.PackageConsumer.Browser -c Release -p:BsyncPackageVersion=<exact-version> -p:RunAOTCompilation=false -p:IntermediateOutputPath=obj/qualification-interp/ -p:OutputPath=bin/qualification-interp/ -o artifacts/browser-qualification-host
dotnet build src/Tools/Bsync.BrowserQualification -c Release
pwsh src/Tools/Bsync.BrowserQualification/bin/Release/net10.0/playwright.ps1 install chromium firefox webkit
dotnet src/Tools/Bsync.BrowserQualification/bin/Release/net10.0/Bsync.BrowserQualification.dll artifacts/browser-qualification-host/wwwroot artifacts/browser-qualification-results
```

The default matrix is 1, 16, 17, 256 and 1024 MiB in all three engines. An optional third argument selects sizes,
such as `1,17`. Each case starts a new browser process and uses a single 1 MiB caller buffer with deterministic data.
Use `--engines=chromium` (or a comma-separated subset) to reproduce a failed case without rerunning other engines.
The report records the selected engines; a subset run must not be described as a complete three-engine matrix.
By default it uses a private context. Add `--persistent` after the optional size list to use a fresh temporary on-disk
profile for each case. The runner records the selected mode and browser storage estimates, closes each process and
removes its generated profile. It never uses an existing user profile or overrides browser quota. Private profiles may
have a lower quota than normal on-disk profiles; retain their quota failures and report the two modes separately.
The output directory must not already exist. Every case is written immediately, including failures; a combined report
records runtime/browser versions and SHA-256 of every published host file. Startup waits have a two-minute timeout;
each workload has a twenty-minute diagnostic deadline. Startup progress and page errors are logged separately.
Earlier ten-minute timeouts remain failed attempts; a later completed run is recorded separately and does
not establish a latency SLO.

Checks cover exact ciphertext write bytes (one chunk write of plaintext length + 28 bytes per aligned append), publication,
full SHA-256 on sequential read, 100 seeded ranges touching at most two chunks each, and cancellation before read admission.
The workload uses the default unlimited hard capacity setting and requires zero inventory scans during append. Finite
configured hard budgets still enforce physical capacity through provider inventory; their browser scan cost is not
represented by this workload and must not be inferred from these timings.
Each stage reports single-managed-thread allocation deltas and nearest-rank p50/p95/p99 operation latency. A probe
checks that the counter advances; unavailable values are null, never reported as zero allocations. With few observations these are
descriptive samples, not statistical tail guarantees. Measurements include instrumentation overhead.

One forced managed collection establishes a baseline after allocating the caller buffer and initializing providers.
No forced collection occurs inside measured stages. Managed bytes are sampled at chunk-provider boundaries while
ciphertext is live, and at operation boundaries. They include runtime allocations and uncollected garbage; subtracting
the baseline is **not** proof of cache-owned live memory or a true instantaneous peak.

Pre-start JavaScript instrumentation observes exported/imported WebAssembly memories, ten-millisecond samples, and
WebCrypto input/result overlap. Reports distinguish WASM reserved linear-memory capacity, Chromium's exposed JavaScript
heap estimate, and visible live crypto-buffer bytes. Firefox/WebKit JS heap values are null when unavailable. These
counters overlap: do not add them together. No internal WebCrypto copies, IndexedDB allocator buffers, browser process
peak or physical storage writes are observable through this tool. Ciphertext-provider I/O excludes metadata and storage
engine amplification. Cancellation measurements cover admission, not interruption of native crypto/storage operations.

The full <32 MiB incremental cache-owned-buffer gate and physical-device/flash qualification remain separate.
The driver computes expected content identities using native platform SHA-256, independently of the bounded browser
implementation. Full verification is a separate measured read pass. This avoids the .NET browser incremental provider's
whole-input buffering, which the initial 1 GiB qualification attempt exposed.
Use distinct intermediate/output directories for AOT and interpreter publications: AOT may strip IL, so sharing them
can make an interpreter host fail before startup. The ordinary functional browser suite can target either publication
with `BSYNC_BROWSER_HOST` and `dotnet test ... --no-build`.

The optional [workflow](../../../.github/workflows/browser-chunks.yml) builds unique exact-package interpreter/AOT
hosts and retains all five sizes for Chromium/Firefox and 1/16/17/256 MiB for WebKit. A separate workflow input probes
1 GiB in WebKit: this machine reported only a 1000 MiB origin quota and the attempt timed out, so that size is outside
its recorded workload ceiling. These are workload/profile limits, not claims about every browser deployment or every
cache operation. This is a separate diagnostic matrix, not the
ten-consecutive designated release gate. A workflow file alone is not evidence of remote execution.
