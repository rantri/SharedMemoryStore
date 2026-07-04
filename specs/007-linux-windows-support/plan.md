# Implementation Plan: Linux, Windows, and Docker Support

**Branch**: `007-linux-windows-support` | **Date**: 2026-07-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/007-linux-windows-support/spec.md`

## Summary

Deliver first-class Linux, Windows, and same-host Docker container support for
SharedMemoryStore runtime and development workflows. The package should open,
publish, acquire, remove, reuse, reserve, recover, diagnose, and dispose stores
with the same public contracts across supported environments.

The technical approach is to replace direct Windows-only memory-mapping and
synchronization decisions with an internal platform resource layer. Windows keeps
named operating-system mappings and synchronization where they already meet the
contract. Linux uses deterministic same-host shared-memory resources backed by a
shared runtime memory location and equivalent synchronization, ownership, and
cleanup behavior. Docker support is a validated same-host deployment profile
where containers share the required IPC, process-liveness, permission, and
capacity capabilities; it is not cross-host sharing or distributed caching.

## Technical Context

**Language/Version**: C# on `.NET 10`, preserving the existing package baseline.

**Primary Dependencies**: No new runtime dependencies beyond the .NET BCL.
Validation may require Docker Engine or Docker Desktop, Docker Compose, and
PowerShell 7 (`pwsh`) on Linux for repository scripts.

**Storage**: Windows named memory-mapped store resources remain supported.
Linux introduces same-host shared-memory resource files in a shared runtime
memory location such as `/dev/shm`, with deterministic names derived from the
public store name. Docker support targets Linux-based same-host containers that
use the same Linux resources and are configured to share the required namespace,
owner-liveness, permissions, and capacity capabilities.

**Testing**: Existing xUnit unit, contract, and integration tests; new
cross-platform runtime tests; new multi-process Linux and Windows tests; new
Docker cross-container validation; sample runs; package-consumption validation;
`scripts/validate-docs.ps1`; `scripts/validate-package-consumption.ps1`;
`dotnet build SharedMemoryStore.slnx -c Release`; `dotnet test
SharedMemoryStore.slnx -c Release`; and `dotnet pack
src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o
artifacts/package`.

**Target Platform**: Supported .NET 10 Linux and Windows environments with the
required same-host shared-resource capabilities, plus Linux-based same-host
Docker containers configured to expose those capabilities. macOS, Windows
containers, cross-host sharing, persistence, orchestration, and distributed-cache
behavior remain out of scope.

**Project Type**: Reusable NuGet library/package with runtime source, public XML
documentation, repository docs, samples, scripts, contract tests, integration
tests, and package-consumption validation.

**Performance Goals**: Preserve existing bounded wait behavior, complete
contention and cancellation outcomes within the caller-selected wait limit plus
250 milliseconds, support at least 10,000 recovery cycles per supported
environment, and complete at least 1,000,000 long-running reuse/churn operations
on Linux and Windows without stale handle acceptance or undocumented failures.

**Constraints**: Preserve public API behavior for Windows consumers unless an
explicit compatibility change is approved. Preserve shared-memory layout and
public data semantics unless a documented layout compatibility change is
required. Keep runtime dependency surface BCL-only. Keep diagnostics
consumer-controlled. Do not introduce hidden background workers, global mutable
configuration, direct console output, cross-host semantics, persistence
guarantees, or malicious-writer protection.

**Scale/Scope**: Runtime store workflows, development workflows, package
consumption, documentation, samples, multi-process tests, and same-host Docker
container validation across Linux and Windows support.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: PASS. The feature expands the core reusable package's
  platform support and keeps application deployment concerns in samples and
  documentation.
- Stable contracts and semantic versioning: PASS. The plan treats public API,
  statuses, layout, diagnostics, platform behavior, and package metadata as
  compatibility contracts. Any required public or layout change needs semantic
  version review, migration notes, and contract tests.
- Test-driven production quality: PASS. The plan requires unit, contract,
  integration, multi-process, Docker, package-consumption, sample, and release
  validation before completion.
- .NET 10 baseline and portable core: PASS. The runtime remains C#/.NET 10,
  while platform-specific behavior is isolated behind documented internal
  adapters.
- Minimal, observable, dependency-conscious design: PASS. The runtime remains
  BCL-only, avoids hidden background work, and reports environment limitations
  through public outcomes and diagnostics.

## Project Structure

### Documentation (this feature)

```text
specs/007-linux-windows-support/
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- platform-runtime-contract.md
|   |-- docker-container-sharing-contract.md
|   |-- development-validation-contract.md
|   `-- compatibility-contract.md
|-- checklists/
|   `-- requirements.md
`-- tasks.md             # Phase 2 output from /speckit-tasks
```

