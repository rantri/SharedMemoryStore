# Release Preparation

This guide is the maintainer checklist for preparing independently versioned
managed, native, and Python releases. The current source identities are NuGet
`SharedMemoryStore` `1.0.1`, CMake `SharedMemoryStore` `0.1.0`, Python
`shared-memory-store` `0.1.0`, C ABI `1.0`, mapped layout `1.2`, and resource
naming `1`. Update this guide when any distribution, ABI, protocol, support,
security, documentation, or sample contract changes.

## Automated Release Process

Releases are deliberately started by a maintainer and automated after that
point. The [release workflow](../.github/workflows/release.yml) validates the
version and release target, runs the full Linux and Docker release suite, packs
the primary and symbol packages, creates a draft GitHub release, publishes to
NuGet.org, and then publishes the GitHub release. CI separately validates the
same commit on Linux and Windows through
[ci.yml](../.github/workflows/ci.yml).

The existing workflow publishes the managed NuGet and GitHub release. Native
CMake installation and Python wheel construction exist in the repository, but
this guide does not claim a native registry or Python index publication path
until dedicated automation, credentials, artifact signing/provenance, and clean
install checks are reviewed.

Configure NuGet.org trusted publishing once before the first automated release:

1. Sign in to NuGet.org as the `rantri` owner and open **Trusted Publishing**.
2. Add a GitHub policy with owner `rantri`, repository
   `SharedMemoryStore`, workflow file `release.yml`, and environment `release`.
3. In GitHub, confirm the `release` environment exists. An optional required
   reviewer adds a second approval before publication.
4. Merge all release changes to `main` and confirm the CI workflow succeeds.

To publish a release:

1. Set `<Version>` and `PackageReleaseNotes` in the package project, then align
   README, packaging documentation, and `CHANGELOG.md`.
2. Complete the compatibility and validation review below.
3. On GitHub, open **Actions**, choose **Release**, select **Run workflow** on
   `main`, enter the exact version without a `v` prefix, and run it.
4. Confirm the workflow publishes `SharedMemoryStore.<version>.nupkg` and
   `.snupkg`, creates tag `v<version>`, and publishes the GitHub release.
5. Allow time for NuGet.org validation and indexing, then install the exact
   published version in a clean consumer.

Do not create the tag or GitHub release first; the workflow owns both. NuGet
package versions are immutable, so a failed release must be diagnosed rather
than retried under the same version with different contents. If NuGet publishing
fails, the workflow intentionally leaves the GitHub release as a draft. If the
package is still absent from NuGet.org, delete that draft and its tag before a
retry. If NuGet.org already has the version, keep the tag and publish the
existing draft after verifying its attached package rather than rebuilding it.

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

For a native or Python release, also verify:

- root `project(... VERSION ...)` and `[project].version` identify the intended
  distribution versions.
- `SharedMemoryStoreConfig.cmake` declares native package, C ABI, layout, and
  resource-naming versions.
- `shared_memory_store.__version__` and `pyproject.toml` agree.
- [`protocol/compatibility.json`](../protocol/compatibility.json) agrees with
  every released distribution.
- wheel contents place the correct native library beside the Python modules and
  the loader rejects an incompatible or misplaced artifact.
- target-platform metadata is not described as completed validation without a
  corresponding recorded run.

## Release Notes and Changelog

Update [CHANGELOG.md](../CHANGELOG.md) in reverse chronological order. Each
entry should identify:

- package version.
- distribution ecosystem and artifact name.
- C ABI, layout, and resource-naming compatibility identities.
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
- [protocol/README.md](../protocol/README.md)
- [native-c-api.md](../specs/008-cpp-python-implementations/contracts/native-c-api.md)
- [cpp-api.md](../specs/008-cpp-python-implementations/contracts/cpp-api.md)
- [python-api.md](../specs/008-cpp-python-implementations/contracts/python-api.md)
- [interoperability.md](../specs/008-cpp-python-implementations/contracts/interoperability.md)
- [packaging.md](../specs/008-cpp-python-implementations/contracts/packaging.md)

