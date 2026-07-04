# Tasks: Linux, Windows, and Docker Support

**Input**: Design documents from `specs/007-linux-windows-support/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [quickstart.md](quickstart.md), [contracts/](contracts/)

**Tests**: Included. This is behavior-changing runtime/platform work and the feature specification requires automated Linux, Windows, Docker, contract, package-consumption, and validation coverage.

**Organization**: Tasks are grouped by user story so each story can be implemented and validated independently after the foundational platform layer is in place.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and does not depend on incomplete tasks.
- **[Story]**: User story label for story phases only.
- Every task includes an exact target file path.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Add feature-specific scaffolding for platform tests, validation scripts, and Docker sample files.

- [X] T001 [P] Create unit-test platform helper scaffold in `tests/SharedMemoryStore.UnitTests/TestSupport/PlatformTestEnvironment.cs`
- [X] T002 [P] Create integration-test platform capability helper scaffold in `tests/SharedMemoryStore.IntegrationTests/TestSupport/PlatformCapabilityProbe.cs`
- [X] T003 [P] Create Docker sample directory and README scaffold in `samples/DockerSharedMemory/README.md`
- [X] T004 [P] Create Docker validation script scaffold in `scripts/validate-docker-shared-memory.ps1`
- [X] T005 [P] Create cross-platform validation script scaffold in `scripts/validate-cross-platform.ps1`
- [X] T006 Create Docker sample project placeholder in `samples/DockerSharedMemory/DockerSharedMemory.csproj`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Build the internal platform resource layer that all user stories depend on.

**Critical**: No user story implementation should begin until this phase is complete.

- [X] T007 [P] Add status and public compatibility contract tests in `tests/SharedMemoryStore.ContractTests/PlatformCompatibilityContractTests.cs`
- [X] T008 [P] Add platform resource naming tests in `tests/SharedMemoryStore.UnitTests/PlatformResourceNameTests.cs`
- [X] T009 [P] Add shared synchronization wait contract tests in `tests/SharedMemoryStore.UnitTests/SharedStoreSynchronizationTests.cs`
- [X] T010 Define deterministic platform resource identity model in `src/SharedMemoryStore/Interop/PlatformResourceName.cs`
- [X] T011 Define shared memory region abstraction in `src/SharedMemoryStore/Interop/ISharedStoreRegion.cs`
- [X] T012 Define shared synchronization abstraction in `src/SharedMemoryStore/Interop/ISharedStoreSynchronization.cs`
- [X] T013 Define environment capability and failure mapping primitives in `src/SharedMemoryStore/Interop/PlatformCapability.cs`
- [X] T014 Implement Windows shared memory region adapter preserving current named mapping behavior in `src/SharedMemoryStore/Interop/WindowsSharedMemoryRegion.cs`
- [X] T015 Implement Windows shared synchronization adapter preserving current named mutex behavior in `src/SharedMemoryStore/Interop/WindowsSharedStoreSynchronization.cs`
- [X] T016 Create platform resource factory that selects Windows or Linux adapters in `src/SharedMemoryStore/Interop/SharedStorePlatform.cs`

**Checkpoint**: Internal platform abstractions exist, Windows adapter preserves current behavior, and user story work can begin.

---

## Phase 3: User Story 1 - Run the Store on Linux and Windows (Priority: P1) MVP

**Goal**: The same public store workflows run successfully on Linux and Windows without platform-specific consumer code.

**Independent Test**: On Linux and Windows, a clean package-consumption workflow creates a store, publishes values, acquires multiple readers, releases leases, removes and reuses slots, exercises reservation and segmented publishing, reads diagnostics, and does not return unsupported-platform for valid configurations.

### Tests for User Story 1

- [X] T017 [P] [US1] Add Linux/Windows open-mode and unsupported-platform contract tests in `tests/SharedMemoryStore.ContractTests/PlatformRuntimeContractTests.cs`
- [X] T018 [P] [US1] Add Linux/Windows same-host multi-process visibility tests in `tests/SharedMemoryStore.IntegrationTests/CrossPlatformStoreIntegrationTests.cs`
- [X] T019 [P] [US1] Add Linux/Windows package-consumption workflow assertions in `tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs`
- [X] T020 [P] [US1] Add direct ingest and segmented publish cross-platform integration tests in `tests/SharedMemoryStore.IntegrationTests/CrossPlatformIngestIntegrationTests.cs`

### Implementation for User Story 1

- [X] T021 [US1] Implement Linux file-backed shared memory region adapter in `src/SharedMemoryStore/Interop/LinuxSharedMemoryRegion.cs`
- [X] T022 [US1] Implement Linux shared synchronization adapter in `src/SharedMemoryStore/Interop/LinuxSharedStoreSynchronization.cs`
- [X] T023 [US1] Update platform factory to select Linux adapters and preserve unsupported outcomes for other platforms in `src/SharedMemoryStore/Interop/SharedStorePlatform.cs`
- [X] T024 [US1] Refactor store construction and disposal to use platform region and synchronization abstractions in `src/SharedMemoryStore/MemoryStore.cs`
- [X] T025 [US1] Refactor mapped-region pointer ownership while preserving accessor disposal semantics in `src/SharedMemoryStore/Interop/MemoryMappedStoreRegion.cs`
- [X] T026 [US1] Implement Linux open-mode semantics for create-new, open-existing, create-or-open, already-exists, not-found, access-denied, and mapping-failed outcomes in `src/SharedMemoryStore/Interop/LinuxSharedMemoryRegion.cs`
- [X] T027 [US1] Update resource name sanitization and collision prevention for Linux and Windows store names in `src/SharedMemoryStore/Interop/PlatformResourceName.cs`
- [X] T028 [US1] Update basic sample runtime expectations for Linux and Windows in `samples/BasicUsage/Program.cs`
- [X] T029 [US1] Update zero-copy ingest sample to treat Linux and Windows as supported runtime targets in `samples/ZeroCopyIngest/Program.cs`

**Checkpoint**: User Story 1 works independently on Linux and Windows host environments.

---

## Phase 4: User Story 2 - Develop and Validate on Linux and Windows (Priority: P1)

**Goal**: Contributors can build, test, validate docs, validate package consumption, run samples, and pack from clean Linux and Windows checkouts.

**Independent Test**: From clean Linux and Windows checkouts, run restore, build, tests, samples, documentation validation, package-consumption validation, and pack using the documented commands.

### Tests for User Story 2

- [X] T030 [P] [US2] Add script portability tests for package-consumption validation in `tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs`
- [X] T031 [P] [US2] Add production-readiness checks for pwsh-compatible scripts in `tests/SharedMemoryStore.IntegrationTests/PackageProductionReadinessIntegrationTests.cs`
- [X] T032 [P] [US2] Add sample run matrix validation coverage in `tests/SharedMemoryStore.IntegrationTests/SampleValidationIntegrationTests.cs`

### Implementation for User Story 2

- [X] T033 [US2] Update package-consumption validation to run on Linux and Windows with portable shell and path handling in `scripts/validate-package-consumption.ps1`
- [X] T034 [US2] Update documentation validation to reject stale Windows-first platform claims in `scripts/validate-docs.ps1`
- [X] T035 [US2] Implement cross-platform validation wrapper for restore, build, tests, samples, docs, package consumption, and pack in `scripts/validate-cross-platform.ps1`
- [X] T036 [US2] Update package-consumption integration test process launch to use `pwsh` on non-Windows hosts in `tests/SharedMemoryStore.IntegrationTests/PackageConsumptionIntegrationTests.cs`
- [X] T037 [US2] Update production-readiness integration test expectations for portable scripts in `tests/SharedMemoryStore.IntegrationTests/PackageProductionReadinessIntegrationTests.cs`
- [X] T038 [US2] Update contributor validation commands for Linux and Windows in `CONTRIBUTING.md`

**Checkpoint**: User Story 2 validation workflow is executable from clean Linux and Windows checkouts.

---

## Phase 5: User Story 3 - Share Stores Between Docker Containers (Priority: P2)

**Goal**: Two same-host Docker containers configured with the required shared-resource capabilities can participate in one shared store.

**Independent Test**: Run Docker validation that starts a writer container and a verifier container, validates cross-container publish/acquire/release/remove/reuse, verifies diagnostics and recovery, runs at least 10,000 cycles, and proves isolated containers fail clearly.

### Tests for User Story 3

- [X] T039 [P] [US3] Add Docker support contract tests for supported and isolated profiles in `tests/SharedMemoryStore.ContractTests/DockerContainerSharingContractTests.cs`
- [X] T040 [P] [US3] Add Docker cross-container integration test wrapper in `tests/SharedMemoryStore.IntegrationTests/DockerSharedMemoryIntegrationTests.cs`
- [X] T041 [P] [US3] Add Docker sample output validation rules in `tests/SharedMemoryStore.IntegrationTests/DockerSampleValidationTests.cs`
- [X] T085 [P] [US3] Add Docker reservation ingest and segmented publish validation rules in `tests/SharedMemoryStore.IntegrationTests/DockerSampleValidationTests.cs`

### Implementation for User Story 3

- [X] T042 [US3] Finalize Docker shared-memory sample project references and package settings in `samples/DockerSharedMemory/DockerSharedMemory.csproj`
- [X] T043 [US3] Implement writer, reader, verifier, recovery, reservation, segmented-publish, and isolated-profile modes in `samples/DockerSharedMemory/Program.cs`
- [X] T044 [US3] Add Docker image definition for the sample in `samples/DockerSharedMemory/Dockerfile`
- [X] T045 [US3] Add supported same-host container Compose profile with shared IPC, owner-liveness, and adequate shared-memory capacity in `samples/DockerSharedMemory/docker-compose.yml`
- [X] T046 [US3] Add isolated negative Compose profile in `samples/DockerSharedMemory/docker-compose.isolated.yml`
- [X] T047 [US3] Implement Docker validation wrapper with supported, isolated, contention, disposal-race, and recovery profiles in `scripts/validate-docker-shared-memory.ps1`
- [X] T086 [US3] Add Docker clean-consumer package validation that builds a fresh container project, references or installs the package, and completes first-use and advanced workflows in `scripts/validate-docker-shared-memory.ps1`
- [X] T048 [US3] Add Docker sample project to solution references in `SharedMemoryStore.slnx`
- [X] T049 [US3] Document Docker sample prerequisites, run commands, expected output, cleanup, and limitations in `samples/DockerSharedMemory/README.md`

**Checkpoint**: User Story 3 proves same-host Docker sharing and clear failure for isolated container profiles.

---

## Phase 6: User Story 4 - Trust Cross-Platform Reliability and Recovery (Priority: P2)

**Goal**: Synchronization, owner recovery, disposal races, long-running reuse, diagnostics, and corruption detection preserve the same data-safety guarantees on Linux, Windows, and supported Docker deployments.

**Independent Test**: Run reliability, lifecycle, contention, recovery, reuse, churn, and diagnostics scenarios on Linux, Windows, and supported Docker profiles with matching public outcomes and no premature reuse of active leases.

### Tests for User Story 4

- [X] T050 [P] [US4] Add cross-platform owner-liveness unit tests in `tests/SharedMemoryStore.UnitTests/LeaseOwnerClassifierCrossPlatformTests.cs`
- [X] T051 [P] [US4] Add cross-platform lease and reservation recovery integration tests in `tests/SharedMemoryStore.IntegrationTests/CrossPlatformRecoveryIntegrationTests.cs`
- [X] T052 [P] [US4] Add cross-platform contention and disposal race tests in `tests/SharedMemoryStore.IntegrationTests/CrossPlatformSynchronizationIntegrationTests.cs`
- [X] T053 [P] [US4] Add long-running reuse and churn validation tests in `tests/SharedMemoryStore.IntegrationTests/CrossPlatformRolloverStressIntegrationTests.cs`
- [X] T054 [P] [US4] Add Docker recovery and abrupt-exit validation in `tests/SharedMemoryStore.IntegrationTests/DockerRecoveryIntegrationTests.cs`
- [X] T087 [P] [US4] Add Docker contention, bounded-wait, cancellation, busy, and disposal-race validation in `tests/SharedMemoryStore.IntegrationTests/DockerSynchronizationIntegrationTests.cs`

### Implementation for User Story 4

- [X] T055 [US4] Remove Windows-only recovery gate and route owner checks through a conservative classifier in `src/SharedMemoryStore/Leasing/LeaseRecovery.cs`
- [X] T056 [US4] Implement Linux and container-safe owner-liveness classification in `src/SharedMemoryStore/Leasing/LeaseOwnerClassifier.cs`
- [X] T057 [US4] Share owner-liveness classification with reservation recovery in `src/SharedMemoryStore/Ingest/ReservationRecovery.cs`
- [X] T058 [US4] Update lease owner process tool for cross-platform and container recovery scenarios in `tests/SharedMemoryStore.LeaseOwnerTool/Program.cs`
- [X] T059 [US4] Harden abandoned synchronization, cancellation, busy, and disposed outcomes through the synchronization abstraction in `src/SharedMemoryStore/MemoryStore.cs`
- [X] T060 [US4] Update diagnostics recording for unsupported or unsafe platform owner decisions in `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`
- [X] T061 [US4] Add long-running cross-platform stress validation entry points in `benchmarks/SharedMemoryStore.Benchmarks/LifecycleStressBenchmarks.cs`

**Checkpoint**: User Story 4 reliability guarantees hold across supported environments.

---

## Phase 7: User Story 5 - Learn Supported Platform Behavior from Public Docs (Priority: P3)

**Goal**: Consumers and maintainers can understand Linux, Windows, and same-host Docker support, prerequisites, validation coverage, limitations, and release impact from public documentation.

**Independent Test**: Documentation validation passes with zero stale Windows-first or unsupported Linux/Docker claims, and a reader can find platform setup and Docker guidance within two navigation steps.

### Tests for User Story 5

- [X] T062 [P] [US5] Add docs validation checks for Linux, Windows, Docker, unsupported scenarios, and stale platform wording in `scripts/validate-docs.ps1`
- [X] T063 [P] [US5] Add package metadata platform wording checks in `tests/SharedMemoryStore.ContractTests/PackageContractTests.cs`

### Implementation for User Story 5

- [X] T064 [US5] Update repository overview and platform support statement in `README.md`
- [X] T065 [US5] Replace Windows-first portability guidance with Linux, Windows, and same-host Docker guidance in `docs/portability.md`
- [X] T066 [US5] Update getting-started and usage platform expectations in `docs/getting-started.md`
- [X] T067 [US5] Update sample navigation and Docker sample links in `docs/samples.md`
- [X] T068 [US5] Update lifecycle, diagnostics, and recovery platform behavior docs in `docs/lifecycle.md`
- [X] T069 [US5] Update architecture and maintainer platform adapter guidance in `docs/architecture.md`
- [X] T070 [US5] Update maintainer validation and release responsibilities in `docs/maintainers.md`
- [X] T071 [US5] Update package and release documentation for Linux, Windows, Docker, and compatibility impact in `docs/releases.md`
- [X] T072 [US5] Update changelog platform support entry in `CHANGELOG.md`
- [X] T073 [US5] Update package release notes and XML platform comments in `src/SharedMemoryStore/SharedMemoryStore.csproj`

**Checkpoint**: User Story 5 documentation and metadata accurately describe supported platform behavior.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Final validation, cleanup, release evidence, and implementation consistency after selected stories are complete.

- [X] T074 [P] Review public API and semantic version impact against `specs/007-linux-windows-support/contracts/compatibility-contract.md`
- [X] T075 [P] Review unsupported environment and security boundary wording against `specs/007-linux-windows-support/contracts/docker-container-sharing-contract.md`
- [X] T076 Run Release build validation against `SharedMemoryStore.slnx`
- [X] T077 Run full Release test suite against `SharedMemoryStore.slnx`
- [X] T078 Run sample validation for all sample projects under `samples/`
- [X] T079 Run documentation validation using `scripts/validate-docs.ps1`
- [X] T080 Run package-consumption validation using `scripts/validate-package-consumption.ps1`
- [X] T081 Run cross-platform validation wrapper using `scripts/validate-cross-platform.ps1`
- [X] T082 Run Docker shared-memory validation using `scripts/validate-docker-shared-memory.ps1`
- [X] T083 Pack release artifact using `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [X] T084 Capture Linux, Windows, Docker, unsupported-scenario, and compatibility evidence in `docs/releases.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies.
- **Phase 2 Foundational**: Depends on Phase 1 and blocks all user stories.
- **Phase 3 US1**: Depends on Phase 2. This is the MVP.
- **Phase 4 US2**: Depends on Phase 2 and benefits from US1 for meaningful Linux validation.
- **Phase 5 US3**: Depends on Phase 2 and should follow US1 so host Linux behavior is established before container validation.
- **Phase 6 US4**: Depends on Phase 2 and can run after US1 starts, but full Docker recovery coverage depends on US3.
- **Phase 7 US5**: Depends on the behavior decisions from US1 through US4, but documentation test scaffolding can start earlier.
- **Phase 8 Polish**: Depends on all selected user stories being complete.

### User Story Dependencies

- **US1 (P1)**: No dependency on other user stories after foundation; delivers MVP runtime support.
- **US2 (P1)**: Can start after foundation; full validation is more useful once US1 is implemented.
- **US3 (P2)**: Depends on the Linux resource behavior from US1.
- **US4 (P2)**: Can start after foundation for host reliability; Docker-specific reliability depends on US3.
- **US5 (P3)**: Depends on final behavior and validation outcomes from US1-US4.

### Parallel Opportunities

- Setup tasks T001-T005 can run in parallel.
- Foundational tests T007-T009 can run in parallel before implementation.
- US1 tests T017-T020 can run in parallel.
- US2 tests T030-T032 can run in parallel.
- US3 tests T039-T041 and T085 can run in parallel.
- US4 tests T050-T054 and T087 can run in parallel.
- US5 docs validation and package metadata tests T062-T063 can run in parallel.
- Documentation updates T064-T073 can be split by file after runtime behavior is finalized.

---

## Parallel Example: User Story 1

```text
Task: "T017 [P] [US1] Add Linux/Windows open-mode and unsupported-platform contract tests in tests/SharedMemoryStore.ContractTests/PlatformRuntimeContractTests.cs"
Task: "T018 [P] [US1] Add Linux/Windows same-host multi-process visibility tests in tests/SharedMemoryStore.IntegrationTests/CrossPlatformStoreIntegrationTests.cs"
Task: "T019 [P] [US1] Add Linux/Windows package-consumption workflow assertions in tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs"
Task: "T020 [P] [US1] Add direct ingest and segmented publish cross-platform integration tests in tests/SharedMemoryStore.IntegrationTests/CrossPlatformIngestIntegrationTests.cs"
```

## Parallel Example: User Story 3

```text
Task: "T039 [P] [US3] Add Docker support contract tests for supported and isolated profiles in tests/SharedMemoryStore.ContractTests/DockerContainerSharingContractTests.cs"
Task: "T040 [P] [US3] Add Docker cross-container integration test wrapper in tests/SharedMemoryStore.IntegrationTests/DockerSharedMemoryIntegrationTests.cs"
Task: "T041 [P] [US3] Add Docker sample output validation rules in tests/SharedMemoryStore.IntegrationTests/DockerSampleValidationTests.cs"
Task: "T085 [P] [US3] Add Docker reservation ingest and segmented publish validation rules in tests/SharedMemoryStore.IntegrationTests/DockerSampleValidationTests.cs"
```

## Parallel Example: User Story 4

```text
Task: "T050 [P] [US4] Add cross-platform owner-liveness unit tests in tests/SharedMemoryStore.UnitTests/LeaseOwnerClassifierCrossPlatformTests.cs"
Task: "T051 [P] [US4] Add cross-platform lease and reservation recovery integration tests in tests/SharedMemoryStore.IntegrationTests/CrossPlatformRecoveryIntegrationTests.cs"
Task: "T052 [P] [US4] Add cross-platform contention and disposal race tests in tests/SharedMemoryStore.IntegrationTests/CrossPlatformSynchronizationIntegrationTests.cs"
Task: "T053 [P] [US4] Add long-running reuse and churn validation tests in tests/SharedMemoryStore.IntegrationTests/CrossPlatformRolloverStressIntegrationTests.cs"
Task: "T054 [P] [US4] Add Docker recovery and abrupt-exit validation in tests/SharedMemoryStore.IntegrationTests/DockerRecoveryIntegrationTests.cs"
Task: "T087 [P] [US4] Add Docker contention, bounded-wait, cancellation, busy, and disposal-race validation in tests/SharedMemoryStore.IntegrationTests/DockerSynchronizationIntegrationTests.cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 setup.
2. Complete Phase 2 foundational platform resource abstractions.
3. Complete Phase 3 US1.
4. Stop and validate Linux and Windows host runtime behavior independently.
5. Demonstrate the package-consumption workflow on both host platforms.

