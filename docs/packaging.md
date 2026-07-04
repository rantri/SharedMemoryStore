# Packaging

The runtime package is built from
[`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj)
and targets `net10.0`. Runtime dependencies are limited to the .NET BCL.

## Current Package Metadata

| Field | Value |
|-------|-------|
| `PackageId` | `SharedMemoryStore` |
| `Version` | `1.0.0` |
| `TargetFramework` | `net10.0` |
| `Description` | `A bounded named shared-memory key-value store for opaque binary values.` |
| `PackageTags` | `shared-memory;memory-mapped-file;zero-copy;linux;windows;docker;library` |
| `PackageLicenseExpression` | `MIT` |
| `PackageReadmeFile` | `README.md` |
| `PackageReleaseNotes` | `Linux, Windows, and same-host Docker support: adds platform resource adapters, Docker validation, portable development workflows, and updated platform documentation while preserving the 1.0.0 public API contract.` |
| `RepositoryType` | `git` |

The package project packs the root [README.md](../README.md) at the package
root so NuGet consumers see the same package purpose, status, first-use
workflow, support path, security path, and contract links as repository
visitors.

## Build and Pack

```powershell
dotnet restore
dotnet build SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

## Clean Consumer Validation

```powershell
scripts/validate-package-consumption.ps1
```

The clean consumer validation packs the local project, creates a clean
`net10.0` console application, installs `SharedMemoryStore` from the local
package source, and exercises the documented first-use path plus advanced
package-surface checks: publish/acquire/release/remove, direct reservation
ingest, segmented publish, recovery status paths, and post-disposal status.
It is expected to run with `pwsh` on Linux and Windows.

This command is a maintainer validation path, not a requirement for ordinary
package users.

## Package README Alignment

The package-facing README is the repository [README.md](../README.md). Keep it
aligned with:

- [Getting started](getting-started.md) for first-use package commands.
- [Samples](samples.md) for runnable sample commands.
- [Support](../SUPPORT.md) and [Security](../SECURITY.md) for reporting paths.
- [Release preparation](releases.md) for release readiness.
- [CHANGELOG.md](../CHANGELOG.md) for package history and compatibility impact.

## Release Notes Alignment

`PackageReleaseNotes`, [CHANGELOG.md](../CHANGELOG.md), and
[Release preparation](releases.md) must agree on:

- package version.
- compatibility impact.
- public API or behavior changes.
- documentation-only changes.
- known limitations.
- validated platform scope.
- migration notes for breaking changes.

Documentation-only changes are patch-level for an already published package
unless they change a public behavior, layout, lifecycle, support, security, or
compatibility promise.

## License and Source Metadata

The package license expression is `MIT` and must match the
[LICENSE](../LICENSE). The project declares `RepositoryType` as `git`. Add a
public repository URL only when maintainers have finalized the hosted repository
URL for publication.
