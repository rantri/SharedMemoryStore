# Implementation Plan: Store Reliability Hardening

**Branch**: `004-store-reliability-hardening` | **Date**: 2026-07-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/004-store-reliability-hardening/spec.md`

## Summary

Harden SharedMemoryStore before additional capability is built on top of it.
The work corrects owner-scoped lease recovery so current-process recovery never
reclaims another live owner's lease, normalizes all disposal races into
documented public outcomes, makes slot and lease probing plus lifecycle
identifiers safe across long-running rollover boundaries, and adds
evidence-led tombstone pressure diagnostics with synchronous internal index
health management when benchmark data proves it is required.

The technical approach preserves the package-first `net10.0` library shape and
BCL-only runtime dependency policy. Reliability behavior is documented through
public contracts, covered with deterministic boundary tests and stress tests,
and surfaced through caller-controlled reports or diagnostics instead of
background workers, console output, or application-specific integrations.

## Technical Context

**Language/Version**: C# targeting `net10.0`. Owner recovery, lifecycle
outcomes, layout state, probe arithmetic, lifecycle identifiers, and index
health semantics are documented in language-neutral contracts for future C++
and Python implementations.

**Primary Dependencies**: Runtime package remains .NET BCL only. The feature
uses existing memory-mapped-file, span, memory, atomics, mutex, process
liveness, and BenchmarkDotNet-based benchmark infrastructure already present in
the repository. No runtime logging, metrics, background worker, or third-party
dependency is introduced.

**Storage**: Existing named OS memory-mapped region with fixed reusable slots,
open-addressed shared key index, lease registry, descriptor storage, payload
storage, slot metadata, and reservation metadata. The feature may revise shared
record fields for rollover-safe lifecycle identifiers, which requires explicit
layout compatibility documentation.

**Testing**: `dotnet test` with unit, contract, and integration coverage.
Validation adds multi-owner recovery tests, disposal-race stress tests,
deterministic cursor and generation rollover tests, tombstone diagnostics tests,
high-churn benchmarks, documentation validation, package-consumption validation,
and `dotnet pack`.

**Target Platform**: .NET 10 supported platforms with named memory-mapped-file
support. Windows remains the first fully validated target for owner liveness
because the current implementation already relies on process identifiers and
named mappings there. Unsupported or ambiguous owner-liveness checks preserve
safety by skipping recovery and returning deterministic unsupported or reported
outcomes.

**Project Type**: Reusable NuGet package/library with public XML documentation,
contract documentation, benchmarks, samples, package metadata, and release
notes.

**Performance Goals**: Lease recovery validates 10,000 multi-owner cycles with
no premature reuse of other live-owner leases. Disposal race stress completes at
least 100,000 public operations without internal disposed-resource exceptions.
Rollover tests drive slot probes, lease-record probes, and lifecycle
identifiers through boundary conditions and complete at least 1,000,000
additional operations without invalid indexes, arithmetic overflow failures, or
stale handle acceptance. Churn benchmarks keep missing-key lookup and new-key
insert latency within 2x of a clean-index baseline after tombstone pressure
management.

**Constraints**: Preserve existing documented publish, reserve, acquire,
remove, release, recovery, diagnostics, and package-consumption behavior except
where unsafe outcomes are corrected. Keep public behavior deterministic across
disposal races. Keep diagnostics consumer-controlled. Do not add direct console
output, global mutable configuration, hidden background cleanup, or broad writer
or versioned replacement APIs. Treat live data safety as more important than
aggressive recovery when owner liveness is unsupported or ambiguous.

**Scale/Scope**: One named bounded store per handle, configurable fixed slots,
fixed key/descriptor/value maxima, lease records, reservation tokens, and
existing zero-copy ingest workflows. Validation covers multi-owner recovery,
concurrent operations during disposal, small-capacity edge cases, cursor
boundary seeding, lifecycle identifier boundary seeding, high unique-key churn,
and existing package workflows.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: PASS. The work hardens reusable package behavior and
  public contracts rather than adding an application-specific service workflow.
- NuGet deliverable: PASS. The plan includes API/XML documentation, contract
  docs, release notes, package-consumption validation, and `dotnet pack`.
- Stable contracts and semantic versioning: PASS. Contract changes, corrected
  outcomes, layout compatibility, and semantic version impact are documented in
  `contracts/`.
- Test-driven production quality: PASS. Unit, contract, integration,
  concurrency, recovery, disposal, rollover, churn benchmark, documentation, and
  package validation are planned.
- .NET 10 baseline and portability: PASS. Implementation remains C#/.NET 10
  while shared semantics are described in language-neutral terms for future C++
  and Python implementations.
- Minimal observable design: PASS. Runtime dependencies remain BCL-only.
  Diagnostics and recovery reports are caller-controlled, and tombstone
  pressure management is synchronous and bounded rather than hidden background
  work.

## Project Structure

### Documentation (this feature)

```text
specs/004-store-reliability-hardening/
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- owner-recovery-contract.md
|   |-- disposal-rollover-contract.md
|   `-- index-health-contract.md
`-- tasks.md             # Phase 2 output from /speckit-tasks
```

### Source Code and Validation (repository root)

```text
src/
`-- SharedMemoryStore/
    |-- SharedMemoryStore.cs
    |-- SharedMemoryStoreOptions.cs
    |-- StoreStatus.cs
    |-- ValueLease.cs
    |-- Diagnostics/
    |   |-- DiagnosticsSnapshot.cs
    |   `-- StoreDiagnostics.cs
    |-- Ingest/
    |   |-- ReservationRecovery.cs
    |   `-- ValueReservation.cs
    |-- Layout/
    |   |-- LayoutConstants.cs
    |   |-- SharedKeyIndex.cs
    |   `-- SharedRecords.cs
    |-- Leasing/
    |   |-- LeaseRecovery.cs
    |   |-- LeaseRegistry.cs
    |   `-- LeaseRelease.cs
    `-- Slots/
        |-- ReusableSlotTable.cs
        `-- SlotReclaimer.cs

tests/
|-- SharedMemoryStore.UnitTests/
|   |-- LeaseRecoveryOwnershipTests.cs
|   |-- StoreDisposalRaceTests.cs
|   |-- ProbeRolloverTests.cs
|   |-- SlotLifecycleIdentifierTests.cs
|   `-- IndexHealthTests.cs
|-- SharedMemoryStore.ContractTests/
|   |-- ReliabilityApiContractTests.cs
|   |-- LifecycleOutcomeContractTests.cs
|   |-- SharedMemoryLayoutContractTests.cs
|   `-- DiagnosticsContractTests.cs
`-- SharedMemoryStore.IntegrationTests/
    |-- MultiOwnerLeaseRecoveryIntegrationTests.cs
    |-- StoreDisposalRaceIntegrationTests.cs
    |-- RolloverStressIntegrationTests.cs
    `-- TombstonePressureIntegrationTests.cs

benchmarks/
`-- SharedMemoryStore.Benchmarks/
    |-- TombstonePressureBenchmarks.cs
    |-- RecoveryOwnershipBenchmarks.cs
    `-- LifecycleRolloverBenchmarks.cs

docs/
|-- lifecycle.md
|-- diagnostics.md
|-- errors.md
|-- performance.md
|-- portability.md
`-- releases.md
```

**Structure Decision**: Keep runtime changes inside the existing
`src/SharedMemoryStore` package. Owner recovery remains under `Leasing/`;
disposal outcomes are centralized in the store lifecycle paths and token
operations; rollover-safe probing and lifecycle identifiers remain in
`Slots/`, `Leasing/`, and `Layout/`; tombstone health lives with
`SharedKeyIndex` and `Diagnostics/`. Tests and benchmarks extend existing
projects so reliability behavior is validated beside current publish, reserve,
acquire, remove, release, recovery, diagnostics, sample, and package flows.

## Complexity Tracking

No constitution violations are planned.

## Phase 0 Research Summary

See [research.md](research.md).

Key decisions:
- Enforce exact owner policy for lease recovery: current-process recovery may
  recover current-process and stale-owner leases, but must skip other live
  owners.
- Expand lease recovery reporting and diagnostics so recovered, active,
  unsupported, and unsafe records are consumer-visible.
- Centralize disposal race handling so public operations and token operations
  return documented outcomes instead of surfacing internal lifecycle
  exceptions.
- Replace rollover-prone probe arithmetic and lifecycle identifiers with
  wrap-safe bounded search and stale-proof lifecycle identity.
- Add tombstone health diagnostics and benchmark-driven synchronous internal
  index maintenance before considering a public maintenance API.
- Keep the runtime dependency surface unchanged.

## Phase 1 Design Summary

See [data-model.md](data-model.md),
[contracts/owner-recovery-contract.md](contracts/owner-recovery-contract.md),
[contracts/disposal-rollover-contract.md](contracts/disposal-rollover-contract.md),
[contracts/index-health-contract.md](contracts/index-health-contract.md), and
[quickstart.md](quickstart.md).

Post-design constitution check remains PASS:
- The design is package-first and keeps the runtime BCL-only.
- Public recovery, lifecycle, rollover, and diagnostics semantics are captured
  as contracts with compatibility impact.
- Tests, benchmarks, docs, packaging, and package-consumption validation are
  part of the planned completion criteria.
- Diagnostics and maintenance remain caller-visible and consumer-controlled;
  no hidden background work or console output is introduced.
