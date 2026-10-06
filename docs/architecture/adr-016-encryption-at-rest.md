# ADR-016: Encryption at rest and replica wipe

- **Status:** Accepted and implemented (unreleased): wipe and storage persistence (part 1), and encryption (part 2,
  2026-10-06) with option 2 below for SQLite and the AES-GCM envelope for IndexedDB.
- **Invariants:** I01 (a local write is durable when it reports success), I07 (accounts are isolated), I17 (schema
  upgrades keep pending work)
- **Related:** improvement plan task G3, ADR-008 (browser storage), ADR-010 (auth and scope), ADR-012 (packaging)

## Context

Replicas hold an account's documents, pending writes, conflict copies and attachment bytes on the device. On a shared
or lost device anyone with file access can read them: SQLite files and IndexedDB databases are plain. Nothing removed
an account's data on sign-out except deleting files by hand, and browsers may evict IndexedDB data under storage
pressure unless the origin was granted persistent storage, which nothing requested.

The plan requires this ADR before encryption ships, and that encryption never becomes a default or a claim without it.

## Threat model

Protects against:

- **A lost or stolen device, or a copied file**, read offline: the SQLite file, its `-wal` journal, or a browser profile.
- **The next user of a shared device** after the previous user signed out (wipe).

Does not protect against:

- **Malware or another process running as the same user while the app runs**: it can read memory, or call the key
  provider itself.
- **A compromised browser origin (XSS)**: script on the origin can read decrypted documents through the app.
- **The server or the network**: transport security is HTTPS; server storage is the host's responsibility.
- **Metadata**: record counts, sizes, ids (SQLite key columns and IndexedDB keys stay plain, because indexes and the
  push queue need them), timing.

## Decision, part 1: wipe and persistence (implemented)

- `SyncSessionOptions<T>.DeleteReplica(account)` deletes what the device holds for an account.
  `SyncSession<T>.DeleteReplicaAsync(account)` stops replication of that account if it runs, closes the replica and
  releases its lease, then calls it; a later open starts empty. `SyncCoordinator.DeleteReplicasAsync(account)` first
  closes every coordinated collection's replica of the account (collections usually share one database), then deletes.
- `SqliteStorePool.DeleteDatabaseAsync(path)` removes a database and its `-wal`, `-shm` and `-journal` files.
- The browser recipe (`AddBrowserSyncCollection`) sets `DeleteReplica` to delete the account's IndexedDB database
  (all its collections; other tabs close their connections and report `outdated`).
- The browser recipe requests persistent storage (`navigator.storage.persist()`) when it opens a replica, and the
  answer is reported in `SyncStatus.PersistentStorage` (`true`, `false`, or `null` when unknown). Opening waits at most
  two seconds for it: Firefox asks the user, and opening must never wait for a person.
- Wiping loses unsynced changes; apps check `SyncStatus.Pending` and warn first. Blobs (attachments, bundles) are
  application files; the app's `DeleteReplica` removes them too (see the Tasks sample's blob directory).

## Decision, part 2: encryption (implemented)

### Key source

The application supplies keys; Bsync never generates, stores or derives them from passwords on its own:

- `SqliteLocalStoreOptions.Key: Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>>>?`: a 256-bit key per database,
  typically created once and kept in the platform's protected store (Windows DPAPI, Android Keystore, iOS Keychain;
  `Bsync.Maui` would offer a helper on MAUI `SecureStorage`, task G1).
- IndexedDB: `IndexedDbStoreOptions.KeyProvider` returning a non-extractable WebCrypto `CryptoKey` held in IndexedDB
  itself (non-extractable keys can be stored but not read out by script). This protects against copying the profile
  to another machine, not against script on the origin.

### SQLite

SQLCipher encrypts whole pages, including the journal, indexes and free pages. `Microsoft.Data.Sqlite` passes the key
as `PRAGMA key`, but only with a native library built with SQLCipher. **This is the decision needed:**

