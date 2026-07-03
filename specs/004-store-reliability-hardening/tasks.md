# Tasks: Store Reliability Hardening

**Input**: Design documents from `specs/004-store-reliability-hardening/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: Required by the feature specification and constitution because this is behavior-changing reliability work.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and has no dependency on another incomplete task.
- **[Story]**: User story label for story phases only.
- Every task includes an exact repository path.

## Phase 1: Setup

**Purpose**: Add shared test and benchmark scaffolding needed by the reliability stories.

- [X] T001 [P] Add a multi-process lease owner harness in `tests/SharedMemoryStore.IntegrationTests/TestSupport/LeaseOwnerProcessHarness.cs`
- [X] T002 [P] Add concurrent operation runner helpers in `tests/SharedMemoryStore.UnitTests/TestSupport/ConcurrentOperationRunner.cs`
- [X] T003 [P] Add rollover seed helpers for internal cursors and lifecycle identifiers in `tests/SharedMemoryStore.UnitTests/TestSupport/RolloverTestHooks.cs`
- [X] T004 [P] Add churn workload key generation helpers in `tests/SharedMemoryStore.UnitTests/TestSupport/ChurnKeyFactory.cs`
- [X] T005 [P] Add benchmark result records for tombstone and rollover validation in `benchmarks/SharedMemoryStore.Benchmarks/ReliabilityBenchmarkResults.cs`

---

## Phase 2: Foundational

**Purpose**: Establish shared reliability primitives that block all user story implementation.

**Critical**: No user story work should begin until this phase is complete.

- [X] T006 Add internal lifecycle identity type and comparison helpers in `src/SharedMemoryStore/Layout/SlotLifecycleId.cs`
- [X] T007 Add internal owner-liveness classification type shell in `src/SharedMemoryStore/Leasing/LeaseOwnerClassifier.cs`
- [X] T008 Add internal lifecycle gate type shell for disposal coordination in `src/SharedMemoryStore/Lifecycle/StoreLifecycleGate.cs`
- [X] T009 Add shared reliability status assertion helpers in `tests/SharedMemoryStore.ContractTests/ReliabilityAssertions.cs`

**Checkpoint**: Foundation ready for story-specific tests and implementation.

---

## Phase 3: User Story 1 - Recover Only Eligible Leases (Priority: P1) MVP

**Goal**: Explicit lease recovery reclaims only eligible current-process or stale-owner leases and never invalidates another live owner.

**Independent Test**: Open one named store from multiple owners, acquire leases from both, run current-process recovery in one owner, and verify other live-owner leases remain valid and continue protecting storage.

### Tests for User Story 1

Write these tests first and confirm they fail before implementation.

- [X] T010 [P] [US1] Add recovery report public shape contract tests in `tests/SharedMemoryStore.ContractTests/ReliabilityApiContractTests.cs`
- [X] T011 [P] [US1] Add current-process, other-live-process, stale-owner, unsupported-owner, and unsafe-record unit tests in `tests/SharedMemoryStore.UnitTests/LeaseRecoveryOwnershipTests.cs`
- [X] T012 [P] [US1] Add multi-owner lease recovery integration tests using the process harness in `tests/SharedMemoryStore.IntegrationTests/MultiOwnerLeaseRecoveryIntegrationTests.cs`

### Implementation for User Story 1

- [X] T013 [US1] Implement owner liveness classification and unsupported fallback behavior in `src/SharedMemoryStore/Leasing/LeaseOwnerClassifier.cs`
- [X] T014 [US1] Extend `LeaseRecoveryReport` with active and failed recovery categories plus XML documentation in `src/SharedMemoryStore/SharedMemoryStoreOptions.cs`
- [X] T015 [US1] Update recovery mutation policy to skip other live owners and report current, stale, unsupported, and unsafe decisions in `src/SharedMemoryStore/Leasing/LeaseRecovery.cs`
- [X] T016 [US1] Add lease recovery result counters to diagnostics internals in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T017 [US1] Add lease recovery result fields and `GetFailureCount` compatibility coverage in `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`
- [X] T018 [US1] Record lease recovery report metrics from `TryRecoverLeases` in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T019 [US1] Document corrected owner recovery policy and disabled-recovery outcomes in `docs/lifecycle.md`
- [X] T020 [US1] Document lease recovery diagnostic fields in `docs/diagnostics.md`
- [X] T021 [US1] Validate owner recovery commands from the quickstart guide in `specs/004-store-reliability-hardening/quickstart.md`

**Checkpoint**: User Story 1 is independently functional when recovery tests pass and other live-owner leases remain valid.

---

## Phase 4: User Story 2 - Return Deterministic Outcomes During Disposal Races (Priority: P2)

**Goal**: Public store operations and token operations racing with disposal complete with documented outcomes and never expose internal disposed-resource exceptions.

**Independent Test**: Repeatedly race disposal against publish, reserve, acquire, remove, recovery, diagnostics, release, reservation operations, and token disposal, then verify every result is documented.

### Tests for User Story 2

Write these tests first and confirm they fail before implementation.

- [X] T022 [P] [US2] Add public lifecycle outcome contract tests in `tests/SharedMemoryStore.ContractTests/LifecycleOutcomeContractTests.cs`
- [X] T023 [P] [US2] Add unit disposal race tests for store methods and token methods in `tests/SharedMemoryStore.UnitTests/StoreDisposalRaceTests.cs`
- [X] T024 [P] [US2] Add integration stress tests for 100,000 disposal-race operations in `tests/SharedMemoryStore.IntegrationTests/StoreDisposalRaceIntegrationTests.cs`

### Implementation for User Story 2

- [X] T025 [US2] Implement the disposal coordination lifecycle gate in `src/SharedMemoryStore/Lifecycle/StoreLifecycleGate.cs`
- [X] T026 [US2] Refactor store lock entry, lock exit, and idempotent dispose behavior to use the lifecycle gate in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T027 [US2] Normalize disposed-resource exceptions from public store operations to documented statuses in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T028 [US2] Harden lease validity, span projection, and release after disposal in `src/SharedMemoryStore/ValueLease.cs`
- [X] T029 [US2] Harden reservation validity, writable view projection, advance, commit, abort, and dispose after disposal in `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T030 [US2] Prevent disposed mapped-memory access from reservation memory views in `src/SharedMemoryStore/Ingest/ReservationMemoryManager.cs`
- [X] T031 [US2] Ensure disposed diagnostic snapshots do not access disposed resources in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T032 [US2] Document disposal lifecycle outcomes and token behavior in `docs/errors.md`
- [X] T033 [US2] Validate disposal race commands from the quickstart guide in `specs/004-store-reliability-hardening/quickstart.md`

