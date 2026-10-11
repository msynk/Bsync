# Disaster recovery and stuck replicas

This runbook is for people who operate a Bsync deployment or support its users. Each section says
what is implemented and tested today, and what is not. The design is recorded in
[ADR-013](../architecture/adr-013-recovery.md).

## 1. The server is restored from a backup

**What must happen on the authority.** A restore loses the history after the backup, so the authority
must:

1. Start a new **epoch**. Every checkpoint issued before the restore is then refused with
   `reset-required` (reason `epoch`).
2. Never reuse a version number. Continue above the highest version the lost history may have issued (a
   **version floor**).
3. Keep the operation receipts that were in the backup.

The reference authority does all three:

```csharp
var backup = server.CreateBackup();                 // documents, receipts, sequence, retention horizon
...
var restored = new InMemorySyncServer<Note>(new InMemorySyncServerOptions<Note>
{
    Cloner = clone, Fingerprint = fingerprint,
    RestoreFrom = backup,
    VersionFloor = highestVersionEverIssued,         // e.g. from monitoring, with a safety margin
});
```

The PostgreSQL authority does the same after the database has been restored (for example with
`pg_restore`, or a point-in-time recovery):

```csharp
var floor = /* a version at or above anything issued before the incident, e.g. from monitoring of
               GetHighestVersionAsync(scope), plus a safety margin */;
await authority.BeginNewEpochAsync(floor);   // new epoch for every collection in the database
```

