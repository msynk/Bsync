# ADR-028: experimental chunked encrypted blob storage

Date: 2026-10-17  
Status: implemented experimental format; independent cryptographic review and performance qualification pending

## Selection and compatibility

Construct `ChunkedEncryptedBlobCache` explicitly with a dedicated `IBlobChunkStore`, orchestration store,
physical per-content locks, physical generation lifetimes, an existing-key provider and platform crypto.
`BlobCacheChunkStore` adapts native `FileBlobCache` or browser `BrowserBlobStore`; every backing object is
one bounded ciphertext chunk. V1 is unchanged, including its 16 MiB cap. V2 never silently opens v1 bytes.
Plaintext SHA-256, declared length and the HTTP blob protocol are unchanged.

Writes permanently require orchestration feature version 2. Establish the exclusive upgrade/quiescence
boundary in [ADR-027](adr-027-account-ownership-and-protected-metadata.md) first. This is an experimental
opt-in local format, not a stable replacement for the existing cache. No independent crypto review has
been obtained; successful implementation tests are not that review.

## Byte and metadata format

All integers in associated data use invariant decimal with no leading zeros. Associated data is UTF-8
encoding of the following fields, separated by exactly one LF, with no trailing LF:

1. `Bsync.chunked/2/AES-256-GCM`
2. `chunk` or `manifest`
3. account `StorageKey`
4. lowercase plaintext SHA-256 (64 ASCII hex characters)
5. generation UUID, 32 lowercase hex characters without separators
6. key ID (the `LocalKeyReference` portable identifier)
7. positive key version
8. zero-based chunk index, or `-1` for a manifest
9. chunk plaintext length, or `-1` for a manifest

Every ciphertext envelope is exactly `nonce[12] || ciphertext[plaintextLength] || tag[16]` using
AES-256-GCM. Platform cryptography supplies fresh random 96-bit nonces on **every** seal, including retries,
tail replacement and manifest updates. Ciphertext chunk identity is SHA-256 of that entire envelope.
No encryption keys are derived from content hashes or byte offsets.

The encrypted manifest plaintext is source-generated JSON with `Version=2`, `Hash`, `Generation`,
`ChunkSize`, `Length`, and ordered `Chunks` descriptors (`Id`, `Length`). Property spelling is case-sensitive;
order/whitespace are writer details, not independent authentication inputs. Chunk size is 64 KiB..1 MiB,
count at most 1024, total at most 1 GiB. All chunks except the final one must be full. Summed lengths must
equal the manifest length. An empty object has no chunks and length zero.

`encrypted-blob/v2/<hash>` contains source-generated JSON `ChunkState`: `Version=2`, optional `Complete`
and `Partial` envelopes, `RecoveryAuthorized`, and `PartialPurpose` (`import`, `rotation`, or null for
ordinary streaming). Each envelope contains `Generation`, `KeyId`, `KeyVersion`, `Ciphertext` (base64
manifest envelope). State payloads are bounded to 524288 characters; manifest base64 to 262144 characters.
Ciphertext reads are bounded to 1 MiB + 28 bytes. The provider's stricter limits always apply.

Internal state writers leave Base64 `+` characters literal to avoid HTML-escaping scratch buffers. The dedicated
relaxed encoder applies only to stored envelopes; plaintext manifest serialization and default readers are unchanged.
Both literal and JSON-escaped Base64 are valid in existing v2 records. This storage JSON is not HTML-safe markup.

## Nonce budget and threat boundary

Random nonces avoid deterministic reuse across concurrent writers and crash retries, but have a birthday
collision probability. A conservative host policy is at most 2^24 aggregate seals per actual key across
all devices, objects, manifests, payload protection and retries (approximately 2^-49 collision probability
for 96-bit uniformly random nonces). This is a proposed ceiling pending independent review, not an
automatically enforced counter. Use distinct actual keys for different purposes/devices where appropriate;
changing only a key ID does not reset the budget. A host unable to enforce its reviewed key policy must
not advertise this format as qualified encryption. No algorithm is implemented outside native/Web Crypto.

Account/hash/generation/key/index/length substitution fails authentication. Ciphertext hash and exact
length are checked before AEAD. The full plaintext SHA is checked before completion; a range read
authenticates its manifest and intersecting chunks, and makes no fresh-integrity claim about untouched
chunks. Missing/truncated/corrupt chunks throw; unavailable keys defer; failed authentication cannot
distinguish a wrong key from damage. Unknown state/manifest versions are retained and refused.

