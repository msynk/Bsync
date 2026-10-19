# Account maintenance

Use `AccountMaintenance` for short exclusive maintenance windows after closing foreground/background work scopes.
It opens fresh registered resources, yields to host-reported urgent work, and checkpoints replay-safe pages. Every
participant must use the same account composition and physical lock namespace outside deletable storage.

This example assumes `packages`, `transfers` and `cache` were opened for `account`, and that their stores are registered
with the composition. Resource factories can instead construct them inside each maintenance scope. Do not reuse a
manager belonging to another account or dispose the scope inside a callback.

```csharp
var runner = new AccountMaintenance(accounts, externalCatalog, timeProvider);
AccountMaintenanceStep[] steps =
[
    new("package-pins", "1", (scope, page, ct) => packages.ReconcilePinsAsync(scope, page, ct)),
    new("transfer-pins", "1", (scope, page, ct) => transfers.ReconcilePinsAsync(scope, page, ct)),
    new("cache-owners", "1", async (scope, page, ct) =>
        (await cache.ReconcileBuiltInOwnersAsync(scope, page, ct)).Progress),
];
var result = await runner.MaintainAsync(account, "cache-maintenance-v1", steps,
    TimeSpan.FromSeconds(2), hasUrgentWork,
    maximumItems: 64, maximumBytes: 64L * 1024 * 1024,
    dryRun: false, cancellationToken: stopping);
```

`hasUrgentWork` receives the open scope and cancellation token; consult durable user work, not just notifications.
`Busy` means a live account user owns resources. `UrgentWork` means the predicate requested deferral. `RemainingWork`
means a later bounded invocation can resume. `RequiresLargerBudget` means the next indivisible unit needs a larger
allowance; raising a time budget alone will not help. `BudgetExpired` preserves replayable work. Caller cancellation
throws. Do not run a tight retry loop for any deferred outcome.

Byte charges in these steps describe logical reservations/expected sizes, including protected content, not freed
disk space. A large manifest/job/cache record may need an allowance exceeding its entire declared size. Metadata
lookups are bounded separately (16 records per page, existing manifest/owner count limits). No content is hashed.

Use `dryRun: true` to inspect the current page without changing pins or the checkpoint. Incompatible step versions/order
require a new plan name. If a page's mutation succeeds but checkpointing fails, the same page will be repeated; custom
steps must be idempotent and honor item/byte/dry-run limits before mutations. Do not register an unbounded legacy API
or a network business operation as a maintenance step.

Monitor `bsync.maintenance.runs{bsync.outcome}` on the `Bsync` meter. It separates `Canceled`, `BudgetExpired`, `Busy`,
`UrgentWork`, `RemainingWork`, `RequiresLargerBudget`, `Completed` and `Failed`, without account/plan/cursor tags.
The [design decision](../architecture/adr-029-account-maintenance.md) documents progress, limits and compatibility.
