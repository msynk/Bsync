# ADR-019: Release provenance and audited package consumers

- **Status:** Accepted (2026-10-16)
- **Scope:** Improvement plan R0; no runtime API, wire or store-format change.

## Context

The six NuGet 0.2.0 packages identify commit `5404785`, whose version prefix is 0.1.0 and whose
source predates the SQL Server project and other release additions. A repository commit field alone
therefore does not establish that the published binaries were built from that clean commit.
The original build inputs cannot be recovered from that field. See the
[baseline inventory](../review/release-baseline-2026-10-16.md).

## Decision

Release candidates use a clean checkout at `v<VersionPrefix>`, with a dated changelog heading
`## <version> - YYYY-MM-DD`. The release tool checks the tag's commit against HEAD before building
and again when recording artifacts. Tags must be protected against movement in repository settings;
the tool cannot enforce remote tag protection. It does not create or move tags.

The release-candidate workflow packs all six libraries and symbol packages, refuses missing or
unexpected artifacts, and verifies each nuspec's ID, version, repository URL and full commit.
It records SHA-256 checksums, the SDK/runtime report, public API baseline hashes and SDK-generated
Source Link maps in a versioned JSON manifest. The separate `Bsync.PackageVerifier` reads the distributed PDB's
embedded Source Link record, validates its canonical repository/commit and document coverage, and matches its content
ID to the DLL's CodeView entry, and verifies embedded-source checksums. Release mode additionally downloads mapped
non-embedded source and verifies each exact checksum; a successful clean tagged run remains a qualification gate. The build canonicalizes
the repository's old `BlazorSync` alias, without modifying a developer's Git remotes.

CI and nightly previews record the same provenance with unique prerelease suffixes. A development
manifest explicitly records a dirty tree; it is not release evidence. Stable manifests require release
preflight. Generated feeds and caches are ignored, so release checkouts remain clean after building.

NuGet auditing covers direct and transitive dependencies. Vulnerability findings and audit service
failures are errors, including for package consumers. SQL Server explicitly carries Pkcs 9.0.21 as
a public dependency minimum, so the remediation survives packing. Consumer projects use only
PackageReference, with source mapping that routes Bsync packages exclusively to the produced feed.
The web consumer also exercises central package management and restores all six packages.

Release candidates are build artifacts, not permission to publish. Provider, browser and physical
device evidence, ten consecutive qualification runs, native distribution maintenance and published
0.2.0 migration fixtures remain independent release gates. A green candidate job alone does not
establish those support claims.

## Consequences

Python 3.11 or later is required for the release tooling; it uses only the standard library.
Failure tests create disposable Git repositories and synthetic package archives. Normal .NET
builds do not require Python. No runtime dependencies, public API baselines or persistence formats
change. No existing 0.2.0 artifact is overwritten or retroactively claimed reproducible.
