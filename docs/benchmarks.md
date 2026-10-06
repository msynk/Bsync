# Benchmarks

Measurements of the [ADR-012](architecture/adr-012-packaging-and-support.md) workloads with BenchmarkDotNet
(`src/Tests/Bsync.Benchmarks`). They were recorded on one development machine in short runs. Treat them as orders
of magnitude and as a baseline for regressions, not as performance claims for any device.

```bash
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*LocalWrite*" "*LocalRead*" "*Merge*" --job short
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*Reconnect*"
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*InitialPull*"
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*ScalePull*"
```

## Recorded run (2026-09-28)

The run used BenchmarkDotNet 0.15.8 and .NET 10.0.12 (x64 RyuJIT) on Windows 10 22H2, with an Intel Core Ultra 7
255H. The storage device was not recorded. The documents were about 1 KiB. The authority was the in-memory one, called in-process, so
there was no network.

### Local UX: durable write and indexed read (target: p95 under 50 ms on a reference device)

These are ShortRun results (3 iterations), so the table gives means, not p95.

| Operation | Store | Mean | Allocated |
|---|---|---:|---:|
| `WriteAsync` (commit + queue) | in-memory | 9.3 µs | 14.8 KB |
| `WriteAsync` | SQLite, `synchronous=FULL` | 850 µs | 26.7 KB |
| `WriteAsync` | SQLite, `synchronous=NORMAL` | 258 µs | 31.2 KB |
| `GetAsync` by id, 10,000 documents | in-memory | 0.88 µs | 1.9 KB |
| `GetAsync` by id, 10,000 documents | SQLite | 29.6 µs | 7.5 KB |
| `QueryAsync`, all 10,000 documents | in-memory | 38.8 ms | 33.5 MB |
| `QueryAsync`, all 10,000 documents | SQLite | 57.2 ms | 43.4 MB |

### Reconnect: 10,000 queued writes converge in one `SyncAsync`

The push batch was 500 operations. Each run was a Monitoring run of 5 iterations with a fresh store.

| Store | Mean | Allocated |
|---|---:|---:|
| in-memory | 824 ms | 643 MB |
| SQLite, `synchronous=FULL` | 2.36 s | 900 MB |

Every run asserts that all 10,000 operations were accepted and nothing was left over.

### Functional: a new replica pulls 10,000 documents

The page size was 500. The run used the same job as the reconnect benchmark.

| Store | Mean | Allocated |
|---|---:|---:|
| in-memory | 243 ms | 239 MB |
| SQLite, `synchronous=FULL` | 1.00 s | 348 MB |

### Scale: a new replica pulls 100,000 documents

The page size was 1,000. Each run was a Monitoring run of 2 iterations. It was run twice: once alongside other
work on the machine, and once alone.

| Store | Mean (run alone) | Mean (other run) | Allocated |
|---|---:|---:|---:|
| in-memory | 6.8 s (±2.6 s between iterations) | 5.3 s | 2.43 GB |
| SQLite, `synchronous=FULL` | 14.3 s | 16.4 s | 3.57 GB |

Allocated is the total over the run, not peak memory. Time grows roughly linearly from 10,000 documents (about
10× for 10× the data).

### Conflict handling

| Operation | Mean | Allocated |
|---|---:|---:|
| `ThreeWayMerge.Merge` of a ~1 KiB document, disjoint edits | 12.6 µs | 22.8 KB |

## Storage and allocation after 0.3.0 (2026-10-05)

Measured with temporary probe tests on the same machine, not with BenchmarkDotNet; the probes were removed after
the run.

| Measurement | Before | After |
|---|---:|---:|
| SQLite file, 10,000 clean documents of ~1 KiB (D8: base not stored when equal to current) | 41,811,968 B (4,181 B/doc) | 14,450,688 B (1,445 B/doc) |
| Allocated per pushed document, in-memory store (D9) | 66.9 KB | 30.7 KB |
| Allocated per accepted write, in-memory server (D9) | 16.4 KB | 8.1 KB |
| Allocated per pushed document, SQLite store (D9) | 80.6 KB | 62.6 KB |

At that point the plan's target of under 16 KB per pushed document was not met; see below.

## Push allocations (task D9, 2026-10-06)

```bash
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- push-alloc memory
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- push-alloc sqlite-full
```

