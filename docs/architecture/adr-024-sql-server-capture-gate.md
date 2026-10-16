# ADR-024: Retain controlled publication after a bounded SQL Server capture spike

Status: Decision recorded; automatic capture adapter not shipped

## Decision and experiment

Keep caller-transaction publication and the explicit coalesced projection outbox as the supported integration paths.
The Tasks projection is the comparison workload: task fields plus related attachments, with server-derived membership.
Automatic capture is justified only for deployments with uncontrolled writers that cannot enqueue projection work.
No such requirement is established by the portable offline milestone.

`ChangeTrackingSpikeTests.SnapshotBoundaryIncludesConcurrentCommitOnlyInCatchupAndRetainsDeletes` passed against a
fresh SQL Server LocalDB database on 2026-10-16. A snapshot preserves its initial values while a second connection
commits update/delete/insert operations. The next snapshot validates the saved Change Tracking version and obtains all
three operations, including the deleted key through an outer join. Truncation invalidates the saved boundary.
This tests one capture boundary; it does not qualify a production capture adapter, CDC or restore recovery.

## Comparison

| Route | Projection input | Cost and boundary |
|---|---|---|
| Caller-transaction publication | Canonical projection from the domain transaction | Immediate feed consistency; all writers participate and acquire feed locks before domain rows |
| Coalesced application outbox | Explicit affected projection IDs | Eventual feed consistency; durable work and domain mutation share a transaction; dispatcher rereads current state |
| Change Tracking | Changed primary keys and operation metadata | Suitable candidate for current-state reconciliation, not an immutable business event history |
| CDC | Log-derived captured changes with LSN boundaries | More operational machinery; potentially useful for history/legacy writers; cannot infer projection membership |

SQL Server supplies both capture mechanisms; neither replaces application authorization or inbound domain validation.
[Microsoft feature comparison](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/track-data-changes-sql-server).

## Candidate Change Tracking algorithm

Enable database/table Change Tracking and snapshot isolation. In one snapshot transaction, validate each contributing
table's minimum valid version, capture the current version, enumerate changes and join current source data. For the
initial load, capture the version and the complete source in that same snapshot. An invalid boundary requires a full
resnapshot. Retention is operational capacity, not permission to silently skip old changes. Restore requires a separately
managed source epoch; a numerically valid version alone cannot detect lost history.
[Microsoft consistency and restore guidance](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/work-with-change-tracking-sql-server).

Proposed Bsync integration, **not implemented**:

1. Materialize a capture batch into durable staging, including source epoch, schema identity, lower/upper boundary and
   affected projection IDs. Seal the batch only after complete source enumeration. Unsealed staging is abandoned on crash.
2. Publish deterministic projections from a sealed batch using one destination transaction per bounded unit. Commit its
   consumption marker and feed changes together. Never advance the capture checkpoint until every unit is consumed.
3. Serialize destination application by source batch order, or enqueue current-state rebuilds so an old event cannot
   overwrite a newer projection. A source snapshot must never be replayed over live synchronous publication without
   a defined ordering rule. Do not mix the routes casually.
4. Rebuild into an isolated staging generation. Reconcile deleted IDs and prior membership, catch up after the captured
   boundary, then atomically switch only with a proven concurrent-writer handoff. Never call `ReplaceScopeAsync` on
   a partial enumeration. Existing Bsync APIs do not make this generic handoff automatic.

Track every related table that influences a projection or its readers, with explicit reverse-key mapping. Deleted
related rows may require retained mapping/tombstones to identify affected parents. A missed related-table change is
an authorization defect, not merely stale display data.

## Deployment, privileges and CDC boundary

Change Tracking needs a primary key and database/table configuration under deployment privileges. Provision and verify
settings in migrations; capture workers must receive only their required read/change-tracking permissions. Schema/key
changes require adapter compatibility validation and potentially a new capture epoch.
[Microsoft setup](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/enable-and-disable-change-tracking-sql-server).

For SQL Server CDC, database enablement requires `sysadmin`; table enablement requires `db_owner`. Configure the
capture-instance columns and access role explicitly. Do not assume availability on every edition or managed service.
[Microsoft CDC setup](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/enable-and-disable-change-data-capture-sql-server).

SQL Server CDC normally relies on Agent capture/cleanup jobs and retention-managed change tables. Monitor job failures,
capture lag, oldest unapplied boundary, cleanup horizon and storage. The candidate must reject an LSN outside the
available interval and resnapshot. A timestamp, identity column or bare rowversion is not a committed capture cursor.
[CDC operation](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/administer-and-monitor-change-data-capture-sql-server),
[CDC validity interval](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/about-change-data-capture-sql-server/).

Restore/attach behavior depends on destination and `KEEP_CDC`; compatible editions and recreated/running jobs must be
verified. Restore must trigger the application's source/Bsync epoch procedure even if capture metadata survives.
[Microsoft CDC restore behavior](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/change-data-capture-and-other-sql-server-features).

A race-free CDC initial-snapshot/log handoff is **not established by this spike**. Before shipping CDC, test its exact
provider/version algorithm against concurrent transactions, capture delay, cleanup, DDL, backup restore and process
death. The LocalDB experiment cannot satisfy that gate. Controlled publication remains the supported route.