**Checkpoint**: User Story 2 is independently functional when disposal race tests pass without internal lifecycle exceptions.

---

## Phase 5: User Story 3 - Preserve Safety Across Long-Running Rollover (Priority: P3)

**Goal**: Probe cursors and slot lifecycle identifiers remain safe across rollover boundaries without invalid indexes, overflow failures, or stale handle acceptance.

**Independent Test**: Seed slot probes, lease probes, and slot lifecycle identity near rollover boundaries, run normal store operations afterward, and verify stale leases and reservations never regain validity.

### Tests for User Story 3

Write these tests first and confirm they fail before implementation.

- [X] T034 [P] [US3] Add shared layout lifecycle identity contract tests in `tests/SharedMemoryStore.ContractTests/SharedMemoryLayoutContractTests.cs`
- [X] T035 [P] [US3] Add slot and lease probe cursor rollover unit tests in `tests/SharedMemoryStore.UnitTests/ProbeRolloverTests.cs`
- [X] T036 [P] [US3] Add stale lease and reservation lifecycle identity boundary tests in `tests/SharedMemoryStore.UnitTests/SlotLifecycleIdentifierTests.cs`
- [X] T037 [P] [US3] Add rollover stress integration tests for 1,000,000 post-boundary operations in `tests/SharedMemoryStore.IntegrationTests/RolloverStressIntegrationTests.cs`

### Implementation for User Story 3

