# ADR-029: Explicit account maintenance and durable owner reconciliation

Status: implemented opt-in proposal, unreleased. This does not make legacy full-list or full-object verification paths bounded.

## Ownership and scheduling

`AccountMaintenance` uses `AccountComposition.OpenMaintenanceAsync` to obtain exclusive physical account ownership
before opening resources. Foreground work, network workers, snapshots and deletion must use the same external lock
namespace. A live participant causes `Busy`; maintenance never expires or takes over its physical lifetime. Network
execution leases remain separate: concurrent foreground local writes are permitted while another process owns the
network lease, but maintenance and deletion cannot run during those account lifetimes.

Exclusive account ownership deliberately favors safety over background maintenance availability. Hosts with a
long-lived foreground scope should schedule a brief maintenance window after closing that scope, or continue using
the existing fine-grained cleanup APIs under their documented ownership rules. Do not suspend a process and assume its
locks are stale. OS handle cleanup after process death can be asynchronous; `Busy` remains a safe retry outcome.
Browser disposal awaits the Web Lock request's completion, not just the callback's release signal. This prevents
immediate subsequent maintenance from racing the previous scope's physical release.

The runner checks the host's urgent-work predicate before each page. A single monotonic `TimeProvider` deadline covers
opening, urgent checks, callbacks, checkpoint commits and joined resource disposal. Caller cancellation throws;
deadline expiry returns `BudgetExpired`. Neither abandons a noncooperative callback or a resource still closing.
Each invocation runs at most 32 pages. It does not guarantee a hard wall-clock cutoff for arbitrary host code.

## Progress and limits

The caller registers an ordered list of 1–32 uniquely named/versioned steps. Portable names/versions are bounded to
128 characters; cursors are bounded to 4096 characters. One schema-1 checkpoint lives at
`maintenance/v1/{account-storage-key}/{plan-name-hash}` in the external catalog. Its signature binds the ordered
names/versions. An incompatible list is rejected; use a new plan identity. Completing all steps resets the checkpoint
for a later fresh scan. Plan checkpoints contain no credentials but a provider cursor can reveal local metadata;
catalog protection and deletion/retention policy are host responsibilities.

Each callback gets the remaining item/byte allowances, its last committed cursor and the dry-run flag. It must stop
before an indivisible unit exceeds those allowances and report `RequiresLargerBudget`, preserving that unit. The
runner validates reported counts before checkpointing but cannot undo a callback's arbitrary side effects. Callbacks
must be replay-safe: process death or cancellation can occur after a page's mutations and before its checkpoint.
No network or external business effects belong in these callbacks. Dry runs change neither data nor checkpoints;
physical lock-file initialization is not considered a data mutation.

There is no implicit byte unit shared by every provider. Each step declares its unit. The built-in owner steps charge
conservative logical/expected bytes, including protected content, once per examined record. They do not read or hash
blob payloads. Metadata lookups and runtime overhead are additional. Multiple references to the same physical content
can therefore be charged more than once across steps. These counters are work allowances, not reclaimed-byte totals.

## Built-in pins

Run the following phases in order, using managers bound to the scope's exact account:

1. `OfflinePackageManager.ReconcilePinsAsync` restores pins from durable immutable manifests after interrupted staging.
   It scans at most 16 metadata records per callback and each manifest is limited to 256 items. Its byte charge is the
   sum of manifest item sizes. It neither authorizes content access nor downloads content.
2. `BlobTransferManager.ReconcilePinsAsync` restores pins for durable jobs, including terminal jobs retained pending an
   explicit release. It scans at most 16 jobs and charges full expected sizes. It does not resume canceled/failed jobs.
3. `ManagedBlobCache.ReconcileBuiltInOwnersAsync` scans at most 16 cache records, each with at most 1000 owners, and
   charges each record's conservative byte reservation. Missing package/transfer records permit orphan-pin removal
   only under exclusive account ownership. Active/latest pointers to missing manifests and unknown cleanup journals
   preserve pins. Unknown schemas/identities, custom/legacy owners and unresolved repair
   records preserve pins. Reader/snapshot pins, including temporary import/rotation pins, also require successful
   physical lifetime probes. Cache-record CAS protects a concurrent change by a nonparticipating writer.

Repair journals and encrypted rotation/generation journals are not themselves cache pins. Repairs retain their
transfer ownership; incomplete/unknown repair state blocks orphan release. Dead temporary rotation lifetimes can
release their cache pins, while retained ciphertext stays inventoried and subject to the encrypted provider's separate
generation/reader/quarantine rules. This maintenance never retires keys or deletes quarantine.

Existing transfer-release journals, read-model generation collection and chunk generation collection remain separate
protocols. Hosts may register compatible steps for them only when those steps enforce the declared allowances and
dry-run contract. The runner does not silently wrap unbounded compatibility methods.

## Compatibility and evidence

The API is additive, `IBlobCache` and the wire protocol are unchanged, and older binaries ignore the new checkpoint
namespace. All writers must still participate in account ownership; mixed old/new processes cannot be made safe by a
new reader alone. New checkpoint records use source-generated JSON and ordinary provider CAS transactions.

Tests cover dry runs, bounds, urgent work, incompatible plans, disposal joining, interrupted-page replay, malformed
owners, CAS conflicts, and native SQLite process death. Browser tests resume a checkpoint across page reload under
Web Locks and restore package/transfer pins through IndexedDB. These are functional checks, not device or performance
qualification. Failure and exact package evidence is recorded with the ownership continuation.
