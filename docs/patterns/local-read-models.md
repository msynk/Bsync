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

Current retention is conservative: retired and interrupted generations remain until account deletion. Set a storage
budget and rebuild frequency suitable for the dataset. Do not repeatedly rebuild large datasets without a host retention
strategy. Old snapshots can still contain removed/previously authorized data; stop readers and delete derived storage
when local policy requires erasure. SQLite orchestration can use SQLCipher; IndexedDB orchestration is not encrypted.

The package-only web consumer includes an executable capture/build/read example. See
[ADR-023](../architecture/adr-023-local-read-model-generations.md) for boundaries and tests.
