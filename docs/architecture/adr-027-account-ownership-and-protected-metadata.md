# ADR-027: account composition, owned read models and protected metadata

Date: 2026-10-17  
Status: implemented; opt-in local formats, qualification limits below

## Account ownership

`AccountComposition` requires a durable catalog and physical `IAccountResourceLock`. Register named
resources and fresh factories before opening work. Names are unique, portable and at most 128 characters.
`OpenWorkAsync` holds shared account ownership before consulting deletion tombstones or opening resources;
the returned scope joins disposal in reverse order before releasing ownership. Factories must clean up if
they throw before returning a resource. Never cache a UI-selected account in a headless factory.

`CreateCoordinator` uses the same registry/catalog/locks for deletion. Data resources precede key resources.
Unknown pending counts block the default deletion policy. The journal remains in the separate catalog;
registering the catalog itself as a deleted resource is invalid host composition. Export/discard policy,
identity provisioning and key retention remain explicit host decisions.

Acquire account lifetime first, then operation/execution ownership, then per-content ownership, then short
metadata transactions. Package pointer operations may acquire content ownership, but transfer workers never
take a package pointer while holding content ownership. Generation readers acquire shared physical lifetime
ownership; collectors only probe for exclusive ownership without waiting under metadata locks. All instances
must use the same physical lock paths/Web Lock namespace. These are cooperative contracts; raw provider
access outside the composition can bypass them.

`RunAsync` starts one `TimeProvider` deadline before opening resources. Work and disposal are joined even
if callbacks ignore cancellation; a late completion is reported as cancellation. This cannot forcibly stop
arbitrary application code. The lower-level APIs remain available with their existing ownership obligations.

## Permanent feature barrier

`IOrchestrationFeatureStore` is an optional capability. SQLite and IndexedDB support feature versions 1 and 2.
The first protected-payload or encrypted-v2 write requires version 2. Third-party stores without this
capability fail before feature writes; they can still use the unchanged base orchestration interface.

SQLite raises `bs_orchestration_schema` (not the document `user_version`). The barrier covers the whole
database. IndexedDB raises the orchestration database version to 2 without changing the record store.
Published 0.4.0 readers reject both on subsequent opens. No automatic downgrade exists. A blocked browser
upgrade fails; a subsequently delivered abandoned upgrade is aborted rather than silently committed.

Before enabling a new writer, **close all older processes/tabs**. A version header cannot revoke an
already-open old SQLite connection. `AccountComposition.UpgradeFeatureStoreAsync` acquires exclusive
account ownership, opens/upgrades/closes the store, and rejects deletion tombstones. All accounts sharing
one SQLite database must be quiescent. Hosts that bypass composition must establish equivalent quiescence.

## Payload protection

`ProtectedOrchestrationStore` selects records through a stable `key -> kind or null` host policy. Null
means a clear control record. Every execution lease used by a fence must be clear; selected fence keys are
rejected before mutation. Do not change a selector while existing writers use that store. The selector is
an explicit low-level policy hook. `OrchestrationPayloadPolicy` provides startup registration of protected
namespaces and exact opaque control keys, rejects overlapping namespaces and unknown keys, and freezes on
use. Use it instead of a permissive catch-all prefix callback. Application-specific record kinds still
belong to the host; the library cannot infer whether arbitrary control payloads contain business data.

| Record kind | Protection policy |
|---|---|
| Execution lease tokens, generation counters, deletion/resource controls | Opaque coordination only; keep fence records clear |
| Package/content descriptors and source references | May reveal application data; select explicitly or isolate in an encrypted store |
| Workflow/intent payloads and canonical results | Application data; select a stable kind |
| Derived read-model rows | Application data; protect when required by the host threat model |
| Credentials, bearer tokens, signed URLs and approval secrets | Never general orchestration payloads, even when protection is enabled |

The payload is `bsync-protected/1:` followed by UTF-8-compatible JSON with `KeyId`, `Version` and
`Ciphertext` (base64). Ciphertext bytes use the existing AES-256-GCM envelope: 12-byte random nonce,
ciphertext, 16-byte tag. Associated data is UTF-8 fields separated by one LF, in this order:
`bsync-protected/1`, account storage key, record key, kind, key ID, invariant decimal key version,
and expiration as invariant decimal Unix milliseconds, or `none`. Reference identities and selectors must
remain stable. Revision is provider-managed CAS state, not encrypted application payload.

All input mutations are validated, all selected payloads encrypted, and expanded output validated before
one underlying atomic CAS/fence batch. Key access runs outside the transaction. The original revisions,
keys and expiration are preserved. Base64 and JSON expansion reduce usable plaintext limits; actual
provider record/batch limits apply to the final serialized envelope. Do not advertise the plaintext
maximum as usable encrypted capacity.

Reads are strict: selected legacy plaintext requires `ProtectPageAsync`; unknown versions, unavailable
keys and failed authentication never trigger plaintext fallback. Bounded page migration also rotates
keys with per-record CAS. Conflicts are preserved and must be revisited in another scan. Retain old keys
until every dependent record, generation, reader and backup has been accounted for. Migration does not
automatically retire keys.

Keys, revisions, sizes, expiration, control records, access patterns and database structure remain visible.
Authentication does not detect restoration of a complete older valid database snapshot. This layer does
not encrypt the document store; native SQLCipher has its separate whole-database boundary.

## Read-model generations

The legacy constructor and `read-model/` namespace retain their historical no-cleanup guarantee.
The new account/physical-lock constructor uses `read-model-v2/` and disposable generation snapshots.
Open acquires shared lifetime ownership and rechecks the active pointer. Disposal joins in-flight page
reads before releasing ownership. Never transfer a live snapshot outside its enclosing account lifetime.

Rebuild creates a discoverable generation marker together with build state; rows and applied counts
commit atomically under the execution fence. `CollectRetiredAsync` takes that same execution ownership,
rechecks active/building pointers, and probes exclusive generation ownership before bounded deletions.
Crash interruption leaves markers and remaining rows for replay. Live readers are never expired by age.
Once cleanup acquires the build fence it clears abandoned building state and can reclaim even the latest
incomplete generation. This permits recovery at the retention ceiling without first starting another build.
An older builder cannot publish after losing that fence; rebuild always starts from a fresh source snapshot.

Defaults are 64 generations, 1,000,000 rows and 128 MiB serialized UTF-16 row payload. These limits count
staging and retained generations, refuse rebuild instead of evicting readers, and exclude database/index
overhead. `GetStorageStatusAsync` uses bounded pages. Cleanup callers must continue returned cursors and
repeat scans to revisit generations protected during earlier passes. A stable host-owned source snapshot
is still required; live pagination does not become consistent merely because publication is atomic.

## Evidence and limits

Core tests exercise deletion overlap, disposal failure, complete budgets, retention, stale fences,
expanded payload limits, rotation/migration and tampering. SQLite and browser provider tests cover atomic
protected batches. Actual 0.4.0 SQLite/JS readers are used for downgrade rejection. See the continuation
record for exact commands and artifact qualification. Independently reviewed cryptographic format
stability remains a separate gate.