- [X] T038 [US3] Finalize lifecycle identity fields and helper methods in `src/SharedMemoryStore/Layout/SlotLifecycleId.cs`
- [X] T039 [US3] Add lifecycle identity fields and layout version handling in `src/SharedMemoryStore/Layout/SharedRecords.cs`
- [X] T040 [US3] Update layout constants for lifecycle identity compatibility in `src/SharedMemoryStore/Layout/LayoutConstants.cs`
- [X] T041 [US3] Update store layout validation and header matching for lifecycle identity fields in `src/SharedMemoryStore/Layout/StoreLayout.cs`
- [X] T042 [US3] Replace rollover-prone slot probe arithmetic and advance lifecycle identity on reclaim in `src/SharedMemoryStore/Slots/ReusableSlotTable.cs`
- [X] T043 [US3] Replace rollover-prone lease record probe arithmetic and capture lifecycle identity in `src/SharedMemoryStore/Leasing/LeaseRegistry.cs`
- [X] T044 [US3] Store and validate full lifecycle identity in key index entries in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [X] T045 [US3] Compare full lifecycle identity in lease token validation in `src/SharedMemoryStore/ValueLease.cs`
- [X] T046 [US3] Compare full lifecycle identity in reservation token validation in `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T047 [US3] Validate full lifecycle identity during final release and reclaim in `src/SharedMemoryStore/Leasing/LeaseRelease.cs`
- [X] T048 [US3] Validate full lifecycle identity during remove-request reclaim in `src/SharedMemoryStore/Slots/SlotReclaimer.cs`
- [X] T049 [US3] Add lifecycle rollover benchmark coverage in `benchmarks/SharedMemoryStore.Benchmarks/LifecycleRolloverBenchmarks.cs`
- [X] T050 [US3] Document long-running lifecycle and layout portability rules in `docs/portability.md`
- [X] T051 [US3] Validate rollover commands from the quickstart guide in `specs/004-store-reliability-hardening/quickstart.md`

**Checkpoint**: User Story 3 is independently functional when rollover tests and stress validation pass without stale handle acceptance.

---

## Phase 6: User Story 4 - Detect and Control Tombstone Pressure (Priority: P4)

**Goal**: Consumers can distinguish tombstone pressure from live capacity pressure and the store avoids sustained near full-table probe behavior under churn.

**Independent Test**: Run high-churn insert, remove, missing-key lookup, and insert workloads; verify diagnostics expose tombstone pressure and post-management latency stays within success criteria.

### Tests for User Story 4

Write these tests first and confirm they fail before implementation.

- [X] T052 [P] [US4] Add index health diagnostic contract tests in `tests/SharedMemoryStore.ContractTests/DiagnosticsContractTests.cs`
- [X] T053 [P] [US4] Add unit tests for occupied, tombstone, empty, reusable capacity, and probe counters in `tests/SharedMemoryStore.UnitTests/IndexHealthTests.cs`
- [X] T054 [P] [US4] Add high-churn integration tests preserving values, leases, reservations, and duplicate detection in `tests/SharedMemoryStore.IntegrationTests/TombstonePressureIntegrationTests.cs`
- [X] T055 [P] [US4] Add tombstone pressure benchmark with clean baseline and managed-pressure runs in `benchmarks/SharedMemoryStore.Benchmarks/TombstonePressureBenchmarks.cs`

### Implementation for User Story 4

- [X] T056 [US4] Add index health fields and XML documentation to diagnostic snapshots in `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`
- [X] T057 [US4] Record bounded probe counts for find, insert, and remove paths in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [X] T058 [US4] Add index state counting and health snapshot methods in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [X] T059 [US4] Add index health counters and compaction counts to diagnostics internals in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T060 [US4] Include index health values in store diagnostic snapshots in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T061 [US4] Implement bounded synchronous tombstone compaction or rehashing in `src/SharedMemoryStore/Layout/SharedKeyIndex.cs`
- [X] T062 [US4] Wire benchmark-selected tombstone pressure threshold into store mutation paths in `src/SharedMemoryStore/SharedMemoryStore.cs`
- [X] T063 [US4] Document tombstone diagnostic fields and pressure interpretation in `docs/diagnostics.md`
- [X] T064 [US4] Document churn benchmark evidence and selected pressure threshold in `docs/performance.md`
- [X] T065 [US4] Validate tombstone diagnostics and churn commands from the quickstart guide in `specs/004-store-reliability-hardening/quickstart.md`

**Checkpoint**: User Story 4 is independently functional when diagnostics distinguish tombstones and churn benchmarks meet the latency criteria.

---

## Phase 7: Polish & Cross-Cutting Concerns

**Purpose**: Release readiness, documentation alignment, and full validation across all stories.

- [X] T066 [P] Update release notes for owner recovery, disposal, rollover, tombstone diagnostics, and semantic version impact in `docs/releases.md`
- [X] T067 [P] Update package-facing usage notes for corrected reliability behavior in `docs/usage.md`
- [X] T068 [P] Update README reliability and diagnostics references in `README.md`
- [X] T069 [P] Update changelog entry for the reliability hardening feature in `CHANGELOG.md`
- [X] T070 Review final public API and layout compatibility against feature contracts in `specs/004-store-reliability-hardening/contracts/owner-recovery-contract.md`
- [X] T071 Review final disposal, rollover, and index health behavior against feature contracts in `specs/004-store-reliability-hardening/contracts/disposal-rollover-contract.md`
- [X] T072 Review final tombstone health behavior against feature contracts in `specs/004-store-reliability-hardening/contracts/index-health-contract.md`
- [X] T073 Run full test, package, documentation, and package-consumption validation commands from `specs/004-store-reliability-hardening/quickstart.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies.
- **Phase 2 Foundational**: Depends on Phase 1 and blocks all user stories.
- **Phase 3 User Story 1**: Depends on Phase 2 and is the MVP.
- **Phase 4 User Story 2**: Depends on Phase 2; can run in parallel with US1 after shared lifecycle files are coordinated.
- **Phase 5 User Story 3**: Depends on Phase 2; should integrate after US1 if lease recovery uses full lifecycle identity.
- **Phase 6 User Story 4**: Depends on Phase 2; can run after diagnostics files are coordinated with US1 and US2.
- **Phase 7 Polish**: Depends on the selected user stories being complete.

