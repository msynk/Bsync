# ADR-041: Bounded Change Tracking staging and atomic current-state publication

Status: experimental optional implementation, superseding ADR-024's unimplemented-adapter status (B08, 2026-10-24).

`SqlServerChangeTrackingCapture` lives in `Bsync.Server.SqlServer.Workflows`. The first supported implementation
profile has source, staging and destination in one database; 1–32 contributing tables; one int/bigint/GUID or bounded
string primary key per table; and at most 10,000 source keys per ordinary sealed batch (host may set a lower bound). Excess work
refuses without advancing the cursor. Multi-database publication, composite keys, unbounded streaming snapshots and
CDC are outside this profile. No automatic authorization or relationship inference is provided.

Deployment explicitly enables snapshot isolation and database/table Change Tracking and installs `MigrationSql`.
Runtime checks prerequisites without changing database features. In one snapshot transaction, capture validates every
table's minimum valid version, captures the upper version and stages all initial/delta keys, including deletes. Keys,
sealed-batch marker and pending cursor commit together. Uncommitted staging rolls back; a sealed pending batch is
replayed after restart. The authoritative published boundary does not advance during staging.

Publication owns one serializable transaction and stream application lock. Host callbacks acquire feed locks in the
same order as synchronous routes, resolve related/deleted keys, reread current canonical data and derive membership.
They must not replay stale staged payloads. Publication, applied marker and cursor advance commit atomically. A lost
commit reply does not repeat an applied batch. Competing workers fail acquisition and retry; there is no expiring lease
that allows an old worker to acknowledge a new one.

Initial/resnapshot callbacks replace the **complete** destination scope/generation, including removals. The bounded
single-database implementation uses transaction isolation for this replacement: old published content remains visible
until commit. A callback that merely upserts listed keys does not meet this contract. `ReplaceScopeAsync` is available
on the existing SQL authority for full membership/deletion reconciliation. Large staged destination generations need
the generation workflow below rather than an ordinary full-scope callback.

The constructor binds an externally managed source restore epoch and a projection schema identity, including configured
tables/keys. An epoch/schema mismatch requires explicit `BeginResnapshotAsync(expectedPreviousEpoch)`. This transition
retains old sealed batches and published content until replacement. Monitor Change Tracking retention ahead of worker
lag; an expired boundary is a resnapshot condition, never permission to skip changes.

## Isolated resnapshot generations (26 October continuation)

Migration 2 preserves existing cursors/batches, adds generation progress and failure diagnostics, and fences older
capture runtimes. `BeginGenerationAsync(expectedPreviousEpoch)` snapshots and seals up to 1,000,000 keys directly in
SQL without loading the inventory into managed memory. The active destination and published cursor remain unchanged.
`BuildGenerationPageAsync` reads at most 10,000 keys and commits host-owned generation rows with its ordinal progress.
Callbacks reread canonical state and must write only rows keyed by the supplied generation ID, including reverse
mapping/membership state. Exceptions roll back both rows and progress; diagnostics retain only the exception type.

After each complete build, `CatchUpGenerationAsync` seals subsequent CT keys for the same page builder. Deleted and
related keys use host mappings just as ordinary capture does. Retention, table generation and external epoch checks
remain mandatory; an invalid generation is abandoned explicitly, never activated or silently restarted.

`ActivateGenerationAsync` first invokes the host's publication-lock callback, then takes source table locks in stable
order, applies a final delta bounded by the ordinary batch limit and invokes full-scope activation in that transaction.
The destination pointer/replacement, applied markers, generation state and published CT boundary commit together.
Writers blocked during handoff appear after the boundary and are processed by ordinary capture. If the final delta
is too large, catch up/build again; no partial replacement occurs. This deliberately trades a brief writer pause for
a well-defined same-database handoff. It is not a cross-database or lock-free algorithm. Synchronous publication must
use the same publication lock ordering; host projection callbacks must reconcile deletion and membership.

`GetGenerationAsync` reports batch, completed/total keys, boundary, timestamp and last failure. Ordinary capture
reports `PendingFailure` through `GetHealthAsync`. Ordinary staging/publication refuses while a generation builds.
Explicit abandonment retains staging evidence and leaves the old scope intact. Cleanup of host generation rows follows
the host's reader ownership policy. See the package-only Workflows sample and `CaptureGenerationTests` for runnable wiring.

Table identity, tracking `begin_version`, creation and modification metadata form a conservative source stamp.
Testing found that truncation's minimum valid version can equal the current cursor, so comparison of numbers alone
missed this case. Tracking begin-version/table changes now force resnapshot. Some DDL can conservatively force one too.
No numeric/stamp check can prove continuity after every restore: change the externally stored epoch after restore and
coordinate every publisher before resuming. See Microsoft's [snapshot/retention guidance](https://learn.microsoft.com/en-us/sql/relational-databases/track-changes/work-with-change-tracking-sql-server)
and [tracking begin-version semantics](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/change-tracking-catalog-views-sys-change-tracking-tables).

LocalDB tests cover sealed replay, rollback, concurrent changes between staging/publication, deleted keys, bounded
overflow, missing prerequisites, epoch transition, truncation and empty full replacement. The first truncate test
failed; both failed and corrected TRX files are retained. `GetHealthAsync` observes pending batch ID/age/key count,
current/published/min-valid versions, epoch/schema mismatch and resnapshot requirements without creating stream state
or advancing a cursor. Version distance is not an elapsed duration or an event count. The additive `StagedUtc` column
records new batch age; old batches report unknown age. Failed projection callbacks retain their pending batch; hosts
monitor worker failures together with its age. The worker meter counts committed publications.

Four real process-kill cases now cover staging before/after seal and publication before/after commit. A related-table
fixture keeps a durable reverse map in the publication transaction, reaches old/new parents after child moves/deletes,
and reconciles reader membership. Schema changes block a sealed publication. Competing workers cannot advance an owned batch, and a fault-injected
boundary below SQL's real minimum-valid version is refused without adding a batch. A real private LocalDB backup/restore
proves the externally changed epoch forces full replacement and subsequent catch-up. SQL Server 2022/cloud, natural
retention-expiry drills and production operational/lag monitoring qualification remain open.

See the [hardening continuation](../review/capabilities-closure-2026-10-26.md).
