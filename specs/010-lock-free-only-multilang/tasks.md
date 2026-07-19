# Tasks: Lock-Free-Only Multi-Language Store

**Input**: Design documents from `specs/010-lock-free-only-multilang/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Tests are mandatory and precede behavior-changing implementation as required by the specification and constitution.

**Organization**: Tasks are grouped by user story. User Story 1 is the independently testable single-protocol MVP; later stories add the complete data lifecycle, recovery/progress, and consumable diagnostics/package surface.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: May run in parallel because it touches different files and has no dependency on an incomplete task in the same phase.
- **[Story]**: Maps the task to a user story in `spec.md`.
- Every task names its concrete file or directory scope.

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish the cross-language source, fixture, agent, and release scaffolding without changing mapped behavior.

- [X] T001 Create the feature-owned release evidence contract and tier placeholders in `specs/010-lock-free-only-multilang/release-qualification.md`
- [X] T002 [P] Register planned SMS2 native modules, agents, and test executables without enabling incomplete behavior in `src/cpp/CMakeLists.txt` and `tests/cpp/CMakeLists.txt`
- [X] T003 [P] Add shared native SMS2 fixture/test helpers and exact JSON fixture loading in `tests/cpp/test_support_v2.hpp`
- [X] T004 [P] Define one versioned cross-runtime JSON-lines command/checkpoint catalog for protocol identity, participant capacity, lifecycle, pause, crash, and diagnostics in `tests/SharedMemoryStore.InteropTests/AgentProtocol.cs`
- [X] T005 Verify and minimally extend repository ignore coverage for native, Python wheel/venv, qualification, trace, and generated-fixture output in `.gitignore` and `.dockerignore`

---

## Phase 2: Foundational (Canonical Protocol and Native Atomic Primitives)

**Purpose**: Make the canonical SMS2 bytes, codecs, and mapped-atomic behavior executable before any runtime may attach or mutate a store.

**⚠️ CRITICAL**: No user-story implementation begins until this phase passes on the host toolchain.

### Tests

- [X] T006 [P] Add failing managed manifest tests for the sole SMS2 identity, complete record offsets, codec vectors, sizing limits, hashes, names, statuses, required features, and offline fixtures in `tests/SharedMemoryStore.ContractTests/LockFreeLayoutContractTests.cs`
- [X] T007 [P] Add failing native mask-7 header, record-offset, layout-arithmetic, count-limit, alignment, and overflow-vector tests in `tests/cpp/layout_v2_tests.cpp`
- [X] T008 [P] Add failing native participant, slot, lease, binding, spill-summary, directory-location, and directory-operation codec tests in `tests/cpp/control_word_tests.cpp`
- [X] T009 [P] Add failing native lock-free/alignment/memory-order plus no-wait/finite/infinite/cancellation budget tests in `tests/cpp/atomic_budget_tests.cpp`
- [X] T010 [P] Add failing cross-process acquire/release publication and sequential-CAS visibility litmus tests in `tests/cpp/mapped_atomic_litmus.cpp` and `tests/cpp/mapped_atomic_agent.cpp`
- [X] T011 [P] Rewrite Python static conformance tests to require only SMS2 records, codecs, sizing, states, features, hashes, names, statuses, and offline fixtures in `tests/python/test_protocol_manifest.py`

### Implementation

- [X] T012 Expand the canonical machine-readable SMS2 authority with all required sizes, offsets, codecs, arithmetic, hash/name/status vectors, malformed cases, and offline fixtures in `protocol/fixtures/v2.0/manifest.json` and `protocol/fixtures/v2.0/generate-fixtures.ps1`
- [X] T013 Synchronize the narrative layout and memory-order rules with the expanded manifest without changing topology or required mask 7 in `protocol/layout-v2.0.md`
- [X] T014 Implement checked SMS2 records, offsets, layout calculation, count limits, required-feature validation, and header matching in `src/cpp/src/layout_v2.hpp` and `src/cpp/src/layout_v2.cpp`
- [X] T015 Implement exact unsigned participant/slot/lease/binding/spill/directory control codecs with reserved-bit and range validation in `src/cpp/src/control_words.hpp`
- [X] T016 Implement qualified acquire-load, release-store, and sequentially consistent 64-bit mapped CAS/RMW with x86-64, always-lock-free, bounds, and alignment gates in `src/cpp/src/mapped_atomic.hpp`
- [X] T017 Implement operation-wide no-wait, finite, infinite, backoff, periodic-check, and opaque cancellation budgeting in `src/cpp/src/operation_budget.hpp`
- [X] T018 Isolate canonical FNV hashing, exact bytes, checked arithmetic, strict UTF-8 validation, and resource-name derivation from the retired layout in `src/cpp/src/protocol.cpp` and `src/cpp/src/internal.hpp`
- [X] T019 Build and run the foundational managed, native, and Python conformance/atomic suites through `SharedMemoryStore.slnx`, `tests/cpp/CMakeLists.txt`, and `tests/python/test_protocol_manifest.py`

**Checkpoint**: Every runtime agrees on SMS2 bytes and the native toolchain has executable lock-free interprocess atomic evidence.

---

## Phase 3: User Story 1 - Open One Canonical Store from Any Runtime (Priority: P1) 🎯 MVP

**Goal**: Remove the profile/legacy product path and allow C#, C++, and Python to create or open the same participant-registered SMS2 mapping.

**Independent Test**: Each runtime creates a store, both other runtimes open it, all report `(2,0,2,7,0)`, participant capacity is enforced/reused, and retired or malformed mappings fail before payload projection.

### Managed tests

- [X] T020 [P] [US1] Replace profile-era reflection assertions with failing single-protocol API and five-field protocol identity assertions in `tests/SharedMemoryStore.ContractTests/SingleProtocolApiContractTests.cs` and remove `tests/SharedMemoryStore.ContractTests/LockFreeProfileApiContractTests.cs`
- [X] T021 [P] [US1] Add failing unconditional SMS2 sizing, slot/participant limits, ordinary helper, and participant-capacity validation tests in `tests/SharedMemoryStore.UnitTests/StoreOptionsValidationTests.cs`
- [X] T022 [P] [US1] Add failing always-present-engine facade and ownership-transfer tests without `IStoreEngine.Profile` in `tests/SharedMemoryStore.UnitTests/MemoryStoreFacadeTests.cs`, `tests/SharedMemoryStore.UnitTests/StoreEngineFactoryOwnershipTests.cs`, and `tests/SharedMemoryStore.UnitTests/StoreLifecycleGateBudgetTests.cs`
- [X] T023 [P] [US1] Replace mixed-profile cases with failing physical-creator, zero-header, retired-header, feature-mask, participant-exhaustion/reuse, actual-capacity, and reopen tests in `tests/SharedMemoryStore.IntegrationTests/LockFreeProfileOpenIntegrationTests.cs`
- [X] T024 [P] [US1] Add failing static inspection proving no public profile selector, `CreateLockFree`, legacy engine, creatable SMS1 path, or v1 shared operation lock in `tests/SharedMemoryStore.ContractTests/RetiredLayoutAbsenceContractTests.cs`

### Managed implementation

- [X] T025 [US1] Remove `StoreProfile`, make ordinary `Create` and `CalculateRequiredBytes` participant-aware SMS2 helpers, and implement the five-field protocol identity in `src/SharedMemoryStore/SharedMemoryStoreOptions.cs` and `src/SharedMemoryStore/StoreProtocolInfo.cs`
- [X] T026 [US1] Make SMS2 count/size/architecture validation unconditional and remove the legacy `StoreLayout` validation result in `src/SharedMemoryStore/Options/SharedMemoryStoreOptionsValidator.cs`
- [X] T027 [US1] Remove `IStoreEngine.Profile`, legacy wrapping, and profile dispatch while renaming the sole engine cold creation path in `src/SharedMemoryStore/Engines/IStoreEngine.cs`, `src/SharedMemoryStore/Engines/StoreEngineFactory.cs`, and `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs`
- [X] T028 [US1] Convert `MemoryStore` to an always-present engine-only facade and delete embedded v1 fields, locking, initialization, operation bodies, handle codecs, test hooks, and nullable dispatch in `src/SharedMemoryStore/MemoryStore.cs`
- [X] T029 [P] [US1] Move canonical key validation/hash behavior from `src/SharedMemoryStore/Layout/StoreKey.cs` to `src/SharedMemoryStore/LockFree/StoreKey.cs` and update SMS2 consumers
- [X] T030 [P] [US1] Split public reservation recovery value types into `src/SharedMemoryStore/ReservationRecoveryOptions.cs` without retaining legacy recovery implementation
- [X] T031 [US1] Delete `src/SharedMemoryStore/Engines/LegacyV12/LegacyV12StoreEngine.cs`, retired files under `src/SharedMemoryStore/Layout/`, all files under `src/SharedMemoryStore/Slots/`, `src/SharedMemoryStore/Leasing/LeaseRecovery.cs`, `src/SharedMemoryStore/Leasing/LeaseRegistry.cs`, `src/SharedMemoryStore/Leasing/LeaseRelease.cs`, and `src/SharedMemoryStore/Ingest/ReservationMemoryManager.cs`
- [X] T032 [US1] Retarget shared managed options factories and process-agent opening to ordinary SMS2 creation in `tests/SharedMemoryStore.UnitTests/TestSupport/StoreTestNames.cs`, `tests/SharedMemoryStore.ContractTests/ContractStoreFactory.cs`, `tests/SharedMemoryStore.IntegrationTests/TestSupport/IntegrationStoreFactory.cs`, `tests/SharedMemoryStore.LockFreeAgent/`, and `tests/SharedMemoryStore.LeaseOwnerTool/`
- [X] T033 [US1] Build `src/SharedMemoryStore/SharedMemoryStore.csproj` with warnings as errors and resolve all single-protocol production compilation failures

### Native cold-open and participant tests

- [X] T034 [P] [US1] Add failing physical-creation, zero/retired header, mask mismatch, actual-capacity, dimension mismatch, and unsupported-architecture tests in `tests/cpp/cold_open_tests.cpp`
- [X] T035 [P] [US1] Add failing Windows mutex-before-mapping, create disposition, bounded wait, ownership transfer, and failed-open cleanup tests in `tests/cpp/platform_windows_v2_tests.cpp`
- [X] T036 [P] [US1] Add failing Linux lifecycle/lock ordering, stable inode, owner anchor, release marker, malformed artifact, namespace identity, and bounded-close tests in `tests/cpp/platform_linux_v2_tests.cpp`
- [X] T037 [P] [US1] Add failing participant registration, table-full, namespace mode, first-claim validation, closing, recovery handoff, reuse, and retirement tests in `tests/cpp/participant_registry_tests.cpp`

### Native cold-open and participant implementation

- [X] T038 [US1] Implement the retained cold-open transaction, physical creation disposition, actual-capacity mapping, ownership transfer, and reverse-order cleanup in `src/cpp/src/cold_open.hpp` and `src/cpp/src/cold_open.cpp`
- [X] T039 [P] [US1] Rework Windows named-gate-before-mapping coordination and retain the gate through participant registration in `src/cpp/src/platform_windows.cpp`
- [X] T040 [US1] Rework Linux `.lifecycle -> .lock -> mapping/owner` coordination, disposition, actual-capacity projection, and reverse cleanup in `src/cpp/src/platform_linux.cpp`
- [X] T041 [US1] Implement Linux private owner anchors, exact sidecar replacement, release markers, reconciliation, conservative orphan sweeping, and bounded close in `src/cpp/src/linux_owner_lifecycle.hpp` and `src/cpp/src/linux_owner_lifecycle.cpp`
- [X] T042 [US1] Implement participant token sizing, process/start/namespace identity, Registering-to-Active publication, table-full proof, and first-claim validation in `src/cpp/src/participant_registry.hpp` and `src/cpp/src/participant_registry.cpp`
- [X] T043 [US1] Implement participant Closing/Recovering handoff, exact-reference scans, generation advance, reuse, and terminal retirement in `src/cpp/src/participant_registry.cpp`
- [X] T044 [US1] Implement store-control validation, exact `Ready -> Corrupt` latch, creator-only SMS2 initialization, and participant-attached open orchestration in `src/cpp/src/store_control.hpp`, `src/cpp/src/store_control.cpp`, and `src/cpp/src/store.cpp`

### ABI/Python/open interoperability tests

- [X] T045 [P] [US1] Rewrite native ABI tests for ABI `0x00020000`, protocol `(2,0,2,7,0)`, participant sizing, status 11, fixed record queries, and absence of v1 fields in `tests/cpp/c_api_tests.cpp`
- [X] T046 [P] [US1] Rewrite Python ABI tests for ABI 2 ctypes sizes/offsets, participant options, status 11, mask 7, package-only loading, and absence of v1 constants in `tests/python/test_native_abi.py`
- [X] T047 [P] [US1] Add failing Python canonical sizing/open, protocol identity, participant exhaustion/reuse, retired/malformed rejection, and unsupported-architecture tests in `tests/python/test_store.py`
- [X] T048 [P] [US1] Replace SMS2 rejection coverage with all-runtime SMS2 acceptance, retired-layout/feature rejection, same-name creation authority, and participant capacity in `tests/SharedMemoryStore.InteropTests/LockFreeLayoutRejectionTests.cs`
- [X] T049 [P] [US1] Add agent-contract tests requiring protocol 2, participant capacity, immutable protocol identity, and canonical statuses from all runtimes in `tests/SharedMemoryStore.InteropTests/AgentProtocolTests.cs` and `tests/python/test_interop_agent.py`

### ABI/Python/open implementation

- [x] T050 [US1] Replace ABI 1 declarations with ABI 2 opaque store handles, participant-aware options/sizing, protocol/layout queries, cancellation handle, record constants, and status 11 in `src/cpp/include/shared_memory_store/c_api.h` and `src/cpp/src/c_api.cpp`
- [x] T051 [US1] Update move-only C++ store wrappers and protocol identity for ABI 2 participant-aware open in `src/cpp/include/shared_memory_store/store.hpp`
- [X] T052 [US1] Replace Python ABI declarations with ABI 2 options/protocol/layout structures, signatures, constants, record checks, and adjacent-library loading in `src/python/shared_memory_store/_native.py`
- [X] T053 [P] [US1] Add `ParticipantTableFull`, retain canonical numeric statuses, and replace resource-naming terminology with resource protocol in `src/python/shared_memory_store/enums.py`
- [X] T054 [US1] Implement canonical Python `StoreOptions`, participant-aware sizing/open, immutable `ProtocolInfo`, and open-handle identity in `src/python/shared_memory_store/store.py`
- [X] T055 [US1] Export Python ABI 2, protocol `(2,0,2,7,0)`, package version placeholder, `ProtocolInfo`, and single-protocol symbols in `src/python/shared_memory_store/__init__.py`
- [X] T056 [P] [US1] Update the managed, native, and Python JSON-lines agents to open SMS2 with participant capacity and emit protocol 2 identity in `tests/SharedMemoryStore.InteropAgent/AgentSession.cs`, `tests/cpp/interop_agent.cpp`, and `tests/python/interop_agent.py`
- [X] T057 [US1] Run the three creator directions and nine open/identity cells through `tests/SharedMemoryStore.InteropTests/` and fix only canonical-open regressions until the User Story 1 checkpoint passes

**Checkpoint**: The repository has one creatable/readable current protocol and every runtime can attach safely before data operations are ported.

---

## Phase 4: User Story 2 - Exchange Values and Lifetimes Across Runtimes (Priority: P1)

**Goal**: Implement the complete publication, reservation, lease, removal, and reuse lifecycle in the native/Python path and retarget all general managed behavior to the sole protocol.

**Independent Test**: Every ordered runtime pair completes exact binary contiguous/segmented publish, reserve/commit/abort, acquire/release, pending removal, final reclaim, collision spill, and republish.

### Native state-machine tests

- [X] T058 [P] [US2] Add failing primary lookup, exact-key, stale/malformed binding, double revalidation, and spill-summary lookup tests in `tests/cpp/directory_lookup_tests.cpp`
- [X] T059 [P] [US2] Add failing deterministic insert/unlink helping, cancellation, target-loss, alternate-location, future-generation, and stable-corruption schedules in `tests/cpp/directory_schedule_tests.cpp`
- [X] T060 [P] [US2] Add failing exact-hash-collision, capacity-preserving overflow, spill churn, witness repoint, and versioned-empty tests in `tests/cpp/directory_collision_tests.cpp`
- [X] T061 [P] [US2] Add failing slot claim/full proof, publication intent, reservation advancement, stale token, commit, abort, reuse, and retirement tests in `tests/cpp/slot_reservation_tests.cpp`
- [X] T062 [P] [US2] Add failing contiguous/segmented publication, duplicate precedence, partial-copy cancellation, operation budget, and zero-copy reservation tests in `tests/cpp/publish_v2_tests.cpp`
- [X] T063 [P] [US2] Add failing lease claim/full proof, activation revalidation, immutable projection, release, stale generation, reuse, and retirement tests in `tests/cpp/lease_v2_tests.cpp`
- [X] T064 [P] [US2] Add failing logical remove, foreign-lease preservation, final-release reclamation, unlink helping, and generation reuse tests in `tests/cpp/remove_reclaim_v2_tests.cpp`

### Native state-machine implementation

- [X] T065 [US2] Implement validated primary/overflow lookup, exact-key comparison, budgeted scans, and versioned spill-summary negative caching in `src/cpp/src/key_directory.hpp` and `src/cpp/src/key_directory.cpp`
- [X] T066 [US2] Implement generation-fenced insert mutation publication, target arbitration, helping, duplicate rejection, exact rollback, and source revalidation in `src/cpp/src/key_directory.cpp`
- [X] T067 [US2] Implement unlink helping, first-location arbitration, alternate cleanup, spill witness repoint/clear, stable tuple confirmation, and corruption classification in `src/cpp/src/key_directory.cpp`
- [X] T068 [US2] Implement participant-owned slot claim, structural classification, generation retirement, and stable exact `StoreFull` proof in `src/cpp/src/slot_table.hpp` and `src/cpp/src/slot_table.cpp`
- [X] T069 [US2] Implement explicit/atomic publication intent, metadata-ready ordering, reservation advance, commit, abort, cancellation handoff, and stale-token fencing in `src/cpp/src/slot_table.cpp`
- [X] T070 [US2] Implement lifetime-validated writable reservation projection without mapped ownership objects in `src/cpp/src/reservation_memory.hpp`
- [X] T071 [US2] Implement bounded contiguous/segmented publish and reserve/advance/commit/abort orchestration in `src/cpp/src/store.cpp`
- [X] T072 [US2] Implement participant-tagged lease claim, stable `LeaseTableFull` proof, activation, final binding revalidation, exact release, reuse, and retirement in `src/cpp/src/lease_registry.hpp` and `src/cpp/src/lease_registry.cpp`
- [X] T073 [US2] Implement acquire and lifetime-fenced immutable value/descriptor projection/release orchestration in `src/cpp/src/store.cpp`
- [X] T074 [US2] Implement logical removal, active-lease classification, directory unlink, cooperative reclamation, helper cleanup, and generation advance in `src/cpp/src/reclaimer.hpp` and `src/cpp/src/reclaimer.cpp`
- [X] T075 [US2] Connect bounded remove and post-release reclamation outcomes without classifying cleanup uncertainty as corruption in `src/cpp/src/store.cpp`

### Managed and Python lifecycle tests/retargeting

- [X] T076 [P] [US2] Remove legacy constructors/testing properties while preserving opaque SMS2 lease/reservation lifetime callbacks in `src/SharedMemoryStore/ValueLease.cs` and `src/SharedMemoryStore/Ingest/ValueReservation.cs`
- [X] T077 [P] [US2] Retarget all protocol-neutral unit state-machine, allocation, corruption, lifecycle, reservation, lease, remove, and reuse tests to ordinary SMS2 creation in `tests/SharedMemoryStore.UnitTests/`
- [X] T078 [P] [US2] Retarget protocol-neutral public behavior tests to SMS2 and remove only v1 record/topology contracts in `tests/SharedMemoryStore.ContractTests/`
- [X] T079 [P] [US2] Retarget general integration coverage to SMS2, remove profile comparisons, and replace v1 layout-reader/tombstone assumptions in `tests/SharedMemoryStore.IntegrationTests/`
- [X] T080 [US2] Delete v1-only test files and hooks after equivalent SMS2 coverage passes in `tests/SharedMemoryStore.UnitTests/CorruptStoreTests.cs`, `tests/SharedMemoryStore.UnitTests/IndexHealthTests.cs`, `tests/SharedMemoryStore.UnitTests/LeaseRecoveryOwnershipTests.cs`, `tests/SharedMemoryStore.UnitTests/ProbeRolloverTests.cs`, `tests/SharedMemoryStore.UnitTests/SlotLifecycleIdentifierTests.cs`, `tests/SharedMemoryStore.UnitTests/SlotPublishStateTests.cs`, `tests/SharedMemoryStore.UnitTests/TestSupport/RolloverTestHooks.cs`, `tests/SharedMemoryStore.ContractTests/IngestLayoutContractTests.cs`, and `tests/SharedMemoryStore.ContractTests/SharedMemoryLayoutContractTests.cs`
- [X] T081 [P] [US2] Add failing Python publish/segments, reservation visibility, lease/remove/reuse, stale-token, participant, and zero-copy view invalidation tests in `tests/python/test_lifecycle.py`
- [X] T082 [P] [US2] Add failing Python same-handle concurrency, close-versus-entered-call, child-token, and derived-memoryview lifetime tests in `tests/python/test_threading.py`
- [X] T083 [US2] Replace broad Python call serialization with close-safe operation-entry accounting and implement ABI 2 publish/reservation/lease/remove/view lifetimes in `src/python/shared_memory_store/store.py`

### Positive interoperability

- [X] T084 [P] [US2] Expand the ordered matrix to all nine runtime pairs for exact binary publish, segments, reserve/commit/abort, acquire/release, remove/final release, and republish in `tests/SharedMemoryStore.InteropTests/CoreExchangeMatrixTests.cs`
- [X] T085 [P] [US2] Add mixed-runtime collision/overflow churn, same-key race, participant capacity, twelve-reader pending-remove/final-reclaim, and mapping-incarnation token scenarios in `tests/SharedMemoryStore.InteropTests/MixedLifecycleTests.cs`
- [X] T086 [P] [US2] Extend native and Python agents with segments, writable reservation, checksum, lease hold/release, collision, remove/reuse, and exact status commands in `tests/cpp/interop_agent.cpp` and `tests/python/interop_agent.py`
- [X] T087 [US2] Make the host interoperability runner build installed/current artifacts and execute the full nine-pair normal and stress lifecycle matrix in `scripts/validate-interoperability.ps1`
- [X] T088 [US2] Run the complete User Story 2 managed/native/Python/nine-pair lifecycle suite and fix every byte, status, lifetime, or capacity regression

**Checkpoint**: All three runtimes exchange complete values and protect the exact same reservation, lease, removal, and reuse lifetimes.

---

## Phase 5: User Story 3 - Survive Contention, Pauses, and Crashes (Priority: P1)

**Goal**: Port exact-incarnation recovery and disposal, prove helpable progress under pause/crash, and remove every hot operation-lock dependency.

**Independent Test**: Each runtime is paused or killed at every persistent transition while the other runtimes help, recover only abandoned state, preserve live owners/views, and reuse capacity without later-generation mutation.

### Tests

- [X] T089 [P] [US3] Add failing native PID/start/namespace participant classification plus exact lease/reservation/directory recovery tests in `tests/cpp/recovery_v2_tests.cpp`
- [X] T090 [P] [US3] Add failing native concurrent close, operation drain, owned-resource cleanup, participant handoff, and borrowed-view invalidation tests in `tests/cpp/disposal_v2_tests.cpp`
- [X] T091 [P] [US3] Add failing native multiprocess pause/crash/help/reuse, raw visibility, and zero-hot-OS-lock scenarios in `tests/cpp/lock_free_multiprocess_tests.cpp` and `tests/cpp/native_fault_agent.cpp`
- [X] T092 [P] [US3] Add cross-runtime pause, abrupt reservation/lease death, exact recovery, stale token, PID reuse, namespace identity, corruption propagation, and held-cold-lock/nonblocking-hot-operation tests in `tests/SharedMemoryStore.InteropTests/RecoveryAndOwnershipTests.cs`
- [X] T093 [P] [US3] Retarget managed production histories, deterministic checkpoint catalogs, disposal races, and option cloning to profileless SMS2 in `tests/SharedMemoryStore.LinearizabilityTests/`, `tests/SharedMemoryStore.LockFreeAgent/`, and `tests/SharedMemoryStore.InteropAgent/`
- [X] T094 [P] [US3] Add Python close/recovery/view race, cancellation, fault-agent, and exact stale-owner outcome tests in `tests/python/test_lifecycle.py`, `tests/python/test_threading.py`, and `tests/python/test_interop_agent.py`

### Implementation

- [X] T095 [US3] Move exact participant owner classification to `src/SharedMemoryStore/LockFree/ParticipantOwnerClassifier.cs` and remove PID-only legacy paths from `src/SharedMemoryStore/Leasing/LeaseOwnerClassifier.cs`
- [X] T096 [US3] Implement native conservative participant-incarnation classification and exact reservation/lease/directory recovery in `src/cpp/src/recovery.hpp` and `src/cpp/src/recovery.cpp`
- [X] T097 [US3] Implement native process-local operation entry/drain, Closing publication, owned-resource cleanup, participant retirement, and ordered teardown in `src/cpp/src/lifecycle_gate.hpp` and `src/cpp/src/store.cpp`
- [X] T098 [US3] Implement native test-only deterministic checkpoints and pause/crash commands without changing packaged production wire state in `src/cpp/src/checkpoint.hpp` and `tests/cpp/native_fault_agent.cpp`
- [X] T099 [US3] Extend managed/native/Python agents with the canonical checkpoint catalog, abrupt exit, recovery, raw corruption injection, and held-cold-lock commands in `tests/SharedMemoryStore.InteropAgent/`, `tests/cpp/interop_agent.cpp`, and `tests/python/interop_agent.py`
- [X] T100 [P] [US3] Make Docker interoperability cover SMS2-only mixed-runtime lifecycle, namespace identity, owner anchors/markers, pause, crash, recovery, and cleanup in `tests/SharedMemoryStore.InteropTests/Dockerfile` and `scripts/validate-interoperability.ps1`
- [X] T101 [US3] Run deterministic, linearizability, 10,000-crash, raw-memory-order, corruption/non-poisoning, held-lock, disposal, and capacity-restoration suites and fix every forbidden outcome

**Checkpoint**: No paused or dead runtime is a store-wide progress dependency, and recovery never reclaims live or later-generation ownership.

---

## Phase 6: User Story 4 - Consume and Diagnose Each Distribution Independently (Priority: P2)

**Goal**: Expose equivalent bounded diagnostics, package each runtime independently, and publish one current compatibility/migration story.

**Independent Test**: Clean external consumers install/link/import each produced artifact, run the lifecycle sample, and report equivalent shared SMS2 facts with clearly local counters.

### Diagnostics and package tests

- [X] T102 [P] [US4] Add failing profile-free managed diagnostics shape tests and remove tombstone/compaction expectations in `tests/SharedMemoryStore.ContractTests/LockFreeDiagnosticsContractTests.cs` and `tests/SharedMemoryStore.UnitTests/DiagnosticsApiShapeTests.cs`
- [X] T103 [P] [US4] Add failing native bounded structural snapshot and local CAS/help/contention/token/recovery telemetry tests in `tests/cpp/diagnostics_v2_tests.cpp`
- [X] T104 [P] [US4] Add failing ABI-2 diagnostics, RAII ownership, installed-consumer, and exported-symbol tests in `tests/cpp/c_api_tests.cpp`, `tests/cpp/store_tests.cpp`, `tests/cpp/interop_agent_protocol_tests.cpp`, and `tests/cpp/package_consumer/main.cpp`
- [X] T105 [P] [US4] Add failing Python protocol/participant/directory/CAS/help/contention/token/recovery diagnostics tests in `tests/python/test_diagnostics.py`
- [X] T106 [P] [US4] Add clean-wheel tests for Python 1.0.0, adjacent ABI-2 loading, wrong/missing ABI rejection, cleared `PYTHONPATH`, unrelated-directory execution, and sdist rebuild in `tests/python/test_installed_package.py`
- [X] T107 [P] [US4] Add failing NuGet 3.0/profile-removal/clean-consumer assertions in `tests/SharedMemoryStore.ContractTests/LockFreePackageContractTests.cs`, `tests/SharedMemoryStore.IntegrationTests/LockFreePackageIntegrationTests.cs`, and `tests/SharedMemoryStore.ContractTests/PackageConsumptionApiTests.cs`
- [X] T108 [P] [US4] Require equivalent shared diagnostics and explicitly local counters from managed/native/Python agents in `tests/SharedMemoryStore.InteropTests/DiagnosticsInteropTests.cs`

### Diagnostics and packaging implementation

- [X] T109 [US4] Remove profile and legacy tombstone/compaction fields while preserving SMS2 directory, participant, retry, help, contention, token, and recovery metrics in `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`, `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`, `src/SharedMemoryStore/Engines/EngineMetrics.cs`, and `src/SharedMemoryStore/LockFree/LockFreeDiagnostics.cs`
- [X] T110 [US4] Implement native bounded structural diagnostics and process-local atomic telemetry without a hot mutex in `src/cpp/src/diagnostics_v2.hpp` and `src/cpp/src/diagnostics_v2.cpp`
- [X] T111 [US4] Complete ABI-2 expanded diagnostics, opaque cancellation, exception containment, and ownership-safe handle destruction in `src/cpp/include/shared_memory_store/c_api.h` and `src/cpp/src/c_api.cpp`
- [X] T112 [US4] Complete move-only C++ API diagnostics/recovery/cancellation wrappers and installed consumer behavior in `src/cpp/include/shared_memory_store/store.hpp` and `tests/cpp/package_consumer/main.cpp`
- [X] T113 [US4] Map complete ABI-2 diagnostics and local lifetime-safe counters into immutable Python values in `src/python/shared_memory_store/store.py`
- [X] T114 [US4] Set native package 1.0.0, C ABI/SOVERSION 2, qualified x64 checks, install exports, and complete source lists in `CMakeLists.txt`, `src/cpp/CMakeLists.txt`, and `cmake/SharedMemoryStoreConfig.cmake.in`
- [X] T115 [US4] Set Python package 1.0.0, include every ABI-2 build input, and preserve architecture-specific adjacent-library wheels in `pyproject.toml` and `src/python/shared_memory_store/__init__.py`
- [X] T116 [US4] Set NuGet 3.0.0 and complete XML documentation/release notes for the breaking single-protocol surface in `src/SharedMemoryStore/SharedMemoryStore.csproj`, `src/SharedMemoryStore/SharedMemoryStoreOptions.cs`, `src/SharedMemoryStore/StoreProtocolInfo.cs`, and `src/SharedMemoryStore/MemoryStore.cs`
- [X] T117 [P] [US4] Retarget managed/native/Python samples to ordinary participant-aware SMS2 creation and protocol identity in `samples/BasicUsage/`, `samples/CppBasicUsage/`, `samples/PythonBasicUsage/`, `samples/DockerSharedMemory/`, `samples/FrameValue/`, `samples/HostedServiceIntegration/`, `samples/LockFreeBrokerKeys/`, and `samples/ZeroCopyIngest/`
- [X] T118 [P] [US4] Collapse benchmarks to one SMS2 protocol and remove Legacy/both/comparator dimensions in `benchmarks/SharedMemoryStore.Benchmarks/LockFreeBenchmarks.cs`, `benchmarks/SharedMemoryStore.SyncProbe/Program.cs`, `benchmarks/SharedMemoryStore.SyncProbe/ProbeCompletionTargetPolicy.cs`, and `benchmarks/SharedMemoryStore.SyncProbe/BenchmarkResults.cs`
- [X] T119 [US4] Make native and Python validation perform clean build/test/install/consumer, wheel/sdist rebuild, ABI mismatch, package-location, and sample gates in `scripts/validate-native.ps1` and `scripts/validate-python.ps1`
- [X] T120 [US4] Publish a one-layout distribution matrix, consolidate inherited resource rules into v2, and delete current v1 protocol documents/fixtures while preserving historical Spec-Kit artifacts and source history in `protocol/README.md`, `protocol/resource-naming-v2.md`, `protocol/compatibility.json`, `protocol/layout-v1.2.md`, `protocol/resource-naming-v1.md`, and `protocol/fixtures/v1.2/`
- [X] T121 [US4] Document the one-protocol architecture, profileless usage, statuses, diagnostics, packaging, portability, versions, and drain-close-recreate-republish migration in `README.md`, `CHANGELOG.md`, `docs/getting-started.md`, `docs/usage.md`, `docs/errors.md`, `docs/diagnostics.md`, `docs/architecture.md`, `docs/packaging.md`, `docs/portability.md`, and `docs/releases.md`
- [X] T122 [US4] Replace legacy-comparison policy with absolute SMS2 native/Python/interop/Docker/package/documentation gates in `.github/workflows/ci.yml`, `scripts/validate-package-consumption.ps1`, `scripts/validate-lock-free-os.ps1`, and `scripts/run-lock-free-qualification.ps1`

**Checkpoint**: All distributions are independently consumable, diagnostic facts agree, and all current metadata advertises exactly one protocol.

---

## Phase 7: Polish, Full Validation, and Release Evidence

**Purpose**: Prove the complete feature on clean Release artifacts and remove all residual product drift.

- [X] T123 Run `dotnet test SharedMemoryStore.slnx -c Release` plus managed package-consumption and sample validation, fixing every failure
- [X] T124 [P] Run `scripts/validate-native.ps1 -Configuration Release`, fixing every native conformance, atomic, lifecycle, C ABI, install, and consumer failure
- [X] T125 [P] Run `scripts/validate-python.ps1 -Configuration Release`, fixing every Python wrapper, lifetime, wheel, sdist, import, and sample failure
- [X] T126 Run `scripts/validate-interoperability.ps1 -Configuration Release -Stress -StressValueCount 1000 -StressLifecycleCycleCount 10000`, fixing every ordered-pair and mixed-runtime failure
- [X] T127 Run Docker and independent Windows x64/Linux x64 raw atomic, cold lifecycle, owner, no-hot-lock, crash, and package validation through `scripts/validate-interoperability.ps1` and `scripts/validate-lock-free-os.ps1`
- [X] T128 [P] Run documentation, link, compatibility-manifest, static public API, binary export, and retired-path inspection through `scripts/validate-docs.ps1` and repository searches
- [X] T129 Run PR, nightly, and full release qualification with immutable artifact-bound reports and record the exact outcome in `specs/010-lock-free-only-multilang/release-qualification.md`
- [X] T130 Execute every command and migration smoke in `specs/010-lock-free-only-multilang/quickstart.md` from clean artifacts and correct any drift
- [X] T131 Run `git diff --check`, clean-build verification, package content inspection, and final task/checklist completeness validation

---

## Phase 8: Convergence

- [ ] T132 Freeze the corrected concurrent-close implementation and rerun immutable Windows x64/Linux x64 PR, nightly, release, independent-review, and final rollup evidence for the exact revision per SC-010 and T129 (partial)
- [X] T133 Harden all sync-probe worker cold opens against transient `StoreBusy` and prove the bounded policy across Windows and Linux with a real cross-process cold-gate regression
- [X] T134 Start broker-directed and large-ingest workers before pinning the in-process producer so child processes inherit the unrestricted processor mask, and prove unique affinity assignments for every applied role on Windows and Linux

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies.
- **Foundational (Phase 2)**: Depends on Setup and blocks all runtime attachment work.
- **User Story 1 (Phase 3)**: Depends on foundational protocol/atomic gates and is the MVP.
- **User Story 2 (Phase 4)**: Depends on safe open, participant registration, and engine-only C# facade from User Story 1.
- **User Story 3 (Phase 5)**: Depends on complete slot/directory/lease lifecycles from User Story 2.
- **User Story 4 (Phase 6)**: Diagnostics depend on the complete shared state machines; packaging may start after User Story 1 but cannot finish before Stories 2-3.
- **Polish (Phase 7)**: Depends on every selected user story and package implementation.

### User Story Dependencies

- **US1**: Independent MVP after Phase 2; proves one protocol and cross-runtime attachment.
- **US2**: Builds on US1 because value tokens embed active participant identity.
- **US3**: Builds on US2 because recovery and disposal operate on complete slot, directory, and lease transitions.
- **US4**: Observes and packages US1-US3; it does not alter protocol correctness.

### Within Each User Story

- Tests and canonical fixture vectors are written and observed failing before implementation.
- Atomic/layout primitives precede cold open; cold open precedes participant registration; participant registration precedes slot/lease claims.
- Directory lookup precedes insert/unlink; slot publication precedes leases; leases precede remove/reclaim; all precede recovery/disposal.
- C ABI exposes only implemented engine behavior; Python wrappers bind only finalized ABI structures for that slice.
- Files shared by multiple tasks are changed sequentially even when surrounding tests are parallel.

### Parallel Opportunities

- Phase 2 managed, native, and Python conformance tests are parallel after shared fixture shape is agreed.
- US1 managed facade work, native platform-specific tests, and Python ABI tests use different files until ABI integration.
- US2 native test families and managed/Python retargeting can run in parallel before native implementation integration.
- US3 managed history, native recovery/disposal tests, and cross-runtime scenario authoring can run in parallel.
- US4 package tests, samples, benchmarks, and documentation can run in parallel after public identities stabilize.
- Phase 7 native and Python clean-package validation can run in parallel; interoperability follows both.

## Parallel Example: User Story 1

```text
Task T020: Single-protocol managed public API tests
Task T034: Native cold-open validation tests
Task T035: Windows cold lifecycle tests
Task T036: Linux cold lifecycle tests
Task T045: C ABI 2 contract tests
Task T046: Python ABI 2 contract tests
```

## Parallel Example: User Story 2

```text
Task T058: Directory lookup tests
Task T061: Slot/reservation tests
Task T063: Lease tests
Task T077: Managed SMS2 test retargeting
Task T081: Python lifecycle tests
Task T084: Nine-pair interoperability tests
```

## Implementation Strategy

### MVP First

1. Complete Phase 1 setup.
2. Complete Phase 2 protocol/atomic foundation.
3. Complete User Story 1 through T057.
4. Validate that all three runtimes create/open one SMS2 mapping and reject retired layouts.
5. Continue immediately because the user requested the complete feature, not an MVP stop.

### Incremental Delivery

1. One protocol and safe attachment.
2. Complete byte/lifetime exchange.
3. Pause/crash/recovery/disposal correctness.
4. Diagnostics, packages, samples, and current documentation.
5. Full cross-platform Release evidence and convergence.

## Notes

- `[P]` tasks touch independent files only; shared implementation files remain sequential.
- Existing C# SMS2 transition logic is the semantic reference, not a dependency to call from native code.
- Historical Spec-Kit artifacts remain history; current product paths and active compatibility metadata become SMS2-only.
- A missing required compiler, tracing facility, Docker engine, or supported kernel is an environment prerequisite failure for release validation, not permission to mark a required task complete.
- Every completed task is marked `[X]` only after its named test or artifact condition passes.
