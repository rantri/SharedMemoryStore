# Package Documentation Contract

## Package Metadata

Package-facing documentation must align with
`src/SharedMemoryStore/SharedMemoryStore.csproj`.

Current required metadata:

- `PackageId`: `SharedMemoryStore`
- `Version`: `0.1.0`
- `TargetFramework`: `net10.0`
- `Description`: bounded named shared-memory key-value store for opaque binary
  values.
- `PackageTags`: shared memory, memory mapped files, zero-copy, library.
- `PackageLicenseExpression`: `MIT`
- `PackageReadmeFile`: `README.md`
- `PackageReleaseNotes`: initial public package contract for publish, acquire,
  release, remove, diagnostics, and reuse.

If implementation changes package metadata before documentation implementation
is complete, docs must update to the new metadata in the same change.

## Package README Requirements

The README packaged with the NuGet package must include:

- package purpose and intended audience.
- current maturity/status.
- minimum target framework and SDK expectations.
- installation command or local package consumption path.
- minimal create/open, publish, acquire, release, remove, and cleanup example.
- deterministic status/error handling summary.
- links to lifecycle, diagnostics, errors, packaging, support, security, and
  source repository documentation.
- clear statement that C++ and Python bindings are future work unless a later
  feature delivers them.

## Release Notes Requirements

Package release notes and `CHANGELOG.md` must agree on:

- package version.
- compatibility impact.
- public API or behavior changes.
- documentation-only changes.
- known limitations and validated platform scope.
- migration notes for breaking changes when applicable.

For prerelease versions, release notes must not imply stable API guarantees
beyond the documented contract status.

## Clean Consumer Validation

Package documentation is valid only when a clean consumer can follow documented
commands to:

1. install the package from the configured package source or local artifact.
2. create/open a named store.
3. publish a value with optional descriptor bytes.
4. acquire and read a lease.
5. release the lease.
6. remove the value.
7. publish again to show reuse.
8. dispose the store.

The existing validation script `scripts/validate-package-consumption.ps1` is the
baseline package consumption check. Documentation must reference it as a
maintainer validation command, not as a requirement for ordinary consumers.

## Compatibility Rules

- Documentation-only changes are patch-level for an already published package
  unless they change a public compatibility promise.
- Any documentation change that redefines public API behavior, layout behavior,
  error outcomes, lifecycle ownership, or support guarantees requires release
  notes and semantic-version review.
- Package documentation must state that runtime dependencies are limited to the
  .NET BCL unless a later feature adds and justifies dependencies.
