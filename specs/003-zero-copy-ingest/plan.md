# Implementation Plan: Zero-Copy Frame Ingest

**Branch**: `003-zero-copy-ingest` | **Date**: 2026-07-02 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/003-zero-copy-ingest/spec.md`

## Summary

Extend SharedMemoryStore with an additive reservation-based ingest workflow for
length-delimited frames whose payload length and descriptor bytes are known
before payload bytes are read. A producer reserves one key, payload length, and
fixed descriptor; receives writable store-owned payload memory; advances the
reservation as bytes are written; then commits atomically so readers see either
no value or the complete immutable value. The same reservation path also backs a
segmented publish helper that copies multiple existing read segments into one
store value without allocating a temporary full-frame array.

The design preserves the existing byte-oriented `TryPublish`, `TryAcquire`,
`ValueLease`, remove, release, diagnostics, slot reuse, and package workflows.
Reserved storage remains invisible to readers until commit, duplicate keys are
blocked while a reservation is pending, incomplete writes can be aborted or
explicitly recovered, and all expected failures return deterministic statuses
without direct console output or hidden background cleanup.

## Technical Context

**Language/Version**: C# targeting `net10.0`. Public reservation lifecycle,
state machine, layout, and error outcomes are documented in a language-neutral
form for future C++ and Python implementations.

**Primary Dependencies**: Runtime package remains .NET BCL only. Core APIs use
existing memory-mapped-file, span, memory, atomic, and `System.Buffers`
primitives. `System.IO.Pipelines` usage is documented and tested only through
examples or sample adapters layered over the core reservation contract; it is
not the definition of shared-memory behavior and must not become a runtime
dependency unless a later plan justifies it.

**Storage**: Existing named OS memory-mapped region with fixed reusable slots,
shared key index, lease registry, descriptor storage, payload storage, and slot
metadata. The ingest feature reuses the existing slot payload/descriptor regions
and treats `SlotPublishing` as the pending reservation state. Slot metadata
tracks reservation write progress while pending.

**Testing**: `dotnet test` with unit, contract, and integration coverage.
Validation adds reservation lifecycle tests, segmented publish tests, exact-byte
commit checks, stale reservation recovery, concurrent reader/producer safety,
allocation checks, package-consumption coverage, samples, and Release
benchmarks.

**Target Platform**: .NET 10 supported platforms with named memory-mapped-file
support. Windows x64 remains the first validated production target for direct
frame ingest. Unsupported or partially supported platforms return deterministic
open, operation, or recovery statuses.

**Project Type**: Reusable NuGet package/library with public XML documentation,
contract documentation, benchmarks, samples, and package metadata updates.

**Performance Goals**: After initialization and warm-up, direct frame ingest
allocates 0 managed heap bytes per frame for payload storage, avoids an
application-level payload copy before publication, and sustains at least the
same frame rate as the existing simple publish benchmark for 1.3 MB values.
Segmented publish stores frames split across at least 16 segments without a
temporary full-payload array. Readers never observe partial reservation bytes
across the concurrency cycles required by the spec.

**Constraints**: Additive public API only; existing simple publish/acquire
behavior remains compatible. Reservations are bounded by configured key,
descriptor, value, slot, and lease limits. Descriptor bytes are fixed at
reservation time. Commit requires the producer to advance exactly the announced
payload length. Cleanup is explicit and consumer-controlled; no hidden
background worker, global mutable configuration, direct console output, or
runtime dependency is introduced. The trusted same-host service boundary remains
the security model.

**Scale/Scope**: Initial validation targets one named store, configurable fixed
slots, about 1.3 MB frame payloads, one or more producers, concurrent readers,
at least 100,000 allocation-sensitive direct-ingest frames, at least 1,000,000
reserve/fill/commit/acquire visibility cycles, and 100,000 failure-injection
cycles for abort, dispose, failed commit, and explicit recovery.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

- Library/package first: PASS. The ingest workflow is a reusable store
  capability, not a protocol-specific socket reader or application workflow.
- NuGet deliverable: PASS. The feature adds public APIs, XML documentation,
  examples, package release notes, and clean package-consumption validation.
- Stable contracts: PASS. Public reservation API, segmented publish behavior,
  shared-memory state rules, diagnostics, status values, and semantic version
  impact are captured in `contracts/`.
- .NET 10 baseline with portability: PASS. Implementation targets C#/.NET 10
  while documenting the reservation state machine and layout rules for future
  C++ and Python consumers.
- Test coverage: PASS. Unit, contract, integration, concurrency, recovery,
  allocation, benchmark, sample, and package-consumption validation are planned.
- Dependency discipline: PASS. Runtime remains BCL-only; optional pipeline
  examples stay outside the runtime contract.
- Diagnostics and resource ownership: PASS. Reservation ownership, writable
  memory lifetime, commit/abort decisions, recovery, reader leases, removal,
  and diagnostics are consumer-controlled and documented.

## Project Structure

### Documentation (this feature)

```text
specs/003-zero-copy-ingest/
|-- plan.md
|-- research.md
|-- data-model.md
|-- quickstart.md
|-- contracts/
|   |-- reservation-api.md
|   |-- ingest-layout.md
|   `-- diagnostics-and-errors.md
`-- tasks.md
```

### Source Code and Validation (repository root)

```text
src/
`-- SharedMemoryStore/
    |-- SharedMemoryStore.cs
    |-- SharedMemoryStoreOptions.cs
    |-- StoreStatus.cs
    |-- ValueLease.cs
    |-- Ingest/
    |   |-- ValueReservation.cs
    |   |-- ReservationRecovery.cs
    |   |-- ReservationMemoryManager.cs
    |   `-- SegmentedPublisher.cs
    |-- Diagnostics/
    |-- Interop/
    |-- Layout/
    |-- Leasing/
    |-- Options/
    `-- Slots/

tests/
|-- SharedMemoryStore.UnitTests/
|   |-- ReservationStateTests.cs
|   |-- ReservationValidationTests.cs
|   |-- SegmentedPublishTests.cs
|   `-- ReservationAllocationTests.cs
|-- SharedMemoryStore.ContractTests/
|   |-- ReservationApiContractTests.cs
|   |-- IngestLayoutContractTests.cs
|   `-- ErrorTaxonomyContractTests.cs
`-- SharedMemoryStore.IntegrationTests/
    |-- ZeroCopyIngestIntegrationTests.cs
    |-- ReservationRecoveryIntegrationTests.cs
    |-- SegmentedFrameIntegrationTests.cs
    `-- IngestVisibilityConcurrencyTests.cs

benchmarks/
`-- SharedMemoryStore.Benchmarks/
    |-- DirectIngestAllocationBenchmarks.cs
    |-- DirectIngestFrameThroughputBenchmarks.cs
    `-- SegmentedPublishBenchmarks.cs

samples/
|-- BasicUsage/
|-- FrameValue/
`-- ZeroCopyIngest/

