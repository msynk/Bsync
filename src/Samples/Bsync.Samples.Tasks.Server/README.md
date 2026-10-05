# Tasks sample: a relational system of record

An ASP.NET Core server whose system of record is an ordinary EF Core table (`dbo.Tasks`) on SQL Server, replicated to
clients by the SQL Server authority (ADR-014):

- **Write handler** (`TaskWriteHandler`): runs in the authority's transaction for every replicated write. It rejects a
  task without a title with the stable code `title-required`, recomputes `Slug` from the title, and saves the row
  through EF Core on the authority's connection and transaction, so the table and the feed commit together.
- **Publisher** (`POST /api/tasks`): an ordinary API endpoint writes the table with EF Core and publishes the document
  in the same transaction (`UpsertAsync(..., transaction.GetDbTransaction())`).
- **Bearer tokens**: every sync and API request needs a JWT; the scope is its `tenant` claim. `POST /api/token` issues
  development tokens for any user and team. Replace it with your identity provider.
- **Clients**: `Bsync.Samples.Tasks.Console` (SQLite replica, works offline) and `Bsync.Samples.Tasks.Client`
  (Blazor WebAssembly, IndexedDB replica, hosted by this server).

## Run

```bash
dotnet run --project src/Samples/Bsync.Samples.Tasks.Server --urls http://localhost:5000   # LocalDB by default
dotnet run --project src/Samples/Bsync.Samples.Tasks.Console -- add Buy milk               # offline is fine
dotnet run --project src/Samples/Bsync.Samples.Tasks.Console -- sync
dotnet run --project src/Samples/Bsync.Samples.Tasks.Console -- list                       # shows "buy-milk", or "Rejected: title-required"
```

Set `ConnectionStrings__Tasks` for another SQL Server. The automated test is
`src/Tests/Bsync.Tests.SqlServer/TasksSampleTests.cs` (needs `BSYNC_SQLSERVER`).

## Give the sync client its own HttpClient

Both clients create a dedicated `HttpClient` for `HttpSyncTransport`. Do not route sync requests through a general
purpose HTTP pipeline that retries, rewrites error bodies or converts failures to exceptions of its own: the transport
reads `Retry-After` and the protocol's problem codes (`reset-required`, `upgrade-required`, `payload-too-large`, ...)
from the original response, and a pipeline that hides them turns recoverable conditions into opaque failures. Adding a
bearer token in a `DelegatingHandler`, as these clients do, is fine.
