# Tasks: Zero-Copy Frame Ingest

**Input**: Design documents from `specs/003-zero-copy-ingest/`

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`,
`contracts/reservation-api.md`, `contracts/ingest-layout.md`,
`contracts/diagnostics-and-errors.md`, `quickstart.md`

**Tests**: Required. This feature changes public library behavior, shared-memory
layout semantics, diagnostics, package contracts, allocation behavior, and
reader safety guarantees.

**Organization**: Tasks are grouped by user story so each story can be
implemented and validated as an independently testable increment.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and has no
  dependency on incomplete same-phase work.
- **[Story]**: Required only for user story phases. Format is `[US1]`,
  `[US2]`, `[US3]`, or `[US4]`.
- Every task includes exact repository paths.

## Path Conventions

- **Library source**: `src/SharedMemoryStore/`
- **Ingest source**: `src/SharedMemoryStore/Ingest/`
- **Unit tests**: `tests/SharedMemoryStore.UnitTests/`
- **Contract tests**: `tests/SharedMemoryStore.ContractTests/`
- **Integration tests**: `tests/SharedMemoryStore.IntegrationTests/`
- **Benchmarks**: `benchmarks/SharedMemoryStore.Benchmarks/`
- **Samples**: `samples/ZeroCopyIngest/`
- **Consumer docs/examples**: `docs/`

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add the feature-specific source, test, sample, and solution anchors
that later phases fill in.

- [X] T001 [P] Create the ingest source anchor in `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T002 [P] Create reservation state unit test scaffold in `tests/SharedMemoryStore.UnitTests/ReservationStateTests.cs`
- [X] T003 [P] Create reservation API contract test scaffold in `tests/SharedMemoryStore.ContractTests/ReservationApiContractTests.cs`
- [X] T004 [P] Create direct ingest integration test scaffold in `tests/SharedMemoryStore.IntegrationTests/ZeroCopyIngestIntegrationTests.cs`
- [X] T005 [P] Create zero-copy ingest sample project in `samples/ZeroCopyIngest/ZeroCopyIngest.csproj`
- [X] T006 Add the zero-copy ingest sample project to `SharedMemoryStore.slnx`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish shared public contract, layout, diagnostics, and
allocation-safe primitives required by all user stories.

**Critical**: No user story work should begin until this phase is complete.

- [X] T007 [P] Append reservation `StoreStatus` values and XML documentation in `src/SharedMemoryStore/StoreStatus.cs`
- [X] T008 [P] Add appended `StoreStatus` numeric contract assertions in `tests/SharedMemoryStore.ContractTests/PublicApiContractTests.cs`
- [X] T009 [P] Set the ingest layout minor version and document `SlotPublishing` reservation semantics in `src/SharedMemoryStore/Layout/LayoutConstants.cs`
- [X] T010 [P] Add layout version and slot state contract tests in `tests/SharedMemoryStore.ContractTests/IngestLayoutContractTests.cs`
- [X] T011 Reset reservation progress, publisher process id, and slot metadata consistently in `src/SharedMemoryStore/Slots/ReusableSlotTable.cs`
- [X] T012 [P] Add allocation-safe writable payload memory helper skeleton in `src/SharedMemoryStore/Ingest/ReservationMemoryManager.cs`
- [X] T013 [P] Add public reservation recovery option and report record structs in `src/SharedMemoryStore/Ingest/ReservationRecovery.cs`
- [X] T014 [P] Add reservation diagnostic properties to `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`
- [X] T015 Extend diagnostic counter storage for appended reservation statuses in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T016 [P] Add BCL-only dependency/package guard coverage in `tests/SharedMemoryStore.ContractTests/PackageContractTests.cs`

**Checkpoint**: Foundation ready. User story implementation can now proceed in
priority order or in parallel where staffing allows.

---

## Phase 3: User Story 1 - Ingest Frames Directly into Shared Memory (Priority: P1)

**Goal**: A producer reserves one key, fixed descriptor, and announced payload
length, fills store-owned memory directly, advances exact progress, and commits
one complete immutable value visible to readers.

**Independent Test**: Reserve storage for a length-delimited frame, fill through
`GetSpan` or `GetMemory`, advance exactly the payload length, commit, and verify
the acquired value and descriptor match while no pending bytes were visible.

