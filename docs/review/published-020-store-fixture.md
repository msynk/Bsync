# Published 0.2.0 store fixtures

Generated 2026-10-16 on Windows x64, .NET SDK 10.0.401/runtime 10.0.12, using only published package references:

- `Bsync` exactly 0.2.0
- `Bsync.Storage.Sqlite` exactly 0.2.0
- `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12, explicitly selected by this fixture host

Generator: `src/Tests/Bsync.Fixtures.V020`, deliberately outside the source solution. It has no project references.
NuGet auditing remained enabled and audit warnings/errors are fatal.

```text
dotnet run --project src/Tests/Bsync.Fixtures.V020 -c Release -- src/Tests/Bsync.Tests/Fixtures/replica-0.2.0.db
dotnet test src/Tests/Bsync.Tests -c Release --filter FullyQualifiedName~PublishedSchemaMigrationTests
```

Fixture SHA-256: `c22928942895d8d9aea4eec9fb48f1ecdded1f92c1f2b99fadc825b2e3c740f6`.
Generation includes an unsent prepared operation, a two-member prepared dependency group, a retained local/server
conflict, a parked `title-forbidden` rejection, and synced/unsynced unknown `colour` fields. Migration tests inspect
the original operation IDs before opening, then assert they are transmitted unchanged after opening with current
source. Both published 0.1.0 and 0.2.0 SQLite fixtures pass this test.

The fixture establishes this SQLite upgrade path only. It does not establish exact source equivalence of historical
packages, browser/mobile migration, encrypted-blob migration or preservation of fields absent from the generator.
Regeneration intentionally creates new operation IDs/timestamps and requires an updated hash and evidence review.

## IndexedDB fixture

`src/Tests/Bsync.Tests.Browser/Fixtures/indexeddb-0.2.0.json` preserves the database schema, indexes, records and metadata
actually created by published Bsync/Bsync.Blazor 0.2.0 in Chromium 153.0.8010.12. Assembly version: 0.2.0.0; schema: 4.
SHA-256: `337df863ed399b96ddc9b72b4fffebc3ebfdaa7f22207ca19f4de93b7cd581ba`.
It includes the same prepared writes, group, conflict, rejection and unknown fields as the SQLite generator.
This JSON is a logical IndexedDB snapshot, not a portable browser-profile binary.

The generator is deliberately outside the solution and references only exact published packages. The capture tool
serves its published assets in a fresh browser context and refuses to overwrite an existing fixture:

```text
dotnet publish src/Tests/Bsync.Fixtures.Browser.V020 -c Release -o artifacts/published-020-browser
dotnet run --project src/Tools/Bsync.FixtureCapture -c Release -- <absolute-published-wwwroot> <new-absolute-output.json>
dotnet test src/Tests/Bsync.Tests.Browser -c Release --filter FullyQualifiedName~Published020Store
```

Tests restore the captured schema/data before opening it with the current .NET store. They assert unchanged operation
IDs on transmission, group delivery, conflict/rejection retention and unknown members in all three Playwright engines.
The same harness is linked into the package-only browser consumer. Schema 4 is unchanged; this qualifies opening and
preservation across package versions, not an invented IndexedDB schema migration or physical-device storage durability.
