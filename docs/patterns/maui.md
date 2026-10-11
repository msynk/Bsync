# Pattern: .NET MAUI host integration

Bsync has no MAUI package. The integration is a few dozen lines over public API, so it ships as a recipe:
[`MauiSync.cs`](../../src/Samples/Bsync.Samples.Hybrid.Maui/MauiSync.cs) in the MAUI sample (task G1). Copy it into
the app and change its namespace. The app then references only `Bsync`, `Bsync.Storage.Sqlite` and a native SQLite
bundle, and the library build needs no MAUI workloads.

| Piece | What it does |
|---|---|
| `MauiSync.UseMauiLifecycle()` | Sets `SyncSessionOptions<T>.AttachLifecycle` (and keeps any lifecycle the options already had): sync when internet access returns, pause when the app's windows stop, resume and sync at once when they come back. |
| `MauiSync.AttachAsync(session)` | The same subscription without the options helper. Disposing the result unsubscribes. |
| `MauiSync.RunInBackgroundAsync(session, account, budget)` | One budgeted sync (`SyncEngine<T>.SyncForAsync`) for an Android `WorkManager` worker or an iOS `BGTaskScheduler` task. Pass `SyncResult.HasRemainingWork` on to the scheduler. |
| `SecureReplicaKeys.GetOrCreateAsync(account)`, `Delete(account)` | A 32-byte key per account in MAUI `SecureStorage` (Keychain, Keystore, DPAPI), for an encrypted SQLite replica (ADR-016). |

```csharp
builder.Services.AddLocalSyncCollection<Note>(_ => new SyncSessionOptions<Note> { /* ... */ }.UseMauiLifecycle());
var key = await SecureReplicaKeys.GetOrCreateAsync(account);          // with SQLitePCLRaw.bundle_e_sqlcipher
var result = await MauiSync.RunInBackgroundAsync(session, account, TimeSpan.FromSeconds(25));
```

Connectivity is a hint only: the session still reconciles on its interval (I13). Registering background tasks is
platform code in the app. Status: used by the MAUI sample on Windows (`MauiHybridTests`); connectivity events,
background runs and `SecureReplicaKeys` are not exercised by a test, and Android, iOS and Mac Catalyst were not built
([support matrix](../support-matrix.md)).
