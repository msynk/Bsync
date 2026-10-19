# Native background execution recipes

`src/Samples/Bsync.Samples.NativeBackground` contains optional Android WorkManager and iOS BGProcessingTask adapters.
It is an unpackaged sample, not a claim of physical-device qualification. The default `net10.0` target builds the common
fresh-scope runner; select `-p:NativeBackgroundTarget=net10.0-android` or `net10.0-ios` on an appropriately equipped host.
The core libraries acquire no Android, Apple or MAUI dependencies. Record exact workload, platform SDK, device and OS
when qualifying these adapters; a portable build does not compile the platform-conditional files.

## Complete reference-host source

The unpackaged [`Bsync.Samples.Background.Android`](../../src/Samples/Bsync.Samples.Background.Android) and
[`Bsync.Samples.Background.Apple`](../../src/Samples/Bsync.Samples.Background.Apple) projects provide application
bootstrap, declared platform configuration, a queue button and foreground/resume reconciliation. Their shared
[`HeadlessSampleHost`](../../src/Samples/Bsync.Samples.NativeBackground/HeadlessSampleHost.cs) opens a durable account
catalog and fresh SQLite scope under the same physical account lock. It verifies registration and deletion tombstones
before reading work. Background callbacks never provision an account; only the explicit foreground demo action does.
Demo work atomically moves bounded local queue items to results; it does not pretend to synchronize a domain server.

Work is persisted before requesting the dispatcher. Fresh scopes reconcile the enqueue/request crash window. Runtime
budgets begin before scope opening and include joined disposal; overruns are reported instead of claiming timely
completion. Production hosts replace the demo handler with their document/transfer work, existing-key provider and
dispatch-time credential acquisition. The demo does not create a replacement encryption key in a headless callback.

| Target | New host evidence | Remaining gates |
|---|---|---|
| Portable headless bootstrap | Executed core tests: relaunch, durable work, stale removed-account callback | Production identity/key/credential integration |
| Android application | Complete source and manual CI build job; local build blocked by missing SDK and SDK download HTTP 404 | Full app build, signed install, custom worker factory, physical lifecycle/key tests |
| Apple application | Complete source and manual simulator build job; not built on this Windows host | Matching Mac/Xcode workload, simulator build, signed device lifecycle/key tests |

The Apple host enables BGProcessing only. The separate URLSession download recipe below is **not enabled or qualified
by this host**. Background uploads remain unsupported. OS-owned plaintext download spool/file-protection policy must
be supplied and tested before enabling downloads on a device. `.github/workflows/native-background.yml` builds the
full hosts after recipe compilation when explicitly dispatched; this local change does not assert that CI ran.

Distribution remains maintained source recipes, not new NuGet packages; the six-package release inventory is unchanged.

At application bootstrap, including headless process relaunch, configure `NativeBackgroundRuntime.Current` with:

- An account-key resolver that consults the durable local account catalog and creates a **fresh** account service scope.
  Refuse deleted/signed-out accounts. Acquire the shared account-resource lifetime before opening stores, use the same
  background execution catalog as foreground work, and obtain existing protected keys without creating replacements.
- A bounded enumeration of pending account keys (maximum 100 per run). It must come from durable catalog state,
  not a window's selected account or transient observer notifications.

The resulting `IBackgroundAccountScope` supplies `Account`, `Dispatcher` and a cooperative `ExecuteAsync` that runs
document synchronization/managed transfers under one account's current credentials. It reports remaining work, key or
credential unavailability honestly. Disposal joins/stops that account's resources. The runner checks the resolved account
identity and uses `BackgroundWorkDispatcher`'s lease and generation fence; a stale worker cannot clear newer work.
Call `runtime.ReconcileAsync(TimeSpan.FromSeconds(25), stopToken)` on foreground/resume as well, even if OS scheduling
reported success. Request generation and execution outcome are durable; OS task delivery is not guaranteed.

## Android