### Tests for User Story 1

Write these tests first and verify they fail before implementation.

- [X] T017 [P] [US1] Add reflection contract tests for `TryReserve` and `ValueReservation` in `tests/SharedMemoryStore.ContractTests/ReservationApiContractTests.cs`
- [X] T018 [P] [US1] Add key, payload length, descriptor length, duplicate key, disposed store, and full store validation tests in `tests/SharedMemoryStore.UnitTests/ReservationValidationTests.cs`
- [X] T019 [P] [US1] Add pending-key invisibility and duplicate pending reservation tests in `tests/SharedMemoryStore.UnitTests/ReservationStateTests.cs`
- [X] T020 [P] [US1] Add direct span and memory fill integration tests in `tests/SharedMemoryStore.IntegrationTests/ZeroCopyIngestIntegrationTests.cs`
- [X] T021 [P] [US1] Add steady-state zero allocation direct ingest tests in `tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs`
- [X] T022 [P] [US1] Add exact-byte commit and over-advance tests in `tests/SharedMemoryStore.UnitTests/ReservationStateTests.cs`

### Implementation for User Story 1

- [X] T023 [P] [US1] Implement the public `ValueReservation` lifecycle token in `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T024 [P] [US1] Implement per-slot `Memory<byte>` backing without per-frame allocation in `src/SharedMemoryStore/Ingest/ReservationMemoryManager.cs`
- [X] T025 [US1] Add `TryReserve` validation, duplicate detection, pending index insertion, and reservation creation in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T026 [US1] Add reservation progress tracking and `Advance` state validation in `src/SharedMemoryStore/Slots/ReusableSlotTable.cs`
- [X] T027 [US1] Add exact-length reservation commit transition and key index handling in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T028 [US1] Ensure pending reservations acquire as `NotFound` until commit in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T029 [US1] Add XML documentation for reservation lifetime, writable memory ownership, descriptor immutability, and exact-byte commit rules in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T030 [US1] Add direct length-prefixed socket-style ingest example in `samples/ZeroCopyIngest/Program.cs`

**Checkpoint**: User Story 1 is the MVP and should be independently functional.

---

## Phase 4: User Story 2 - Publish Already Buffered Segmented Frames Efficiently (Priority: P2)

**Goal**: Publish frames already available as one or more read segments without
flattening them into a temporary full-payload array.

**Independent Test**: Publish a `ReadOnlySequence<byte>` split across at least
16 segments and verify the stored value matches the logical concatenation while
allocation tracking shows no temporary full-frame array.

### Tests for User Story 2

Write these tests first and verify they fail before implementation.

- [X] T031 [P] [US2] Add multi-segment and one-segment unit tests in `tests/SharedMemoryStore.UnitTests/SegmentedPublishTests.cs`
- [X] T032 [P] [US2] Add `TryPublishSegments` public API contract tests in `tests/SharedMemoryStore.ContractTests/ReservationApiContractTests.cs`
- [X] T033 [P] [US2] Add 16-segment and one-segment integration tests in `tests/SharedMemoryStore.IntegrationTests/SegmentedFrameIntegrationTests.cs`
- [X] T034 [P] [US2] Add no-temporary-full-payload allocation tests in `tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs`

### Implementation for User Story 2

- [X] T035 [P] [US2] Implement segment copy orchestration over the reservation path in `src/SharedMemoryStore/Ingest/SegmentedPublisher.cs`
- [X] T036 [US2] Add `TryPublishSegments` public method using `ReadOnlySequence<byte>` in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T037 [US2] Abort active reservations on segmented copy, advance, or commit failure in `src/SharedMemoryStore/Ingest/SegmentedPublisher.cs`
- [X] T038 [US2] Return deterministic segmented publish statuses and `copiedBytes` outcomes in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T039 [US2] Add segmented buffered frame example to `samples/ZeroCopyIngest/Program.cs`
- [X] T040 [US2] Document the segmented `ReadOnlySequence<byte>` workflow and allocation contract in `docs/examples.md`

**Checkpoint**: User Stories 1 and 2 should both work independently.

---

## Phase 5: User Story 3 - Abort and Recover Incomplete Frame Writes (Priority: P3)

**Goal**: A producer can abort an incomplete reservation, dispose an active
reservation safely, and an owner can explicitly recover stale reservations
without exposing corrupt or partial bytes.

**Independent Test**: Reserve, partially write, abort, verify the key is not
visible, verify the slot becomes reusable, then recover a controlled stale
reservation and inspect the recovery report plus diagnostics.

### Tests for User Story 3

Write these tests first and verify they fail before implementation.

- [X] T041 [P] [US3] Add abort, dispose, repeated commit, repeated abort, and commit-after-abort state tests in `tests/SharedMemoryStore.UnitTests/ReservationStateTests.cs`
- [X] T042 [P] [US3] Add reservation status and diagnostics taxonomy tests in `tests/SharedMemoryStore.ContractTests/ErrorTaxonomyContractTests.cs`
- [X] T043 [P] [US3] Add explicit stale reservation recovery integration tests in `tests/SharedMemoryStore.IntegrationTests/ReservationRecoveryIntegrationTests.cs`
- [X] T044 [P] [US3] Add abort and recovery diagnostic counter tests in `tests/SharedMemoryStore.UnitTests/ReservationValidationTests.cs`
- [X] T045 [P] [US3] Add failure-injection allocation tests for abort, failed commit, and recovery scan in `tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs`

### Implementation for User Story 3

- [X] T046 [US3] Implement `Abort` and `Dispose` completion semantics in `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T047 [US3] Remove pending key index entries before reclaiming aborted reservations in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [X] T048 [US3] Implement stale reservation scanning and owner-policy evaluation in `src/SharedMemoryStore/Ingest/ReservationRecovery.cs`
- [X] T049 [US3] Add `TryRecoverReservations` public method and report population in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T050 [US3] Record abort, failed commit, invalid reservation, incomplete reservation, repeated completion, and recovery diagnostics in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T051 [US3] Document abort, dispose, stale recovery, and producer ownership rules in `docs/lifecycle.md`