### Source Code (repository root)

```text
src/SharedMemoryStore/
|-- Interop/
|   |-- MemoryMappedStoreRegion.cs
|   |-- platform resource adapter updates
|   `-- shared resource naming/cleanup support
|-- Leasing/
|   |-- LeaseOwnerClassifier.cs
|   `-- lease recovery owner-liveness updates
|-- Ingest/
|   `-- reservation recovery owner-liveness updates
|-- Options/
|   `-- option validation updates if compatibility review approves new fields
|-- Layout/
|   `-- layout updates only if owner/resource metadata requires review
`-- XML documentation updates for platform behavior

tests/
|-- SharedMemoryStore.ContractTests/
|   `-- platform support, status, package, and compatibility contracts
|-- SharedMemoryStore.IntegrationTests/
|   |-- Linux/Windows multi-process visibility and contention tests
|   |-- Docker container sharing validation entry points
|   `-- recovery, disposal, reuse, diagnostics, and package consumption tests
|-- SharedMemoryStore.UnitTests/
|   `-- resource naming, option validation, cleanup, and owner classification
`-- SharedMemoryStore.LeaseOwnerTool/
    `-- cross-platform and container-aware owner-process scenarios

samples/
|-- BasicUsage/
|-- FrameValue/
|-- ZeroCopyIngest/
|-- HostedServiceIntegration/
`-- DockerSharedMemory/       # New same-host container sample/validation path

scripts/
|-- validate-docs.ps1
|-- validate-package-consumption.ps1
|-- validate-cross-platform.ps1        # New or expanded validation entry point
`-- validate-docker-shared-memory.ps1  # New Docker validation wrapper

docs/
|-- portability.md
|-- getting-started.md
|-- samples.md
|-- diagnostics.md
|-- lifecycle.md
|-- architecture.md
|-- maintainers.md
|-- packaging.md
`-- releases.md
```

**Structure Decision**: Keep platform support in the existing core package and
isolate OS-specific behavior under `Interop/` plus owner-liveness helpers under
`Leasing/` and `Ingest/`. Add tests, scripts, docs, and a focused Docker sample
around the existing package instead of introducing a separate runtime package or
service wrapper.

## Complexity Tracking

No constitution violations are planned.

## Phase 0 Research Summary

See [research.md](research.md).

Key decisions:
- Keep the runtime package BCL-only and introduce internal platform adapters for
  store regions, synchronization, resource naming, owner liveness, and cleanup.
- Keep Windows behavior compatible by preserving named Windows resources where
  they already satisfy the contract.
- Implement Linux runtime support with deterministic same-host shared-memory
  resources in a shared runtime memory location, with cleanup and open-mode
  semantics that match public store contracts.
- Treat Docker support as a same-host deployment profile requiring shared IPC
  and owner-liveness capabilities, validated with Docker Compose or equivalent
  Docker CLI commands.
- Keep unsupported-platform outcomes for platforms outside Linux and Windows and
  add or reuse environment-capability outcomes only if semantic review approves
  the public status change.

## Phase 1 Design Summary

See [data-model.md](data-model.md),
[contracts/platform-runtime-contract.md](contracts/platform-runtime-contract.md),
[contracts/docker-container-sharing-contract.md](contracts/docker-container-sharing-contract.md),
[contracts/development-validation-contract.md](contracts/development-validation-contract.md),
[contracts/compatibility-contract.md](contracts/compatibility-contract.md), and
[quickstart.md](quickstart.md).

Post-design constitution check remains PASS:
- The design keeps SharedMemoryStore as one reusable package with no runtime
  dependency additions.
- Public platform behavior, statuses, layout, package metadata, and docs are
  treated as release contracts.
- Tests and validation explicitly cover Linux, Windows, and same-host Docker
  support before packaging.
- Platform-specific implementation is isolated behind resource, synchronization,
  and owner-liveness adapters.
- Diagnostics and failure outcomes stay consumer-controlled, with no hidden
  background workers or direct console output in library code.
