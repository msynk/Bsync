# ADR-018: Declared secondary indexes in the local stores

- **Status:** Accepted (2026-10-06) and implemented: in-memory, SQLite (schema 5) and IndexedDB (schema 4) stores,
  with shared conformance cases (`LocalStoreIndexConformance`) run in .NET and in Chromium, Firefox and WebKit.
- **Invariants:** I01 (local writes are durable), I02 (isolation), I07 (collections are isolated), I17 (schema
  upgrades keep pending work)
- **Related:** improvement plan tasks E1 and E2, ADR-004 (store operations), ADR-008 (browser storage),
  `docs/benchmarks.md` ("Local queries")

## Context

Task E1 measured the views an app shows through `LocalSyncCollection.QueryAsync` on a Windows desktop (Intel Core
Ultra 7 255H, .NET 10.0.12). A 50-item page ordered by a `DateTimeOffset` field over 50,000 documents of about 1 KiB
has a p95 of 318 ms with the in-memory store and 365 ms with SQLite. The bar is p95 under 50 ms on a named device, so
it is missed on the fastest device available, by a factor of six to seven. The cause is structural: a custom
`SyncQuery.Order` loads and deserializes the whole collection (219 MB allocated per page with SQLite at 50,000
documents) and sorts it in memory. Task E2 is therefore required.

The plan describes E2 as: SQLite generated columns via `json_extract` plus an index; IndexedDB indexes on a key path
inside the stored record. It names a stop condition: "the IndexedDB key-path approach cannot index values inside the
stored envelope; propose a store schema change in an ADR instead".

That condition holds. The IndexedDB store keeps each document state as a **JSON string** (`current`, `base`, the
pending payload and conflict copies are strings, so 64-bit numbers and unknown members survive exactly; ADR-008).
An IndexedDB key path cannot reach into a string, so no index on a document field can be declared without changing
what the store writes. The SQLite half is possible as planned, but `json_extract` has two problems of its own:

- It reads the JSON produced by the application's `JsonTypeInfo`. A naming policy, a custom converter or a
  `DateTimeOffset` written with an offset other than zero changes what the path finds and how values sort (ISO
  strings with different offsets do not sort by instant).
- The two stores must share one contract and one set of conformance cases (plan, E2). Ordering computed by SQLite
  from JSON and ordering computed by IndexedDB from something else would differ at the edges.

## Proposal

Index keys are computed in .NET from the typed document and stored next to the record by both stores, in one
transaction with the record.

1. **Declaration.** `SyncIndex<TDocument>.Create(name, d => d.Field, version)` with a value of type `string`,
   `bool`, an integer type, `double`, `decimal`, `DateTimeOffset`, `DateTime`, `DateOnly`, `TimeOnly`, `TimeSpan`,
   `Guid` or an enum (nullable allowed). Stores receive their indexes when they are opened. `version` changes when
   the extractor changes meaning, so stores rebuild the keys.
2. **Key encoding.** One order-preserving string encoding in the core, so both stores and the in-memory store sort
   identically by ordinal comparison: a type tag followed by fixed-width hexadecimal for numbers and instants
   (`DateTimeOffset` by UTC ticks), and the raw string for strings. `null` sorts first. The SQLite store stores the
   key as UTF-16BE bytes, as it already does for ids, so its binary order equals ordinal order.
3. **SQLite (schema 5).** Table `bs_index(collection, name, key, id_key)`, primary key on all four columns
   (`WITHOUT ROWID`), and an index on `(collection, id_key)` for maintenance. Rows exist only for visible, live
   records and are rewritten whenever a record is written or removed. Migration from schema 4 adds the table; the
   keys are filled when a store opens with indexes whose names or versions differ from those recorded in `bs_meta`.
   Records and pending work are untouched.
4. **IndexedDB (schema 4).** One new `multiEntry` index `ix` on a record field `ixKeys`, an array of
   `[collection, name, key, id]` arrays written by `.NET` with the record (present only for live, visible records).
   One index serves every declared index, so declaring a new one never needs a database upgrade; only the keys of
   existing records are filled, in stamped batches like any other write. The upgrade from schema 3 adds the index.
5. **Query API.** `SyncQuery<T>` gains `Index` (an index and a direction), an optional key range on it, and `Skip`.
   `ISyncCollection<T>.CountAsync(query)` is added with a default implementation. `Where` remains for residual
   filtering after the range. `ILocalStore<T>` gains an index page read and an index count with default
   implementations that evaluate in memory, so custom stores stay correct without changes.
6. **Conformance.** New store conformance cases, run against the in-memory, SQLite and IndexedDB stores: order and
   range including `null`, equal keys ordered by id, paging across equal keys, a re-indexed document moving, a deleted
   or hidden document leaving the index, a rebuilt index after a version change with pending work kept, and the same
   results from all three stores for the same writes.

## Consequences

- Both on-device schemas change (SQLite 4 to 5, IndexedDB 3 to 4). Older applications refuse the upgraded databases,
  as with every schema change so far; IndexedDB tabs still running the old application are closed as `outdated`.
- Each indexed write costs one extra row (SQLite) or a few array entries (IndexedDB) per declared index.
- An extractor must be a pure function of the document. Changing it without changing `version` leaves stale keys;
  this is documented, not detected.
- Strings sort by ordinal (UTF-16 code unit) order, not by culture. Culture-aware sorting stays in memory.

## Alternatives considered

- **Generated columns with `json_extract` (SQLite) and no IndexedDB change.** Rejected: the stores would not share a
  contract, and the plan requires one.
- **Storing documents as structured objects in IndexedDB** so key paths reach fields. Rejected: it loses 64-bit
  precision and the exact preservation of unknown members (ADR-008), and would change every existing record.
- **One IndexedDB index per declared index.** Rejected: each new declaration would need a database version change,
  which closes other tabs.

## Implementation notes

- The query API is `SyncQuery.Index` (built with `SyncIndex<T, TValue>`: `All`, `Equal`, `Between`, `From`, `Before`,
  then `Descending()`), `SyncQuery.Skip`, and `ISyncCollection.CountAsync`. `Order` and `Index` cannot be combined.
- Stores take their indexes when they are opened (`InMemoryLocalStore` constructor, `SqliteLocalStore.OpenAsync`,
  `IndexedDbLocalStore.OpenAsync`, `AddBrowserSyncCollection(indexes:)`). `bs_meta`/`meta` key `indexes` records the
  set (names and versions) the stored keys were built for. A store that declares another set rebuilds them when it
  opens; a writer with another set marks them unusable (`!`), and queries are then evaluated in memory until a store
  with the right set opens. In IndexedDB, a rebuild holds a token naming its target set, so writers with the same set
  keep keys during the rebuild, and a writer with another set cancels it.
- SQLite walks `bs_index` first (`CROSS JOIN`), so a page stops at its limit; index rows exist exactly for live,
  visible records (purges delete them too), so counts read `bs_index` alone.
- Measured (desktop, 50,000 documents): a 50-item ordered page in 0.35 ms (in-memory) and 0.45 ms (SQLite) instead
  of 313 ms and 337 ms; see `docs/benchmarks.md`. IndexedDB timings in browsers were not measured.
