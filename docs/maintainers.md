# Maintainers

This guide defines the maintenance process for documentation, samples, package
metadata, release notes, public contracts, and evidence-backed claims. Use it
with [Architecture](architecture.md), [Release preparation](releases.md), and
the managed documentation quickstart at
[specs/006-improve-docs-samples/quickstart.md](../specs/006-improve-docs-samples/quickstart.md)
and the native/Python validation quickstart at
[specs/008-cpp-python-implementations/quickstart.md](../specs/008-cpp-python-implementations/quickstart.md).

## Contract Boundaries

Stable public contracts include:

- public API behavior documented by
  [public-api.md](../specs/001-frame-memory-store/contracts/public-api.md).
- public API names, signatures, option names, status names, and XML
  documentation examples in `src/SharedMemoryStore/`.
- package metadata in
  [`src/SharedMemoryStore/SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj).
- shared-memory layout and state semantics documented by
  [shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).
- reservation behavior documented by
  [reservation-api.md](../specs/003-zero-copy-ingest/contracts/reservation-api.md),
  [ingest-layout.md](../specs/003-zero-copy-ingest/contracts/ingest-layout.md),
  and
  [reservation-memory-contract.md](../specs/005-api-production-readiness/contracts/reservation-memory-contract.md).
- diagnostics and contention behavior documented by
  [diagnostics-integration-contract.md](../specs/005-api-production-readiness/contracts/diagnostics-integration-contract.md)
  and
  [contention-configuration-contract.md](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md).
- owner recovery behavior documented by
  [owner-recovery-contract.md](../specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md).
- production public API readiness documented by
  [public-api-contract.md](../specs/005-api-production-readiness/contracts/public-api-contract.md).
- layout, resource naming, versions, and conformance fixtures under
  [`protocol/`](../protocol/).
- fixed-width native C ABI `1.0` documented by
  [native-c-api.md](../specs/008-cpp-python-implementations/contracts/native-c-api.md).
- the public C++ and Python surfaces documented by
  [cpp-api.md](../specs/008-cpp-python-implementations/contracts/cpp-api.md)
  and [python-api.md](../specs/008-cpp-python-implementations/contracts/python-api.md).
- distribution compatibility and packaging documented by
  [interoperability.md](../specs/008-cpp-python-implementations/contracts/interoperability.md),
  [packaging.md](../specs/008-cpp-python-implementations/contracts/packaging.md),
  and [`protocol/compatibility.json`](../protocol/compatibility.json).

Current implementation details include private type organization, search
cursor choices, compaction thresholds, and helper method structure. They may be
documented for maintainability, but they are not compatibility guarantees unless
a public contract says so.

## Documentation Maintenance Checklist

Apply this checklist whenever public behavior, public API names, statuses,
sample behavior, package metadata, performance claims, platform support,
diagnostics, or release status changes.

| Change area | Update these surfaces |
|-------------|----------------------|
| Public API name, method, option, or token shape | `README.md`, `docs/getting-started.md`, `docs/usage.md`, `docs/examples.md`, XML docs, sample source, sample READMEs, public contracts, `scripts/validate-docs.ps1` |
| Status name or outcome behavior | `docs/errors.md`, `docs/diagnostics.md`, `docs/lifecycle.md`, sample READMEs, contract tests, error taxonomy contract |
| Shared-memory layout or lifecycle identity | `docs/architecture.md`, `docs/portability.md`, `docs/lifecycle.md`, layout contracts, contract tests, `CHANGELOG.md` |
| Reservation memory or ingest behavior | `docs/usage.md`, `docs/examples.md`, `docs/lifecycle.md`, `samples/ZeroCopyIngest/README.md`, reservation contracts |
| Diagnostics fields or failure accounting | `docs/diagnostics.md`, `docs/integration.md`, `docs/architecture.md`, diagnostics contracts, validation script |
| Performance claim | `docs/performance.md`, benchmark command/result notes, `docs/releases.md`, `CHANGELOG.md` if release-affecting |
| Platform or portability scope | `docs/portability.md`, `SUPPORT.md`, sample READMEs, Docker sample docs, release notes |
| Package metadata or release notes | `src/SharedMemoryStore/SharedMemoryStore.csproj`, `README.md`, `docs/packaging.md`, `docs/releases.md`, `CHANGELOG.md` |
| C ABI symbol, structure, width, or ownership rule | `c_api.h`, native ABI contract, protocol fixtures, C ABI tests, Python `ctypes` declarations, compatibility metadata, changelog |
| C++ public wrapper | `store.hpp`, C++ API contract, native tests, C++ sample, packaging and getting-started guides |
| Python public API, loader, or view lifetime | Python modules, Python API contract, wheel tests, Python sample, packaging and getting-started guides |
| Layout, resource naming, or cross-runtime behavior | `protocol/`, all three implementations, static conformance tests, ordered-pair tests, portability, security, compatibility metadata |
| Native or Python distribution version | root `CMakeLists.txt` or `pyproject.toml`, compatibility metadata, packaging, README, changelog, release preparation, support policy |
| Sample command or output | sample source, sample README, `docs/samples.md`, `specs/006-improve-docs-samples/sample-validation.md` |
| Documentation-only clarification | affected doc, `docs/index.md` if navigation changes, `scripts/validate-docs.ps1`, release-impact review |

## Validation Commands

Run the full validation path before release:

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

Use
[`scripts/validate-docs.ps1`](../scripts/validate-docs.ps1) for documentation
inventory, links, placeholders, sample README sections, public names, statuses,
package metadata, and release-note alignment. Use
[`scripts/validate-package-consumption.ps1`](../scripts/validate-package-consumption.ps1)
for clean package-source consumption. Use
[`SharedMemoryStore.slnx`](../SharedMemoryStore.slnx) for build and test
coverage.

The native wrapper validates CTest, installation, and a clean external CMake
consumer. Python validation must use a built wheel installed into a clean
environment so repository imports cannot hide a missing native artifact. The
interoperability test run must record which agent executables were available;
the existence of nine theory rows is not evidence that all nine ran on both
platforms.

Use `bin/python` instead of `Scripts/python` on Linux.

## Review Questions

For every change, answer:

- Which public contracts could this change affect?
- Which docs and samples must be updated?
- Which validation commands must pass?
- Does this change affect package metadata, changelog, or release notes?
- Does this wording alter a public compatibility promise?
- Is every performance or platform claim backed by evidence and scoped to the
  validated environment?
- Does the change preserve the rules against hidden background work, broad core
  service abstractions, persistence promises, distributed-cache claims,
  unsupported platforms, arbitrary native-library loading, and unverified
  interoperability claims?

## Documentation-Only Review

Documentation-only changes still require engineering review. Check:

- links resolve and no placeholders remain.
- public API names and status names match source.
- examples and sample command outputs remain current.
- package metadata and release notes stay aligned.
- support and security paths still point readers to `SUPPORT.md` and
  `SECURITY.md`.
- maintainer internals explain current implementation without turning private
  details into public contracts.

## Performance Evidence Rules

Public performance wording must name whether it is:

- measured result from a benchmark or validation command.
- design expectation without a numeric guarantee.
- unvalidated scenario that should not be treated as a claim.

Record OS, CPU, .NET SDK, package version, payload sizes, slot counts,
producer/reader counts, segment counts, final statuses, and benchmark command
for measured claims. Update [Performance scope](performance.md),
[Release preparation](releases.md), and [CHANGELOG.md](../CHANGELOG.md) when a
release-facing performance claim changes.

## Release Responsibilities

Before publishing:

- verify package metadata and `PackageReleaseNotes` in
  [`SharedMemoryStore.csproj`](../src/SharedMemoryStore/SharedMemoryStore.csproj).
- verify package README content in [README.md](../README.md).
- align [CHANGELOG.md](../CHANGELOG.md), [Release preparation](releases.md),
  and [Packaging](packaging.md).
- align NuGet, CMake, Python, C ABI, layout, and resource-naming versions with
  [`protocol/compatibility.json`](../protocol/compatibility.json).
- review [SUPPORT.md](../SUPPORT.md) and [SECURITY.md](../SECURITY.md).
- update sample validation notes in
  [sample-validation.md](../specs/006-improve-docs-samples/sample-validation.md)
  when sample output changes.
- update coverage notes in
  [documentation-coverage.md](../specs/006-improve-docs-samples/documentation-coverage.md)
  when reader journeys or workflow coverage changes.
- capture Linux, Windows, Docker, unsupported-profile, and compatibility
  validation evidence in [Release preparation](releases.md) before publishing a
  platform-support release.
- capture native CTest and clean CMake consumption, installed-wheel tests, and
  every ordered runtime pair claimed for the release.

## Boundaries To Preserve

Do not introduce or imply:

- hidden background cleanup or telemetry workers in the core package.
- required hosting, logging, dependency injection, health-check, or options
  framework dependencies.
- persistence after process and mapping lifetime.
- network-distributed cache semantics.
- protection from malicious same-host writers that already have mapping access.
- macOS, Windows-container, default-isolated Docker, or cross-host support
  beyond validated scope.
- native or Python registry publication before release automation and artifacts
  are actually available.
- Windows/Linux or ordered-pair validation based only on target metadata or
  skipped tests.
