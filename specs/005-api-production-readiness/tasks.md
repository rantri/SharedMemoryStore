# Tasks: API Production Readiness

**Input**: Design documents from `specs/005-api-production-readiness/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required by the feature specification and constitution. Write the tests in each user story before implementation and verify they fail for the current API surface.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated as an independent increment.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and does not depend on incomplete tasks in the same phase
- **[Story]**: User story label from [spec.md](spec.md)
- Every task names exact project-relative file paths

## Phase 1: Setup

**Purpose**: Establish shared implementation tracking and test utilities used by the production-readiness work.

- [X] T001 Create public API change inventory in specs/005-api-production-readiness/api-change-inventory.md mapping renamed types, statuses, diagnostics members, options members, and migration notes
- [X] T002 [P] Create requirement traceability matrix in specs/005-api-production-readiness/acceptance-traceability.md linking FR-001 through FR-020 and SC-001 through SC-009 to planned tests
- [X] T003 [P] Add production-readiness test category constants in tests/SharedMemoryStore.ContractTests/ProductionReadinessTestCategories.cs
- [X] T004 [P] Add shared public API reflection helper in tests/SharedMemoryStore.ContractTests/PublicApiAssertions.cs

---

## Phase 2: Foundational

**Purpose**: Public contract primitives that block multiple user stories.

**Critical**: No user story implementation should begin until these shared contract primitives compile.

- [X] T005 Add `InvalidKey`, `StoreBusy`, and `OperationCanceled` values with XML docs in src/SharedMemoryStore/StoreStatus.cs
- [X] T006 Add open/create equivalents for busy and cancellation outcomes with XML docs in src/SharedMemoryStore/StoreStatus.cs
- [X] T007 [P] Add `StoreWaitOptions` with one-second `Default`, `NoWait`, `Infinite`, timeout validation, cancellation token support, and XML docs in src/SharedMemoryStore/StoreWaitOptions.cs
- [X] T008 [P] Add public option validation result types in src/SharedMemoryStore/Options/StoreOptionsValidationResult.cs
- [X] T009 Update diagnostics failure counting capacity for all new status values in src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs
- [X] T010 Update diagnostics snapshot construction for all new status values in src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs

**Checkpoint**: Shared status, wait-policy, diagnostics-count, and validation-result primitives compile.

---

## Phase 3: User Story 1 - Use a Clear Public Store API (Priority: P1) MVP

**Goal**: Consumers can import `SharedMemoryStore` and use the primary `MemoryStore` type without aliases or namespace/type collisions.

**Independent Test**: Build package-consumption examples that create/open a store and perform the main read/write flow with `using SharedMemoryStore;` and `MemoryStore` directly.

### Tests for User Story 1

- [X] T011 [P] [US1] Add reflection contract tests for the final `MemoryStore` type and removed namespace/type collision in tests/SharedMemoryStore.ContractTests/ProductionApiContractTests.cs
- [X] T012 [P] [US1] Add clean consumer compile test that imports `SharedMemoryStore` and references `MemoryStore` without aliases in tests/SharedMemoryStore.IntegrationTests/PackageProductionReadinessIntegrationTests.cs
- [X] T013 [P] [US1] Add package-consumption API sample assertions for `MemoryStore.TryCreateOrOpen` in tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs
- [X] T014 [P] [US1] Add documentation snippet validation for final primary store identity in scripts/validate-docs.ps1

### Implementation for User Story 1

- [X] T015 [US1] Rename src/SharedMemoryStore/SharedMemoryStore.cs to src/SharedMemoryStore/MemoryStore.cs and rename the public class and constructors to `MemoryStore`
- [X] T016 [US1] Update public XML documentation references from `SharedMemoryStore.TryCreateOrOpen` to `MemoryStore.TryCreateOrOpen` in src/SharedMemoryStore/MemoryStore.cs
- [X] T017 [US1] Update contract test factory usage for the renamed primary type in tests/SharedMemoryStore.ContractTests/ContractStoreFactory.cs
- [X] T018 [US1] Update unit and integration test support naming helpers for the renamed primary type in tests/SharedMemoryStore.UnitTests/TestSupport/StoreTestNames.cs
- [X] T019 [P] [US1] Update basic sample code to use `MemoryStore` in samples/BasicUsage/Program.cs
- [X] T020 [P] [US1] Update frame sample code to use `MemoryStore` in samples/FrameValue/Program.cs
- [X] T021 [P] [US1] Update zero-copy ingest sample code to use `MemoryStore` in samples/ZeroCopyIngest/Program.cs
- [X] T022 [US1] Update public naming migration notes and examples in README.md, docs/getting-started.md, docs/usage.md, docs/examples.md, and docs/releases.md

**Checkpoint**: User Story 1 works independently when contract tests, package-consumption validation, docs validation, and samples compile with `MemoryStore`.

---

## Phase 4: User Story 2 - Prevent Reservation Memory From Outliving Its Contract (Priority: P1)

**Goal**: Reservation writable access cannot mutate committed, aborted, disposed, store-disposed, recovered, or reused storage after the reservation lifecycle completes.

**Independent Test**: Retain every public writable access path from a reservation, complete or abandon the reservation, reuse the slot for at least 10,000 cycles, and verify retained handles cannot mutate visible payloads.

### Tests for User Story 2

- [X] T023 [P] [US2] Add retained write access tests for commit, abort, dispose, store disposal, and slot reuse in tests/SharedMemoryStore.UnitTests/ReservationMemoryLifetimeTests.cs
- [X] T024 [P] [US2] Add 10,000-cycle reservation reuse safety test in tests/SharedMemoryStore.IntegrationTests/ReservationReuseSafetyIntegrationTests.cs
- [X] T025 [P] [US2] Add contract test proving `ValueReservation` does not expose general retained writable `Memory<byte>` in tests/SharedMemoryStore.ContractTests/ReservationMemoryContractTests.cs
- [X] T026 [P] [US2] Add reader immutability regression test for committed payloads after retained reservation handles in tests/SharedMemoryStore.ContractTests/ValueLeaseContractTests.cs

### Implementation for User Story 2

- [X] T027 [US2] Remove or make non-public `ValueReservation.GetMemory` and update XML docs in src/SharedMemoryStore/Ingest/ValueReservation.cs
- [X] T028 [US2] Remove public reservation memory path usage from `MemoryStore.GetReservationMemory` in src/SharedMemoryStore/MemoryStore.cs
- [X] T029 [US2] Restrict `ReservationMemoryManager.GetMemory` to internal implementation needs or remove it entirely in src/SharedMemoryStore/Ingest/ReservationMemoryManager.cs
- [X] T030 [US2] Normalize stale and completed reservation token outcomes in src/SharedMemoryStore/Ingest/ValueReservation.cs
- [X] T031 [US2] Ensure commit, abort, dispose, recovery, and store-disposed reservation paths invalidate stale write access in src/SharedMemoryStore/MemoryStore.cs
- [X] T032 [US2] Update reservation lifecycle documentation in docs/lifecycle.md and docs/usage.md
- [X] T033 [US2] Update zero-copy ingest documentation to exclude retained writable memory from basic examples in docs/examples.md and samples/ZeroCopyIngest/README.md
- [X] T034 [US2] Update reservation API migration notes in docs/releases.md

**Checkpoint**: User Story 2 works independently when reservation lifetime tests and zero-copy ingest examples prove no safe public retained write handle can mutate completed or reused storage.

---

## Phase 5: User Story 3 - Bound Public Operation Waiting (Priority: P1)

**Goal**: Every public operation that can wait on shared synchronization has documented bounded wait, cancellation, busy, timeout, and disposed-store outcomes.

**Independent Test**: Hold shared synchronization from another owner and verify each public operation returns the documented contention outcome within the caller-selected wait limit.

### Tests for User Story 3

- [X] T035 [P] [US3] Add `StoreWaitOptions` validation and one-second default-policy tolerance tests in tests/SharedMemoryStore.UnitTests/StoreWaitPolicyTests.cs
- [X] T036 [P] [US3] Add wait-policy contract tests for open/create, diagnostics, and public operation families in tests/SharedMemoryStore.ContractTests/ContentionContractTests.cs
- [X] T037 [P] [US3] Add cross-process contended synchronization tests in tests/SharedMemoryStore.IntegrationTests/ContendedSynchronizationIntegrationTests.cs
- [X] T038 [P] [US3] Add lifecycle dispose race while waiting tests in tests/SharedMemoryStore.UnitTests/StoreDisposalRaceTests.cs

### Implementation for User Story 3

- [X] T039 [US3] Add timeout and cancellation-aware lifecycle entry helpers in src/SharedMemoryStore/Lifecycle/StoreLifecycleGate.cs
- [X] T040 [US3] Replace indefinite mutex waits with timeout-aware lock acquisition helpers in src/SharedMemoryStore/MemoryStore.cs
- [X] T041 [US3] Add wait-policy overloads for `TryCreateOrOpen`, `TryPublish`, `TryReserve`, `TryPublishSegments`, `TryAcquire`, `TryRemove`, `TryRecoverLeases`, `TryRecoverReservations`, and status-returning `TryGetDiagnostics` in src/SharedMemoryStore/MemoryStore.cs
- [X] T042 [US3] Add wait-policy support for lease release behavior in src/SharedMemoryStore/ValueLease.cs
- [X] T043 [US3] Add wait-policy support for reservation `Advance`, `Commit`, `Abort`, and `Dispose` behavior in src/SharedMemoryStore/Ingest/ValueReservation.cs
- [X] T044 [US3] Thread wait-policy parameters through segmented publishing in src/SharedMemoryStore/Ingest/SegmentedPublisher.cs
- [X] T045 [US3] Record `StoreBusy` and `OperationCanceled` failures consistently in src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs
- [X] T046 [US3] Update contention, cancellation, abandoned synchronization, diagnostics contention, and one-second default wait behavior docs in docs/lifecycle.md, docs/errors.md, and docs/usage.md
- [X] T047 [US3] Update production wait-policy migration notes in docs/releases.md

**Checkpoint**: User Story 3 works independently when each contended public operation returns busy, cancellation, or disposed outcomes within the requested wait policy.

---

## Phase 6: User Story 4 - Configure and Validate the Store Safely (Priority: P2)

**Goal**: Consumers can create valid options, derive required storage size, reject invalid open modes, and distinguish invalid keys from oversized keys.

**Independent Test**: Exercise valid and invalid option combinations plus empty and oversized keys across public entry points and verify every outcome matches the documented contract.

### Tests for User Story 4

- [X] T048 [P] [US4] Add valid-by-construction and invalid option tests in tests/SharedMemoryStore.UnitTests/StoreOptionsValidationTests.cs
- [X] T049 [P] [US4] Add empty-key and oversized-key unit tests across publish, reserve, acquire, and remove in tests/SharedMemoryStore.UnitTests/KeyValidationTests.cs
- [X] T050 [P] [US4] Add configuration contract tests for invalid open modes, derived sizes, and validation details in tests/SharedMemoryStore.ContractTests/ConfigurationContractTests.cs
- [X] T051 [P] [US4] Add package-consumption tests for option helper usage in tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs

### Implementation for User Story 4

- [X] T052 [US4] Add `SharedMemoryStoreOptions.Create` and public validation helpers in src/SharedMemoryStore/SharedMemoryStoreOptions.cs
- [X] T053 [US4] Implement detailed option validation including undefined `OpenMode` rejection in src/SharedMemoryStore/Options/SharedMemoryStoreOptionsValidator.cs
- [X] T054 [US4] Populate public option validation detail results in src/SharedMemoryStore/Options/StoreOptionsValidationResult.cs
- [X] T055 [US4] Change empty-key validation to return `InvalidKey` while preserving oversized-key `KeyTooLarge` in src/SharedMemoryStore/Layout/StoreKey.cs
- [X] T056 [US4] Update publish, reserve, acquire, and remove paths to record `InvalidKey` distinctly in src/SharedMemoryStore/MemoryStore.cs
- [X] T057 [US4] Update diagnostics snapshot failure-count access for `InvalidKey` in src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs
- [X] T058 [US4] Update option, key, status, and size calculation documentation in docs/errors.md, docs/usage.md, and docs/getting-started.md
- [X] T059 [US4] Update validation and status taxonomy migration notes in docs/releases.md

**Checkpoint**: User Story 4 works independently when invalid configuration and key-validation tests return documented, distinguishable outcomes.

---

## Phase 7: User Story 5 - Keep Production Integrations Optional and Focused (Priority: P3)

**Goal**: Diagnostics use stable aggregate failure-count APIs, the core package has no hosting dependencies, and optional integration guidance stays narrow and opt-in.

**Independent Test**: Review the public package surface and optional integration sample to verify the core package restores and packs without hosting dependencies and no broad concrete-store mirror interface is introduced.

### Tests for User Story 5

- [X] T060 [P] [US5] Add diagnostics API shape tests for aggregate failure counts and pruned convenience names in tests/SharedMemoryStore.UnitTests/DiagnosticsApiShapeTests.cs
- [X] T061 [P] [US5] Add diagnostics contract tests for `GetFailureCount(StoreStatus)` across all non-success statuses in tests/SharedMemoryStore.ContractTests/DiagnosticsContractTests.cs
- [X] T062 [P] [US5] Add core package dependency tests proving no `Microsoft.Extensions.*` dependency in src/SharedMemoryStore/SharedMemoryStore.csproj via tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs
- [X] T063 [P] [US5] Add public API tests proving no broad `ISharedMemoryStore` mirror interface is exposed in tests/SharedMemoryStore.ContractTests/ProductionApiContractTests.cs

### Implementation for User Story 5

- [X] T064 [US5] Remove or obsolete per-status failure convenience properties that duplicate `GetFailureCount` in src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs
- [X] T065 [US5] Preserve aggregate failure-count access for every public non-success status in src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs
- [X] T066 [P] [US5] Add optional hosted-service integration sample project outside the core package in samples/HostedServiceIntegration/HostedServiceIntegration.csproj
- [X] T067 [US5] Add hosted-service sample lifecycle, health, shutdown, cleanup, and recovery flow in samples/HostedServiceIntegration/Program.cs
- [X] T068 [US5] Document optional hosting integration boundaries, sample validation, and narrow interface rules in docs/integration.md
- [X] T069 [US5] Update diagnostics aggregate-access documentation in docs/diagnostics.md
- [X] T070 [US5] Link optional integration and diagnostics guidance from README.md and docs/index.md

**Checkpoint**: User Story 5 works independently when diagnostics contract tests pass, the core package remains dependency-light, and optional integration guidance is separate from the core runtime package.

---

## Phase 8: Polish and Cross-Cutting Validation

**Purpose**: Finish release documentation, package metadata, and full validation across all stories.

- [X] T071 [P] Update package release notes and production API version impact in src/SharedMemoryStore/SharedMemoryStore.csproj
- [X] T072 [P] Update changelog entry for production API readiness in CHANGELOG.md
- [X] T073 [P] Update public API readiness release guide in docs/releases.md
- [X] T074 Run documentation and public XML documentation validation from scripts/validate-docs.ps1 and fix remaining snippet failures in docs/
- [X] T075 Run package consumption validation from scripts/validate-package-consumption.ps1 and fix remaining consumer compile failures in tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs
- [X] T076 Add and run steady-state allocation regression coverage for publish, reserve, acquire, remove, release, diagnostics, recovery, and wait-policy paths in tests/SharedMemoryStore.UnitTests/StoreWaitPolicyTests.cs and benchmarks/SharedMemoryStore.Benchmarks/FailureLatencyBenchmarks.cs
- [X] T077 If the optional hosted sample is present, build and run lifecycle, health, shutdown, cleanup, and recovery validation in samples/HostedServiceIntegration/HostedServiceIntegration.csproj and record validation notes in samples/HostedServiceIntegration/README.md
- [X] T078 Run full release tests with `dotnet test .\SharedMemoryStore.slnx -c Release` and fix failures in SharedMemoryStore.slnx
- [X] T079 Run release package build with `dotnet pack .\src\SharedMemoryStore\SharedMemoryStore.csproj -c Release --no-build` and fix packaging issues in src/SharedMemoryStore/SharedMemoryStore.csproj

---

## Dependencies and Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies.
- **Phase 2 Foundational**: Depends on Phase 1 and blocks all user stories.
- **Phase 3 US1**: Depends on Phase 2. This is the MVP and should be completed first because the renamed primary type affects examples and tests.
- **Phase 4 US2**: Depends on Phase 2. Can be implemented after or alongside US1 if the branch has already applied the primary type rename.
- **Phase 5 US3**: Depends on Phase 2. Can be implemented independently after shared statuses and wait options compile.
- **Phase 6 US4**: Depends on Phase 2. Can be implemented independently after shared statuses and validation result types compile.
- **Phase 7 US5**: Depends on Phase 2. Can be implemented independently after diagnostics can count all statuses.
- **Phase 8 Polish**: Depends on all selected user stories.

### User Story Dependencies

- **US1 (P1)**: No story dependency. Recommended MVP because it stabilizes the public type name.
- **US2 (P1)**: No domain dependency on US1, but implementation files should use the final `MemoryStore` name if US1 is already merged.
- **US3 (P1)**: Depends on foundational wait-policy and status primitives only.
- **US4 (P2)**: Depends on foundational validation and status primitives only.
- **US5 (P3)**: Depends on foundational diagnostics-count support only.

### Within Each User Story

- Tests must be written first and confirmed failing against the current implementation.
- Public contract tests should precede implementation changes that alter public API.
- Documentation and migration notes should be updated in the same story phase as the public API behavior they describe.
- Each story checkpoint should pass before moving to the next priority story in a single-developer workflow.

---

## Parallel Opportunities

- Setup tasks T002, T003, and T004 can run in parallel.
- Foundational tasks T007 and T008 can run in parallel with T005 and T006, then T009 and T010 follow after status values are known.
- US1 test tasks T011 through T014 can run in parallel.
- US1 sample update tasks T019 through T021 can run in parallel after T015.
- US2 test tasks T023 through T026 can run in parallel.
- US3 test tasks T035 through T038 can run in parallel.
- US4 test tasks T048 through T051 can run in parallel.
- US5 test tasks T060 through T063 can run in parallel.
- Polish documentation tasks T071 through T073 can run in parallel before final validation.

---

## Parallel Example: User Story 1

```text
Task: "Add reflection contract tests for the final MemoryStore type and removed namespace/type collision in tests/SharedMemoryStore.ContractTests/ProductionApiContractTests.cs"
Task: "Add clean consumer compile test that imports SharedMemoryStore and references MemoryStore without aliases in tests/SharedMemoryStore.IntegrationTests/PackageProductionReadinessIntegrationTests.cs"
Task: "Add package-consumption API sample assertions for MemoryStore.TryCreateOrOpen in tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs"
Task: "Add documentation snippet validation for final primary store identity in scripts/validate-docs.ps1"
```

## Parallel Example: User Story 2

```text
Task: "Add retained write access tests for commit, abort, dispose, store disposal, and slot reuse in tests/SharedMemoryStore.UnitTests/ReservationMemoryLifetimeTests.cs"
Task: "Add 10,000-cycle reservation reuse safety test in tests/SharedMemoryStore.IntegrationTests/ReservationReuseSafetyIntegrationTests.cs"
Task: "Add contract test proving ValueReservation does not expose general retained writable Memory<byte> in tests/SharedMemoryStore.ContractTests/ReservationMemoryContractTests.cs"
```

## Parallel Example: User Story 3

```text
Task: "Add StoreWaitOptions validation and default-policy tests in tests/SharedMemoryStore.UnitTests/StoreWaitPolicyTests.cs"
Task: "Add wait-policy contract tests for public operation families in tests/SharedMemoryStore.ContractTests/ContentionContractTests.cs"
Task: "Add cross-process contended synchronization tests in tests/SharedMemoryStore.IntegrationTests/ContendedSynchronizationIntegrationTests.cs"
```

## Parallel Example: User Story 4

```text
Task: "Add valid-by-construction and invalid option tests in tests/SharedMemoryStore.UnitTests/StoreOptionsValidationTests.cs"
Task: "Add empty-key and oversized-key unit tests across publish, reserve, acquire, and remove in tests/SharedMemoryStore.UnitTests/KeyValidationTests.cs"
Task: "Add configuration contract tests for invalid open modes, derived sizes, and validation details in tests/SharedMemoryStore.ContractTests/ConfigurationContractTests.cs"
```

## Parallel Example: User Story 5

```text
Task: "Add diagnostics API shape tests for aggregate failure counts and pruned convenience names in tests/SharedMemoryStore.UnitTests/DiagnosticsApiShapeTests.cs"
Task: "Add diagnostics contract tests for GetFailureCount(StoreStatus) across all non-success statuses in tests/SharedMemoryStore.ContractTests/DiagnosticsContractTests.cs"
Task: "Add core package dependency tests proving no Microsoft.Extensions.* dependency in src/SharedMemoryStore/SharedMemoryStore.csproj via tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs"
```

---

## Implementation Strategy

### MVP First

1. Complete Phase 1 setup.
2. Complete Phase 2 foundational contract primitives.
3. Complete Phase 3 User Story 1.
4. Stop and validate US1 independently with contract tests, package-consumption validation, docs validation, and sample builds.

### Incremental Delivery

1. Deliver US1 to stabilize the public type identity.
2. Deliver US2 to close the reservation memory safety hole.
3. Deliver US3 to make public synchronization waits bounded and deterministic.
4. Deliver US4 to strengthen option, key, and status contracts.
5. Deliver US5 to prune diagnostics names and keep production integrations optional.
6. Complete Phase 8 full release validation.

### Parallel Team Strategy

1. One developer completes Phase 2 status, wait-policy, diagnostics, and validation primitives.
2. After Phase 2 compiles, separate developers can implement US2, US3, US4, and US5 test files in parallel.
3. US1 should merge early so later story work uses `MemoryStore` consistently.
4. Final validation should run only after all selected stories are integrated.

---

## Notes

- `[P]` tasks use different files and can be assigned concurrently after their phase dependencies are met.
- Tests are included because the feature specification explicitly requires automated validation and the constitution requires behavior-changing work to be tested.
- Avoid adding broad interfaces or core hosting dependencies unless a later feature changes the documented integration decision.
- Keep public examples, samples, XML docs, release notes, and package-consumption tests aligned with the final API names.
