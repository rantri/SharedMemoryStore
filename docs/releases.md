# Release Preparation

This guide is the maintainer checklist for preparing a package release. It is
written for the current `1.0.0` package and should be updated when package
metadata, public API behavior, compatibility scope, support policy, security
reporting, documentation scope, or sample behavior changes.

## Package Metadata

Verify
[`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj)
before publishing:

- `PackageId` is `SharedMemoryStore`.
- `Version` matches the release being prepared.
- `TargetFramework` is `net10.0` unless a feature plan changes the baseline.
- `Description` still describes a bounded named shared-memory key-value store
  for opaque binary values.
- `PackageTags` include shared memory, memory mapped files, zero-copy, and
  library terms.
- `PackageLicenseExpression` is `MIT` and matches the [LICENSE](../LICENSE).
- `PackageReadmeFile` is `README.md` and the project packs the root README at
  the package root.
- `PackageReleaseNotes` matches the release entry in
  [CHANGELOG.md](../CHANGELOG.md) and the metadata table in
  [Packaging](packaging.md).
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

For the `1.0.0` production API line, public API, layout, lifecycle, error, or
support-policy changes require explicit semantic-version review and migration
notes.

## Compatibility Review

Compare public docs with these contracts:

- [public-api.md](../specs/001-frame-memory-store/contracts/public-api.md)
- [error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md)
- [shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md)
- [reservation-api.md](../specs/003-zero-copy-ingest/contracts/reservation-api.md)
- [ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md)
- [diagnostics-and-errors.md](../specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md)
- [owner-recovery-contract.md](../specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md)
- [disposal-rollover-contract.md](../specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md)
- [index-health-contract.md](../specs/004-store-reliability-hardening/contracts/index-health-contract.md)
- [public-api-contract.md](../specs/005-api-production-readiness/contracts/public-api-contract.md)
- [contention-configuration-contract.md](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md)
- [diagnostics-integration-contract.md](../specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md)
- [reservation-memory-contract.md](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md)

Confirm that docs do not claim current C++ or Python bindings, broad
cross-platform support, unmeasured hardware performance, application-specific
frame parsing by the core store, hidden background work, persistence, or
cross-host cache behavior.

## Documentation-Only Release Review

Documentation-only changes still need release review:

- Run `scripts/validate-docs.ps1`.
- Confirm examples and sample outputs match current source.
- Confirm package metadata, `PackageReleaseNotes`, README, packaging guide,
  changelog, and release notes agree.
- Confirm compatibility wording did not change public behavior promises.
- Confirm known limitations, platform scope, performance claims, support scope,
  and security reporting paths remain current.
- Update [Maintainers](maintainers.md) if the maintenance rules changed.

## Support and Security

- Review [SUPPORT.md](../SUPPORT.md) for current support scope and unsupported
  scenarios.
- Review [SECURITY.md](../SECURITY.md) and confirm GitHub private vulnerability
  reporting or another owner-approved private reporting path is available
  before publication.
- Confirm issue templates and the pull request template still route questions,
  bugs, documentation issues, feature requests, and security disclosures to the
  right place.

## Validation Commands

Run these commands from a clean checkout:

```powershell
scripts/validate-docs.ps1
dotnet build SharedMemoryStore.slnx -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
scripts/validate-package-consumption.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
```

Expected result: documentation inventory, placeholder checks, internal links,
package metadata alignment, sample README contracts, public API/status drift
checks, sample commands, clean package consumption, tests, and pack all pass.

Benchmark commands are required when release notes make performance claims:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *SegmentedPublish*
```

## 1.0.0 Documentation and Samples Excellence Notes

The documentation and samples excellence update reorganizes the reader journey,
adds concept, sample ladder, architecture, and maintainer guides, expands sample
README contracts, strengthens validation, and aligns package-facing metadata and
release guidance. Runtime behavior and the `1.0.0` public API contract remain
unchanged.

## 1.0.0 Production API Readiness Notes

The `1.0.0` release is the production public API contract step. Migration from
`0.2.0` requires:

- Replace `SharedMemoryStore.SharedMemoryStore` with `MemoryStore`.
- Replace retained reservation `GetMemory` writes with immediate `GetSpan`
  writes followed by `Advance`.
- Handle `InvalidKey`, `StoreBusy`, and `OperationCanceled` outcomes.
- Replace diagnostics convenience members with
  `GetFailureCount(StoreStatus.SomeStatus)`.
- Keep hosting, dependency injection, logging, and health integration outside
  the core package unless an optional adapter package or sample is used.

## 0.2.0 Readiness Notes

Validation run on 2026-07-02:

- `scripts/validate-docs.ps1`: passed.
- `dotnet build SharedMemoryStore.slnx -c Release`: passed.
- `dotnet test SharedMemoryStore.slnx -c Release --no-build`: passed, 85 tests.
- `dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release`: passed.
- `scripts/validate-package-consumption.ps1`: passed and packed
  `SharedMemoryStore.0.2.0.nupkg`.
- `dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation direct-allocation`:
  passed. Result: 100,000 frames, 0 total allocated bytes, 0.000 allocated
  bytes/frame, final status `Success`.
- `dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation tombstone-pressure`:
  passed. Result: 512 churn operations, 128 index entries, 0 final tombstones,
  544 synchronous compactions, early pressure detection before the 75% worst-case
  probe threshold, missing lookup and insert timings within 2x of clean-index
  baselines, and preservation of active leases, pending reservations, duplicate
  detection, and visible values.
- `dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *TombstonePressure* --job Dry`:
  passed BenchmarkDotNet discovery and dry execution.
- `dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --validation sustained-throughput`:
  passed. Environment: .NET 10.0.5, Windows NT 10.0.26200.0, 32 logical CPUs,
  1,300,000-byte payloads. Simple publish measured 22,782.13 publishes/s across
  1,366,928 frames; direct ingest measured 30,620.36 frames/s across 1,837,222
  frames. Direct ingest was 1.344x simple publish, a 34.4% increase.

## Reliability Hardening Notes

The reliability hardening update corrects owner-scoped lease recovery, makes
post-disposal outcomes deterministic at public boundaries, adds rollover-safe
slot lifecycle identity, and exposes key-index tombstone health diagnostics with
synchronous internal compaction. Package id and target framework remain
unchanged. Runtime dependencies remain .NET BCL only. Shared-memory layout major
version remains `1`; minor version advances to `2` because shared records now
include reuse epochs.
