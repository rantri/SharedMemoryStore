# Packaging

SharedMemoryStore ships independently versioned NuGet, CMake, and Python
artifacts. They share layout `1.2` and resource naming `1`; matching package
versions are not required. The managed runtime package is built from
[`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj)
and targets `net10.0`. Runtime dependencies are limited to the .NET BCL.

## Managed NuGet Metadata

| Field | Value |
|-------|-------|
| `PackageId` | `SharedMemoryStore` |
| `Version` | `1.0.2` |
| `TargetFramework` | `net10.0` |
| `Description` | `A bounded named shared-memory key-value store for opaque binary values.` |
| `PackageTags` | `shared-memory;memory-mapped-file;zero-copy;linux;windows;docker;library` |
| `PackageLicenseExpression` | `MIT` |
| `PackageProjectUrl` | `https://github.com/rantri/SharedMemoryStore` |
| `PackageReadmeFile` | `README.md` |
| `PackageReleaseNotes` | `NuGet SharedMemoryStore 1.0.2 preserves Linux, Windows, and same-host Docker support, corrects Linux layout-mismatch reporting, and rejects unsupported managed processes early while preserving the public API, status values, BCL-only runtime surface, layout 1.2, and resource naming 1. Repository source adds independently versioned native C++ and Python 0.1.0 sibling distributions using C ABI 1.0, validated with .NET on Windows and Linux; v1.0.2 neither includes them in NuGet nor publishes them to PyPI or a native package registry.` |
| `RepositoryType` | `git` |
| `RepositoryUrl` | `https://github.com/rantri/SharedMemoryStore` |
| `SymbolPackageFormat` | `snupkg` |

The package project packs the root [README.md](../README.md) at the package
root so NuGet consumers see the same package purpose, status, first-use
workflow, support path, security path, and contract links as repository
visitors.

## Native CMake Distribution

The root CMake project is version `0.1.0`, requires CMake 3.20 or newer and a
C++20 compiler, and builds `shared_memory_store` as a shared library. Optional
tests, samples, and static library targets are controlled by
`SMS_BUILD_TESTS`, `SMS_BUILD_SAMPLES`, and `SMS_BUILD_STATIC`.

Installation exports `SharedMemoryStore::SharedMemoryStore` and these package
identities:

- native package `0.1.0`.
- C ABI `1.0`.
- mapped layout `1.2`.
- resource naming `1`.

The installed development artifact includes the C header
`shared_memory_store/c_api.h` and C++ header
`shared_memory_store/store.hpp`. The C ABI uses fixed-width integers,
versioned structures, caller-owned byte ranges, and opaque store, lease, and
reservation handles. The C++ header adds move-only RAII wrappers.

Build and validate the install plus clean `find_package` consumer with:

```powershell
pwsh ./scripts/validate-native.ps1 -Configuration Release
```

## Python Wheel Distribution

The root [`pyproject.toml`](../pyproject.toml) defines
`shared-memory-store` `0.1.0` for Python 3.10 or newer. `scikit-build-core` is a
build dependency only. A platform wheel contains the Python modules and the
native shared library directly beside them; installing a completed wheel does
not require a compiler or third-party Python runtime package.

The loader uses standard-library `ctypes`, loads only
`shared_memory_store.dll` or `libshared_memory_store.so` from the package, and
validates C ABI major version, layout `1.2`, resource naming `1`, and canonical
record sizes. It deliberately does not search the current directory, `PATH`,
or a system library path.

Build a wheel and inspect it through a clean environment:

```powershell
python -m pip install build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
```

Use `bin/python` on Linux. Source distributions include the root CMake project,
native sources and headers, Python sources, compatibility metadata, README, and
license so their wheel build has the complete native input.

The reproducible repository gate builds and inspects both the wheel and source
distribution, rebuilds a wheel from the source archive, and runs the installed
sample from an unrelated directory:

```powershell
pwsh ./scripts/validate-python.ps1 -Configuration Release
```

## Compatibility Identities

| Distribution | Version | ABI requirement | Creates/reads | Resource naming |
|--------------|---------|-----------------|---------------|-----------------|
| NuGet `SharedMemoryStore` | `1.0.2` | Not applicable | layout `1.2` | `1` |
| CMake `SharedMemoryStore` | `0.1.0` | provides C ABI `1.0` | layout `1.2` | `1` |
| Python `shared-memory-store` | `0.1.0` | requires C ABI `1.0` | layout `1.2` | `1` |

The authoritative machine-readable declaration is
[`protocol/compatibility.json`](../protocol/compatibility.json). Release
evidence for a target OS and ordered runtime pair must still be recorded; a
metadata entry alone is not proof that a validation run completed.

## Build and Pack the Managed Package

```powershell
dotnet restore
dotnet build SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Packing produces both `SharedMemoryStore.<version>.nupkg` and the portable-symbol
package `SharedMemoryStore.<version>.snupkg`. NuGet.org publishes the symbol
package to its symbol server alongside the primary package.

## Automated Publication

The managed jobs in [CI](../.github/workflows/ci.yml) validate Linux and Windows
on pull requests and pushes to `main`. The manually triggered
[release workflow](../.github/workflows/release.yml) performs the full release
validation, including Docker, verifies that the version is unused, creates the
package and symbols, publishes them to NuGet.org with trusted publishing, and
creates the matching GitHub release and `v<version>` tag.

That workflow publishes the managed NuGet artifact. The repository currently
defines native install artifacts and Python wheel builds, but registry
publication for a native archive or Python package is not implied until a
separate release path and its credentials are reviewed.

The one-time trusted-publishing policy and the exact release procedure are
documented in [Release preparation](releases.md).

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

The corresponding native clean consumer is part of
`scripts/validate-native.ps1`. Python clean consumption means installing the
built wheel into a fresh virtual environment, confirming the adjacent native
artifact exists, and running tests or the sample from a directory that cannot
import repository sources.

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
- distribution and registry being released.
- C ABI, layout, and resource-naming compatibility identities.
- compatibility impact.
- public API or behavior changes.
- documentation-only changes.
- known limitations.
- validated platform scope.
- migration notes for breaking changes.

Documentation-only changes are patch-level for an already published
distribution unless they change a public behavior, ABI, layout, lifecycle,
support, security, or compatibility promise.

## License and Source Metadata

The package license expression is `MIT` and must match the
[LICENSE](../LICENSE). The project declares `RepositoryType` as `git`. Add a
public repository URL only when maintainers have finalized the hosted repository
URL for publication.
