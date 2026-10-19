# Object-store qualification

This opt-in runner exercises `S3BlobStore` and `HttpBlobTransfer` against a selected S3-compatible service, with
Chromium, Firefox and WebKit CORS checks. It does not need SQL Server. Its in-process ticket authority is deliberately
synthetic: tenant authorization and SQL transaction tests remain in `Bsync.Tests.SqlServer`.

Use a **dedicated, already existing test bucket that has never enabled versioning**, with permissions to read bucket
versioning, list objects, and read/write/delete objects. The tool refuses versioned/suspended buckets because deleting
current keys would leave historical test versions. It creates a random `bsync-qualification/<run-id>/` prefix and
cleans only that prefix, including on failed checks. It never creates/deletes buckets or modifies their policies.
Incomplete cleanup is a failed check; the report retains the prefix for operator follow-up.

Configure these environment variables through your normal secret mechanism:

| Variable | Meaning |
|---|---|
| `BSYNC_OBJECTSTORE_ENDPOINT` | HTTPS S3 service endpoint, without credentials/query/fragment |
| `BSYNC_OBJECTSTORE_BUCKET` | Dedicated test bucket |
| `AWS_REGION` | Signing region for the bucket |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` | Test principal credentials |
| `AWS_SESSION_TOKEN` | Optional temporary-credential token |
| `BSYNC_OBJECTSTORE_ORIGIN_PORT` | Local browser origin port; default `58991` |

No credential values, presigned URLs, exception messages, browser traces or response bodies are written to the report.
Errors retain a static check code or exception type and HTTP status. The host, bucket, region and isolated test prefix
are recorded. Raw third-party diagnostic logging should remain disabled when running with real credentials.

The bucket CORS policy must allow origin `http://127.0.0.1:58991` (or the selected port), methods `PUT`, `GET`, `HEAD`,
request headers `Content-Type` and `Range`, and expose `Content-Range`. For example, have the bucket owner apply:

```json
{"CORSRules":[{"AllowedOrigins":["http://127.0.0.1:58991"],"AllowedMethods":["PUT","GET","HEAD"],
"AllowedHeaders":["Content-Type","Range"],"ExposeHeaders":["Content-Range"],"MaxAgeSeconds":300}]}
```

Browser failures are retained; a native HTTP success cannot substitute for CORS. See AWS's
[CORS configuration elements](https://docs.aws.amazon.com/AmazonS3/latest/userguide/ManageCorsUsing.html) and
[presigned URL expiration rules](https://docs.aws.amazon.com/AmazonS3/latest/userguide/using-presigned-url.html).

```powershell
dotnet build src/Tools/Bsync.ObjectStoreQualification -c Release -p:BsyncPackageVersion=0.4.0-gapclosure.20261019.1
# Install the three Playwright engines if this machine does not already have them.
powershell -File src/Tools/Bsync.ObjectStoreQualification/bin/Release/net10.0/playwright.ps1 install
dotnet src/Tools/Bsync.ObjectStoreQualification/bin/Release/net10.0/Bsync.ObjectStoreQualification.dll artifacts/objectstore-run-1
```

Omitting `BsyncPackageVersion` selects project references. The report records hashes of the loaded tool, core and server
assemblies; preserve the selected package archives and compare their DLL hashes when qualifying exact packages. Output
directories must be new. Every check, including cleanup, must pass for process exit 0 and `Passed=true`.

The checks cover a 6 MiB direct upload, acknowledgement loss after durable completion, replay without resending,
service enforcement of a two-second ticket expiry, a real `HttpBlobTransfer` refresh while resuming a two-MiB partial,
no bearer/cookies on direct requests, browser PUT/full-hash/range/header exposure in all three engines, and orphan
removal without deleting completed content. The expiry check waits four seconds and uses a fresh request; synchronize
the client/server clocks before qualification. The run has a ten-minute cancellation deadline and bounded HTTP/browser
operations. These are correctness checks, not throughput or mobile performance measurements.

`--emulator` permits only a loopback endpoint, including HTTP. It **always** sets `ExternalServiceQualified=false`.
An emulator that accepts expired signatures fails the expiry and refresh checks; those failures must not be ignored to
claim object-store support. Physical-device tests are deferred by the user as of 2026-10-19; that does not waive these
non-device service checks.

`--local-service` is a distinct mode for a real implementation running on loopback, permitting HTTP with ephemeral
test credentials. It reports `LocalServiceQualified` only when every check passes and always leaves
`ExternalServiceQualified=false`. This exercises server semantics without claiming cloud IAM, TLS or deployment policy.
The browser checks also reject persisted `aws-chunked` content encoding, which WebKit cannot decode as object content.

The [SeaweedFS runner](run-local-seaweed.py) starts a supplied, verified official Windows executable on loopback with
random credentials and a new data directory/bucket, configures CORS only on that generated bucket, runs qualification,
checks cleanup and stops the server. It requires `boto3` in the selected Python environment. It never downloads or installs
the server itself. SeaweedFS 4.48's official `windows_amd64.zip` archive used here has SHA-256
`fe90c04c0620ad1a1c756f86cd5e1443773f56a446688077a4bb5ec04c3cc874`.

```powershell
python src/Tools/Bsync.ObjectStoreQualification/run-local-seaweed.py <verified-weed.exe> artifacts/new-local-service-run
```

The sample sends a precomputed SHA-256 checksum and disables chunked upload encoding for verified files; payload signing
and service checksum validation remain enabled. The local service check is evidence for that exact implementation/version,
not certification of all S3-compatible products. Historical failed runs must remain alongside passing runs.

The optional [GitHub workflow](../../../.github/workflows/object-store.yml) packs unique local core/server packages,
builds this runner against them and retains packages/results. Configure the endpoint/bucket/region repository variables
and the three `BSYNC_OBJECTSTORE_*` credential secrets named in the workflow before dispatch. The workflow never publishes
packages. Its existence is not evidence that the service qualification has run.
