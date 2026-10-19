# ADR-035: Durable chunk scrubbing and key-retention observations

Status: Implemented for experimental chunked v2; independent crypto and device qualification remain open.

## Durable integrity work

`ChunkedEncryptedBlobCache.ScrubAsync` authenticates the sealed manifest and a bounded number of remaining chunks.
The invocation supplies a time budget, plaintext-byte allowance and chunk limit. It never starts a chunk that cannot
fit the remaining allowance. Its original monotonic deadline includes lock acquisition, key access, checkpoint writes
and joined lock disposal. Caller cancellation throws; own expiry returns `Deferred` with the last acknowledged durable
prefix. A provider that ignores cancellation is joined and can overrun the budget.

Each successful chunk advances `encrypted-scrub/v1/<generation>` using CAS. The checkpoint is AES-GCM protected with
the generation key and associated data purpose `scrub/1`, account, hash, generation and key identity. Its version-1
plaintext contains the SHA-256 of the sealed manifest ciphertext and the next chunk index. The parser caps stored text
at 4,096 characters and validates the version, manifest binding and index. Fresh random nonces use the existing
encryption provider. **Every checkpoint seal consumes the key's documented encryption-invocation allowance.**

No hash compression state or new cryptographic primitive is introduced. Publication already checks the full plaintext
hash; scrubbing authenticates each stored chunk against that sealed manifest. `Complete` means all chunks authenticated
over the scan's successive invocations, not that every byte is healthy at one instant. Damage after an earlier chunk
was observed is detected by a later fresh scan or read. `restart: true` clears the existing checkpoint before scanning;
ordinary calls resume, including after a completed scan. `BlobVerification.VerifyAsync` remains the fresh full-SHA-256
operation and requires one sufficient budget. Restoring a complete older valid snapshot remains outside local
anti-rollback guarantees.

Per-object operation ownership serializes scrub, publication and collection. A replacement/rotation uses another
generation and cannot reuse the old cursor. Authentication, key and I/O failures preserve bytes and progress and do
not authorize repair. Readers continue using their own generation. Collection deletes a retired checkpoint only when
the old generation's physical chunks and readers have gone.

## Key obligations

Generation creation atomically writes the existing generation journal and a new
`encrypted-generation-key/v1/<generation>` record before any chunk write. The version-1 payload contains key ID and
version; account scope comes from the store/cache. Collection removes both journals atomically after physical cleanup.
Partial, abandoned, retired, reader-held and quarantined generations therefore remain visible to
`GetKeyRetentionPageAsync`, even with keys locked. A legacy generation without a key journal reports an unknown key;
it must never be interpreted as safe retirement. Unrecognized or malformed records fail conservatively.

The page is an observation, not permission to delete a key. The host must prevent concurrent writers/readers using
exclusive account ownership, scan all pages from the beginning, retain keys for every known/unknown obligation, and
include v1 stores, protected metadata, backups and other key users. V1 migration deliberately retains its source.
Key deletion remains host-owned; this library never deletes keys. Journal metadata is not an independent trusted
anchor against malicious storage rollback/removal.

## Compatibility and validation

The encrypted object/chunk format and document wire protocol remain unchanged. These additive journal namespaces are
independently versioned. Older experimental v2 writers do not create the key journal; unknown references conservatively
block retirement. Mixed writers still require the documented account ownership and downgrade barrier.

Tests cover bounded/resumed reads, restart, rotation, tampered checkpoints, canceled/late reads, locked keys, legacy
unknown references, live readers/quarantines and process death before/after a SQLite checkpoint. Browser harness tests
exercise Web Crypto checkpoints, IndexedDB progress and reader-held key obligations. See the
[execution record](../review/gap-closure-completion-2026-10-18.md) for exact artifacts and remaining gates.