docs/
|-- usage.md
|-- lifecycle.md
|-- diagnostics.md
|-- performance.md
|-- portability.md
`-- examples.md
```

**Structure Decision**: Keep the runtime in the existing
`src/SharedMemoryStore` package and add an `Ingest/` responsibility area for
reservation tokens, progress tracking, recovery, and segmented publishing. The
existing slot, layout, lease, and diagnostics components remain the authority
for shared-memory state. Tests and benchmarks are extended in their current
projects so the new workflow is validated beside existing publish/acquire
behavior. A sample demonstrates direct socket-style receive and segmented
pipeline-style publication without making pipeline APIs the core contract.

## Complexity Tracking

No constitution violations are planned.

## Phase 0 Research Summary

See [research.md](research.md).

Key decisions:
- Use fixed-slot reservations as the core zero-copy ingest primitive.
- Expose writable store-owned payload memory through span and memory views, with
  per-slot backing objects allocated during create/open rather than per frame.
- Insert the key at reservation time and keep `SlotPublishing` invisible to
  readers until commit.
- Track write progress through `Advance` and require exact payload length before
  commit.
- Implement segmented publish over the reservation path using `ReadOnlySequence<byte>`.
- Keep stale reservation cleanup explicit and owner-controlled.
- Keep runtime dependencies unchanged and document socket/pipeline examples as
  adapters over the core reservation contract.

## Phase 1 Design Summary

See [data-model.md](data-model.md),
[contracts/reservation-api.md](contracts/reservation-api.md),
[contracts/ingest-layout.md](contracts/ingest-layout.md),
[contracts/diagnostics-and-errors.md](contracts/diagnostics-and-errors.md), and
[quickstart.md](quickstart.md).

Post-design constitution check remains PASS:
- The API is package-first and protocol-neutral.
- Existing reader lease and byte publish semantics are preserved.
- Contract additions, semantic version impact, and layout minor-version rules
  are documented.
- No runtime dependency or hidden background cleanup is introduced.
- Validation covers tests, benchmarks, recovery, samples, package consumption,
  and documentation updates.
