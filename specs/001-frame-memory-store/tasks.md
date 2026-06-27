# Tasks: Shared Memory Value Store

**Input**: Design documents from `specs/001-frame-memory-store/`

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`, `contracts/public-api.md`, `contracts/shared-memory-layout.md`, `contracts/error-taxonomy.md`, `quickstart.md`

**Tests**: Required. This is behavior-changing library work governed by the project constitution, so each user story includes unit, contract, integration, and relevant benchmark or stress validation tasks before implementation tasks.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing. Setup and foundational phases must complete before user story implementation starts.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Create the solution, package, test, benchmark, sample, and documentation structure from the implementation plan.

- [ ] T001 Create solution file `SharedMemoryStore.sln` with `src/`, `tests/`, `benchmarks/`, and `samples/` project entries
- [ ] T002 Create library project `src/SharedMemoryStore/SharedMemoryStore.csproj` targeting `net10.0` with BCL-only runtime dependencies
- [ ] T003 [P] Create unit test project `tests/SharedMemoryStore.UnitTests/SharedMemoryStore.UnitTests.csproj` with xUnit and Microsoft.NET.Test.Sdk
- [ ] T004 [P] Create contract test project `tests/SharedMemoryStore.ContractTests/SharedMemoryStore.ContractTests.csproj` with xUnit and Microsoft.NET.Test.Sdk
- [ ] T005 [P] Create integration test project `tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj` with xUnit and Microsoft.NET.Test.Sdk
- [ ] T006 [P] Create benchmark project `benchmarks/SharedMemoryStore.Benchmarks/SharedMemoryStore.Benchmarks.csproj` with BenchmarkDotNet outside runtime dependencies
- [ ] T007 [P] Create basic usage sample project `samples/BasicUsage/BasicUsage.csproj` targeting `net10.0`
- [ ] T008 [P] Create frame value sample project `samples/FrameValue/FrameValue.csproj` targeting `net10.0`
- [ ] T009 Configure package metadata, nullable annotations, XML documentation, deterministic builds, and release properties in `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [ ] T010 Create initial documentation files `docs/lifecycle.md`, `docs/packaging.md`, and `docs/portability.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Establish public contracts, layout primitives, diagnostics, mapped-region ownership, and shared test infrastructure required by every user story.

**Critical**: No user story work can begin until this phase is complete.

- [ ] T011 [P] Add shared test store naming and cleanup fixtures in `tests/SharedMemoryStore.UnitTests/TestSupport/StoreTestNames.cs`
- [ ] T012 [P] Add allocation measurement helpers in `tests/SharedMemoryStore.UnitTests/TestSupport/AllocationAssert.cs`
- [ ] T013 [P] Add public API and status enum contract test skeletons in `tests/SharedMemoryStore.ContractTests/PublicApiContractTests.cs`
- [ ] T014 Define `StoreOpenStatus` and `StoreStatus` public enums from the error taxonomy in `src/SharedMemoryStore/StoreStatus.cs`
- [ ] T015 Define `SharedMemoryStoreOptions` and `OpenMode` public configuration contracts in `src/SharedMemoryStore/SharedMemoryStoreOptions.cs`
- [ ] T016 Implement option validation for names, capacity, slot count, key size, descriptor size, value size, and lease record count in `src/SharedMemoryStore/Options/SharedMemoryStoreOptionsValidator.cs`
- [ ] T017 Define the disposable `SharedMemoryStore` public shell, create/open entry point, operation stubs, and disposal guard in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [ ] T018 [P] Implement layout version, magic, alignment, section, and state constants in `src/SharedMemoryStore/Layout/LayoutConstants.cs`
- [ ] T019 Implement mapped-region size calculation and section offset validation in `src/SharedMemoryStore/Layout/StoreLayout.cs`
- [ ] T020 [P] Implement unmanaged store header, index entry, slot metadata, and lease record structs in `src/SharedMemoryStore/Layout/SharedRecords.cs`
- [ ] T021 Implement the named memory-mapped file adapter and unsupported-platform detection in `src/SharedMemoryStore/Interop/MemoryMappedStoreRegion.cs`
- [ ] T022 Implement deterministic diagnostic counters and `DiagnosticsSnapshot` in `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`
- [ ] T023 Expose internals to test projects for layout verification in `src/SharedMemoryStore/Properties/AssemblyInfo.cs`
- [ ] T024 Document semantic versioning, layout compatibility, platform support, and future C++/Python rules in `docs/portability.md`

**Checkpoint**: Foundation ready. User story implementation can now begin.

---

## Phase 3: User Story 1 - Publish Values at High Rate (Priority: P1) MVP

**Goal**: A producer can publish keyed binary values with optional descriptor bytes into preallocated shared-memory slots without per-value runtime heap allocation after initialization and warm-up.

**Independent Test**: Initialize a store, publish multiple 1.3 MB values with unique byte keys and descriptors, verify persisted key/value/descriptor bytes through shared layout inspection, and assert steady-state publish allocation is 0 bytes.

### Tests for User Story 1

- [ ] T025 [P] [US1] Add unit tests for key, descriptor, value size boundaries and publish validation in `tests/SharedMemoryStore.UnitTests/PublishValidationTests.cs`
- [ ] T026 [P] [US1] Add unit tests for slot reservation, publish commit, abort, and generation behavior in `tests/SharedMemoryStore.UnitTests/SlotPublishStateTests.cs`
- [ ] T027 [P] [US1] Add contract tests for `TryPublish` success, `DuplicateKey`, `KeyTooLarge`, `ValueTooLarge`, `DescriptorTooLarge`, and `StoreFull` statuses in `tests/SharedMemoryStore.ContractTests/PublishContractTests.cs`
- [ ] T028 [P] [US1] Add integration test for publishing 1.3 MB values and descriptor bytes into a named store in `tests/SharedMemoryStore.IntegrationTests/PublishIntegrationTests.cs`
- [ ] T029 [P] [US1] Add shared-memory layout inspection helper for publish assertions in `tests/SharedMemoryStore.IntegrationTests/TestSupport/SharedMemoryLayoutReader.cs`
- [ ] T030 [P] [US1] Add publish allocation benchmark after warm-up in `benchmarks/SharedMemoryStore.Benchmarks/PublishAllocationBenchmarks.cs`

### Implementation for User Story 1

- [ ] T031 [P] [US1] Implement byte-key validation and stable 64-bit hashing in `src/SharedMemoryStore/Layout/StoreKey.cs`
- [ ] T032 [P] [US1] Implement open-addressed shared key index lookup and insert paths in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [ ] T033 [P] [US1] Implement reusable slot reservation, commit, abort, and generation transitions in `src/SharedMemoryStore/Slots/ReusableSlotTable.cs`
- [ ] T034 [US1] Implement descriptor and payload copy into fixed shared-memory slot sections in `src/SharedMemoryStore/Slots/SlotWriter.cs`
- [ ] T035 [US1] Wire `TryPublish` validation, duplicate detection, slot reservation, index insertion, commit, and rollback in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [ ] T036 [US1] Increment publish failure and capacity pressure counters in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [ ] T037 [US1] Add XML documentation for create/open and publish APIs in `src/SharedMemoryStore/SharedMemoryStore.cs`

**Checkpoint**: User Story 1 is independently testable through publish tests, layout inspection, and allocation benchmark.

---

## Phase 4: User Story 2 - Acquire Values from Processing Services (Priority: P2)

**Goal**: A processing service can acquire a read lease by key, read value and descriptor spans directly from shared memory, and release the lease exactly once while storage remains protected.

**Independent Test**: Publish one value, acquire it from multiple simulated services, verify every reader observes identical bytes without payload copying, release each lease, and verify usage count protection remains until the final release.

### Tests for User Story 2

- [ ] T038 [P] [US2] Add unit tests for lease record reservation, generation validation, and lease table full behavior in `tests/SharedMemoryStore.UnitTests/LeaseRegistryTests.cs`
- [ ] T039 [P] [US2] Add contract tests for `ValueLease.IsValid`, span lengths, `Release()`, `Dispose()`, `InvalidLease`, and `LeaseAlreadyReleased` in `tests/SharedMemoryStore.ContractTests/ValueLeaseContractTests.cs`
- [ ] T040 [P] [US2] Add integration test for multiple readers acquiring the same published value in `tests/SharedMemoryStore.IntegrationTests/MultiReaderAcquireIntegrationTests.cs`
- [ ] T041 [P] [US2] Add concurrency tests for acquire, release, duplicate-key publish, publish/remove, and adjacent-slot races in `tests/SharedMemoryStore.IntegrationTests/AcquireReleaseConcurrencyTests.cs`
- [ ] T042 [P] [US2] Add acquire and release allocation benchmark after warm-up in `benchmarks/SharedMemoryStore.Benchmarks/LeaseAllocationBenchmarks.cs`

### Implementation for User Story 2

- [ ] T043 [P] [US2] Implement the `ValueLease` readonly struct API and release ownership fields in `src/SharedMemoryStore/ValueLease.cs`
- [ ] T044 [P] [US2] Implement lease registry reservation, activation, release, and abandoned state transitions in `src/SharedMemoryStore/Leasing/LeaseRegistry.cs`
- [ ] T045 [US2] Implement `TryAcquire` key lookup, slot generation check, usage increment, and lease record activation in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [ ] T046 [US2] Implement generation-checked lease release and double-release protection in `src/SharedMemoryStore/Leasing/LeaseRelease.cs`
- [ ] T047 [US2] Implement read-only value and descriptor span projection from mapped slot offsets in `src/SharedMemoryStore/Slots/SlotReader.cs`
- [ ] T048 [US2] Increment acquire, release, and lease failure counters in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [ ] T049 [US2] Document lease lifetime, span validity, release ownership, and reader responsibilities in `docs/lifecycle.md`

**Checkpoint**: User Stories 1 and 2 work together and remain independently verifiable through publish and acquire/release tests.

---

## Phase 5: User Story 3 - Remove Values and Reuse Memory (Priority: P3)

**Goal**: Store owners can remove values by key, keep leased values protected until all readers release, and reuse freed slots without increasing configured store memory.

**Independent Test**: Publish values into multiple slots, acquire and remove a leased value, release readers, publish new values, and verify the same slot storage is reused with bounded memory and valid generation changes.

### Tests for User Story 3

- [ ] T050 [P] [US3] Add unit tests for remove with no active leases, remove while leased, final release reclaim, and generation increment in `tests/SharedMemoryStore.UnitTests/RemoveReuseStateTests.cs`
- [ ] T051 [P] [US3] Add contract tests for `TryRemove` success, `NotFound`, `RemovePending`, `InvalidLease`, and `CorruptStore` outcomes in `tests/SharedMemoryStore.ContractTests/RemoveReuseContractTests.cs`
- [ ] T052 [P] [US3] Add integration test for publish, acquire, remove, release, and slot reuse in `tests/SharedMemoryStore.IntegrationTests/RemoveReuseIntegrationTests.cs`
- [ ] T053 [P] [US3] Add stale lease recovery integration tests for supported and unsupported platform outcomes in `tests/SharedMemoryStore.IntegrationTests/LeaseRecoveryIntegrationTests.cs`
- [ ] T054 [P] [US3] Add reuse memory benchmark for one million publish/remove/reuse cycles in `benchmarks/SharedMemoryStore.Benchmarks/ReuseBenchmarks.cs`

### Implementation for User Story 3

- [ ] T055 [P] [US3] Implement remove state transitions for `Published`, `RemoveRequested`, `Reclaiming`, and `Free` slots in `src/SharedMemoryStore/Slots/SlotReclaimer.cs`
- [ ] T056 [P] [US3] Implement index tombstone and key removal behavior in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [ ] T057 [US3] Wire `TryRemove` key lookup, pending removal, immediate reclaim, and deterministic status returns in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [ ] T058 [US3] Integrate final lease release with pending-removal reclaim in `src/SharedMemoryStore/Leasing/LeaseRelease.cs`
- [ ] T059 [US3] Implement reusable free-slot selection without managed allocation in `src/SharedMemoryStore/Slots/ReusableSlotTable.cs`
- [ ] T060 [US3] Implement explicit stale lease recovery API and report struct in `src/SharedMemoryStore/Leasing/LeaseRecovery.cs`
- [ ] T061 [US3] Increment remove, reuse, stale lease, and corruption counters in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [ ] T062 [US3] Document removal, pending removal, stale lease recovery, and owner cleanup responsibilities in `docs/lifecycle.md`

**Checkpoint**: User Stories 1 through 3 deliver the bounded-memory store lifecycle: publish, acquire, release, remove, and reuse.

---

## Phase 6: User Story 4 - Use Frame Data as a Store Value (Priority: P4)

**Goal**: A computation server can represent frame header, metadata, and payload as opaque value and descriptor bytes while the core store remains frame-neutral.

**Independent Test**: Publish frame-shaped 1.3 MB values with descriptor bytes, acquire them from multiple readers, interpret descriptor data outside the core store, remove the values, and verify identical behavior for non-frame payloads.

### Tests for User Story 4

- [ ] T063 [P] [US4] Add integration test for frame-shaped descriptor and payload storage through general APIs in `tests/SharedMemoryStore.IntegrationTests/FrameValueIntegrationTests.cs`
- [ ] T064 [P] [US4] Add contract test proving no frame-specific public API is required in `tests/SharedMemoryStore.ContractTests/FrameNeutralContractTests.cs`
- [ ] T065 [P] [US4] Add frame throughput benchmark for 500 publishes per second over 60 seconds in `benchmarks/SharedMemoryStore.Benchmarks/FrameThroughputBenchmarks.cs`

### Implementation for User Story 4

- [ ] T066 [P] [US4] Implement sample-only frame descriptor builder in `samples/FrameValue/FrameDescriptor.cs`
- [ ] T067 [US4] Implement end-to-end frame publishing, multi-reader acquire, release, remove, and reuse sample in `samples/FrameValue/Program.cs`
- [ ] T068 [US4] Add non-frame comparison path to prove identical store behavior in `samples/FrameValue/Program.cs`
- [ ] T069 [US4] Document frame-as-opaque-value guidance and descriptor ownership in `docs/lifecycle.md`

**Checkpoint**: Frame scenario is validated without adding frame-specific store operations.

---

## Phase 7: User Story 5 - Consume as a General Library (Priority: P5)

**Goal**: A developer can install the package into a clean project, use public APIs only, and observe documented deterministic outcomes for normal and failure scenarios.

**Independent Test**: Pack the library, install it into a clean consumer project, run the documented example, and verify create/open, publish, acquire, release, remove, reuse, and failure-status behavior complete in under 5 minutes.

### Tests for User Story 5

- [ ] T070 [P] [US5] Add package metadata and XML documentation contract tests in `tests/SharedMemoryStore.ContractTests/PackageContractTests.cs`
- [ ] T071 [P] [US5] Add clean consumer package smoke test in `tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs`
- [ ] T072 [P] [US5] Add deterministic failure outcome integration tests for full store, duplicate key, missing key, oversized value, invalid release, and disposed store in `tests/SharedMemoryStore.IntegrationTests/FailureOutcomeIntegrationTests.cs`

### Implementation for User Story 5

- [ ] T073 [US5] Finalize package id, version, authorship, description, tags, README, release notes, and license metadata in `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [ ] T074 [US5] Implement public create/open, publish, acquire, release, remove, diagnostics, and reuse example in `samples/BasicUsage/Program.cs`
- [ ] T075 [US5] Add local package consumption validation script in `scripts/validate-package-consumption.ps1`
- [ ] T076 [US5] Document installation, package creation, clean-project consumption, and release validation in `docs/packaging.md`
- [ ] T077 [US5] Document public failure statuses and caller-owned diagnostics formatting in `docs/lifecycle.md`

