# ADR-037: Maintain versioned native background integration recipes

Status: implementation/build qualification in progress (B04, 2026-10-24).

Keep the existing NativeBackground runtime and complete Android/Apple hosts as maintained integration recipes,
versioned by immutable source commit and host artifact hash. Do not introduce a second scheduler or add platform SDK
dependencies to portable Bsync. Consumers own OS registration, entitlements, signing, credential acquisition and key
accessibility policy. A recipe revision is supported on a platform only after its complete platform-conditional host
build and named physical-device lifecycle matrix pass.

Foreground recovery remains mandatory. Native workers create fresh account scopes, join admitted work before scope
disposal, preserve pending work when keys/credentials are unavailable and reconcile OS tasks with account deletion.
The existing account and transfer identities remain the authority. A late callback must pass the same deletion and
ownership checks as foreground work. Scheduling is opportunistic and promises no delivery deadline.

The first Windows full-host build failed XA5300 without a configured Android SDK. The continuation downloaded and
SHA-256-verified Microsoft OpenJDK 21.0.8 into isolated artifacts and reran the Android dependency installer with that
JDK. Android platform/build/platform-tools/command-line downloads still returned HTTP 404, so the complete host build
remains unqualified. Original evidence is under `artifacts/capabilities/`; JDK identity and the second installer log/binlog
are under `artifacts/capabilities-closure/`. A failed dependency install is not a platform compilation pass.
Apple signing/build infrastructure and physical Android/Apple/iPadOS devices remain external gates. A portable
net10.0 build does not qualify platform code, and a successful APK build would not qualify locked-device behavior.