`push-alloc` runs the `Reconnect` workload (10,000 queued writes of ~1 KiB pushed to the in-process in-memory server in
batches of 500) once to warm up, then measures one more run with `GC.GetTotalAllocatedBytes`, and lists the types the
runtime's allocation ticks sampled most. The client and the in-process server are both counted. Same machine as above.

| Store | Before | After |
|---|---:|---:|
| In-memory | 31.0 KB | 14.5 KB |
| SQLite, `synchronous=FULL` | 63.1 KB | 47.2 KB |

What changed, all without giving up isolation (I02):

- The engine marks its own record transforms as pure (they never change their input) and says when it does not read
  the resulting record; the in-memory store then hands such a transform its stored record instead of a copy, and
  returns no copy of the result. These hints are internal to `Bsync`.
- An accepted document from the server is no longer copied by the engine before the store copies it, and the
  in-memory store keeps that answer instead of copying it again (nothing else references it).
- The server's operation fingerprint hashes the JSON bytes of `DocumentCloner.JsonFingerprint` directly instead of
  building the text (same digest), and JSON clones reuse a writer per thread.
- The SQLite store reuses its read and write commands within one update transaction, and deserializes equal JSON texts
  in a row (a pending payload is usually the current document) once.

Per pushed document the in-memory path now makes four copies of the document: one when reading the pending queue
(handed to `ReadyToPush`), one sent to the server, and the server's own two (its stored copy and the copy it answers
with). The SQLite store still reads and writes each state as JSON text twice per push; its target was not set.

## Local queries (task E1, 2026-10-06)

```bash
dotnet run -c Release --project src/Tests/Bsync.Benchmarks -- --filter "*LocalQuery*" --job short
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll peak <file.db> 50000
```

Same machine as above (Windows 10 22H2, Intel Core Ultra 7 255H, .NET 10.0.12), BenchmarkDotNet ShortRun. Both
queries go through the public `LocalSyncCollection.QueryAsync` on clean, synced documents of about 1 KiB. "Ordered"
is a 50-item page ordered by a `DateTimeOffset` field (`SyncQuery.Order`); "selective" is a 50-item page of
`Where = d => d.Category == 42`, which 1% of the documents match, in the default id order.

| Query | Documents | Store | Mean | P95 | Allocated |
|---|---:|---|---:|---:|---:|
| ordered page | 10,000 | in-memory | 30.2 ms | 32.2 ms | 23.7 MB |
| ordered page | 10,000 | SQLite, `FULL` | 57.9 ms | 60.6 ms | 43.7 MB |
| ordered page | 50,000 | in-memory | 313.4 ms | 317.8 ms | 119.1 MB |
| ordered page | 50,000 | SQLite, `FULL` | 337.1 ms | 365.3 ms | 218.5 MB |
| selective page | 10,000 | in-memory | 13.9 ms | 14.2 ms | 15.4 MB |
| selective page | 10,000 | SQLite, `FULL` | 23.5 ms | 25.6 ms | 21.8 MB |
| selective page | 50,000 | in-memory | 115.6 ms | 117.8 ms | 34.7 MB |
| selective page | 50,000 | SQLite, `FULL` | 20.3 ms | 22.3 ms | 21.8 MB |

Peak working set of a fresh process that opens a SQLite replica and reads one ordered page (cold, including JIT):
63.6 MiB at 10,000 documents (195 ms) and 154.9 MiB at 50,000 (509 ms); 29 MiB after opening the store.

The selective page stops once it has 50 matches, which here are within the first 5,000 documents in id order, so its
SQLite cost does not grow with the collection; a filter whose matches are rare or late scans everything. The
in-memory store's paged read sorts its keys on every page, which is why its selective page grows with the collection.

**Bar: p95 under 50 ms for a 50-item ordered page. Missed** with `SyncQuery.Order` on this desktop at 50,000 documents
by six to seven times, and at 10,000 documents with SQLite. Phones and tablets are slower. Hence task E2 (below).

### With declared indexes (task E2, 2026-10-06)

The same documents and machine, with an index on `Due` ([ADR-018](architecture/adr-018-local-secondary-indexes.md)).
"Indexed page" is the same 50 newest-`Due` documents as "ordered page" above; "range page" is the second 50-item page
of a 100-day range of `Due` (`Skip = 50`); "range count" counts that range (about 14% of the documents).

