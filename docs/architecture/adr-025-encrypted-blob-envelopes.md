# ADR-025: Optional bounded authenticated blob envelopes

Status: implemented, unreleased. Native tests and browser qualification are recorded in the implementation review.

## Decision and boundary

`EncryptedBlobCache` wraps a dedicated `IBlobCache` and account-scoped orchestration store. It encrypts both unfinished
downloads and complete local blobs before handing any payload to the backing cache. The underlying filesystem or
IndexedDB contains only ciphertext payloads. The orchestration records remain plaintext unless separately protected.
This is independent of database encryption. The default and maximum object size is 16 MiB, configurable downward.
Append and range-read costs are deliberately explicit: each append authenticates and rewrites the entire partial;
each open authenticates the entire blob and returns a seekable memory stream. There is no encrypted chunk format,
plaintext temporary file, streaming decryption, or claim of efficient multi-gigabyte support in version 1.

The primitives are platform [AES-GCM](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.aesgcm)
and browser [Web Crypto AES-GCM](https://www.w3.org/TR/webcrypto/#aes-gcm). The portable implementation does not
implement AES, GCM, a KDF, or a password scheme. Browser contexts must support secure-context Web Crypto and Web Locks.

## Format and authentication

Envelope version 1 is `12-byte random nonce | ciphertext | 16-byte GCM tag`, with a 32-byte host-owned key.
Each encryption, retry and rotation obtains a fresh nonce from the platform cryptographic random generator.
Hosts must use dedicated blob keys, keep total invocations under the GCM random-IV bound of 2^32 per key across all
devices, and rotate substantially earlier (recommended at most 2^24 invocations). Keys shared across unrelated systems
make that bound impossible for this library to enforce. Nonce uniqueness is probabilistic, not a persisted counter;
cloning a database does not clone a nonce generator or introduce a counter-reuse failure.

Associated data is UTF-8 newline-separated, in this exact order, without a trailing newline:

1. `Bsync.EncryptedBlob/1`
2. AccountScope.StorageKey
3. Lowercase plaintext SHA-256
4. `complete` or `partial`
5. ASCII key identifier
6. Positive key version in invariant decimal
7. Plaintext byte length in invariant decimal

Fields cannot contain newlines. Metadata schema 1 maps the plaintext hash to independent complete/partial envelope
references, each containing the ciphertext SHA-256, plaintext length, key ID and version. The ciphertext digest,
exact envelope length, AEAD authentication and (for complete content) plaintext digest are all checked before exposing
bytes. Wrong length, truncation, reordered bytes, substituted mode/hash/account or a wrong key fails closed. A partial
has no claim of complete-content validity until its plaintext digest matches the announced hash.

Plaintext identity remains unchanged on the wire and across rotation. Hashes leak equality and permit guessing known
content; sizes, account namespace, key identities, access timing and completeness remain visible. This does not protect
an unlocked runtime, XSS, malicious application code, memory inspection, OS paging/crash dumps or attacker-driven
deletion/rollback of an entire storage snapshot. Stream disposal zeroes its owned plaintext array; runtime/interop
copies cannot be guaranteed erased. Browser imported keys are non-extractable and never persisted by this adapter.

## Commit, rotation, migration and recovery

Every writer uses the same non-expiring account cache lock. Before storing ciphertext it journals the ciphertext hash.
Only after the backing import acknowledges durable bytes does a compare-and-write switch the metadata reference.
Complete promotion re-encrypts with the authenticated complete mode. Rotation authenticates both existing parts first,
stages both under the configured new write key and switches both references in one metadata transaction. A crash before
that commit leaves old references; after it, new references. No implicit key provisioning or old-key deletion occurs.
Readers already hold authenticated memory and remain valid across replacement/rotation.

After commit, replaced ciphertext is reclaimed. Cleanup I/O failures retain journal entries for explicit reconciliation.
Reconciliation validates/authenticates every current reference before deleting only journaled, unreferenced envelopes;
unknown backing objects and import staging remain protected. Maintenance refuses more than 10,000 metadata or journal
records per namespace instead of deleting from an incomplete scan. Missing/corrupt ciphertext, a locked or missing key,
and unsupported metadata schemas never trigger repair, empty-cache fallback or replacement. Explicit Delete/Discard
are still deletion requests; hosts must apply lifecycle/pending-work rules before invoking them.

Existing complete plaintext caches migrate by copy-and-verify into a separate encrypted cache. `MigrateAsync` preserves
the source and hash; interrupted copies are repeatable. Quiesce legacy writers, copy every protected pending-upload and
package blob, verify them under the destination, then switch the host's account/cache binding. Transfer jobs/pins use
plaintext identities and need not be renumbered. Legacy unfinished downloads must restart; they are never silently
deleted by migration. Source removal is a separate explicit host decision after verification. Rolling back to code that
does not understand encrypted metadata requires the preserved source or an explicit decrypted export.

`ManagedBlobCache` recognizes the encrypted adapter and reserves whole replacement-envelope space, including nonce/tag
and both generations, before writes. Use `RotateEncryptionAsync` through that outer cache to retain the budget and pins.
Direct rotation/import through the inner cache bypasses managed budgets, just like any direct inner mutation. Failed
staging remains counted as protected capacity until authenticated reconciliation. Physical filesystem/database overhead
is excluded from these payload budgets; browser quota estimates remain hints.

## Qualification

Tests exercise native/browser interoperability, partial restart, ranged reads, wrong and locked keys, tampering,
truncation, reordered ciphertext, metadata substitution, failed two-part rotation, SQLite migration and budget refusal.
Fault injection is labeled separately from actual process termination. Neither this ADR nor a successful desktop browser
test qualifies physical mobile key storage or an independent cryptographic implementation/security review.