**Checkpoint**: User Stories 1 through 3 should be independently functional.

---

## Phase 6: User Story 4 - Preserve Reader Safety and Existing Store Workflows (Priority: P4)

**Goal**: Existing byte-oriented publish, acquire, lease release, remove, reuse,
diagnostics, and packaging workflows remain compatible while committed ingest
values follow the same immutable reader contract.

**Independent Test**: Publish values through both `TryPublish` and reservation
commit, acquire them from multiple readers, remove committed ingest values while
leases are held, and verify storage is not reused until final release.

### Tests for User Story 4

Write these tests first and verify they fail before implementation where new
behavior is not yet present.

- [X] T052 [P] [US4] Add mixed simple publish and ingest acquire/remove integration tests in `tests/SharedMemoryStore.IntegrationTests/ZeroCopyIngestIntegrationTests.cs`
- [X] T053 [P] [US4] Add reader visibility and remove-while-leased concurrency stress tests in `tests/SharedMemoryStore.IntegrationTests/IngestVisibilityConcurrencyTests.cs`
- [X] T054 [P] [US4] Extend simple publish compatibility assertions in `tests/SharedMemoryStore.ContractTests/PublishContractTests.cs`
- [X] T055 [P] [US4] Add package consumption coverage for reservation APIs in `tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs`

### Implementation for User Story 4

- [X] T056 [US4] Preserve `TryPublish` statuses, descriptor behavior, and allocation contract while integrating with ingest internals in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T057 [US4] Ensure committed reservation values obey remove-while-leased slot reuse rules in `src/SharedMemoryStore/Slots/SlotReclaimer.cs`
- [X] T058 [US4] Keep `ValueLease` value and descriptor spans immutable for committed ingest values in `src/SharedMemoryStore/ValueLease.cs`
- [X] T059 [US4] Add trusted same-host service boundary and future C++/Python portability guidance in `docs/portability.md`
- [X] T060 [US4] Update the public usage guide with direct ingest, segmented publish, reader acquire, remove, and release workflows in `docs/usage.md`

**Checkpoint**: All user stories should be independently functional and
compatible with existing package behavior.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Performance validation, documentation completeness, package
metadata, samples, and release readiness across all stories.