| Query | Documents | Store | Mean | P95 | Allocated |
|---|---:|---|---:|---:|---:|
| indexed page | 10,000 | in-memory | 0.20 ms | 0.22 ms | 485 KB |
| indexed page | 10,000 | SQLite, `FULL` | 0.35 ms | 0.37 ms | 896 KB |
| indexed page | 50,000 | in-memory | 0.32 ms | 0.35 ms | 486 KB |
| indexed page | 50,000 | SQLite, `FULL` | 0.45 ms | 0.47 ms | 897 KB |
| range page | 50,000 | in-memory | 0.22 ms | 0.22 ms | 486 KB |
| range page | 50,000 | SQLite, `FULL` | 0.50 ms | 0.51 ms | 898 KB |
| range count | 50,000 | in-memory | 0.15 ms | 0.16 ms | 1 KB |
| range count | 50,000 | SQLite, `FULL` | 0.42 ms | 0.44 ms | 7 KB |

**Bar met on this desktop** by about a hundred times, and the cost no longer grows with the collection. Two fixes
came from measuring: SQLite's planner had joined from the records (83 ms for the indexed page at 50,000) until the
query forced the index first, and counts read only the index once purges removed their rows (72 ms before). Not
measured: IndexedDB in browsers, phones and tablets.

Not measured for E1: a mid-range Android device and an iPad (none available), IndexedDB in any browser, and first
sync at 50,000 documents (100,000 is under "Scale" above).

## Authority throughput with concurrent sessions (task H, 2026-10-06)

```bash
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll throughput sqlserver "<connection to master>" 16 500
dotnet src/Tests/Bsync.Benchmarks/bin/Release/net10.0/Bsync.Benchmarks.dll throughput postgres "<connection to postgres>" 16 500
```

Each run creates a database, runs, and drops it. Sessions are engines on in-memory stores that call the authority
in-process (no HTTP), with push batches of 100 and pull pages of 500, documents of about 1 KiB. "Push" uploads every
session's documents concurrently; "pull" lets as many new replicas pull everything they can see concurrently. Same
machine as above, SQL Server 2025 LocalDB (Express) on the same disk, two runs each:

| Sessions × documents | Feeds | Push (operations/s) | Pull (changes/s) |
|---|---|---:|---:|
| 16 × 500 | one shared feed | 1,359 / 1,302 | 20,232 / 24,335 (16 replicas × 8,000) |
| 16 × 500 | one feed per session | 5,123 / 5,552 | 32,781 / 33,782 |
| 4 × 2,000 | one shared feed | 2,042 | 44,590 (4 replicas × 8,000) |
| 4 × 2,000 | one feed per session | 5,640 | 40,014 |

Writes to one feed are serialized by its feed lock (ADR-005), so a shared feed's push throughput falls as sessions
contend for it; separate feeds (tenants) commit in parallel. Pulls take no feed lock. LocalDB is a development
edition; a production SQL Server on separate storage will differ.

PostgreSQL 17.6 (portable Windows binaries, default configuration, same machine and disk):

| Sessions × documents | Feeds | Push (operations/s) | Pull (changes/s) |
|---|---|---:|---:|
| 16 × 500 | one shared feed | 813 | 26,795 (16 replicas × 8,000) |
| 16 × 500 | one feed per session | 10,101 | 41,528 |
| 4 × 2,000 | one shared feed | 1,502 | 45,473 (4 replicas × 8,000) |
| 4 × 2,000 | one feed per session | 5,162 | 44,839 |

The same pattern: one feed's writes are serialized (its row lock, ADR-005), separate feeds scale. One run each.

## Observations

- A durable SQLite write stays around a millisecond on this machine, far inside the 50 ms target. A
  reference *mobile* device has not been measured.
- `QueryAsync` materializes the whole collection. It takes tens of milliseconds at 10,000 documents, so large
  collections need paging or indexed queries (not implemented).
- Allocation per synced document is high: about 64 KB per document to push and 24 KB to pull in the recorded
  run. JSON cloning for isolation and fingerprinting dominates. 0.3.0 roughly halves the push cost with the
  in-memory store (see above).
- Not measured:
  - peak memory;
  - PostgreSQL throughput with concurrent sessions (the authority exists; no benchmark yet);
  - IndexedDB in browsers;
  - network latency.