**Checkpoint**: The feature is consumable as a general NuGet library using only public contracts and documentation.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Validate release quality, cross-story performance, documentation, formatting, and package readiness.

- [ ] T078 [P] Add corruption detection and safe error-mode tests in `tests/SharedMemoryStore.UnitTests/CorruptStoreTests.cs`
- [ ] T079 [P] Add process-style multi-store lifecycle tests in `tests/SharedMemoryStore.IntegrationTests/MultiStoreLifecycleIntegrationTests.cs`
- [ ] T080 [P] Add benchmark hardware and configuration reporting in `benchmarks/SharedMemoryStore.Benchmarks/BenchmarkEnvironment.cs`
- [ ] T081 Review and complete XML documentation on all public types in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [ ] T082 Review and complete XML documentation on all public types in `src/SharedMemoryStore/ValueLease.cs`
- [ ] T083 Run formatting and analyzer validation for `SharedMemoryStore.sln`
- [ ] T084 Run full Release test validation for `SharedMemoryStore.sln`
- [ ] T085 Run Release package validation for `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [ ] T086 Run quickstart validation scenarios from `specs/001-frame-memory-store/quickstart.md`
- [ ] T087 [P] Add remove and slot-reuse allocation benchmark after warm-up in `benchmarks/SharedMemoryStore.Benchmarks/RemoveReuseAllocationBenchmarks.cs`
- [ ] T088 [P] Add 100,000-cycle producer/four-reader publish/acquire/release/remove lifecycle stress benchmark in `benchmarks/SharedMemoryStore.Benchmarks/LifecycleStressBenchmarks.cs`
- [ ] T089 [P] Add deterministic failure-latency benchmark with p95 and maximum observed latency reporting in `benchmarks/SharedMemoryStore.Benchmarks/FailureLatencyBenchmarks.cs`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup and blocks all user stories.
- **User Story 1 (Phase 3)**: Depends on Foundational and is the MVP.
- **User Story 2 (Phase 4)**: Depends on Foundational and integrates with US1 publish behavior for end-to-end testing.
- **User Story 3 (Phase 5)**: Depends on US2 release semantics for pending-removal reclaim.
- **User Story 4 (Phase 6)**: Depends on US1, US2, and US3 for full frame lifecycle validation.
- **User Story 5 (Phase 7)**: Depends on the public API shape from US1 through US4.
- **Polish (Phase 8)**: Depends on all selected user stories being complete.

### User Story Dependencies

- **US1 Publish Values at High Rate**: Can start after Foundational; no dependency on other stories for its layout-inspection validation.
- **US2 Acquire Values from Processing Services**: Can start after Foundational; end-to-end scenarios depend on values published by US1.
- **US3 Remove Values and Reuse Memory**: Depends on US2 release behavior for leased removal and final reclaim.
- **US4 Use Frame Data as a Store Value**: Depends on publish, acquire, release, remove, and reuse APIs from US1-US3.
- **US5 Consume as a General Library**: Depends on stable public APIs, samples, packaging, and docs from prior stories.

### Within Each User Story

- Write tests first and confirm they fail before implementation.
- Implement public contract behavior before internal optimization.
- Keep runtime package dependencies limited to the .NET BCL.
- Keep diagnostics caller-controlled and avoid direct console output from library code.
- Validate allocation-sensitive hot paths after functionality passes.

---

## Parallel Opportunities

- Setup tasks T003-T008 can run in parallel after T001-T002 decisions are clear.
- Foundational test support tasks T011-T013 can run in parallel with layout constant work T018-T020.
- US1 tests T025-T030 can run in parallel before implementation starts; implementation tasks T031-T033 can run in parallel before T034-T035 integration.
- US2 tests T038-T042 can run in parallel; `ValueLease` T043 and `LeaseRegistry` T044 can run in parallel before T045-T047 integration.
- US3 tests T050-T054 can run in parallel; `SlotReclaimer` T055 and index removal T056 can run in parallel before T057-T060 integration.
- US4 tests T063-T065 and sample descriptor work T066 can run in parallel after US1-US3 APIs exist.
- US5 tests T070-T072 can run in parallel with sample and documentation tasks T074-T077 after packaging metadata T073 is stable.

---

## Parallel Example: User Story 1

```text
Task: T025 Add unit tests for key, descriptor, value size boundaries and publish validation in tests/SharedMemoryStore.UnitTests/PublishValidationTests.cs
Task: T026 Add unit tests for slot reservation, publish commit, abort, and generation behavior in tests/SharedMemoryStore.UnitTests/SlotPublishStateTests.cs
Task: T027 Add contract tests for TryPublish statuses in tests/SharedMemoryStore.ContractTests/PublishContractTests.cs
Task: T028 Add integration test for 1.3 MB values in tests/SharedMemoryStore.IntegrationTests/PublishIntegrationTests.cs
Task: T030 Add publish allocation benchmark in benchmarks/SharedMemoryStore.Benchmarks/PublishAllocationBenchmarks.cs
```

## Parallel Example: User Story 2

```text
Task: T038 Add lease registry unit tests in tests/SharedMemoryStore.UnitTests/LeaseRegistryTests.cs
Task: T039 Add ValueLease contract tests in tests/SharedMemoryStore.ContractTests/ValueLeaseContractTests.cs
Task: T040 Add multi-reader acquire integration tests in tests/SharedMemoryStore.IntegrationTests/MultiReaderAcquireIntegrationTests.cs
Task: T041 Add acquire/release, duplicate-key publish, publish/remove, and adjacent-slot race tests in tests/SharedMemoryStore.IntegrationTests/AcquireReleaseConcurrencyTests.cs
Task: T042 Add lease allocation benchmarks in benchmarks/SharedMemoryStore.Benchmarks/LeaseAllocationBenchmarks.cs
```

## Parallel Example: User Story 3

```text
Task: T050 Add remove/reuse state unit tests in tests/SharedMemoryStore.UnitTests/RemoveReuseStateTests.cs
Task: T051 Add remove/reuse contract tests in tests/SharedMemoryStore.ContractTests/RemoveReuseContractTests.cs
Task: T052 Add remove/reuse integration tests in tests/SharedMemoryStore.IntegrationTests/RemoveReuseIntegrationTests.cs
Task: T053 Add stale lease recovery integration tests in tests/SharedMemoryStore.IntegrationTests/LeaseRecoveryIntegrationTests.cs
Task: T054 Add reuse benchmarks in benchmarks/SharedMemoryStore.Benchmarks/ReuseBenchmarks.cs
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 and Phase 2.
2. Complete Phase 3 tests and implementation for US1.
3. Validate publish behavior, descriptor persistence, duplicate detection, capacity behavior, and 0-byte steady-state publish allocation.
4. Stop and review the public API and shared-memory layout before adding lease and removal behavior.

### Incremental Delivery

1. Add US1 to publish bounded values into shared memory.
2. Add US2 to acquire and release zero-copy read leases.
3. Add US3 to remove values and reuse slots safely.
4. Add US4 to validate frame-shaped values without frame-specific core APIs.
5. Add US5 to package and consume the library from a clean project.

### Team Parallelization

1. Complete Setup and Foundational tasks together.
2. Split tests by project while implementation owners work on layout, slots, leasing, and diagnostics boundaries.
3. After US1-US3 APIs are stable, run US4 samples/benchmarks and US5 packaging/docs in parallel.
4. Gate release on `dotnet test`, `dotnet pack`, benchmark evidence, and quickstart validation.

---

## Format Validation

- All task lines use `- [ ] T###` checklist format.
- Parallel tasks use `[P]` only when they touch independent files or can run before dependent integration.
- User story phase tasks include `[US1]`, `[US2]`, `[US3]`, `[US4]`, or `[US5]`.
- Setup, foundational, and polish tasks do not include story labels.
- Every task description includes at least one exact file path.
