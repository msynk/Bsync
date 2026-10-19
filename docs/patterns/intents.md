# Pattern: immutable intents (actions executed once)

Most offline edits are state: "the task's title is now X". Some are actions: "complete this task", "approve this
request", "send this invoice". Replaying state twice is harmless; replaying an action twice is not, and two actions
must never merge into one. This page shows how to model actions on top of the ordinary engine. The complete,
tested execution example is in the Tasks sample (task F3):

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
3. **Keep command identity independently of receipts.** If a response is lost, the same transport operation replays
   its receipt. The sample additionally commits `dbo.IntentExecutions` with the domain mutation/result, so a new
   operation ID cannot repeat a command after its result document disappears. Results are readable for 90 days;
   identity/fingerprint tombstones remain indefinitely and expired results refuse re-execution. Do not purge those
   tombstones with transport receipts. Existing deployments need an explicit historical backfill/replay policy.
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

## Reusable request contracts

`IntentRequest` holds immutable command/kind/schema/target/revision fields, a bounded JSON payload snapshot and copied
prerequisite references. `IntentDefinition<TPayload>` registers one known kind/version with source-generated JSON
metadata. It refuses other definitions before decoding; it never activates a type from an incoming string. Persist
the request inside an ordinary `ISyncEntity` with `Id = Request.CommandId`, then save that document once. The existing
replica pending operations provide delivery; there is no additional intent outbox. Prerequisites are application
references, not a claim of cross-collection atomicity or authorization.

```csharp
public sealed record AcknowledgePayload(string Revision);
[JsonSerializable(typeof(AcknowledgePayload))]
[JsonSerializable(typeof(IntentRequest))]
public partial class ActionJson : JsonSerializerContext;

var acknowledge = new IntentDefinition<AcknowledgePayload>(
    "acknowledge-revision", 1, ActionJson.Default.AcknowledgePayload);
var request = acknowledge.Create(Guid.CreateVersion7().ToString(), "document-7", "r5", new("r5"));
// Embed request in your replicated action document; await collection.SaveAsync(document).
// Server: match a registered definition, derive actor/scope from authenticated context,
// then recheck permissions and the target revision inside the execution transaction.
```

`IntentExecutionResult` is a separate server-owned result contract. Publish it in a server-owned document or canonical
envelope; reject/overwrite submitted result fields. `IntentStatus` keeps local persistence, ordinary transport status
and execution observations independent. A null execution result means unknown; `Synced` with a rejected result is a
valid business refusal. A queued result is not successful execution. Canceling a client wait changes none of these
durable facts: model cancellation as another command referencing the original command, with its own server outcome.

The Tasks sample's `TaskIntentDefinitions` adapts completion/rename to these registrations while preserving its
existing wire shape and ledger fingerprint. Core tests also persist completion and document-revision acknowledgment
requests across SQLite restart. The acknowledgment example does not implement a universal document authorization or
execution service. Payloads are JSON objects up to 64 KiB/depth 32, with at most 64 distinct prerequisite references;
requests and results have independent application schema versions. Register old versions explicitly during migration.

Command execution remains an application recipe. `IntentLedgerTests` verifies concurrent command retries, rollback, independent
transport IDs, authenticated actor binding, forged result replacement and result expiration on real SQL Server.
See [ADR-022](../architecture/adr-022-workflow-publication.md) for limits, lock ordering and external-effect semantics.
Use the [recoverable workflow helper](recoverable-work.md) for local multi-step progress; external effects still need
an application-transaction outbox and receiver idempotency. The request contracts do not provide execution deduplication.

## External-effect outbox recipe

[`ExternalEffectOutbox`](../../src/Samples/Bsync.Samples.Tasks.Server/ExternalEffectOutbox.cs) is an optional, separate
SQL Server recipe. Call `EnqueueAsync` in the same transaction as domain changes, command-ledger identity and the
published queued result. It retains an immutable payload under a hash of length-framed scope/command identities;
case/trailing-space differences do not collapse under SQL collation. Derive scope from authenticated context and
include your deployment/integration namespace so independent environments cannot share receiver idempotency keys.
The recipe does not replace the command ledger's actor binding or domain authorization.

`DrainAsync` claims a bounded batch, closes its database connection, then calls the registered receiver with a stable
idempotency key. The receiver must retain that key for the full replay horizon. Without receiver deduplication, delivery
is explicitly at least once and may repeat external effects. No network call belongs in `publishResult`: that callback
publishes the final result using the supplied connection/transaction. It must acquire feed locks before outbox/domain
locks. Result publication and claim acknowledgement commit together; an expired/stolen claim rolls them both back.
Notify listeners only after `DrainAsync` returns committed outcomes; lost hints remain harmless.

The registered sender must revalidate current permissions and dependencies before executing delayed work. Resolve
the actor from trusted command-ledger data; a queued payload is not an authorization grant and submitted actor fields
are not evidence of identity. Return a final business refusal when authority has been revoked.

Claims last 30 seconds and receiver cancellation is cooperative with a 20-second budget. An uncooperative receiver
can outlive its claim and overlap a retry, so fencing alone cannot prevent duplicate external effects. Failures retry
after 30 seconds; five failed responses/exceptions quarantine the request while unrelated requests continue. A host
repair can explicitly requeue a quarantined row by resetting its state/attempts/retry time without changing identity or
payload. Cancellation retains work; it cannot recall an accepted effect. Completed/rejected identities remain retained.
Do not delete them or shorten receiver retention without an enforced replay horizon and explicit recreation policy.

The example table is installed separately with `InitializeAsync`; the Tasks server does not automatically start a
dispatcher. Use an explicit production migration and register a host worker plus application-specific result publisher.
Tests cover enqueue rollback, immutable identity, a lost receiver reply, a failure before result commit, competing and
expired claims, distinct case-sensitive identities and poison isolation. They use a simulated idempotent receiver;
qualification of a particular external service remains the consuming application's responsibility.

### Receivers with different retry guarantees

The new `DrainAsync(IExternalEffectReceiver, ...)` overload uses
[`ExternalEffectDelivery`](../../src/Samples/Bsync.Samples.Tasks.Server/ExternalEffectReceiver.cs). Declare deduplication
and status-lookup capabilities explicitly. The existing delegate overload retains its caller-supplied deduplication
contract. The capability-aware overload persists the attempt before sending and applies this policy on replay:

| Receiver observation | Next action |
|---|---|
| Confirmed success/rejection | Publish that canonical result and acknowledge under the claim fence |
| Pending | Preserve and retry lookup later |
| Definitive terminal non-execution | A new send is permissible after current authorization |
| Unknown, with deduplication | Retry under the same immutable receiver key |
| Unknown, without deduplication | `ReconciliationRequired`; no automatic duplicate send |
| Credentials unavailable | Preserve queued work without consuming the poison-attempt budget |

`AuthorizeAsync` acquires current credentials and revalidates dependencies at dispatch time. It must distinguish a
temporary inability to authorize from a final domain rejection supplied through the adapter's result policy. A send
exception or cancellation after acceptance does not prove non-execution. Fenced database acknowledgement does not
provide receiver deduplication. Canceling a client wait cannot recall an accepted external operation.

`ExternalReceiverConformanceTests` supplies deduplicating, lookup-only and neither-capability receivers and verifies
lost-reply behavior. Existing SQL Server intent/publication tests continue to cover atomic domain/ledger/result work,
rollback, retained replay identity, audience removal and dropped hints. Use the [file-service bridge](external-file-bridge.md)
for content integration; permissions and external receiver implementations remain application-owned.
