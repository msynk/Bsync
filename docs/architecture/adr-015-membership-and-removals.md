# ADR-015: Read membership in the feed, and removals without a resnapshot

- **Status:** Accepted (2026-10-05). Implemented by the in-memory, SQL Server and PostgreSQL authorities (0.3.0,
  unreleased; PostgreSQL schema 2, table `bs_document_access`, 2026-10-06).
- **Invariants:** I03, I06, I07, I10, I14
- **Related:** ADR-005 (feed ordering), ADR-010 (scopes), ADR-014 (SQL Server authority), improvement plan task C1

## Context

Two weaknesses of ADR-010's model (findings F2 and F3):

1. `CanRead` runs in memory after the page query's `LIMIT`. A caller who may read 500 of 100,000 documents in a scope
   gets mostly empty pages with `hasMore: true`, and the server reads every document of the scope to serve it.
2. The only way to take a document away from one caller is to change the scope fingerprint, which resets the whole
   collection for that caller (`scope-changed`) and resnapshots everything.

## Decision

### Readers belong to a document version

- An authority may be configured with `Readers` (the principal keys that may read a document, computed from the
  document and from whatever the application keeps about it) and `PrincipalKey` (the caller's key, from its
  `SyncCallContext`). Without them, nothing changes.
- The reader set is computed at **every write** (replicated push, write handler outcome, publisher) and stored next to the
  document version. A change of the reader set is a change of the document: the publisher's "unchanged content" check
  compares readers too, so republishing a document after a membership change elsewhere (a user left a team) writes a new
  version.
- Per document and principal the authority keeps one access row: `(feed, document, principal, version, granted)`.
  A write updates the rows of every current reader to the new version (`granted = true`) and the row of every principal
  that just lost access (`granted = false`, once). Principals that never had access get no row: their removals would
  reveal ids they never saw.

### Pull per principal

- With membership, a pull for principal P reads P's access rows with `version > checkpoint`, in version order, joined to
  the documents. Pages are full: cost is proportional to what P can see. `CanRead` remains a final guard.
- A granted row returns the document (`changes`). A revoked row returns its id in `removals` (below).
- **Ordering.** Access rows get their versions from the feed sequence, under the same feed lock as the document write
  (ADR-005). A page and its checkpoint therefore still cover a committed, gap-free prefix of P's view, and at most one
  entry per document (its latest row) appears in a page.

### Removals (protocol feature `removals`)

- Replicas that understand removals say so in the pull request: `features: ["removals"]`. The response then carries
  `removals: [ids]` and feature `removals`.
- A replica applies a page atomically: clean copies of removed ids are purged (never deleted on the server); a record
  with local changes or a kept conflict is kept and hidden (`MissingAfterReset`), its upload is answered `forbidden` and
  parked (I19); it reappears if access is granted again. A purge is repeated safely if the page's commit is interrupted.
- **Peers without the feature.** A pull request without `features: ["removals"]` that would need a removal is answered
  `reset-required` with reason `scope-changed`: the old replica resnapshots and purges, as with ADR-010. Old replicas never
  keep a revoked document. Older authorities never send removals; new replicas keep working with them.

### Writes

- With membership, a write to an existing document requires the caller to be among its current readers; otherwise it is
  rejected with `forbidden`. Outcomes never reveal a document the caller cannot read (as with `CanRead`).

### Shared documents and time windows (recipe)

- A writable document shared by several principals lives once, in a group or tenant scope; `Readers` lists who may see
  it. Concurrent edits by two readers conflict as usual.
- A moving time window ("the last 30 days") must not be a scope fingerprint: it would reset every replica every day.
  Express it with retention (tombstones and receipts age out, ADR-005) plus explicit pins in `Readers` for documents that
  must stay visible longer.

## Alternatives rejected

- **A separate grant/revoke log with its own positions.** Needs a second allocator under the same lock and lets a grant
  reference a document version the replica does not have; tying readers to document versions avoids both.
- **Filtering in memory with a larger window.** Still unbounded work per pull and still sparse pages.

## Verification

Done-when of task C1, tested on the in-memory authority and on SQL Server: two principals editing one shared document
get a real conflict; revoking access removes the clean copy through `removals` without a reset and hides a dirty draft;
a filtered pull over 100,000 documents of which the caller sees 500 returns full pages; a replica without the feature
resets with `scope-changed`; a moving window based on retention does not emit `scope-changed`.
