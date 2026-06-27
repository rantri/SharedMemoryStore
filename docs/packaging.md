# Packaging

The runtime package is built from `src/SharedMemoryStore/SharedMemoryStore.csproj`
and targets `net10.0`. Runtime dependencies are limited to the .NET BCL. This
feature does not add runtime dependencies or change runtime behavior.

Current package metadata:

| Field | Value |
|-------|-------|
| `PackageId` | `SharedMemoryStore` |
| `Version` | `0.1.0` |
| `TargetFramework` | `net10.0` |
| `Description` | `A bounded named shared-memory key-value store for opaque binary values.` |
| `PackageTags` | `shared-memory;memory-mapped-file;zero-copy;library` |
| `PackageLicenseExpression` | `MIT` |
| `PackageReadmeFile` | `README.md` |
| `PackageReleaseNotes` | `Initial public package contract for publish, acquire, release, remove, diagnostics, and reuse.` |
| `RepositoryType` | `git` |

The package project packs the root [readme](../README.md) at the package root so
NuGet consumers see the same package purpose, status, first-use workflow,
support path, security path, and contract links as repository visitors.

## Build and Pack

```powershell
dotnet restore
dotnet build -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

## Clean Consumer Validation

```powershell
scripts/validate-package-consumption.ps1
```

The clean consumer validation packs the local project, creates a clean console
application, installs `SharedMemoryStore` from the local package source, and
exercises create/open, publish, acquire, release, remove, reuse, and dispose.
This command is a maintainer validation path, not a requirement for ordinary
package users.

## Release Notes

`PackageReleaseNotes` and [CHANGELOG.md](../CHANGELOG.md) must agree on package
version, compatibility impact, public API or behavior changes, documentation-only
changes, known limitations, and validated platform scope. See
[Release preparation](releases.md) for the complete maintainer checklist.

Documentation-only changes are patch-level for an already published package
unless they change a public behavior, layout, lifecycle, support, security, or
compatibility promise.

## License and Source Metadata

The package license expression is `MIT` and must match the [license file](../LICENSE).
The project declares `RepositoryType` as `git`. A public repository URL should be
added only when maintainers have finalized the hosted repository URL for
publication.