1. **Depend on `Microsoft.Data.Sqlite.Core` and let the application choose the native bundle** (`e_sqlite3` for plain,
   an SQLCipher bundle for encrypted). Breaking for every native app (they must add a bundle), but one package.
2. **Keep `Bsync.Storage.Sqlite` as is and add `Bsync.Storage.Sqlite.Encrypted`**, which depends on
   `Microsoft.Data.Sqlite.Core` plus an SQLCipher bundle. Apps choose a package, not a bundle. The two cannot be
   referenced together (both provide the SQLite native library).
3. **Encrypt values, not pages:** encrypt each JSON column with AES-GCM in .NET before it is written. Works with any
   SQLite build, but leaves the schema, ids, sizes and journal metadata plain, and costs every read and write.

Which SQLCipher build to use (the community `SQLitePCLRaw.bundle_e_sqlcipher` or Zetetic's commercial package) is a
licensing and support decision for the maintainer. Recommended: option 2 with the community bundle, verified on every
platform in the support matrix before it is documented as supported.

Rotation: `PRAGMA rekey` in one transaction, exposed as `SqliteLocalStore.RekeyAsync(newKey)`; a failed rekey leaves the
old key valid. Migration of an existing plain database: `sqlcipher_export` into a new encrypted file, swapped in while
no session has it open (the same procedure as a restore).

### IndexedDB

An AES-GCM envelope around every serialized document (current, base, pending payload, conflict copies) inside the
existing records, with a random 96-bit nonce per value and the record key as additional authenticated data, so a
value cannot be moved to another record. Indexes and keys stay plain (see the threat model). Rotation re-encrypts
records in stamped batches, keeping both keys until done. This is a store schema change (version 4 or 5, depending on
ADR-018).

### As implemented

- **SQLite:** package `Bsync.Storage.Sqlite.Encrypted` (option 2). It compiles the same sources as
  `Bsync.Storage.Sqlite` (symbol `BSYNC_SQLCIPHER`) against `Microsoft.Data.Sqlite.Core` and
  `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11, the community SQLCipher build. **That bundle's last release is 2.1.11 and
  the SQLitePCLRaw 3 line no longer publishes it**: a maintained source (Zetetic's packages, or a self-built bundle) is
  needed before this is relied on in production. `SqliteLocalStoreOptions.EncryptionKey` (32 bytes) is passed to
  SQLCipher as a raw key (`x'…'`), so no password derivation runs per connection (the test suite went from 29 s to 1 s).
  `SqliteEncryption.EncryptAsync` (plain to encrypted, in place, with `sqlcipher_export`), `RekeyAsync`, `IsPlain`,
  `NewKey`; recovery overloads take the key. A wrong or missing key raises `SqliteStoreUnreadableException` (also in the
  plain package, for an encrypted file) and changes nothing.
- **IndexedDB:** `IndexedDbStoreOptions.EncryptionKey` (32 bytes, supplied by the application, imported as a
  non-extractable WebCrypto key and kept in memory). Values are sealed outside transactions (awaiting WebCrypto inside
  one would end it). A sealed check value in the database refuses a missing or wrong key (`LocalStoreUnavailableException`,
  reason `key`). Values written before a key was used stay readable and are sealed when next written. Declared index
  values (ADR-018) are stored readable; do not index fields that must stay secret in an encrypted browser replica.
- **Verified:** the shared store and index conformance cases with encryption (SQLite on Windows; IndexedDB in Chromium,
  Firefox and WebKit), no plain text in the stored data, wrong and missing keys refused, encrypt-in-place with pending
  work, rekey, wipe. Not verified: Android, iOS, macOS (the SQLCipher native build differs per platform).

### Done when (from the plan)

An encrypted SQLite file cannot be opened without the key; a wrong key fails explicitly (never as an empty or new
replica); wipe leaves no files for the account; encryption is opt-in and documented with this threat model.

## Consequences

- Wipe and persistence are available now; encryption is not, and no document claims it.
- Whichever SQLite option is chosen, the support matrix lists encrypted storage separately per platform.
