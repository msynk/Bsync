# Remaining implementation audit — 26 October 2026

Scope: compare both implementation plans against the current working tree, excluding release qualification and tests
as deliverables. This continuation changes source; the earlier `.4` package evidence does not describe these new bytes.
Nothing was published or tagged, and the work remains experimental.

| Actual gap at the start of this pass | Implementation now present |
| --- | --- |
| Raw browser import/completion used whole-file hashing and payload reads (B02/B03) | `BrowserBlobStore` uses bounded SHA-256 and 1 MiB interop reads. Schema 3 stores new complete raw objects as immutable Blobs, preserves open snapshots, fences changed partials, and renames without reading all payloads. A capability probe handles sessions that reject Blob persistence. Their fallback is explicitly limited to 16 MiB; larger partials are retained. |
| Optional package-set children and their failure/progress semantics (older B7/newer B06) | Root schema 2 adds optional children. Required children alone gate activation. Candidate/active optional failures are separate, and a snapshot fixes its available membership. Missing/revoked optional children do not invalidate required content; malformed manifests and infrastructure errors still fail. |
| Large isolated resnapshot generation, catch-up, concurrent-writer handoff and progress (B08) | Capture migration 2 adds durable generations, SQL-to-SQL key staging up to 1,000,000 keys, bounded transactional page builds, resumable catch-up, explicit abandonment, failure diagnostics and atomic full-scope activation with the CT cursor. Final publication locks precede source writer locks. |
| Apple reference host did not connect the existing URLSession/inbox recipe (older B3/newer B04) | AppDelegate now resolves registered accounts on relaunch, forwards exact session callbacks, imports verified pinned bytes, exposes receipt/ticket renewal, applies an explicit spool policy and joins cancellation/import/store disposal before deletion. Portable reference-host factories refuse deleted/unregistered identities. |
| Stale plan checkboxes and missing integration guidance | Both plans point to this audit; package-set ownership/retirement and existing key-retention contracts are distinguished from remaining qualification. ADRs, compatibility notes, support matrix and the package-only workflow sample describe the new behavior. |

The audit also found implementation defects while exercising these changes: Debug SHA-256 compression repeatedly
allocated its constants; they now live in one static array. The native reference host's deletion retry attempted to
delete files below an already removed directory; it now handles that state. Private WebKit Blob rejection could leave
a transaction action waiting forever; transaction failure is now observed concurrently with cursor work, and Blob
persistence is probed before attempting real completion.

## Work that was already implemented

The older plan's unchecked grouped-package items largely described existing code: immutable references, independent
child pins, one root pointer, fixed snapshots, retirement guards and shared ownership. Optional children were the
missing behavior. Nested groups remain intentionally unrepresentable (depth one); child dependency cycles are rejected.

Native account scopes, joined worker cancellation, stable Android work/Java identities, backoff, Apple processing-task
expiration and foreground fallback already existed. Android factory coexistence and physical lifecycle behavior remain
qualification, not another portable scheduler implementation. The newly connected Apple download path still needs an
Apple SDK build/device run before support promotion.

Key retention already reports complete, partial, retired, reader-held and quarantined generations. Unknown references
conservatively block key retirement. `ILocalKeyProvider` cannot delete keys; the supported host contract requires
exclusive account ownership and inclusion of v1 sources, protected metadata, backups and every other key user before
host key deletion. No cache-only "safe to delete" API is introduced.

Multipart initiation/reconciliation/completion/abort, scoped provider sessions, orphan sweeping, verified publication,
the file-service bridge, relational command/projection/effect helpers, migrations and worker integration were present.
Host authorization, canonical projection/membership logic, credential acquisition, backup catalogs and retention policy
remain deliberate application contracts rather than library omissions.

## Verification and remaining gates

Focused core tests cover optional membership, repaired/revoked children, native receipt relaunch/deletion and SHA-256
vectors/allocation bounds. SQL tests cover page rollback, reconstructed builders, deletions/membership changes, bounded
catch-up, activation replay and a concurrent writer held across handoff. Browser regressions instrument whole-file
hashing/materialization and payload inventory, along with capacity and encrypted-chunk behavior. Results and retained
failed attempts are under `artifacts/capabilities-closure/implementation-*`.

Recorded focused results: 28 core/API cases and 16 SQL Server cases passed. The first final browser group passed
18 cases across Chromium, Firefox and WebKit, including raw streaming/refusal, ledger races, encrypted chunks and
package sets. A subsequent 12-case browser group passed after joined-mutation/import cleanup changes, covering raw
streaming, ordinary blobs, repair and encrypted partial reload/rotation. The WebKit large-raw case asserts explicit refusal with its entire partial retained; it is not counted
as successful large-raw persistence. Initial failed runs (WebKit transaction hang, Debug hash allocations and repeated
native deletion) are retained separately from the corrected runs.

Remaining work outside this implementation pass is qualification/release: independent crypto review; complete signed
Android/Apple builds and physical-device behavior; external provider/deployment evidence including MinIO; published
version compatibility fixtures and the full hosted ten-green-run gate; clean immutable release provenance and an
authorized release. These are not marked complete. Broader unsupported profiles (CDC, cross-database capture, composite
source keys and nested package sets) were not silently added to the plans' bounded implementation contracts.
