# Versioned external-file bridge

[`VersionedFileBlobStore`](../../src/Samples/Bsync.Samples.Tasks.Server/VersionedFileBlobStore.cs) is an unpackaged
reference adapter implementing the actual server `IBlobStore` protocol. Register it with the existing blob endpoints
and a server-side `IBlobAccess` that derives read/write authority from authenticated domain ownership. A file ID or
SHA-256 is not an authorization token. The Tasks server does not enable an external integration automatically.

Implement `IVersionedFileService` only if the service provides immutable versions, trustworthy full SHA-256/length,
idempotent completion under a supplied operation ID, and completion lookup. Acquire credentials at each call; never
put them in mapping records. A service without these guarantees needs an explicit reconciliation workflow and cannot
be represented as supporting this recipe by guessing a hash or retrying non-idempotent completion.

The adapter uses server-owned `FileSystemBlobStore` staging plus durable orchestration metadata shared by all adapter
instances. Upload offsets resume through the normal Bsync protocol. Each uploader's complete bytes are hash checked
before its durable proof is recorded; knowing another object's hash cannot bypass that uploader's verification.
The stable external operation ID is persisted before `PutAsync`, and retries call `FindCompletionAsync` before a new
put. Full identity/length and bounded immutable IDs are checked before the mapping becomes reachable. Execution-fenced
mapping acknowledgement follows the external call; no metadata/database transaction contains network work.

A lost completion reply therefore reuses the same operation ID and external immutable version. Local staging remains
until acknowledgement. Loss of required staging is an explicit recovery condition, not permission to invent a new
upload. Uploader proofs and operation identities must be retained for the application's replay horizon; this sample
does not automatically expire them. Repeating `DeleteAsync` resumes its durable deletion intent. Orphan cleanup must
first prove that no authorized domain owner or pending mapping still references that exact immutable version.

This adapter deliberately uses streamed Bsync downloads, including the endpoints' range handling. It returns no
external presign capability, and does not pass an unrelated service's presign endpoint to `HttpBlobTransfer`. If a
production adapter adds tickets, separately qualify expiry/refresh, credential isolation, range semantics and the
actual service's CORS policy. Mock service tests do not establish these properties for a real object store.

`VersionedFileBridgeTests` exercises lost reply recovery with one external put, durable mapping replay, uploader proof
isolation, invalid bytes, bounded listing and deletion. It is an executable integration contract to copy/adapt with
the consuming service; it is not a new provider package or a claim that arbitrary file services are interchangeable.