### User Story Dependencies

- **US1 Recover Only Eligible Leases**: MVP and highest priority. No dependency on other stories after foundational scaffolding.
- **US2 Disposal Races**: Independent after foundational scaffolding, but it modifies `SharedMemoryStore.cs`, `ValueLease.cs`, and `ValueReservation.cs`, so coordinate with US1 and US3 edits.
- **US3 Long-Running Rollover**: Independent after foundational scaffolding, but it changes shared layout semantics and should be integrated before final package validation.
- **US4 Tombstone Pressure**: Independent after foundational scaffolding, but it shares diagnostics files with US1 and US2.

### Within Each User Story

- Write contract, unit, integration, and benchmark tests first.
- Confirm tests fail for the missing behavior.
- Implement runtime changes.
- Update docs for the changed public or diagnostic contract.
- Run the story-specific quickstart validation.

---

## Parallel Execution Examples

### User Story 1

```text
Task: "Add recovery report public shape contract tests in tests/SharedMemoryStore.ContractTests/ReliabilityApiContractTests.cs"
Task: "Add current-process, other-live-process, stale-owner, unsupported-owner, and unsafe-record unit tests in tests/SharedMemoryStore.UnitTests/LeaseRecoveryOwnershipTests.cs"
Task: "Add multi-owner lease recovery integration tests using the process harness in tests/SharedMemoryStore.IntegrationTests/MultiOwnerLeaseRecoveryIntegrationTests.cs"
```

### User Story 2

```text
Task: "Add public lifecycle outcome contract tests in tests/SharedMemoryStore.ContractTests/LifecycleOutcomeContractTests.cs"
Task: "Add unit disposal race tests for store methods and token methods in tests/SharedMemoryStore.UnitTests/StoreDisposalRaceTests.cs"
Task: "Add integration stress tests for 100,000 disposal-race operations in tests/SharedMemoryStore.IntegrationTests/StoreDisposalRaceIntegrationTests.cs"
```

