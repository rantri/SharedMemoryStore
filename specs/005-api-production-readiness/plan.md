# Implementation Plan: API Production Readiness

**Branch**: `005-api-production-readiness` | **Date**: 2026-07-03 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/005-api-production-readiness/spec.md`

## Summary

Make the SharedMemoryStore package production-ready from a public API
perspective before the next release. The work removes the namespace/type naming
collision, closes the reservation writable-memory lifetime hole, gives every
public synchronization path a deterministic wait contract, strengthens
configuration and key validation, prunes misleading diagnostics convenience
names, and keeps any service-hosting support in an optional integration surface.

The technical approach keeps the core package a `net10.0` library with only BCL
runtime dependencies. Breaking public corrections are accepted because the
package is still pre-broad-release, but they are treated as a major production
API contract step with migration notes, contract tests, package-consumption
validation, and documentation examples compiled against the final surface.

## Technical Context

**Language/Version**: C# targeting `net10.0`. Public contract documents remain
language-neutral where they define memory lifetime, synchronization outcomes,
validation semantics, and diagnostics taxonomy for future C++ and Python
implementations.

**Primary Dependencies**: The core `SharedMemoryStore` package remains .NET BCL
only. Existing test projects continue to use xUnit infrastructure already
present in the repository. Optional service-hosting integration, if delivered,
lives in a separate package/project and may depend on `Microsoft.Extensions.*`
abstractions without adding those dependencies to the core package.

**Storage**: Existing named OS memory-mapped region with fixed reusable slots,
shared key index, lease records, reservation metadata, descriptor storage, and
payload storage. This feature does not require a shared-memory layout change,
but it changes which public handles may expose writable reservation memory.

**Testing**: `dotnet test` with unit, contract, integration, package
consumption, documentation example, and release-validation coverage. Validation
adds public API naming tests, retained reservation write-handle tests, bounded
wait and cancellation tests for every public operation family, option and key
validation tests, diagnostics contract tests, optional integration tests if an
adapter package is added, and `dotnet pack`.

**Target Platform**: .NET 10 supported platforms where named memory-mapped files
and named synchronization primitives are available. Windows remains the first
fully validated target for cross-process synchronization. Unsupported or
ambiguous platform behavior is reported through documented statuses rather than
implicit fallback behavior.

**Project Type**: Reusable NuGet package/library with public XML
documentation, contract documentation, release notes, package metadata,
samples, and optional integration adapters kept separate from the core package.

**Performance Goals**: API hardening must not add steady-state allocations to
normal publish, reserve, acquire, remove, release, diagnostics, or recovery
paths beyond documented wait-policy setup. The default wait policy is one
second, and bounded wait paths must return within the caller-selected limit plus
250 milliseconds of scheduler tolerance. Reservation lifetime tests must prove
retained write access cannot mutate committed or reused storage after at least
10,000 reuse cycles. Package examples must compile without aliasing the primary
store type.

**Constraints**: Preserve package-first design, BCL-only core runtime
dependencies, caller-controlled diagnostics, deterministic public statuses, and
existing shared-memory semantics except where unsafe or misleading public API
behavior is corrected. Do not add direct console output, global mutable
configuration, hidden background workers, broad interfaces that mirror the
concrete store, or service-hosting dependencies to the core package.

**Scale/Scope**: One named bounded store per handle; fixed slot, lease, key,
descriptor, value, and mapped-region capacities; existing zero-copy ingest,
lease, removal, diagnostics, recovery, benchmark, sample, and package
workflows. Scope covers public API shape and contract hardening rather than new
storage capabilities.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: PASS. The feature improves the reusable package API
  and keeps application-specific hosting support outside the core package.
- NuGet deliverable: PASS. The plan includes package metadata, XML docs,
  release notes, package-consumption validation, and `dotnet pack`.
- Stable contracts and semantic versioning: PASS. All public API changes,
  memory-lifetime changes, contention outcomes, diagnostics changes, and
  optional integration boundaries are documented in contracts with migration
  guidance and major-release impact.
- Test-driven production quality: PASS. The plan includes unit, contract,
  integration, stress, package-consumption, documentation, and packaging
  validation before release.
- .NET 10 baseline and portability: PASS. The implementation remains C#/.NET 10
  while public synchronization, memory lifetime, and validation semantics are
  documented without unnecessary C#-specific assumptions.
- Minimal observable design: PASS. The core package remains dependency-light.
  Diagnostics are caller-controlled, synchronization outcomes are explicit, and
  optional integrations are separate.

## Project Structure

### Documentation (this feature)

```text
specs/005-api-production-readiness/
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- public-api-contract.md
|   |-- reservation-memory-contract.md
|   |-- contention-configuration-contract.md
|   `-- diagnostics-integration-contract.md
`-- tasks.md             # Phase 2 output from /speckit-tasks
```

### Source Code and Validation (repository root)

```text
src/
|-- SharedMemoryStore/
|   |-- MemoryStore.cs                 # renamed primary store type
|   |-- SharedMemoryStoreOptions.cs
|   |-- StoreStatus.cs
|   |-- StoreWaitOptions.cs
|   |-- ValueLease.cs
|   |-- Diagnostics/
|   |   |-- DiagnosticsSnapshot.cs
|   |   `-- StoreDiagnostics.cs
|   |-- Ingest/
|   |   |-- ValueReservation.cs
|   |   |-- ReservationMemoryManager.cs # internal only if still needed
|   |   `-- ReservationRecovery.cs
|   |-- Lifecycle/
|   |   `-- StoreLifecycleGate.cs
|   |-- Options/
|   |   |-- SharedMemoryStoreOptionsValidator.cs
|   |   `-- StoreOptionsValidationResult.cs
|   `-- [existing Layout, Leasing, Slots, Interop files]
`-- SharedMemoryStore.Hosting/         # optional adapter package, if implemented
    |-- SharedMemoryStore.Hosting.csproj
    |-- SharedMemoryStoreHealthCheck.cs
    `-- SharedMemoryStoreLifecycleService.cs

tests/
|-- SharedMemoryStore.UnitTests/
|   |-- PublicStoreIdentityTests.cs
|   |-- ReservationMemoryLifetimeTests.cs
|   |-- StoreWaitPolicyTests.cs
|   |-- StoreOptionsValidationTests.cs
|   |-- KeyValidationTests.cs
|   `-- DiagnosticsApiShapeTests.cs
|-- SharedMemoryStore.ContractTests/
|   |-- ProductionApiContractTests.cs
|   |-- ReservationMemoryContractTests.cs
|   |-- ContentionContractTests.cs
|   |-- ConfigurationContractTests.cs
|   |-- DiagnosticsContractTests.cs
|   `-- PackageConsumptionApiTests.cs
|-- SharedMemoryStore.IntegrationTests/
|   |-- ContendedSynchronizationIntegrationTests.cs
|   |-- ReservationReuseSafetyIntegrationTests.cs
|   `-- PackageProductionReadinessIntegrationTests.cs
`-- SharedMemoryStore.Hosting.Tests/    # only if optional adapter package is added

samples/
|-- BasicUsage/
|-- FrameValue/
|-- ZeroCopyIngest/
`-- HostedServiceIntegration/           # optional sample, separate dependencies