Confirm that docs describe the delivered C++ and Python surfaces without
claiming registry publication, unrun platform/pair validation, broad macOS or
cross-host support, unmeasured hardware performance,
application-specific frame parsing by the core store, hidden background work,
persistence, Windows-container support, default-isolated Docker support, or
cross-host cache behavior.

## Documentation-Only Release Review

Documentation-only changes still need release review:

- Run `scripts/validate-docs.ps1`.
- Confirm examples and sample outputs match current source.
- Confirm package metadata, `PackageReleaseNotes`, README, packaging guide,
  changelog, and release notes agree.
- Confirm CMake, Python, C ABI, layout, resource-naming, and compatibility
  metadata versions agree.
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
- Confirm reports can identify the affected managed, native, or Python
  distribution and that all docs preserve the trusted same-host participant
  boundary.

## Validation Commands

Run these commands from a clean checkout:

```powershell
pwsh ./scripts/validate-docs.ps1
dotnet build SharedMemoryStore.slnx -c Release
dotnet run --project samples/BasicUsage/BasicUsage.csproj -c Release
dotnet run --project samples/FrameValue/FrameValue.csproj -c Release
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet run --project samples/HostedServiceIntegration/HostedServiceIntegration.csproj -c Release
dotnet run --project samples/DockerSharedMemory/DockerSharedMemory.csproj -c Release -- all
pwsh ./scripts/validate-package-consumption.ps1
dotnet test SharedMemoryStore.slnx -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
pwsh ./scripts/validate-cross-platform.ps1 -SkipDocker
pwsh ./scripts/validate-docker-shared-memory.ps1
pwsh ./scripts/validate-native.ps1 -Configuration Release
python -m pip install build
python -m build --wheel
python -m venv artifacts/python-consumer
artifacts/python-consumer/Scripts/python -m pip install (Get-ChildItem dist/*.whl | Select-Object -First 1)
$env:SMS_TEST_INSTALLED_PACKAGE = '1'
artifacts/python-consumer/Scripts/python -m unittest discover -s tests/python -v
artifacts/python-consumer/Scripts/python samples/PythonBasicUsage/main.py
dotnet test tests/SharedMemoryStore.InteropTests/SharedMemoryStore.InteropTests.csproj -c Release
```

Use `bin/python` instead of `Scripts/python` on Linux. Configure the C++ and
Python agent paths required by the interoperability harness and record every
ordered pair that actually executes. A skipped or unavailable agent is not a
passing release result.

Expected result: documentation inventory, placeholder checks, internal links,
metadata alignment, sample contracts, managed regressions and package
consumption, native CTest/install/clean CMake consumption, installed-wheel
tests, samples, and every claimed runtime pair all pass.

## Linux, Windows, and Docker Support Notes

This release adds Linux and Windows as first-class runtime and development
targets and adds a supported same-host Docker profile for Linux containers that
share IPC, owner-liveness, permissions, and shared-memory capacity. The public
API, status taxonomy, runtime dependencies, and shared-memory layout major
version remain compatible.

Release evidence to capture before publication:

- Windows restore, build, test, sample, docs, package-consumption, and pack
  validation.
- Linux restore, build, test, sample, docs, package-consumption, and pack
  validation.
- Docker supported-profile validation through
  `scripts/validate-docker-shared-memory.ps1`.
- Docker isolated-profile validation that fails clearly without silent sharing.
- Docker advanced, recovery, contention, disposal-race, and clean-consumer
  validation profiles.
- Compatibility review against
  [compatibility-contract.md](../specs/007-linux-windows-support/contracts/compatibility-contract.md).

Validation evidence captured on 2026-07-03 and 2026-07-04:

- Windows host `pwsh ./scripts/validate-cross-platform.ps1 -SkipDocker`:
  passed restore, build, tests, host samples, Docker sample local mode, docs
  validation, package consumption, and pack.
- Linux host through WSL Ubuntu 24.04
  `pwsh ./scripts/validate-cross-platform.ps1 -SkipDocker` with isolated
  `DOTNET_CLI_HOME`: passed restore, build, tests, host samples, Docker sample
  local mode, docs validation, package consumption, and pack.
