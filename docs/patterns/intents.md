# Pattern: immutable intents (actions executed once)

Most offline edits are state: "the task's title is now X". Some are actions: "complete this task", "approve this
request", "send this invoice". Replaying state twice is harmless; replaying an action twice is not, and two actions
must never merge into one. This page shows how to model actions on top of the ordinary engine, with no extra library
types. The complete, tested example is in the Tasks sample (task F3):

- [`TaskIntent`](../../src/Samples/Bsync.Samples.Tasks.Shared/TaskIntent.cs): the intent document, kinds, states and codes.
- [`IntentWriteHandler`](../../src/Samples/Bsync.Samples.Tasks.Server/IntentWriteHandler.cs): execution on the server.
- [`TasksClient`](../../src/Samples/Bsync.Samples.Tasks.Console/TasksClient.cs): creating intents and the sync order.
- `TasksSampleTests.IntentsExecuteOnce` (`src/Tests/Bsync.Tests.SqlServer`): the guarantees below, end to end on
  SQL Server, Kestrel and SQLite replicas.

## The shape

An intent is a document in a collection of its own:

| Member | Set by | Meaning |
|---|---|---|
| `Id` | client | A new id per intent (a GUID v7). Never reused, so intents never coalesce. |
| `Kind`, `TaskId`, payload (`Title`) | client | What to do, to what. |
| `TaskRevision` | client | The target's revision the user saw; `null` means "whatever it is now". |
| `CreatedAt` | client | Informational only (device clocks are not trusted). |
| `State`, `Code`, `ExecutedBy` | **server** | `pending`, `executed` or `rejected`; a stable code when rejected; who executed it. |

The target document carries a server-maintained `Revision`, incremented by every accepted change, so an intent can say
"only if nothing changed since I looked".

## Rules

1. **Create, never edit.** A client saves an intent once. The server's write handler rejects any later write to an
   existing intent with a stable code (`intent-immutable`); the server's copy stays as it was.
2. **Execute in the accepting transaction.** The write handler of the intents collection changes the application's
   tables and publishes the changed target document (`ISyncPublisher.UpsertAsync(scope, document, transaction)`), all
   in the authority's transaction (ADR-014). The intent is accepted, the action happens and the feed changes together,
   or none of them does.
3. **Exactly once comes from receipts.** If the response is lost after the commit, the replica sends the same
   operation again; the authority answers from its receipt and does not run the handler again. Keep receipts at least
   as long as replicas may stay offline (`AddSyncRetention`, task D5).
4. **Acceptance is not execution.** The handler always replaces `State`, `Code` and `ExecutedBy` with its own values.
   An intent that cannot run (the target changed or was deleted, a rule fails, the kind is unknown) is still
   **accepted**, with `State = rejected` and a code: it replicates back, its code survives restarts, and the sync
   status of the record is `Synced`. Only malformed writes (such as edits of an intent) are sync-level rejections.
5. **Use the caller, not the payload.** Identities come from `SyncWriteContext.Caller` (scope and principal). An
   intent never names who executes it.
6. **Wait for missing targets.** When the target has not reached the server yet (created offline on the same or
   another device), return `SyncWriteDecision.RetryLater(PushErrorCodes.DependencyMissing)`. No receipt is stored;
   the replica keeps the intent pending and sends it again with backoff (task D3), and it executes once the target
   exists.
7. **Sync targets first.** A client pushes the target collection before the intents collection, then pulls targets
   again to receive what the intents changed. With a `SyncCoordinator`, declare the intents collection with
   `DependsOn` the targets.

## What the UI shows

- Sync status of the intent record (`GetItemStatusAsync`): `Pending` until uploaded (also while waiting for its
  target), `Synced` once the server decided, `Rejected` only for sync-level refusals.
- Execution state on the document: `pending`, `executed`, or `rejected` with a code the UI can explain.

## When to add library types

The plan's rule is to extract types only when a sample has two intents sharing them. The Tasks sample has two kinds
(`complete`, `rename`) sharing one document type and one handler, and nothing in that code is specific to Bsync beyond
what the engine already provides. No package types were added.