Use `AndroidBackgroundScheduler(applicationContext)` via `dispatcher.RequestAsync`. The recipe uses a unique
`bsync/{Account.StorageKey}` name, connectivity/charging constraints, exponential backoff and a bounded headless worker.
Configure the runtime in `Application.OnCreate`, retain the worker's registered Java name across upgrades and allow
AndroidX's WorkManager initializer in the merged manifest. Declare INTERNET and ACCESS_NETWORK_STATE in the host.
If the host supplies a custom WorkManager configuration/worker factory, integrate it explicitly; do not initialize
WorkManager twice. Register `scheduler.CancelAsync(account)` in coordinated account deletion before removing
its catalog/stores/keys. No periodic precision, expedited entitlement or foreground-service execution is promised.

`OnStopped` cancels the same token used by account work. WorkManager retries incomplete/unavailable work with backoff;
foreground overlap is handled by the persisted execution lease. KEEP may coalesce a request with a worker just finishing;
the durable pending state and foreground fallback cover that scheduling race. Callbacks must honor cancellation and
join network operations before scope disposal. Review current [WorkManager lifecycle and unique-work behavior](https://developer.android.com/develop/background-work/background-tasks/persistent/how-to/manage-work)
and [constraints](https://developer.android.com/develop/background-work/background-tasks/persistent/getting-started/define-work).

## iOS/iPadOS

Choose a reverse-DNS identifier, for example `com.example.app.bsync.processing`, and include exactly that value in
`BGTaskSchedulerPermittedIdentifiers`. Add `processing` to `UIBackgroundModes`. Construct/register one
`IosBackgroundScheduler(identifier)` during AppDelegate launch before launch completes, including headless launches;
registration failure is configuration failure, not success. Submit through the durable dispatcher after work is saved.
The recipe requests network connectivity, wires expiration cancellation, sets completion once after work is joined,
and resubmits incomplete work. EarliestBeginDate is a hint; it is not a schedule or deadline. Per-account charging
constraints are explicitly unsupported by this application-wide recipe.

Key availability while locked depends on the application's keychain accessibility policy; the scope must return
KeyUnavailable and preserve queued work. Never display an unlock prompt from headless execution. Large OS-owned file
downloads use the separate opt-in `IosBackgroundDownloads` recipe below; bounded document work still uses
HttpBlobTransfer. See [Apple BGTaskScheduler](https://developer.apple.com/documentation/backgroundtasks/bgtaskscheduler)
and [BGProcessingTask](https://developer.apple.com/documentation/backgroundtasks/bgprocessingtask).

### Background download callbacks and relaunch

`BackgroundDownloadInbox` persists a schema-1 account/hash/size/pin-owner receipt before an OS task can run.
It uses a dedicated per-account orchestration store, spool directory and `FileBlobCacheLock` directory. Do not reuse
the managed cache's metadata lock directory. `IosBackgroundDownloads` recreates the same account-specific URLSession
identifier during AppDelegate launch, so native callbacks do not require the original page or service lifetime.

1. Load a bounded durable account catalog during **every** launch, including launches for background events. Construct
   and retain one inbox and one `IosBackgroundDownloads` for each active account; never re-register a deleted account.
   Use a stable application prefix across upgrades. Catalog/receipt access must be possible under the selected
   device-lock policy; unavailable metadata leaves work pending, not complete.
2. Call `inbox.PrepareAsync(hash, size, durablePinOwner)` before scheduling. Obtain an authorized HTTPS presigned GET
   ticket through the account's authority connection, then call `downloads.ResumeAsync(id, ticket)`. Do not pass a
   bearer token, authority cookies or authentication handler to the OS session. This session disables shared cookies
   and credential storage. Tickets remain secrets managed by the OS; they are never written into the receipt catalog.
3. In AppDelegate's `HandleEventsForBackgroundUrl`, resolve the **exact** identifier to the retained session and call
   `downloads.HandleEvents(completionHandler)`. The delegate synchronously moves the OS temporary file before returning,
   then calls the saved completion handler on the main thread after the event batch finishes. Never start lengthy
   cache verification inside this delegate. See Apple's [background download lifecycle](https://developer.apple.com/documentation/foundation/downloading-files-in-the-background)
   and [temporary-file lifetime](https://developer.apple.com/documentation/foundation/urlsessiondownloaddelegate/urlsession(_:downloadtask:didfinishdownloadingto:)).
4. On launch, foreground resume or an available processing budget, call `inbox.DrainAsync(openFreshAccount, token)`.
   The resolver must acquire the account-resource lifetime, refuse deleted/signed-out accounts, and return that exact
   account and its managed cache. Hash/length verification precedes pinned import. Locked keys, wrong accounts, capacity
   failures and interrupted receipt commits retain the spool and receipt. A replay uses the same pin owner.
5. Enumerate `inbox.ListAsync()` after draining. For pending receipts with no staged file, obtain a fresh ticket and
   retry `ResumeAsync`; existing OS tasks are reconciled by their durable ID, including suspended tasks. Failed or
   expired transfers restart whole downloads. A corrupt staged file requires explicit cancel/repair, not silent
   deletion. Completed receipts are retained until the host calls `ForgetAsync` after ensuring no OS task remains.
6. Before account deletion, stop new scheduling and await `downloads.InvalidateAsync()` to cancel and join OS callbacks.
   Remove the account from the durable relaunch catalog, then delete its inbox/metadata/spool/cache/keys under the
   exclusive account-resource lifetime. Normal app suspension must **not** invalidate the background session.

The inbox reserves at most 100 receipts and 512 MiB by default, includes retained unknown spool files in capacity,
and never deletes unknown files. Progress callbacks cancel oversized responses, but the OS owns its temporary storage;
the reservation is not a hard limit on OS buffers or filesystem consumption. Configure host storage-pressure policy.
The spool contains **plaintext**, including while waiting for cache keys. Set the application's iOS file-protection and
backup-exclusion policy for both spool and catalog. Do not enable this recipe where plaintext OS temporary files are
forbidden: encrypted-cache guarantees cover the cache, not URLSession's staging. Background uploads and application
extension shared-container sessions are outside this recipe. Force-quitting the app may prevent OS relaunch/delivery;
foreground reconciliation remains required. No guaranteed completion time is promised.

Five portable inbox tests cover SQLite reopen, duplicate delivery, locked keys, account mismatch, quota, corrupt bytes,
unknown-file retention and replay after an injected receipt-commit failure. The iOS delegate passes reference binding
compilation; actual OS callback ordering, suspension, relaunch and file protection still require Apple device tests.

## Required device qualification

Run OS-delivered jobs (not only manual invocation), kill/relaunch, expiry during upload, foreground ownership overlap,
locked-device keys, revoked credentials, charging/connectivity changes and prolonged postponement. Assert durable work
remains pending after every unavailable/deferred outcome. Record simulator/emulator results separately from hardware.
At implementation time this Windows workspace cannot qualify iOS execution or either physical-device scheduler.

## Build prerequisites and recorded limits

Use the official [.NET Android dependency installer](https://learn.microsoft.com/en-us/dotnet/android/getting-started/installation/dependencies)
and [Android SDK downloads](https://developer.android.com/studio). The 2026-10-16 Windows attempt installed Android
workload 36.1.69 and Microsoft JDK 17.0.14; the SDK archive request returned HTTP 404. Android source was separately
compiled against the installed reference assemblies and AndroidX Work Runtime 2.11.2.1 with warnings treated as errors.
This catches C# API/nullable/linker-annotation issues but does not build an APK, execute WorkManager or validate a manifest.
The manually invoked `.github/workflows/native-background.yml` is intended to perform complete builds on equipped hosts; its remote execution is not established
by local YAML validation. On Apple hosts, match the workload's required Xcode version before running that job.

The iOS source also passed C# binding compilation against Microsoft.iOS.Ref.net10.0_26.5 version 26.5.10318 on Windows.
This is a reference check only; Xcode, an app bundle, background entitlements and OS callbacks were not exercised.
Reproduce the checks after building Bsync Release and restoring the Android target:

```text
python scripts/check_native_bindings.py android --android-ref <Android.Ref-pack/ref/net10.0>
python scripts/check_native_bindings.py ios --ios-package <Microsoft.iOS.Ref.net10.0_26.5.26.5.10318.nupkg>
```
