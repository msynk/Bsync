# Published 0.2.0 SQLite fixture

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
