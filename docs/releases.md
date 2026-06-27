# Release Preparation

This guide is the maintainer checklist for preparing a package release. It is
written for the current prerelease `0.1.0` package and should be updated when
package metadata, public API behavior, compatibility scope, support policy, or
security reporting changes.

## Package Metadata

Verify `src/SharedMemoryStore/SharedMemoryStore.csproj` before publishing:

- `PackageId` is `SharedMemoryStore`.
- `Version` matches the release being prepared.
- `TargetFramework` is `net10.0` unless a feature plan changes the baseline.
- `Description` still describes a bounded named shared-memory key-value store
  for opaque binary values.
- `PackageTags` include shared memory, memory mapped files, zero-copy, and
  library terms.
- `PackageLicenseExpression` is `MIT` and matches the [license file](../LICENSE).
- `PackageReadmeFile` is `README.md` and the project packs the root README at
  the package root.
- `PackageReleaseNotes` matches the release entry in [CHANGELOG.md](../CHANGELOG.md).
- `RepositoryType` remains `git`. Add an owner-approved repository URL before a
  public package publication if the repository host is finalized.

## Release Notes and Changelog

Update [CHANGELOG.md](../CHANGELOG.md) in reverse chronological order. Each
entry should identify:

- package version.
- public API or behavior impact.
- package metadata impact.
- documentation-only changes.
- compatibility impact.
- validated platform scope.
- known limitations.
- migration notes for breaking changes.

For this prerelease line, do not imply stable `1.0.0` compatibility. Public API,
layout, lifecycle, error, or support-policy changes still require explicit
semantic-version review.

## Compatibility Review

Before release, compare public docs with these contracts:

- [Public API contract](../specs/001-frame-memory-store/contracts/public-api.md)
- [Error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [Shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)

Confirm that the docs do not claim current C++ or Python bindings, broad
cross-platform support, unmeasured hardware performance, or application-specific
frame parsing by the core store.

## Support and Security

- Review [SUPPORT.md](../SUPPORT.md) for current support scope and unsupported
  scenarios.
- Review [SECURITY.md](../SECURITY.md) and confirm GitHub private vulnerability
  reporting or another owner-approved private reporting path is available before
  publication.
- Confirm issue templates and the pull request template still route questions,
  bugs, documentation issues, feature requests, and security disclosures to the
  right place.

## Validation Commands

Run these commands from a clean checkout:

```powershell
scripts/validate-docs.ps1
scripts/validate-package-consumption.ps1
dotnet test -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected result: documentation inventory, placeholder checks, internal links,
package metadata alignment, samples, clean package consumption, tests, and pack
all pass.

## Documentation-Only Changes

Documentation-only releases are patch-level for an already published package
when they clarify existing behavior without changing a public compatibility
promise. A documentation change is not documentation-only if it redefines public
API behavior, shared-memory layout behavior, error outcomes, lifecycle
ownership, security process, or support guarantees.