Clear indexes disclose plaintext hashes, generations, key references, sizes, timing and access patterns.
Control journals and pointer CAS assume a trustworthy provider for concurrency/availability; they are not
an independent tamper-resistant monotonic anchor. An attacker can delete bytes or restore an entire older
valid database snapshot. This design cannot detect complete rollback without a separately trusted anchor.

## Durability, ownership and capacity

Generation inventory is written before any chunk. Each `encrypted-chunk/v2/<generation>/<cipherhash>`
journal is written before its physical write. A partial manifest is committed only after all referenced
chunks are durable. Replacing a short tail writes a new immutable chunk, switches the partial pointer,
then deletes the old tail. Process death can leave accounted orphan chunks, never acknowledged progress
that points to an unfinished write. Only full plaintext verification can publish `Complete`.

Per-content physical ownership spans fresh state reads, writes and pointer CAS. Shared generation lifetime
ownership protects readers; GC requires a nonblocking exclusive probe. Account ownership encloses both.
A separate capacity lock covers physical inventory and one bounded chunk write, not network reads or
full-object hashing. The hard budget counts all physical chunk/staging bytes, including retired readers
and interrupted writes; it excludes orchestration/index overhead. Lack of room refuses progress.

`ListPageAsync` pages object states, including partial/empty records. `CollectAsync` scans at most 32
generation records and removes at most its chunk limit. `ReconcileChunksAsync` removes journaled chunks
not referenced by the current authenticated manifest, such as an interrupted tail write. Preserve returned
cursors and repeat complete scans to revisit live-reader generations. Unknown state and quarantines are
preserved, never reclaimed by age. Do not mutate the dedicated raw chunk cache directly.

## Readers, migration and recovery

Readers support asynchronous reads and seek; synchronous reads/disposal are deliberately unsupported.
Use `await using`. One decrypted chunk is retained per reader and zeroed on replacement/disposal.
Concurrent reads/disposal are joined; arbitrary concurrent seeks are not a supported Stream usage.
Browser runtime/interop copies have separate lifetimes; zeroing one managed buffer does not erase them all.

Seekable stable imports hash the source first and verify the staged generation again before publication.
Known-hash streaming uses append/complete. Rotation records purpose/key identity and resumes only matching
committed progress; an unrelated partial must be finished or explicitly discarded. V1 migration copies
and verifies into v2 and retains v1, including any v1 reader/key dependency. Old keys cannot be retired
until all old generations, v1 data, quarantines, readers and backups have been accounted for by the host.
Budget overloads cover ownership, key access, copying, verification and publication; timeout throws
cancellation and leaves resumable state. Full hash verification restarts after interruption; no serialized
hash checkpoint format is claimed.

On WebAssembly, full-content completion, verification, import, recovery and managed/package verification use an
internal streaming SHA-256 implementation with one 64-byte tail, eight state words and a 64-word schedule. Native
targets retain platform hashing. This avoids .NET's browser incremental provider buffering the entire input for
one-shot managed hashing. Compression/padding follow FIPS 180-4; native known-answer, fragmentation and >2^32-bit length
tests compare the browser implementation with platform digests. This additional implementation remains within the
independent cryptographic-review gate; tests alone do not close that review. Chunk AES-GCM still uses WebCrypto.

Automatic replacement capability is deliberately absent. `RecoverAsync` requires explicit host policy and
a seekable authorized source. It verifies source identity **before mutation**, records the old state under
`encrypted-quarantine/v2/`, permanently protects referenced generations, then stages and verifies the new
generation. Quarantine is never automatically collected. Keep source authorization current at invocation;
this low-level API does not acquire server credentials or infer permission from a content hash.

## Qualification

Native tests cover 17 MiB, 256 MiB and 1 GiB using bounded chunk writes; browser tests use 17 MiB across
reload, range reads, key rotation and reader-safe collection in Chromium/Firefox/WebKit. Real native process
termination covers both sides of rotation publication. These establish behavior on tested targets, not
measured peak-memory or p95 guarantees. A checked-in vector generated independently with Node `node:crypto`
is consumed by native and Web Crypto tests, including an authenticated invalid-order manifest. Regenerate
with `node scripts/generate_chunked_vector.mjs`; fixed fixture nonces/keys must never enter production.
Independent design review, named-device performance,
browser duplication measurements and physical-device key behavior remain release gates.
