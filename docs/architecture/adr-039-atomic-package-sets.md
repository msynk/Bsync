# ADR-039: Atomic roots over existing immutable offline packages

Status: experimental implementation and local regression coverage (B06, 2026-10-24).

Add package-set methods to `OfflinePackageManager`, reusing its manifests, cache pins, authorization, transfer jobs,
verification and physical reader ownership. One immutable schema-1 root binds 1–32 unique child package IDs, exact
revisions and canonical manifest SHA-256 identities. Each child keeps the existing 256-item cap. Root metadata is
bounded to 64 KiB UTF-16 and aggregate child metadata to 4 MiB UTF-16. Shared hashes must declare the same length.

The supported profile is flat. Schema 1 requires every child; schema 2 allows `Optional = true` on exact child references.
Nested sets have no representation. Optional items and dependency-cycle validation retain existing child semantics. This bounds traversal to
one set-to-package level. Integrity identities do not authorize content; the host's authorization callback still runs.

Staging commits the root and child references atomically after staging/pinning each child. A crash before the root
commit can leave prepared children, never a partially active set. Activation verifies every required item and changes
one durable active-root pointer. Snapshots open every child through that exact root and hold existing reader pins.
Root and sorted child locks coordinate retirement. A referenced child cannot be retired; retiring an old root releases
references, then existing explicit child retirement releases its pins. Live snapshots remain readable throughout.

Set status verifies both active and latest candidate roots. `MissingRequired`/`BytesRemaining` describe the candidate
(shared missing hashes counted once); `ActiveMissingRequired` reports repair needs in the active root. `VerifiedUtc`
is a local observation, not a server freshness guarantee. Failed candidate preparation preserves the active root.

Schema-2 optional children must supply valid, authorized immutable manifests at staging, but their required content
need not be present at activation. Later missing/corrupt content or revoked access excludes that child when opening
a snapshot, with `UnavailableOptionalChildren` reporting `incomplete`/`unauthorized`. Opening an excluded child's item
returns null; unknown child identities still throw. Repair requires a new snapshot and never changes an existing
snapshot's membership. `OptionalFailures` and `ActiveOptionalFailures` distinguish candidate and active observations.
Unknown schemas, malformed manifests, key unavailability and infrastructure failures still fail explicitly; they are
not reclassified as optional-content failures. Optional references/pins retain the same retirement ownership.

First use raises the permanent orchestration feature barrier to **3**. Close older processes/tabs under exclusive
account ownership before migration. Published 0.5.0 readers refuse version 3 on reopen; they must not run concurrently
through the upgrade. Existing single-package records retain their meaning. Unknown newer versions are retained/refused.
SQLite, memory and IndexedDB providers accept 3; unsupported custom providers refuse before set staging.

Tests exercise 300 items across two children, shared-byte accounting, failed candidate activation, reconstructed
managers, substitution/revision reuse, retirement and old/new readers. Four real process-kill cases cover root staging
and activation before/after durable commits. A fixture created by published 0.5.0 upgrades with pending work, partial
bytes and pins intact; the actual old binary then refuses feature version 3. Three browser engines exercise 260-item
sets through reload, retirement, authorization revocation and storage loss. Native quota and same-length corruption
checks preserve the old root and pins. Shared content is queued, pinned and verified once per hash within a child,
while every item retains its missing-content status. Verification caching lasts only for that operation; item opens
still verify content again. Physical-device and broader deployment qualification remain external gates.
See the [execution record](../review/capabilities-implementation-2026-10-24.md).

See the [hardening continuation](../review/capabilities-closure-2026-10-26.md).