docs/
|-- getting-started.md
|-- usage.md
|-- examples.md
|-- lifecycle.md
|-- diagnostics.md
|-- errors.md
|-- packaging.md
`-- releases.md
```

**Structure Decision**: Keep production API changes in the existing core
package. Rename the concrete public store type in place, preserve the existing
namespace and package ID, and update docs, samples, and tests to use the final
identity. Keep synchronization, lifecycle, validation, diagnostics, and ingest
changes beside the current implementation areas. Add an optional
`SharedMemoryStore.Hosting` project only if service-hosting support is
implemented; otherwise document the concrete core API and avoid speculative
interfaces.

## Complexity Tracking

No constitution violations are planned.

## Phase 0 Research Summary

See [research.md](research.md).

Key decisions:
- Rename the primary concrete store type to `MemoryStore` in the
  `SharedMemoryStore` namespace and document the breaking migration from
  `SharedMemoryStore.SharedMemoryStore`.
- Remove general-purpose retained writable `Memory<byte>` reservation access
  from the public API. Keep reservation writes span-scoped and synchronous.
- Add a `StoreWaitOptions` contract and contention statuses so every public
  operation that can wait on shared synchronization has bounded timeout and
  cancellation semantics, including open/create and a status-returning
  diagnostics path.
- Add public valid-by-construction option helpers and validation details while
  rejecting undefined `OpenMode` values as invalid options.
- Add an `InvalidKey` outcome so empty keys are distinguishable from oversized
  keys.
- Keep diagnostics aggregate-first through `GetFailureCount(StoreStatus)` and
  remove or obsolete clunky per-status failure-count convenience names before
  the production API release.
- Avoid broad store-mirroring interfaces. Optional hosting integration, if
  added, must be a separate package with narrow lifecycle and health adapters.

## Phase 1 Design Summary

See [data-model.md](data-model.md),
[contracts/public-api-contract.md](contracts/public-api-contract.md),
[contracts/reservation-memory-contract.md](contracts/reservation-memory-contract.md),
[contracts/contention-configuration-contract.md](contracts/contention-configuration-contract.md),
[contracts/diagnostics-integration-contract.md](contracts/diagnostics-integration-contract.md),
and [quickstart.md](quickstart.md).

Post-design constitution check remains PASS:
- The design is package-first and keeps the core runtime BCL-only.
- Public API, memory-lifetime, contention, validation, diagnostics, and optional
  integration contracts are documented with compatibility impact.
- Tests, docs, samples, package-consumption validation, release notes, and
  package build are part of the planned completion criteria.
- Diagnostics and hosting remain caller-controlled and opt-in; no hidden
  background workers, console output, or broad concrete-store interfaces are
  introduced.