- [X] T061 [P] Add direct ingest allocation benchmark in `benchmarks/SharedMemoryStore.Benchmarks/DirectIngestAllocationBenchmarks.cs`
- [X] T062 [P] Add direct ingest frame throughput benchmark in `benchmarks/SharedMemoryStore.Benchmarks/DirectIngestFrameThroughputBenchmarks.cs`
- [X] T063 [P] Add segmented publish benchmark in `benchmarks/SharedMemoryStore.Benchmarks/SegmentedPublishBenchmarks.cs`
- [X] T064 Record ingest benchmark dimensions in `benchmarks/SharedMemoryStore.Benchmarks/BenchmarkEnvironment.cs`
- [X] T065 Update performance documentation with ingest benchmark commands and required result fields in `docs/performance.md`
- [X] T066 Update public documentation navigation and release notes in `README.md`, `docs/index.md`, and `CHANGELOG.md`
- [X] T067 Update package release notes and metadata for the additive minor feature in `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [X] T068 Extend clean package consumption validation with reservation and segmented publish smoke coverage in `scripts/validate-package-consumption.ps1`
- [X] T069 Add zero-copy ingest sample walkthrough in `samples/ZeroCopyIngest/README.md`
- [X] T070 Run the quickstart validation matrix from `specs/003-zero-copy-ingest/quickstart.md` and record release-readiness notes in `docs/releases.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1: Setup** has no dependencies.
- **Phase 2: Foundational** depends on Phase 1 and blocks all user stories.
- **Phase 3: User Story 1** depends on Phase 2 and is the MVP.
- **Phase 4: User Story 2** depends on Phase 2 and uses the reservation path
  from User Story 1 for the intended implementation.
- **Phase 5: User Story 3** depends on Phase 2 and integrates most naturally
  after User Story 1.
- **Phase 6: User Story 4** depends on Phase 2 and should be finalized after
  the committed-value behavior from User Stories 1 through 3 is stable.
- **Phase 7: Polish** depends on all desired user stories for the release scope.

### User Story Dependencies

- **US1 (P1)**: No dependency on other user stories after Phase 2.
- **US2 (P2)**: Can be designed after Phase 2, but implementation should reuse
  the US1 reservation path.
- **US3 (P3)**: Can be designed after Phase 2, but abort and recovery behavior
  depends on US1 reservation state.
- **US4 (P4)**: Validates compatibility across US1, US2, US3, and existing
  workflows.

### Within Each User Story

- Tests must be written and observed failing before implementation.
- Public API and contract tests come before implementation internals.
- Core lifecycle implementation comes before diagnostics and documentation.
- Allocation and concurrency checks come before marking the story complete.
- Each story is validated independently before starting the next priority when
  working sequentially.

## Parallel Opportunities

- Setup scaffolds T001 through T005 can run in parallel.
- Foundational contract and diagnostic tasks T007 through T010, T012 through
  T014, and T016 can run in parallel.
- User story test files marked `[P]` can be written in parallel before
  implementation.
- US2 segmented helper work can proceed in parallel with US3 recovery work after
  US1 establishes the reservation path.
- Benchmark tasks T061 through T063 can run in parallel after the related APIs
  compile.

## Parallel Example: User Story 1

```text
Task: "T017 [P] [US1] Add reflection contract tests for TryReserve and ValueReservation in tests/SharedMemoryStore.ContractTests/ReservationApiContractTests.cs"
Task: "T018 [P] [US1] Add key, payload length, descriptor length, duplicate key, disposed store, and full store validation tests in tests/SharedMemoryStore.UnitTests/ReservationValidationTests.cs"
Task: "T020 [P] [US1] Add direct span and memory fill integration tests in tests/SharedMemoryStore.IntegrationTests/ZeroCopyIngestIntegrationTests.cs"
Task: "T021 [P] [US1] Add steady-state zero allocation direct ingest tests in tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs"
```

## Parallel Example: User Story 2

```text
Task: "T031 [P] [US2] Add multi-segment and one-segment unit tests in tests/SharedMemoryStore.UnitTests/SegmentedPublishTests.cs"
Task: "T032 [P] [US2] Add TryPublishSegments public API contract tests in tests/SharedMemoryStore.ContractTests/ReservationApiContractTests.cs"
Task: "T033 [P] [US2] Add 16-segment and one-segment integration tests in tests/SharedMemoryStore.IntegrationTests/SegmentedFrameIntegrationTests.cs"
Task: "T034 [P] [US2] Add no-temporary-full-payload allocation tests in tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs"
```