- Linux-based Docker supported profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Supported`:
  passed cross-container create, open, acquire, active-lease protected remove,
  release, republish, remove, reuse, diagnostics, and 10,000 churn cycles.
- Linux-based Docker advanced profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Advanced`:
  passed reservation, segmented publish, recovery entry point, and diagnostics
  workflows inside configured containers.
- Linux-based Docker recovery profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Recovery -SkipComposeBuild`:
  passed abrupt-exit lease and reservation owner recovery with recovered counts.
- Linux-based Docker contention profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Contention -SkipComposeBuild`:
  passed cancellation, no-wait busy, and bounded wait busy outcomes.
- Linux-based Docker disposal-race profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile DisposalRace -SkipComposeBuild`:
  passed documented lifecycle outcomes under disposal race workload.
- Linux-based Docker clean-consumer profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile CleanConsumer`:
  packed `SharedMemoryStore`, built a fresh container project from the package,
  and passed first-use, reservation, segmented publish, diagnostics, recovery,
  and disposal workflows.
- Linux-based Docker isolated profile
  `pwsh ./scripts/validate-docker-shared-memory.ps1 -Profile Isolated -SkipComposeBuild`:
  passed with `NotFound` for the isolated verifier.
- Compatibility review: no public API additions, no public status additions, no
  runtime dependency additions, and no shared-memory layout major-version
  change were introduced by the platform adapter work.

Benchmark commands are required when release notes make performance claims:

```powershell
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj -c Release -- --filter *SegmentedPublish*
```

## Native and Python 0.1.0 Preparation Notes (Unreleased)

The repository now contains the implementation and packaging inputs for:

- a C++20 native core with Windows/Linux adapters, fixed-width C ABI `1.0`,
  move-only C++ RAII wrappers, CMake install/export rules, native tests, and a
  basic sample.
- a Python 3.10+ `ctypes` package with context-managed stores, leases, and
  reservations, package-adjacent native loading, wheel configuration, Python
  tests, and a basic sample.
- canonical layout `1.2`, resource naming `1`, compatibility metadata,
  conformance fixtures, test-only JSON-lines agents, and the ordered 3x3 core
  exchange harness.

These sibling distributions do not change the managed `1.0.1` public API,
status numbers, NuGet runtime dependencies, or mapped layout. Their `0.1.0`
versions are alpha lines and are not included in the NuGet package.

Before publishing native or Python artifacts or describing a platform as
release-validated, capture all of the following:

- native configure, compile, CTest, install, basic sample, and clean external
  `find_package` consumption on Windows x64 and Linux x64.
- a wheel build and clean installed-wheel tests on both targets, including
  proof that the bundled native library loads without repository or system
  search-path fallback.
- every ordered .NET/C++/Python producer-consumer pair on each target, plus
  mixed lease removal/reuse, reservation lifecycle, bounded contention, crash
  recovery, and Linux owner cleanup.
- existing managed, documentation, Docker, package-consumption, security, and
  vulnerability regression gates.

No PyPI, native archive registry, or automatic native/Python publication is
claimed by this source change. Add and review those release channels separately.

## 1.0.1 Production Hardening Notes

The 2026-07-09 review completed the following release evidence:

- Windows Release build completed with zero warnings and 153 tests passed.
- Linux .NET 10 container validation passed 43 contract, 62 unit, and 47
  integration tests; the package-consumption path was validated separately in
  a clean Linux container.
- The full Docker matrix passed supported sharing, advanced ingest, abrupt-exit
  recovery, contention, disposal race, isolated negative behavior, and clean
  packed-package consumption.
- Documentation validation, formatting/analyzers, package packing, Windows
  clean-consumer validation, and current transitive NuGet vulnerability checks
  passed.
- Public API names, status values, runtime dependencies, and the shared-memory
  layout remain compatible with `1.0.0`.

External publication gates completed on 2026-07-10: GitHub private vulnerability
reporting is enabled for the public repository, the `release` GitHub environment
is restricted to `main`, and the NuGet.org trusted-publishing policy targets
`rantri/SharedMemoryStore`, `release.yml`, and the `release` environment.

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
  writes followed by `Advance`, or use `DangerousGetMemory` only for trusted
  direct-I/O adapters that need `Memory<byte>`.
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