### Incremental Delivery

1. Deliver US1 for Linux and Windows runtime support.
2. Deliver US2 so maintainers can validate from clean Linux and Windows checkouts.
3. Deliver US3 for supported same-host Docker sharing.
4. Deliver US4 to harden reliability and recovery across supported environments.
5. Deliver US5 to publish accurate support docs, metadata, and release evidence.

### Parallel Team Strategy

1. One engineer completes platform abstractions and Windows compatibility tests.
2. One engineer starts Linux region/synchronization tests and implementation after foundation.
3. One engineer prepares Docker sample and validation once Linux host behavior is stable.
4. One engineer updates validation scripts and docs after behavior decisions stabilize.

## Notes

- Every runtime behavior change should be covered by a failing test before implementation.
- Avoid public API or layout changes unless the compatibility contract requires and documents them.
- Preserve Windows compatibility at every checkpoint.
- Docker support is same-host shared-memory participation, not cross-host or distributed-cache behavior.

## Phase 9: Convergence

- [X] T088 Update Linux recovery contracts and 10,000-cycle multi-owner recovery tests to expect supported recovery on both Linux and Windows per FR-006 / SC-006 (partial)
- [X] T089 Add Docker clean-consumer package validation that installs or references the packed package in a fresh container project and completes first-use, reservation, segmented publish, diagnostics, recovery, and disposal workflows per FR-013 (missing)
- [X] T090 Extend the supported Docker profile to execute reservation, segmented publish, diagnostics, and recovery workflows inside configured containers instead of only in the local sample run per FR-022 / SC-004 (partial)
- [X] T091 Add Docker abrupt-exit lease and reservation recovery validation in `tests/SharedMemoryStore.IntegrationTests/DockerRecoveryIntegrationTests.cs` and wire a recovery profile through `scripts/validate-docker-shared-memory.ps1` per US3/AC3 / SC-006 (missing)
- [X] T092 Add Docker contention, bounded-wait, cancellation, busy, and disposal-race validation in `tests/SharedMemoryStore.IntegrationTests/DockerSynchronizationIntegrationTests.cs` and expose contention and disposal-race profiles from `scripts/validate-docker-shared-memory.ps1` per FR-005 / SC-005 (missing)
- [X] T093 Run clean standalone Linux checkout validation and record restore, build, tests, samples, docs, package consumption, pack, Docker, unsupported-profile, and compatibility evidence in `docs/releases.md` per SC-008 / SC-009 (partial)
