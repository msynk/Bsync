# Rebuildable local read models

Use a read model for derived application state that can be regenerated from authoritative local documents. Keep
ordinary editable data in the existing collections and pending-operation pipeline. `LocalReadModel` provides durable
staging, applied-row counts, complete-generation activation and immutable readers over `IOrchestrationStore`.

First capture a stable input. For a small dataset, the host can hold a gate used by every source writer and replication
applier, copy the necessary values and source epoch/version into immutable application records, then release the gate.
For larger datasets, keep a real source snapshot transaction open while enumerating. Live `Skip`/`Take` collection
pages are not a replacement for either boundary. If the host cannot establish one, expose the projection as provisional
application data instead of passing it to this complete-generation API.

```csharp
// metadata belongs to exactly one AccountScope. captured was copied under the host's source write gate.
var model = new LocalReadModel(metadata, "work-summary");
async IAsyncEnumerable<ReadModelRow> Project()
{
    foreach (var item in captured.Items)
        yield return new(item.Id, JsonSerializer.Serialize(item, AppJson.Default.Summary));
    await Task.CompletedTask;
}
var activated = await model.RebuildAsync("summary-v2", captured.EpochAndVersion,
    Project(), TimeSpan.FromSeconds(20), cancellationToken);
var status = await model.GetStatusAsync(cancellationToken);
var snapshot = await model.OpenActiveAsync(cancellationToken);
if (snapshot is not null)
{
    // Always interpret the row payload with snapshot.Generation.SchemaVersion.
    // Compare SourceVersion to a reliable current source version, or label freshness unknown.
    var page = await snapshot.ReadPageAsync(100, cancellationToken: cancellationToken);
    // Pass page.Continuation back to this same snapshot to continue; do not open a different generation mid-query.
}
```

`false` activation means another builder owns execution, the budget expired, or ownership was lost. Host cancellation
throws. In every case the prior active generation remains readable; durable `BuildingGeneration`/`AppliedRows` report
unfinished work. Restart with a newly captured complete input. Do not claim that a staging count is a source cursor.
The applied count and its rows commit atomically, but the source and destination do not share a transaction.

Rebuild on startup/resume even if no observer fired. Full replacement removes absent source rows and handles source
epoch resets, removals/regrants and schema changes. Select explicitly whether a conflict contributes the local current
value, server base, both, or a separate conflict indicator. Read-model output does not resolve a conflict.

The legacy constructor retains retired/interrupted generations until account deletion. For bounded retention, opt into
`new LocalReadModel(metadata, name, account, generationLocks, retention: options)` with a physical account lock provider
shared by every process/tab. This uses a separate v2 namespace. Dispose snapshots with `await using`, continue the
bounded `CollectRetiredAsync` cursor, and periodically restart scans to revisit reader-protected generations. Default
ceilings (64 generations, 1 million rows, 128 MiB serialized UTF-16 row payload) refuse rebuild rather than evict readers.
Database/index overhead is additional. `GetStorageStatusAsync` reports retained/staging rows and generations.
Native process-death coverage holds an old snapshot in a separate process, verifies that collection preserves its rows,
then kills that process and reclaims the retired generation in bounded pages. A suspended live reader is never expired
by age. This test does not substitute for browser or physical-device lifecycle qualification.

Old snapshots can still contain removed/previously authorized data; stop readers and delete derived storage when local
policy requires erasure. SQLite orchestration can use SQLCipher. Browser payload protection is a separate opt-in
`ProtectedOrchestrationStore`; encrypted blobs alone do not encrypt read models. See [ADR-027](../architecture/adr-027-account-ownership-and-protected-metadata.md).

The package-only web consumer includes an executable capture/build/read example. See
[ADR-023](../architecture/adr-023-local-read-model-generations.md) for boundaries and tests.