### User Story 3

```text
Task: "Add shared layout lifecycle identity contract tests in tests/SharedMemoryStore.ContractTests/SharedMemoryLayoutContractTests.cs"
Task: "Add slot and lease probe cursor rollover unit tests in tests/SharedMemoryStore.UnitTests/ProbeRolloverTests.cs"
Task: "Add stale lease and reservation lifecycle identity boundary tests in tests/SharedMemoryStore.UnitTests/SlotLifecycleIdentifierTests.cs"
Task: "Add rollover stress integration tests for 1,000,000 post-boundary operations in tests/SharedMemoryStore.IntegrationTests/RolloverStressIntegrationTests.cs"
```

### User Story 4

```text
Task: "Add index health diagnostic contract tests in tests/SharedMemoryStore.ContractTests/DiagnosticsContractTests.cs"
Task: "Add unit tests for occupied, tombstone, empty, reusable capacity, and probe counters in tests/SharedMemoryStore.UnitTests/IndexHealthTests.cs"
Task: "Add high-churn integration tests preserving values, leases, reservations, and duplicate detection in tests/SharedMemoryStore.IntegrationTests/TombstonePressureIntegrationTests.cs"
Task: "Add tombstone pressure benchmark with clean baseline and managed-pressure runs in benchmarks/SharedMemoryStore.Benchmarks/TombstonePressureBenchmarks.cs"
```

---

## Implementation Strategy

### MVP First: User Story 1 Only

1. Complete Phase 1 setup.
2. Complete Phase 2 foundational scaffolding.
3. Complete Phase 3 owner-safe recovery.
4. Stop and validate US1 independently with the owner recovery commands in [quickstart.md](quickstart.md).

### Incremental Delivery

1. Deliver US1 to close the correctness issue in explicit lease recovery.
2. Deliver US2 to normalize lifecycle/disposal races.
3. Deliver US3 to make long-running rollover behavior safe.
4. Deliver US4 to expose and bound tombstone pressure.
5. Complete polish and full package validation.

### Parallel Team Strategy

After Phase 2:
- Developer A: US1 owner recovery tests and implementation.
- Developer B: US2 disposal lifecycle tests and implementation.
- Developer C: US3 rollover tests and implementation.
- Developer D: US4 diagnostics, benchmarks, and index health work.

Coordinate edits to `src/SharedMemoryStore/SharedMemoryStore.cs`, `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`, `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`, `src/SharedMemoryStore/ValueLease.cs`, and `src/SharedMemoryStore/Ingest/ValueReservation.cs`.

---

## Notes

- Tasks marked `[P]` are safe to start in parallel when their phase dependency is met.
- Each user story includes tests before implementation because reliability behavior is part of the public package contract.
- Keep runtime dependencies BCL-only.
- Do not add background workers, console output, global mutable configuration, broad writer APIs, or versioned replacement APIs.
- Commit after each completed story or coherent task group.

---

## Phase 8: Convergence

- [X] T074 Add real two-process same-store lease recovery validation with at least 10,000 multi-owner and stale-owner recovery cycles per SC-001/SC-002 (partial)
- [X] T075 Expand the 100,000-operation disposal race stress coverage to include publish, reserve, acquire, remove, lease and reservation recovery, diagnostics, lease release, reservation advance, commit, abort, token disposal, and concurrent dispose callers per SC-003 (partial)
- [X] T076 Refactor `SharedMemoryStore` public operation entry and exit to use `StoreLifecycleGate` active-operation accounting for disposal coordination per T026 (partial)
- [X] T077 Scale rollover boundary validation to 1,000,000 post-boundary operations and include concurrent publish, acquire, remove, release, reserve, commit, abort, and recovery activity per SC-004 (partial)
- [X] T078 Add tombstone pressure benchmark evidence with clean-index baseline, pressure-state detection before 75% worst-case probe cost, and post-management missing-key and new-insert latency within 2x while preserving active leases, pending reservations, duplicate detection, and visible values per SC-005/SC-006 (partial)
