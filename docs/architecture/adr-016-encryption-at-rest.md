# ADR-016: Encryption at rest and replica wipe

- **Status:** Wipe and storage persistence **accepted and implemented** (unreleased). Encryption **proposed**, not
  implemented: it needs a maintainer decision on the SQLite provider (below).
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

## Proposal, part 2: encryption (not implemented)

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

### Done when (from the plan)

An encrypted SQLite file cannot be opened without the key; a wrong key fails explicitly (never as an empty or new
replica); wipe leaves no files for the account; encryption is opt-in and documented with this threat model.

## Consequences

- Wipe and persistence are available now; encryption is not, and no document claims it.
- Whichever SQLite option is chosen, the support matrix lists encrypted storage separately per platform.