The SQL Server authority is restored the same way (`RESTORE DATABASE`, or a point-in-time restore of the
application database, which holds the `bsync` schema next to the application's tables, ADR-014):

```csharp
var floor = /* at or above anything issued before the incident, e.g. GetHighestVersionAsync(scope) from monitoring */;
await authority.BeginNewEpochAsync(floor);   // new epoch, and every feed (also feeds created later) continues above it
```

A restore drill for SQL Server, step by step:

1. Before the incident, monitoring records `GetHighestVersionAsync(scope)` per feed (or the `bsync.feeds` table's
   `sequence` column) at least as often as backups are taken.
2. Stop the application's server processes (or put them in maintenance mode), so no replica writes to the database
   while it is restored.
3. `RESTORE DATABASE` (or a point-in-time restore) of the application database. The `bsync` schema and the
   application's tables come back to the same point in time.
4. Before serving requests again, call `BeginNewEpochAsync(floor)` once, with `floor` above every version recorded in
   step 1 plus a margin. Every feed, including feeds created later, continues above it.
5. Start the processes. Replicas reset on their next sync (below); pending local work is kept and uploaded again.
6. Watch `bsync.resets{reason="epoch"}` (client) and `bsync.server.push.operations{outcome="Conflict"}` (server) for
   the following hours; a spike of conflicts means edits the restore lost are being re-decided.

Restoring the application database restores the application's rows and the replication projection to the same
point in time. Tested: `SqlServerAuthorityTests.RestoreDrill` (tables restored from a copy, new epoch, replicas reset
and keep pending edits, no version reused) and `NewFeedStartsAboveFloor`.

Receipts restored with the tables are kept. Tested: `PostgreSqlAuthorityTests.RestoreDrill`. It restores the
tables from a copy taken earlier and checks that replicas reset, keep pending edits and reuse no version. A
`pg_dump`/`pg_restore` round trip was not scripted.

**What users see.** Each replica resets automatically on its next sync:

- It keeps its unsynchronized edits and kept conflicts, and uploads them again. An upload the backup already
  contained is replayed from its receipt. An upload the restore lost is accepted again, or answered as a
  conflict.
- Documents created after the backup exist only on devices. Clean copies of them are hidden (`MissingAfterReset`);
  they are not deleted and are not uploaded again. Documents with local edits are uploaded again and so
  reappear on the server.
- `SyncResult.ResetPerformed` and `MissingAfterReset` report it. Tested by `ResetTests` and by 60 seeded
  randomized schedules with 12–26 restores each.

## 2. Retention: purging tombstones and receipts

- `PurgeTombstones(throughVersion)` removes deleted documents at or below a version and raises the
  **retention horizon**. Replicas whose checkpoint is older than the horizon reset with reason `expired`
  and drop documents the server no longer has. An edit based on a purged document is rejected with
  `base-expired`, never resurrected; writing the document again recreates it explicitly.
- `PurgeReceipts(throughVersion)` removes receipts of accepted operations. Choose a horizon older than the
  longest time a device may stay offline with unsent work. A resend after its receipt was purged is never
  applied twice, but it comes back as a conflict. With the default policy it is then kept for the user,
  although the write had in fact succeeded.
- `AddSyncRetention` (in `Bsync.Server.AspNetCore`) runs both on a schedule for every in-memory, PostgreSQL or SQL
  Server authority it is given: it samples each feed's head every `Interval` (default one hour) and purges what is
  older than `MaxOfflineHorizon` (default 45 days; receipts: `ReceiptHorizon`, never shorter). A receipt horizon
  shorter than the offline horizon fails the host at startup. Samples live in memory, so after a restart nothing is
  purged until one horizon has passed again; run it in one process (several only repeat the same purge). Tested:
  `RetentionServiceTests`, `SqlServerAuthorityTests.RetentionPurgesEachScope`.
- Without the service, schedule `PurgeTombstonesAsync(scope, version)` and `PurgeReceiptsAsync(scope, version)` per
  scope yourself. They are tested by the public authority conformance suite on both durable authorities, and in
  `PostgreSqlAuthorityTests.Retention`.
- Replicas drop their own clean tombstones below the server's horizon (feature `retention`), so device storage does
  not grow with deletions either.

## 3. Access changes

When a user's visible set changes (revoked or granted permissions, a different filter), the authority's
`ScopeFingerprint` changes. The replica's next pull gets `reset-required` with reason `scope-changed`. The
replica resnapshots and removes documents it may no longer see from the device; they are never deleted on
the server. Records with local changes or kept conflicts are never removed. A pending edit to a document that
is no longer writable is rejected (`forbidden`) and kept. Tested by `SelectiveSyncTests`.

## 4. A device's SQLite database is damaged

Symptoms: `SqliteException` with error 11 (`SQLITE_CORRUPT`) or 26 (`SQLITE_NOTADB`), or a failed check.

1. Stop the engine and dispose the store.
2. Check the file:
   ```csharp
   var problems = await SqliteStoreRecovery.CheckAsync(path);   // empty = sound
   ```
3. If there are problems, rebuild:
   ```csharp
   var report = await SqliteStoreRecovery.RebuildAsync(path);
   // report.DamagedCopy: the old file, moved aside (never deleted)
   // report.SalvagedRecords / DiscardedRecords / ReadError / Complete
   ```
4. Open the store again as usual. It has a new replica id, so derive the clock node from its new
   incarnation. Then sync.

What the rebuild keeps:

- It copies every readable record with local work, with its pending operation id. Uploads that had already
  reached the server are therefore replayed, not duplicated.
- It keeps kept conflicts, rejections, and the clock high-water mark.
- Clean data is pulled again from the server.

What it cannot do:

- It cannot recover rows on unreadable pages. The report says reading was incomplete, but it cannot count
  what was lost.
- The damaged copy contains user data: keep it under the same protection as the database, and delete it
  once support no longer needs it.

Tested by `RecoveryTests`: a healthy file; a file overwritten at the header; a file with a 16 KiB stretch of
damaged pages (369 of 400 records recovered in the recorded run); a row with damaged JSON. The tests damage
files directly; storage-level faults such as torn writes and power loss were not exercised.

## 5. A browser's IndexedDB data is gone

Browsers may evict storage, and users may clear site data. The replica detects the change by its new replica
id and repopulates from the server (`IndexedDbBrowserTests.DeletedDatabaseIsRepopulated`). Unsynchronized
work in the evicted database is **lost**. Call `IndexedDbLocalStore.RequestPersistenceAsync()` to make eviction
less likely. It is not called automatically, because some browsers (Firefox) prompt the user, and browsers decide
whether to grant it.

## 6. A queue is stuck

| Situation | How to see it | What to do |
|---|---|---|
| The server rejected a change (validation, permission, clock skew, `base-expired`) | `GetRejectedAsync`, `SyncItemState.Rejected` | Fix the cause, then `RetryRejectedAsync` / `ISyncCollection.RetryAsync` (a new operation), or `RevertAsync` to discard the change. |
| A change conflicted and was kept (default policy) | `GetConflictsAsync`, `SyncItemState.Conflicted` | `ResolveConflictAsync` (for example with `ThreeWayMerge`) or `DiscardConflictAsync`. |
| Uploads keep failing transiently | `SyncStatus.State` `Offline`, `Detail` | Nothing: the session retries with backoff and honours `Retry-After`. |
| `upgrade-required` | `SyncStatus.State` `AttentionRequired` | Ship the app update; local work is kept (section 7). |
| Local work must move to a new store or device | `ExportLocalChangesAsync` | `ImportLocalChangesAsync` into the new engine, then sync. Serialize the export with your source-generated JSON context if it must cross a process. |

## 7. Schema upgrades

**Store schema** (SQLite `PRAGMA user_version`, IndexedDB database version):

- Upgrades are forward-only and run in place on first open. Pending work is kept: this is tested for 1→2 on
  SQLite and in three browser engines.
- A database written by a newer app version is refused and left untouched (SQLite
  `SqliteStoreSchemaException`, IndexedDB `outdated`).
- Do not let two app versions share one database file. In browsers, a newer tab closes older tabs' connections.

**Domain schema** (`Bsync-Schema` header). To roll out a new document shape:

1. Deploy a server that accepts both ids (`SupportedSchemas = { "notes-v1", "notes-v2" }`) and whose
   documents are readable by both app versions.
2. Ship the new app with `SchemaId = "notes-v2"`.
3. When old clients are gone, or you decide to cut them off, drop `notes-v1`. Old clients then stop with
   `upgrade-required`, keep their work, and upload it after updating. Tested by
   `SchemaUpgradeTests.RollingUpgrade`.

Stored documents are read with the app's current JSON contract. Keep changes JSON-compatible: add members,
and use `[JsonPropertyName]` to keep old names. Declare `[JsonExtensionData]` so older apps do not erase new
members. To change a shape, let the document upgrade itself on read: `IJsonOnDeserialized` plus
`DocumentUpgrade.TryTake` moves old members found in the extension data (ADR-013, `DocumentUpgradeTests`). Let
upload queues drain first: an operation in flight across the change is answered `operation-id-reused`.

## 8. Capacity (SQL Server)

- **Writes to one feed are serialized** by the feed lock (ADR-005). Measured on a development machine with LocalDB
  (`docs/benchmarks.md`): about 1,300 operations per second for one shared feed with 16 concurrent sessions, about
  5,500 with one feed per session. Plan for the busiest feed (tenant), not the total.
- **Pulls take no feed lock** and scale with readers: 20,000 to 45,000 changes per second in the same runs.
- **Storage:** each document is stored once in `bsync.documents` (JSON plus a few dozen bytes of metadata), each
  accepted operation leaves a receipt until it is purged, and tombstones stay until purged. With read membership
  (ADR-015), `bsync.document_access` holds one row per principal per document.
- **Indexes** are created with the tables; nothing needs tuning for the measured volumes. Monitor page latch waits on
  `bsync.feeds` if one feed receives most writes.
- **Replica audit** rows (if enabled) grow with replicas × collections × new checkpoints; purge them by age.

## 9. Checklist before going to production

With the PostgreSQL or SQL Server authority:

- A backup schedule, and a tested restore that starts a new epoch with a version floor.
- A receipt retention period longer than the longest supported offline period.
- A tombstone retention period, with users told what happens to devices offline for longer.
- Monitoring of rejected and conflicted counts per replica, and of `bsync.server.requests{result="unavailable"}`
  (docs/operations/observability.md).
- Uploads drained before changing a document's shape (ADR-013), or both shapes readable during the roll-out.
