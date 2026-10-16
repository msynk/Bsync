# Pattern: local unlock independent of sign-in

`ILocalKeyProvider.AcquireAsync` retrieves an existing `LocalKeyReference` (account, key ID and positive version),
returning `Available`, `Locked` or `Unavailable`. It never provisions, rotates or replaces a key. In an `OpenReplica`
factory, acquire the exact reference retained with the resource, use `access.Key` only for opening, then dispose the
access handle and clear any temporary arrays. Accessing a locked/unavailable key throws `ReplicaKeyUnavailableException`,
which the account coordinator maps to a local opening outcome without deleting data or reporting an invented pending count.

Network credentials use the existing `CredentialRenewal`/coalescing contract and the coordinator's separate credential
check. A disconnected device cannot observe remote revocation until it receives new information. Revocation is enforced
by the server when the device reconnects; do not claim that it retroactively erases already accessible offline content.

## Native protected-store lookup

The MAUI sample's [ProtectedLocalKeys](../../src/Samples/Bsync.Samples.Hybrid.Maui/ProtectedLocalKeys.cs) reads an exact
account/key/version from `ISecureStorage`. It reports missing, malformed or inaccessible entries as unavailable. MAUI's
portable API cannot reliably distinguish device-lock failures, so this recipe does not guess that a key is permanently lost.
Never call its older `SecureReplicaKeys.GetOrCreateAsync` when opening existing encrypted storage: missing key material is
not permission to overwrite identity or create a replacement database.

Provisioning is a separate foreground host operation under exclusive account setup ownership. Generate 32 random bytes,
durably retain the intended key reference/recovery policy, and store the base64 key through `SecureStorage.SetAsync` using
`ProtectedLocalKeys.StorageName(reference)`. Do not silently overwrite an existing entry. Register key removal last in
account deletion, after encrypted replicas/content have been removed. Protected storage access and locked-device behavior
still require platform qualification; the default MAUI sample database remains plain SQLite. Select exactly one qualified
SQLCipher bundle when enabling its existing encrypted-store API.

## Browser offline-unlock example

The Notes PWA includes an isolated **Offline unlock example**. The user supplies a base64-encoded, uniformly random
32-byte recovery key kept outside the browser. First use creates an encrypted notebook; subsequent opens require the same
key. This is a recovery-key demonstration, not a password derivation recipe. The example uses an in-memory provider,
disables replication and never constructs a network transport. Lock closes the store before clearing the key provider
and UI text. Reload requires entry again. A wrong key retains the existing database; it never opens a plain replacement.

The protection boundary is persisted document payloads while the key is absent. IDs, flags, timestamps and declared
indexes remain visible. Browser-origin scripts, extensions with access, injected code, developer tools and an unlocked
device can read accessible plaintext or keys; non-extractable WebCrypto keys do not prevent an authorized origin from
decrypting data. JS/.NET strings and imported engine keys cannot be guaranteed immediately zeroed by this sample.
Origin eviction can remove ciphertext, and storage persistence requests are only hints. Browser orchestration metadata
and blob bytes are not encrypted by this document-store example. No key is written to IndexedDB, localStorage or CacheStorage.

The published service worker intercepts only manifest-listed assets and the sample's entry navigation. API, sync, blob,
hint and third-party requests bypass it; adding application routes requires an explicit shell-navigation allowlist.
Closed-tab synchronization is unsupported. Offline app-shell reload remains unqualified in Playwright WebKit on Windows.