## Parallel Example: User Story 3

```text
Task: "T041 [P] [US3] Add abort, dispose, repeated commit, repeated abort, and commit-after-abort state tests in tests/SharedMemoryStore.UnitTests/ReservationStateTests.cs"
Task: "T042 [P] [US3] Add reservation status and diagnostics taxonomy tests in tests/SharedMemoryStore.ContractTests/ErrorTaxonomyContractTests.cs"
Task: "T043 [P] [US3] Add explicit stale reservation recovery integration tests in tests/SharedMemoryStore.IntegrationTests/ReservationRecoveryIntegrationTests.cs"
Task: "T045 [P] [US3] Add failure-injection allocation tests for abort, failed commit, and recovery scan in tests/SharedMemoryStore.UnitTests/ReservationAllocationTests.cs"
```

## Parallel Example: User Story 4

```text
Task: "T052 [P] [US4] Add mixed simple publish and ingest acquire/remove integration tests in tests/SharedMemoryStore.IntegrationTests/ZeroCopyIngestIntegrationTests.cs"
Task: "T053 [P] [US4] Add reader visibility and remove-while-leased concurrency stress tests in tests/SharedMemoryStore.IntegrationTests/IngestVisibilityConcurrencyTests.cs"
Task: "T054 [P] [US4] Extend simple publish compatibility assertions in tests/SharedMemoryStore.ContractTests/PublishContractTests.cs"
Task: "T055 [P] [US4] Add package consumption coverage for reservation APIs in tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs"
```

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 setup.
2. Complete Phase 2 foundation.
3. Complete Phase 3 User Story 1.
4. Validate direct reserve/fill/advance/commit/acquire behavior independently.
5. Run the US1 unit, contract, integration, and allocation tests before adding
   segmented publish or recovery.

### Incremental Delivery

1. Deliver US1 direct frame ingest as the MVP.
2. Add US2 segmented publish over the same reservation path.
3. Add US3 abort, dispose, and explicit recovery hardening.
4. Add US4 compatibility and reader-safety validation across existing workflows.
5. Complete benchmarks, docs, sample, package metadata, and quickstart matrix.

### Validation Commands

```powershell
dotnet test tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj -c Release
dotnet test tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj -c Release
dotnet test tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj -c Release
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *DirectIngest*
dotnet run --project benchmarks/SharedMemoryStore.Benchmarks -c Release -- --filter *SegmentedPublish*
dotnet run --project samples/ZeroCopyIngest/ZeroCopyIngest.csproj -c Release
dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package
powershell -ExecutionPolicy Bypass -File scripts/validate-package-consumption.ps1
```

## Phase 8: Convergence

- [X] T071 Run the full direct-ingest and simple-publish sustained throughput benchmarks and record the relative comparison in `docs/releases.md` per SC-006/T070
- [X] T072 Add a 1,000,000-cycle reserve/fill/commit/acquire visibility stress validation with concurrent readers and producers in `tests/SharedMemoryStore.IntegrationTests/IngestVisibilityConcurrencyTests.cs` per SC-004
- [X] T073 Add a 100,000-cycle failure-injection validation for abort, dispose, failed commit, explicit recovery, and capacity reclamation in `tests/SharedMemoryStore.IntegrationTests/ReservationRecoveryIntegrationTests.cs` or `benchmarks/SharedMemoryStore.Benchmarks/` per SC-005
- [X] T074 Add an explicit 100,000-frame direct-ingest allocation validation result using `BenchmarkEnvironment.DirectIngestAllocationFrames` in `benchmarks/SharedMemoryStore.Benchmarks/DirectIngestAllocationBenchmarks.cs` and document the result fields per SC-001
- [X] T075 Add runnable length-prefixed socket-style and `System.IO.Pipelines` adapter examples plus a separate reader example, and cover them in clean consumer validation where practical, per FR-015/FR-017/LC-009/SC-008
- [X] T076 Document the trusted same-host service boundary and the lack of protection against malicious in-boundary writers in `docs/portability.md` and linked usage guidance per FR-016/LC-008
- [X] T077 Extend reservation recovery diagnostics to expose active, unsupported, and failed recovery result counts in `DiagnosticsSnapshot`, docs, and tests per FR-013/SC-009


