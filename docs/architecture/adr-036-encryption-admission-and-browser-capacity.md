# ADR-036: Encryption invocation admission and browser payload accounting

Status: implementation under qualification (capabilities B02/B03, 2026-10-24).

`BudgetedBlobEncryption` decorates the existing encryption boundary. Before every seal it reserves one invocation
through `IEncryptionUsageBudget`. Identity is SHA-256 over a fixed domain and the actual 256-bit key, so key aliases,
account names, key versions and encryption purposes cannot reset the counter. The decorator owns and clears a key copy
across the admission await. A reservation is never refunded, even after cancellation, crypto failure or process death.
Reads are unaffected. This adds no cryptographic format or primitive and does not satisfy independent crypto review.

`OrchestrationEncryptionUsageBudget` uses compare-and-write on existing durable metadata. The persisted ceiling can
be lowered but cannot be raised by another constructor. Unsupported/corrupt state fails closed. The host chooses a
reviewed limit, retains the ledger for the key's entire usable life and must prevent rollback to an older ledger. All
encryption paths using that key must share admission, including protected metadata. Do not encrypt the budget ledger
through its own decorator. A deployment sharing keys across devices must use a shared authority or allocate disjoint
durable per-device budgets; a per-account local counter alone cannot establish an aggregate multi-device bound.

The raw IndexedDB blob provider advances to schema 2, adding a capacity store. Payload puts/deletes and size deltas
commit in the same strict transaction. No separate reservation survives an aborted IndexedDB transaction. Existing
encrypted-v2 capacity admission continues using the shared physical capacity lock; its `GetStoredBytesAsync` now reads
one ledger record instead of every ciphertext payload. Partial append chooses its next index inside that transaction,
so concurrent callers cannot overwrite an admitted append. Completion refuses concurrent appends rather than deleting
their progress. Partial generation tokens also prevent a discard/reappend under identical keys from being mistaken for
the bytes previously verified; rename uses the same fence. Tokens are removed with the corresponding partial records.
Existing content verification still determines readiness.

Schema-1 upgrades retain bytes and rebuild the ledger in durable pages before admitting writes. Explicit
`ReconcileCapacityAsync` starts/resumes the same paged process after out-of-band changes. Writers fail with
`accounting-uncertain` while reconciliation is pending; readers remain available. Interrupted pages commit both the
cursor and accumulated accounting, or neither. Older schema-1 opens fail with VersionError. Close old processes/tabs
before upgrade; no library can revoke an already executing legacy transaction or a nonparticipating raw IndexedDB writer.

The budget counts stored payload bytes including partial/staging and retained ciphertext generations. Metadata,
indexes, JS copies of raw open-reader snapshots and origin-wide browser overhead are excluded. This is not an origin
quota guarantee. Arbitrary external insertion/replacement bypassing the provider requires explicit reconciliation;
external deletion can conservatively overcharge until reconciliation. Browser eviction remains detectable as missing
content through verification, never proof of freshness.

The 26 October implementation continuation upgrades blob storage to schema 3. Raw import/completion uses the existing
bounded SHA-256 implementation and 1 MiB interop slices. Verification builds an immutable Blob a partial chunk at a time;
publication checks the partial generation before atomically replacing the object. Newly stored Blob handles support
bounded range slices and remain immutable for open readers after replacement/deletion. Rename no longer reads every
payload with `getAll`. Existing ArrayBuffer objects remain readable and retain their historical materialization cost.

Blob persistence is probed in a separate transaction. Private WebKit configurations that refuse it retain ArrayBuffer
completion up to 16 MiB; larger completion explicitly refuses and retains its partial. There is no unbounded fallback.
Encrypted-v2 physical records remain within that limit. Transaction failure is observed concurrently with cursor work
so an aborted engine request cannot strand completion indefinitely. Blob backing storage, metadata and reader-held
browser storage remain outside managed/JS buffer accounting; no new physical-process peak claim is made.

Finite-budget measurements now include 1/16/17/256 MiB in all three engines with zero payload inventory cursor scans;
capacity admission performs one ledger read per appended chunk. `partialLength` reads keys and size records without
materializing partial payloads. Exact candidate identities and larger-case results are recorded in the
[hardening continuation](../review/capabilities-closure-2026-10-26.md). Cross-tab termination/fault coverage, independent
crypto review and physical-device gates remain distinct from these local measurements. The first three-engine
regression checks upgrade paging, concurrent append accounting, explicit reconciliation refusal/resume, deletion and
old-writer refusal. No new stable support claim follows from these checks alone.

The exact `.4` preview also passes two-tab last-capacity races in all three engines: only one distinct chunk is
admitted, and its full ciphertext byte count survives closing both tabs. This assumes the required exclusive format
upgrade has completed first; concurrent first-time upgrades remain explicitly refused. Chromium/Firefox pass finite
1 GiB workloads, while WebKit passes 256 MiB under its reported 1000 MiB origin quota.
