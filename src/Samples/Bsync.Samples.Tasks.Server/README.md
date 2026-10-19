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

## Opt in to the reader-specific projection

`TaskAudienceProjection` shares publication plumbing between API, sync, intent and ACL writers. It uses the
caller's SQL Server transaction: it does not open or commit a connection, wrap an EF execution strategy, authorize
a mutation, or discover dependencies. The default server still exposes the tenant-wide Tasks feed only.

For a host that chooses the separate reader-specific feed, initialize the audience authority first, then construct
the task authority with the registered handler:

```csharp
var audience = await TaskAudiencePublication.CreateAuthorityAsync(connectionString);
var dependent = new TaskAudienceProjection(audience);
var tasks = await SqlServerSyncAuthority<TaskDocument>.CreateAsync(new()
{
    ConnectionString = connectionString,
    Collection = TasksJson.Collection,
    DocumentType = TasksJson.Default.TaskDocument,
    WriteHandler = new TaskWriteHandler { AudienceProjection = dependent },
});
var acl = new TaskAudiencePublication(connectionString, tasks, audience);
await acl.InitializeAsync(); // before admitting writes
tasks.Committed += dependent.NotifyCommitted;
var api = new TaskApiPublication(connectionString, tasks) { AudienceProjection = dependent };
var intentHandler = new IntentWriteHandler(tasks) { AudienceProjection = dependent };
```

Use `api` in the authenticated POST route and `intentHandler` when creating the intent authority. Map the audience
authority as a read-only collection using its generated JSON metadata. Derive tenant/actor from authentication and
authorize ACL changes server-side. The tenant-wide Tasks endpoint still grants tenant-wide reads: do not expose it
to restricted readers if the application's confidentiality boundary is per-task membership. Adding an audience feed
does not restrict the original endpoint, intents, or blob access policy.

The total lock order is **intent feed (when used), Tasks feed, audience feed, ledger/domain/attachment/ACL rows**.
The API execution strategy retains its visible fresh-context/connection/transaction boundary. The sync and intent
handlers enlist in their authority's transaction; they never retry or commit it themselves. After `SaveChanges`,
dependent publication reads current server-owned rows and commits with the domain write and source feed. A delete
publishes a tombstone. Grants and removals use `TaskAudiencePublication.SetReadersAsync` in the same order.

| Contributing writer | Required registration/action |
|---|---|
| API create (`TaskApiPublication`) | Set `AudienceProjection`; retry the entire existing transaction delegate |
| Sync update/delete (`TaskWriteHandler`) | Set `AudienceProjection` before creating the authority |
| Intent completion/rename (`IntentWriteHandler`) | Set `AudienceProjection`; retain the command ledger |
| ACL changes | Use `SetReadersAsync`; authorize the change before entering it |
| Admin, import, raw SQL, related-table writer | Acquire Tasks then dependent lock before reading/writing rows; save and publish affected IDs before commit |
| Optional projection outbox / repair jobs | Register any new contributing mutation explicitly; the existing task-only outbox does not automatically maintain audience dependencies |

For a custom writer use `tasks.AcquirePublicationLockAsync`, `dependent.AcquireLockAsync`, domain validation and
mutation, `dependent.PublishAsync`, and finally commit, in that order on the same connection/transaction. Publish
the Tasks projection too if its contributing fields changed. The helper validates connection/transaction pairing;
the application still owns lock ordering and authorization. No generic interceptor covers unregistered writers.

Forward hints only after commit. The `tasks.Committed` subscription above covers standalone task pushes and the API.
Enlisted intent pushes do not emit a task commit event; after the **outer intent** commit a host can send a scope-only
hint (`new AuthorityCommit(commit.Scope, [])`) to Tasks and audience. Do not forward intent IDs as task IDs. Remove
subscriptions and dispose authorities on shutdown. Poll/catch-up remains required because hints can be lost.

Replay of an unchanged API creation sends a hint without republishing; enabling a new dependent projection requires
an explicit bounded `acl.ReconcileAsync` backfill before serving it. Check its task/reader limits and use a different
reviewed rebuild design if they are insufficient. Deployments must inventory every writer; these samples cannot prove
coverage of a consuming application's raw SQL or jobs.

`TaskDependentPublicationTests` exercises two-feed retries/rollback, reader visibility, sync deletes, intent ledger
atomicity, caller ownership and both transaction-holding orders. `TasksSampleTests.ConcurrentRestAndSyncCreationKeepOneCanonicalDomainEffect`
races twelve API/sync pairs through real authenticated HTTP endpoints. These tests require `BSYNC_SQLSERVER`.
