# Tasks: Lock-Free Shared-Memory Key-Value Store

**Input**: Design documents from `specs/009-lock-free-publish-read/`

**Prerequisites**: `plan.md`, `spec.md`, `research.md`, `data-model.md`,
`contracts/`, and `quickstart.md`

**Tests**: Required. For every behavior phase, create/run the listed failing
test before implementing the corresponding production task.

**Organization**: Tasks are grouped by user story. User Story 2 precedes User
Story 1 because the read story's independent test needs a real committed v2
value; both remain P1 and are validated independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: May run in parallel with adjacent tasks because files and dependencies
  do not overlap.
- **[US#]**: Maps to the numbered user story in `spec.md`.
- Every task names the primary file(s) it changes or validates.

## Phase 1: Setup and Baseline

**Purpose**: Preserve the known-good legacy baseline and add test-only project
shells without changing runtime behavior.

- [X] T001 Run `dotnet test SharedMemoryStore.slnx -c Release` and `dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release`; record commands, environment, pass/fail counts, and package API snapshot in `specs/009-lock-free-publish-read/baseline.md`
- [X] T002 Create a compiling test-only executable stub in `tests/SharedMemoryStore.LockFreeAgent/SharedMemoryStore.LockFreeAgent.csproj` and `tests/SharedMemoryStore.LockFreeAgent/Program.cs`, add it to `SharedMemoryStore.slnx`, and add a build/copy project reference from `tests/SharedMemoryStore.IntegrationTests/SharedMemoryStore.IntegrationTests.csproj` so project-scoped integration runs can launch it
- [X] T003 Create `tests/SharedMemoryStore.LinearizabilityTests/SharedMemoryStore.LinearizabilityTests.csproj` and add it to `SharedMemoryStore.slnx` with xUnit/project references matching existing test projects
- [X] T004 Create a reproducible tracked `benchmarks/SharedMemoryStore.SyncProbe/SharedMemoryStore.SyncProbe.csproj` and `benchmarks/SharedMemoryStore.SyncProbe/Program.cs` scaffold from the documented baseline: controller/worker subprocesses, acquire-release and publish-remove modes, 1/2/4/8 processes, three trials, bounded latency samples, JSON environment/results, p50/p95/p99/max, fairness, status failures, and no runtime package dependency; add it to `SharedMemoryStore.slnx` (the ignored local artifact may be consulted but is not an input contract)

**Checkpoint**: Existing tests/package pass and new empty tooling projects build.

---

## Phase 2: Foundational Profile, Facade, Layout, and Atomic Gate

**Purpose**: Establish compatibility-preserving dispatch and prove the platform
can support the atomic protocol before implementing user stories.

**CRITICAL**: No lock-free user-story implementation begins until the mapped
atomic litmus passes on the current supported platform and the legacy suite still
passes after facade extraction.

### Failing tests first

- [X] T005 [P] Add failing API/profile/signature/numeric-assignment tests for `StoreProfile`, `CreateLockFree`, profile-aware sizing, `ParticipantRecordCount` default 64/validation, appended `ParticipantTableFull=11`, `ProtocolInfo`, and unchanged legacy members in `tests/SharedMemoryStore.ContractTests/LockFreeProfileApiContractTests.cs`
- [X] T006 [P] Add failing facade-routing and opaque token-incarnation tests using a fake engine in `tests/SharedMemoryStore.UnitTests/MemoryStoreFacadeTests.cs`
- [X] T007 [P] Add failing layout-2.0 participant/slot/lease control encodings, lock-free `SlotCount` range `1..1,048,575`, exact 22-bit target plus 33-bit generation encodings/reserved-bit rejection for `DirectoryLocation` and `DirectoryOperation`, exact participant index/generation masks and configured terminal retirement, participant section/64-byte stride, all offsets, 8-byte atomic alignment, binding codec, sequentially consistent RMW requirement, x64 gate, and checked-overflow tests in `tests/SharedMemoryStore.ContractTests/LockFreeLayoutContractTests.cs`
- [X] T008 [P] Add failing local lifetime-gate race/allocation tests that pause entry/exit/dispose transitions in `tests/SharedMemoryStore.UnitTests/LockFreeLifecycleGateTests.cs`
- [X] T009 [P] Add failing same-name v1.2/v2 header-first incompatible-size tests, participant open/close/reuse/exhaustion behavior, and Linux v1-compatible live owner-sidecar tests in `tests/SharedMemoryStore.IntegrationTests/LockFreeProfileOpenIntegrationTests.cs`
- [X] T010 Add failing cross-process aligned mapped `Interlocked`/`Volatile` publication/CAS tests, a two-word acquire/remove Dekker litmus that forbids both participants observing the old value after SC RMW, and non-x64 rejection in `tests/SharedMemoryStore.IntegrationTests/MappedAtomicLitmusIntegrationTests.cs` and `tests/SharedMemoryStore.LockFreeAgent/Program.cs`

### Foundational implementation

- [X] T011 Implement `StoreProfile`, `ParticipantRecordCount`, additive profile/sizing helpers, appended `ParticipantTableFull`, unchanged legacy signatures/status numbers, and `StoreProtocolInfo` in `src/SharedMemoryStore/SharedMemoryStoreOptions.cs`, `src/SharedMemoryStore/StoreStatus.cs`, and `src/SharedMemoryStore/StoreProtocolInfo.cs` until T005 passes
- [X] T012 Implement engine-neutral `LeaseHandle`, `ReservationHandle`, metrics, and synchronous span-safe `IStoreEngine` abstractions in `src/SharedMemoryStore/Engines/StoreTokenHandles.cs`, `src/SharedMemoryStore/Engines/EngineMetrics.cs`, and `src/SharedMemoryStore/Engines/IStoreEngine.cs`
- [X] T013 Extract current layout-v1.2 behavior into `src/SharedMemoryStore/Engines/LegacyV12/LegacyV12StoreEngine.cs`, implement `src/SharedMemoryStore/Engines/StoreEngineFactory.cs`, and refactor `src/SharedMemoryStore/MemoryStore.cs`, `src/SharedMemoryStore/ValueLease.cs`, and `src/SharedMemoryStore/Ingest/ValueReservation.cs` into the stable facade until T006 and all existing legacy tests pass
- [X] T014 Replace monitor-based operation entry with a CAS/ref-count local gate while preserving disposal ordering in `src/SharedMemoryStore/Lifecycle/StoreLifecycleGate.cs` until T008 and existing disposal tests pass
- [X] T015 [P] Implement the lock-free `SlotCount` ceiling and layout-v2 participant/slot/lease constants/codecs, exact participant index/generation masks with retirement at the configured hot-token maximum, generation-tagged 22-bit-target directory location/operation encodings with reserved-bit validation, explicit records, checked calculator, binding codec, SC RMW wrappers, and offset assertions in `src/SharedMemoryStore/LayoutV2/LayoutV2Constants.cs`, `src/SharedMemoryStore/LayoutV2/SharedRecordsV2.cs`, `src/SharedMemoryStore/LayoutV2/StoreLayoutV2.cs`, `src/SharedMemoryStore/Options/SharedMemoryStoreOptionsValidator.cs`, `src/SharedMemoryStore/LockFree/ParticipantToken.cs`, `src/SharedMemoryStore/LockFree/IndexBinding.cs`, `src/SharedMemoryStore/LockFree/DirectoryLocation.cs`, `src/SharedMemoryStore/LockFree/DirectoryOperation.cs`, and `src/SharedMemoryStore/LockFree/AtomicControlWord.cs` until T007 passes
- [X] T016 Refactor existing-region discovery to probe actual header identity before requested-size projection, preserve Linux live owner records, retain the legacy-compatible named lock only for cold initialization, and dispatch profile discovery in `src/SharedMemoryStore/Interop/SharedStorePlatform.cs`, `src/SharedMemoryStore/Interop/WindowsSharedMemoryRegion.cs`, and `src/SharedMemoryStore/Engines/StoreEngineFactory.cs`
- [X] T017 Implement layout-v2 cold header/section initialization, participant `Free/Registering/Active/Closing/Reclaiming` allocation, empty-handle close/reuse/exhaustion, x64 validation, and open cleanup in `src/SharedMemoryStore/LockFree/LockFreeParticipantRegistry.cs` and `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs` until T009 passes
- [X] T018 Complete the raw two-process atomic litmus commands in `tests/SharedMemoryStore.LockFreeAgent/Program.cs`, run T010 in Release on the current development OS, and record architecture/runtime results in `specs/009-lock-free-publish-read/atomic-litmus-results.md`; stop local implementation if aligned mapped atomics fail, while retaining the distinct dual-platform release gate
- [X] T019 Run all legacy unit/contract/integration/interop tests and package consumption after extraction; record the green compatibility checkpoint in `specs/009-lock-free-publish-read/baseline.md`

**Checkpoint**: Legacy is unchanged, v2 opens safely, mapped atomics pass, and the
facade/layout foundation is ready.

---

## Phase 3: User Story 2 - Publish Directly Into Shared Memory (Priority: P1)

**Goal**: Reserve one exact key/slot, write directly into mapped memory, and make
complete bytes visible at one commit CAS without a global operation owner.

**Independent Test**: Concurrent simple, segmented, and direct reservations
publish exact bytes; unfinished bytes stay invisible; same-key races have one
winner; a paused publisher does not stop unrelated publication.

### Failing tests first

- [X] T020 [P] [US2] Add failing exhaustive finite-state binding/control/generation-tagged directory-operation/location tests for every insert-help pause, including pause after validation followed by descriptor completion, reclaim, slot reuse, stale target-cell installation, exact rollback, and no later-generation mutation; retain bidirectional checkpoint completeness and publish/publish reference histories in `tests/SharedMemoryStore.UnitTests/LockFreeCheckpointCoverageTests.cs`, `tests/SharedMemoryStore.UnitTests/LockFreeDirectoryStateTests.cs`, `tests/SharedMemoryStore.LinearizabilityTests/ReferenceStoreModel.cs`, `tests/SharedMemoryStore.LinearizabilityTests/LinearizabilityChecker.cs`, `tests/SharedMemoryStore.LinearizabilityTests/CheckerSelfTests.cs`, and `tests/SharedMemoryStore.LinearizabilityTests/PublicationHistoryTests.cs`
- [X] T021 [P] [US2] Add failing exact-collision tests for two-choice primary lanes, spill-summary gating, full `SlotCount` overflow admission, generation-tagged exact unlink, and stale-helper residue cleanup without later-generation damage in `tests/SharedMemoryStore.UnitTests/LockFreeDirectoryCollisionTests.cs`
- [X] T022 [P] [US2] Add failing first-claim participant-token, reservation generation, exclusive single-producer, exact-advance, commit/abort/recovery, terminal retirement, writable lifetime, and cancellation/deadline immediately before/after binding and commit tests in `tests/SharedMemoryStore.UnitTests/LockFreeReservationStateTests.cs`
- [X] T023 [P] [US2] Add failing public simple/segmented/reservation visibility, empty/oversized/zero-length boundaries, status, cancellation cleanup, and zero operation-synchronizer-call tests for the lock-free profile in `tests/SharedMemoryStore.ContractTests/LockFreePublishContractTests.cs`
- [X] T024 [US2] Add failing concurrent same-key/unrelated-key publisher, paused insertion-helper, and bounded cancellation/deadline ownership-cleanup integration tests in `tests/SharedMemoryStore.IntegrationTests/LockFreePublishIntegrationTests.cs`

### Implementation

- [X] T025 [P] [US2] Implement a generic/static no-op production checkpoint specialization, friend-only instrumented engine factory for the cross-process agent, canonical checkpoint catalog, and scheduler in `src/SharedMemoryStore/LockFree/LockFreeCheckpoint.cs`, `src/SharedMemoryStore/Properties/AssemblyInfo.cs`, and `tests/SharedMemoryStore.UnitTests/TestSupport/ControlledLockFreeScheduler.cs` until the T020 completeness test proves every checkpoint has before/after/pause/crash/race classifications
- [X] T026 [US2] Implement participant-bearing generation-fenced free-slot claim with exact participant revalidation, stale generation-tagged directory-residue cleanup, complete ordinary metadata overwrite only after exclusive `Free -> Initializing` claim, payload accounting, commit/abort ownership clearing, cancellation handoff, and retirement without delayed-helper ordinary cleanup stores in `src/SharedMemoryStore/LockFree/LockFreeSlotTable.cs` until T022 and the revised T020 pass
- [X] T027 [US2] Implement primary bucket/overflow lookup and a single helpable generation-tagged insert/unlink/abort protocol: require operation/location/binding/control generation agreement, use exact full-word CAS for every phase/location write, roll back an old target binding when its operation cannot advance, treat stale residue as helpable rather than later-generation corruption, preserve exact-key/spill/capacity invariants, and expose the common abort/unlink helper in `src/SharedMemoryStore/LockFree/LockFreeKeyDirectory.cs` until T020-T021 pass
- [X] T028 [US2] Implement sparse lazily warmed per-slot writable memory-manager pages so span-only/read-only handles do not allocate one managed object per configured slot, retain zero-allocation reuse after warm-up, and enforce reservation view lifetime in `src/SharedMemoryStore/LockFree/LockFreeReservationMemory.cs`
- [X] T029 [US2] Implement v2 `TryReserve`, simple publish, segmented publish, exclusive reservation projection/advance/commit/abort/dispose, stale-publisher generation revalidation, the shared exact abort/unlink helper (no direct descriptor zeroing), reservation-aware participant Closing cleanup/zero-reference proof, pre-ordering cancellation cleanup/helpable handoff, post-ordering normal outcomes, and facade routing without the operation synchronizer in `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs` until T023-T024 and revised T020 pass
- [X] T030 [US2] Add and pass warmed zero-allocation reservation/abort, duplicate, invalid/incomplete, and bounded successful commits up to configured capacity in `tests/SharedMemoryStore.UnitTests/LockFreePublishAllocationTests.cs`; defer the one-million complete publish/remove reuse cycle to T051

**Checkpoint**: User Story 2 is independently usable; publication has one key
owner, zero-copy commit visibility, bounded retries, and unrelated-key progress.

---

## Phase 4: User Story 1 - Read Values Concurrently by Key (Priority: P1)

**Goal**: Let many independent processes acquire the same or different keys and
project immutable mapped bytes through incarnation-fenced shared leases.

**Independent Test**: Publish stable values, then 6/12 workers and an observer
lease overlapping keys, verify exact bytes/checksums, and pause one reader
without stopping other readers.

### Failing tests first

- [X] T031 [P] [US1] Add failing participant-bearing lease first-claim/revalidation, activate/release ownership clearing, reuse/incarnation, and cancellation/deadline immediately before/after activation tests in `tests/SharedMemoryStore.UnitTests/LockFreeLeaseRegistryTests.cs`
- [X] T032 [P] [US1] Add failing lookup double-validation, commit/acquire, missing-key, hash-collision, stale-binding, bounded cleanup schedules, and minimal acquire histories in `tests/SharedMemoryStore.UnitTests/LockFreeAcquireStateTests.cs` and `tests/SharedMemoryStore.LinearizabilityTests/AcquireHistoryTests.cs`
- [X] T033 [P] [US1] Add failing public lease projection/lifetime/status/profile/cancellation and zero operation-synchronizer-call contract tests in `tests/SharedMemoryStore.ContractTests/LockFreeLeaseContractTests.cs`
- [X] T034 [US1] Add failing 1/6/12-reader same-key and distributed-key cross-process scenarios plus paused observer in `tests/SharedMemoryStore.IntegrationTests/LockFreeMultiReaderIntegrationTests.cs` and corresponding agent commands in `tests/SharedMemoryStore.LockFreeAgent/Program.cs`

### Implementation

- [X] T035 [US1] Implement participant-token-fenced global lease-record claim with exact participant revalidation, activation, stable scan, ownership-clearing exact release/recovery handoff, recycle, cancellation cleanup, and retirement in `src/SharedMemoryStore/LockFree/LockFreeLeaseRegistry.cs` until T031 passes
- [X] T036 [US1] Implement double-validated primary/marked-overflow exact-key lookup and stale-binding help in `src/SharedMemoryStore/LockFree/LockFreeKeyDirectory.cs` until T032 passes
- [X] T037 [US1] Implement v2 `TryAcquire`, post-activation directory/slot/participant revalidation, zero-copy descriptor/payload projection, release/dispose, lease-aware participant Closing cleanup/zero-reference proof, bounded cancellation cleanup, and facade routing without the operation synchronizer in `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs` until T033 passes
- [X] T038 [US1] Complete acquire/release/checksum/pause commands in `tests/SharedMemoryStore.LockFreeAgent/Program.cs` and pass T034 without using a production broker or global lock
- [X] T039 [US1] Add and pass the barrier-controlled 12-process same-key lease/checksum setup portion in `tests/SharedMemoryStore.IntegrationTests/LockFreeBroadcastLeaseIntegrationTests.cs`
- [X] T040 [US1] Add and pass a one-million-cycle warmed zero-allocation acquire/project/release and expected-miss/full gate in `tests/SharedMemoryStore.UnitTests/LockFreeAcquireAllocationTests.cs`
- [X] T041 [US1] Extend `benchmarks/SharedMemoryStore.SyncProbe/Program.cs` with legacy/v2 selection, affinity-if-available, 1/6/12 same-key and distributed-key modes, status histograms, and stable JSON, then run the short reader scaling smoke and record `specs/009-lock-free-publish-read/benchmark-results/smoke-readers.json`

**Checkpoint**: User Story 1 is independently usable by broker-directed workers
and unrelated readers; one reader never owns progress for the store or key.

---

## Phase 5: User Story 3 - Remove and Reuse Without Stalling (Priority: P1)

**Goal**: Logically remove at one CAS, preserve existing leases, reject new
leases, and reclaim exactly once after the last exact lease.

**Independent Test**: Race acquire/remove/release/reclaim/republish over colliding
and unrelated keys, pause every transition, and verify exact old lease bytes and
safe generation reuse.

### Failing tests first

- [X] T042 [P] [US3] Add failing SC-ordered acquire/logical-remove deterministic schedules and minimal histories, including activation and cancellation/deadline immediately before/after removal plus conservative `RemovePending` on post-removal scan expiry, in `tests/SharedMemoryStore.UnitTests/LockFreeRemoveStateTests.cs` and `tests/SharedMemoryStore.LinearizabilityTests/RemoveHistoryTests.cs`
- [X] T043 [P] [US3] Add failing release/reclaim, cancellation handoff, generation-tagged exact-once unlink-help, helper pause-after-validation through reclaim/reuse, stale operation/location/binding residue cleanup, stale release/remove, and republish-after-reuse tests in `tests/SharedMemoryStore.UnitTests/LockFreeReclamationTests.cs`
- [X] T044 [P] [US3] Add failing public logical-success/conservative `RemovePending` for active leases or bounded classification/cooperative-physical-reclaim/duplicate-until-reclaim/cancellation and zero operation-synchronizer-call contracts in `tests/SharedMemoryStore.ContractTests/LockFreeRemoveContractTests.cs`
- [X] T045 [US3] Extend the failing 12-process barrier scenario through remove, rejected new acquire, final release, and one reclamation in `tests/SharedMemoryStore.IntegrationTests/LockFreeBroadcastLeaseIntegrationTests.cs`
- [X] T046 [US3] Complete the failing collision-heavy multi-process remove/reuse and early/late missing/publication latency test in `tests/SharedMemoryStore.IntegrationTests/LockFreeChurnIntegrationTests.cs`, including disjoint keys sharing one canonical bucket and asserting zero `CorruptStore`, false miss, later-generation mutation, or leaked capacity

### Implementation

- [X] T047 [US3] Implement SC-ordered logical `Published -> RemoveRequested`, stable exact lease scan, `Success` only after completed no-active classification, conservative `RemovePending` for active leases or post-ordering bound expiry, and facade routing without the operation synchronizer in `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs` until T042 and T044 pass
- [X] T048 [US3] Implement cooperative exact-once reclaim through the common generation-tagged unlink/abort helper, exact operation/location clearing without unconditional stores, no ordinary metadata zeroing by delayed helpers, generation advance/retirement, stale-residue handling, and release/allocation-pressure helping in `src/SharedMemoryStore/LockFree/LockFreeReclaimer.cs` until revised T043 passes
- [X] T049 [US3] Integrate release-triggered and retrying-remove reclamation in `src/SharedMemoryStore/LockFree/LockFreeLeaseRegistry.cs` and `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs` until T045 passes
- [X] T050 [US3] Complete churn/remove/reuse agent commands and pass the bounded integration workload in `tests/SharedMemoryStore.LockFreeAgent/Program.cs` and `tests/SharedMemoryStore.IntegrationTests/LockFreeChurnIntegrationTests.cs`
- [X] T051 [US3] Add and pass one-million complete warmed publish/reserve/commit/acquire/project/release/remove/reuse zero-allocation cycles plus exact capacity-restoration gates in `tests/SharedMemoryStore.UnitTests/LockFreeRemoveReuseAllocationTests.cs`

**Checkpoint**: User Story 3 safely churns all configured capacity with existing
leases intact and no global index maintenance.

---

## Phase 6: User Story 4 - Survive Participant Pauses and Failures (Priority: P2)

**Goal**: Healthy processes progress past stopped participants; explicit exact-
incarnation recovery restores only safely abandoned slots/records.

**Independent Test**: Pause/kill agents at every checkpoint, continue healthy
operations, recover, fill to capacity, and replay stale tokens after reuse.

### Failing tests first

- [X] T052 [P] [US4] Add failing participant Registering/Active/Closing/Recovering/Reclaiming/reuse/retirement, PID-reuse/process-start identity, complete token, first-claim crash, table exhaustion, diagnostics, helping, and unsupported classification tests in `tests/SharedMemoryStore.UnitTests/LockFreeParticipantRegistryTests.cs`
- [X] T053 [P] [US4] Add failing reservation recovery versus commit/abort/helper and cancellation/deadline schedules, participant retirement, and report-count tests in `tests/SharedMemoryStore.UnitTests/LockFreeReservationRecoveryTests.cs`
- [X] T054 [P] [US4] Add failing lease recovery versus live release/reclaim/record reuse and cancellation/deadline schedules, participant retirement, and report-count tests in `tests/SharedMemoryStore.UnitTests/LockFreeLeaseRecoveryTests.cs`
- [X] T055 [US4] Add failing checkpoint pause/kill/recover/fill/stale-token scenarios by consuming every entry in the canonical checkpoint catalog in `tests/SharedMemoryStore.IntegrationTests/LockFreeCrashRecoveryIntegrationTests.cs` and `tests/SharedMemoryStore.LockFreeAgent/Program.cs`
- [X] T056 [P] [US4] Add failing local handle disposal versus every operation/token callback while a second handle progresses in `tests/SharedMemoryStore.IntegrationTests/LockFreeDisposalIntegrationTests.cs`

### Implementation

- [X] T057 [US4] Complete participant control/token creation, PID/identity-kind/start classification, conservative liveness, zero-reference retirement, and stale Registering handling in `src/SharedMemoryStore/LockFree/LockFreeParticipantRegistry.cs`, `src/SharedMemoryStore/LockFree/ParticipantIncarnation.cs`, and `src/SharedMemoryStore/Leasing/LeaseOwnerClassifier.cs` until T052 passes
- [X] T058 [US4] Implement participant-token exact-CAS reservation recovery/help/reporting and bounded cancellation handoff in `src/SharedMemoryStore/LockFree/LockFreeRecovery.cs` until T053 passes
- [X] T059 [US4] Implement participant-token exact-CAS lease claim/active recovery, record-incarnation fencing, reclaim help, reporting, and bounded cancellation handoff in `src/SharedMemoryStore/LockFree/LockFreeRecovery.cs` until T054 passes
- [X] T060 [US4] Complete and harden participant `Active -> Closing -> Reclaiming`, all-resource token cleanup, stable zero-reference proof, record reuse/retirement helping, and local dispose ordering in `src/SharedMemoryStore/LockFree/LockFreeStoreEngine.cs`, `src/SharedMemoryStore/LockFree/LockFreeParticipantRegistry.cs`, and `src/SharedMemoryStore/MemoryStore.cs` until T056 passes
- [X] T061 [US4] Complete every entry in the then-current agent checkpoint/crash catalog, run T055 in Release, and record capacity/live-owner/stale-token evidence in `specs/009-lock-free-publish-read/recovery-results.md`; later catalog additions remain owned by their convergence task

**Checkpoint**: User Story 4 proves stopped owners retain only local bounded
resources and recovery cannot reclaim live/current ownership.

---

## Phase 7: User Story 5 - Operate and Upgrade Safely (Priority: P3)

**Goal**: Expose bounded diagnostics, reject incompatible participants, preserve
legacy/package behavior, and document explicit rollout/rollback.

**Independent Test**: Exercise mixed load and diagnostics while opening legacy,
v2, old native/Python, package, upgrade, and rollback scenarios.

### Failing tests first

- [X] T062 [P] [US5] Add failing additive diagnostics/profile/participant occupancy/exhaustion/spill/retry/help/recovery snapshot contracts in `tests/SharedMemoryStore.ContractTests/LockFreeDiagnosticsContractTests.cs`
- [X] T063 [P] [US5] Add failing live diagnostics versus data-operation progress tests in `tests/SharedMemoryStore.IntegrationTests/LockFreeDiagnosticsIntegrationTests.cs`
- [X] T064 [P] [US5] Add failing compatibility-manifest and updated C++/Python v1.2-only v2 fail-closed rejection-before-payload tests in `tests/SharedMemoryStore.InteropTests/LockFreeLayoutRejectionTests.cs`
- [X] T065 [P] [US5] Add failing packed C# 1.0.2 smaller/equal/oversized/all-open-mode fail-closed mapping tests, new default-legacy/explicit-v2/header-first incompatibility, Linux live-owner preservation, participant-capacity consumption, same-name upgrade, and rollback tests in `tests/SharedMemoryStore.ContractTests/LockFreePackageContractTests.cs` and `tests/SharedMemoryStore.IntegrationTests/LockFreePackageIntegrationTests.cs`

### Implementation and documentation

- [X] T066 [US5] Implement engine-neutral/additive diagnostics and bounded v2 scans/counters without correctness dependencies in `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`, `src/SharedMemoryStore/Diagnostics/StoreDiagnostics.cs`, and `src/SharedMemoryStore/LockFree/LockFreeDiagnostics.cs` until T062-T063 pass
- [X] T067 [P] [US5] Publish and verify the revised exact layout-2.0 offsets/state/memory-order fixtures, generation-tagged directory encodings, reserved bits, and lock-free slot ceiling in `protocol/layout-v2.0.md`, `protocol/fixtures/v2.0/manifest.json`, and `tests/SharedMemoryStore.ContractTests/LockFreeLayoutContractTests.cs`
- [X] T068 [P] [US5] Document resource protocol 2 and cold-only ordinary-lock participation in `protocol/resource-naming-v2.md`
- [X] T069 [US5] Update `protocol/compatibility.json` for C# layout 1.2/2.0 and C++/Python 1.2-only support, and implement fail-closed SMS2 header rejection in `src/cpp/src/protocol.cpp`, `src/cpp/src/store.cpp`, `src/cpp/src/platform_windows.cpp`, `src/cpp/src/platform_linux.cpp`, and the C-ABI-backed `src/python/shared_memory_store/store.py` only where T064 proves a gap
- [X] T070 [US5] Update package version/release notes and XML docs for every additive/changed public symbol—profile, protocol info, participant capacity/status, diagnostics, wait/remove semantics, reservation single-writer lifetime, and protocol identity—in `src/SharedMemoryStore/SharedMemoryStore.csproj`, `src/SharedMemoryStore/SharedMemoryStoreOptions.cs`, `src/SharedMemoryStore/StoreStatus.cs`, `src/SharedMemoryStore/StoreProtocolInfo.cs`, `src/SharedMemoryStore/MemoryStore.cs`, `src/SharedMemoryStore/ValueLease.cs`, `src/SharedMemoryStore/Ingest/ValueReservation.cs`, and `src/SharedMemoryStore/Diagnostics/DiagnosticsSnapshot.cs`; add all-new-public-symbol XML-doc assertions to `tests/SharedMemoryStore.ContractTests/LockFreeProfileApiContractTests.cs` until T065 passes
- [X] T071 [P] [US5] Update `README.md` and `protocol/README.md` with KV-only scope, lock-free versus wait-free meaning, profile selection, ownership, trust boundary, performance interpretation, migration, and rollback
- [X] T072 [P] [US5] Implement the compilable broker-key sample described by `quickstart.md` in `samples/LockFreeBrokerKeys/LockFreeBrokerKeys.csproj` and `samples/LockFreeBrokerKeys/Program.cs`; add it to `SharedMemoryStore.slnx`
- [X] T073 [US5] Add a sample validation test covering one producer, configurable 6-12 workers, observer, pending remove, missing key, and explicit recovery in `tests/SharedMemoryStore.IntegrationTests/LockFreeSampleValidationTests.cs`
- [X] T074 [US5] Run v1.2-only, v2-only, incompatible mixed-version, package, sample, upgrade, and rollback tests and record the matrix in `specs/009-lock-free-publish-read/compatibility-results.md`

**Checkpoint**: User Story 5 provides observable operations and a safe explicit
deployment contract without making the store a stream or broker.

---

## Phase 8: Cross-Story Correctness, Performance, and Release Qualification

**Purpose**: Prove the combined protocol, success criteria, package, and
non-convergence gates.

- [X] T075 Extend the already self-tested per-story reference model/checker with the combined reservation/lease/removal/recovery state space and deterministic failing-history minimization in `tests/SharedMemoryStore.LinearizabilityTests/ReferenceStoreModel.cs`, `tests/SharedMemoryStore.LinearizabilityTests/LinearizabilityChecker.cs`, and `tests/SharedMemoryStore.LinearizabilityTests/CheckerSelfTests.cs`
- [X] T076 Add seeded/minimized histories for publish/publish, commit/acquire, acquire/remove, release/reclaim, recovery/live action, disposal/operation, participant/value/lease capacity, cancellation, and stale tokens in `tests/SharedMemoryStore.LinearizabilityTests/LockFreeHistoryTests.cs`; pass deterministic and randomized configured tiers including one-million configured repetitions for every SC-011 race family
- [X] T077 [P] Implement and pass counting/throwing synchronization plus held-legacy-lock tests for every v2 operation in `tests/SharedMemoryStore.IntegrationTests/LockFreeNoOperationLockIntegrationTests.cs`, and a complete `StoreWaitOptions` matrix—open, publish/simple/segmented, reserve/advance/commit/abort, acquire/projection/release, remove/reclaim, diagnostics, recovery, disposal, NoWait/finite/infinite/timeout/cancellation before/after ordering, limit+250 ms, zero owner-controlled leakage—in `tests/SharedMemoryStore.IntegrationTests/LockFreeWaitPolicyMatrixIntegrationTests.cs`
- [X] T078 [P] Implement and self-test `scripts/validate-lock-free-os.ps1` subcommands for Windows/Linux architecture gating, primitive litmus, raw visibility, held-lock/no-lock trace, checkpoint/crash, Release tests, Docker/native/Python, samples, and pack; implement Linux marked-interval `strace` plus optional Docker pause coverage in `tests/SharedMemoryStore.IntegrationTests/LockFreeOsTraceIntegrationTests.cs`, with unavailable prerequisites reported as not-qualified
- [X] T079 Implement the complete process/affinity/JSON workload matrix and broker-key pipe protocol in `benchmarks/SharedMemoryStore.SyncProbe/Program.cs`, `benchmarks/SharedMemoryStore.SyncProbe/BenchmarkProtocol.cs`, and `benchmarks/SharedMemoryStore.SyncProbe/BenchmarkResults.cs`
- [X] T080 [P] Add BenchmarkDotNet profile/allocation/collision/recovery benchmarks in `benchmarks/SharedMemoryStore.Benchmarks/LockFreeProfileBenchmarks.cs` and register them in `benchmarks/SharedMemoryStore.Benchmarks/Program.cs`
- [X] T081 Implement and pass a production-no-op, raw Release full-protocol visibility/reuse smoke—sequence/complement/key/generation/full payload, independent publisher/readers/remover, no shared logging fences, aggressive reuse—in `tests/SharedMemoryStore.IntegrationTests/LockFreeRawVisibilityIntegrationTests.cs` and `tests/SharedMemoryStore.LockFreeAgent/Program.cs`
- [X] T082 Historical R5 freeze, superseded by T118-T121: freeze the then-current one-shot short correctness/raw-visibility/performance convergence contract in `specs/009-lock-free-publish-read/benchmark-results/short-report.md`; its closure would have been derived only from `artifacts/lock-free-qualification/009-final-r5-pr/{summary.json,sync-probe.json}` on the common clean commit, with no tracked post-run edit
- [X] T083 Create reproducible `pr`, `nightly`, and `release` tier orchestration with exact commands/timeouts/seeds in `scripts/run-lock-free-qualification.ps1` and `specs/009-lock-free-publish-read/qualification-config.json`, including bounded-operation limit-plus-250-ms and zero owner-controlled slot/lease/participant leakage assertions
- [X] T084 Freeze the configured nightly/release stress, recovery, churn, and bounded-wait execution contract in `specs/009-lock-free-publish-read/release-qualification.md`; final closure is machine-derived from the fixed ignored artifacts and exact config fields, with no tracked post-run edit
- [X] T085 Historical R5 freeze, superseded by T118-T121: freeze the then-current 60-second/three-trial, 100,000,000-operation, and 100,000 direct-1.3-MB-frame execution contract; authoritative raw JSON would remain in immutable ignored `artifacts/lock-free-qualification/009-final-r5-release/{summary.json,sync-probe.json}` rather than being copied into tracked `specs/`, with closure machine-derived and no tracked post-run edit
- [X] T086 Historical R5 freeze, superseded by T118-T121: freeze distinct Windows-x64 and Linux-x64 atomic/raw/no-lock/crash/tests/interop/sample/pack gates; its pass/fail/not-qualified result would have been derived from `artifacts/lock-free-qualification/009-final-r5-release/os-validation.json` and `artifacts/lock-free-os-validation/009-final-r5-linux-x64.json` with identical clean provenance and no tracked post-run edit
- [X] T087 Re-run `quickstart.md` end to end against the packed package and correct any API/sample drift in `specs/009-lock-free-publish-read/quickstart.md` and `samples/LockFreeBrokerKeys/`
- [X] T088 Freeze the independent concurrency-review acceptance contract covering public API, semver, atomic/control encodings, participant first-claim recovery, ABA, helping, cancellation cleanup, allocations, diagnostics, compatibility, and tests; conditional closure requires `code-review.md` to have no unresolved High or Medium finding for the exact clean provenance in final JSON, with no tracked post-run edit
- [X] T089 Freeze `specs/009-lock-free-publish-read/checklists/implementation.md` and the SC-001..SC-018 machine-evidence mapping; every checked/pass statement is conditional on linked JSON for the common clean commit, and no tracked post-run edit is allowed
- [X] T090 [P] Specify and freeze the required-feature-bit versioned-empty `SpillSummary` codec, exact Present/Empty transitions, layout field, protocol manifest, and old/new pre-release v2 mutual rejection in `spec.md`, `research.md`, `data-model.md`, `contracts/`, `protocol/`, and `LockFreeLayoutContractTests.cs`
- [X] T091 Implement summary-before-cell publication, stable same-canonical cleanup before mutation release, real overflow-scan telemetry, budgeted exact key equality, and conservative fail-closed uncertainty handling in `SpillSummary.cs`, `LockFreeKeyDirectory.cs`, `LockFreeDiagnostics.cs`, and engine/header wiring
- [X] T092 Add deterministic checkpoints and controlled tests for stale setter/clearer ABA, post-CAS validation loss, abort/fallback cleanup, exact clear ordering, normal churn convergence, late missing-scan avoidance, malformed codec words, and crash-agent routing in unit/integration/agent projects
- [X] T093 Rebuild Release, pass focused/full regression and independent code review, then run the strict three-trial 4,096-slot/10,000-cycle CR-M06 workload with nonzero cleanup diagnostics, zero late-window scans, zero correctness failures, and late/early p99 at most 2x; preserve before/after artifacts and conclusions
- [X] T094 Correct delayed directory helpers so a generation `G` insert/unlink can never clear or publish generation `G+1` location/operation state; add deterministic reuse schedules plus the 10-by-10-second publish/remove generation stress artifact
- [X] T095 Make reservation cancellation dominate insert helping at Prepared, TargetSelected, BindingChanged, overflow Empty, and completed-insert return windows; classify legal Aborting/Reclaiming/future-generation observations as `InvalidReservation` while retaining fail-closed `CorruptStore` for impossible same/lower-generation states
- [X] T096 Append and route checkpoints 52-55, expand SC-017 to all new mutation phases, and add deterministic primary/overflow insert-cancellation schedules proving no false corruption, no discoverable canceled key, directory drain, and capacity reuse
- [X] T097 Correct the reference model for documented retrying remove and add a production-backed sequential history proving repeated protected `RemovePending`, release/reclaim, then `NotFound`
- [X] T098 Freeze the post-T094-T097 focused/full, PR/nightly/release, dual-OS, and review rerun contract; final closure and evidence hashes are derived from the fixed ignored artifacts' manifests on one clean commit, with no tracked post-run edit
- [X] T099 Assign immutable per-slot `PublicationIntent` and required-feature bits 0/1, and make explicit-reservation versus atomic-publication ordering, duplicate classification, recovery, compatibility rejection, and layout fixtures agree across production code, contracts, protocol documents, and tests
- [X] T100 Prove the intent-aware create-conflict resolver and strict atomic-candidate cancellation protocol under NoWait/finite/infinite budgets, including a fresh-lookup retry charge after successful help and fail-closed handling of impossible same/lower-generation states
- [X] T101 Revalidate every invalid directory binding by exact source-word/slot-generation observation before returning corruption, and preserve the ten-by-ten-second plus deterministic primary/overflow/spill-summary stale-reference correction evidence
- [X] T102 Complete the physical `StoreFull` resource proof: per-handle nonblocking double-collect scratch, exact control-word equality ordering witness, `StoreBusy` on movement/competing local proof, malformed-control fail-closed behavior, reference-checker consumption, and deterministic/linearizability tests
- [X] T103 Harden `qualification-config.json`, `scripts/run-lock-free-qualification.ps1`, and `scripts/validate-lock-free-os.ps1` so configured counts/leak claims, full-suite TRX, exact performance rows/trials, restore/tool prerequisites, dual-platform OS results, source/toolchain hashes, immutable evidence paths, and nonzero not-qualified exits are machine-checkable; validate both scripts through non-executing dry runs
- [X] T104 Historical R5 freeze, superseded by T118-T121: freeze then-current one-shot PR/nightly/release execution at `009-final-r5-pr`, `009-final-r5-nightly`, and `009-final-r5-release` without overwriting preserved diagnostics or rejected earlier final candidates; its conditional closure required matching Windows-x64/Linux-x64 schema-v3 evidence and identical clean commit/source-manifest provenance, entirely machine-derived with no tracked post-run edit
- [X] T105 Freeze `benchmark-results/short-report.md`, `release-qualification.md`, and `checklists/implementation.md` before execution; every SC-001..SC-018 and review conclusion is conditionally closed only by linked final JSON and identical clean provenance, and failing/missing evidence invalidates the freeze rather than permitting a tracked post-run edit
- [X] T106 Fence Linux recovery by exact store/participant PID-namespace identities and monotonic Enabled/Mixed mode under required-feature bit 2 (mask 7); publish Mixed before a differing/unproven opener's first Registering CAS, preserve ordinary KV access, conservatively retain partial Registering owners, validate stable Active identities, and cover layout/checkpoint/crash behavior on Windows/Linux

---

## Phase 9: Convergence

- [X] T107 Harden the bounded C# cold create/open/close transaction so only physical creation authorizes initialization, Windows coordinates before mapping, and Linux orders lifecycle reconciliation, stale deletion, mapping lock, mapping/owner-anchor publication, reverse gate release, and post-gate failed-open cleanup within the caller's original wait budget; preserve conservative anchor/sidecar/release-marker liveness and hot key-value lock freedom. Correct directory joint-tuple validation and legal cancellation/location handoffs so delayed generation-G helpers cannot expose, clear, or overwrite generation-G+1 state; cover future-generation fail-closed behavior, first-valid-location arbitration, alternate cleanup, and post-CAS withdrawal with checkpoints 66-67 and the complete 67-entry Windows/Linux crash matrix. Correct the Linux raw tiny-operation workload to use two deterministic rotating keys per worker in distinct canonical buckets with a recorded catalog digest, unbiased fixed early/late reservoirs whose independent running maximum cannot evict a stall, exact host-tuple binding, mask-valid sparse affinity, paired-success cycle coherence, and zero checksum/corruption evidence. Bind exact one/eight-process raw trials and enforce one-process intrinsic-p99 non-regression, eight-process throughput non-regression, <=3x lock-free self-amplification, <=10 us absolute p99, and every-lock-free-trial <=10 ms sampled maximum for both scenarios; require the release importer to reproduce the decision and revalidate the exact OS evidence tree and command-log bindings. Replace process-associated Linux coordination with fail-closed OFD locks in current C# and native adapters, retain stable `.lock`/`.lifecycle` inodes, dispose synchronization before region/owner cleanup on every teardown path, and prove same-PID load-context/native exclusion plus concurrent final-close/reopen and foreign exclusion. Run focused regressions, anchor/release-marker tests, full Windows/Linux Release suites, a clean Linux `-Command all` diagnostic, and independent production/qualification-harness review with no unresolved High or Medium finding per FR-044, FR-054..FR-057, LC-009, SC-001..SC-018, and US1's independent test
- [X] T108 Historical R5 terminal freeze, superseded by T118-T121: freeze the then-current one-shot PR, nightly, release, Windows-x64, and Linux-x64 execution and machine-acceptance contract for one clean commit. This task's checkbox records only that the R5 commands, immutable paths, exact manifest/file-set/log binding, completion revalidation, identical clean commit/source-manifest provenance, and no-unresolved-High-or-Medium-review requirements were frozen; it does not assert that the ignored artifacts exist or passed. R5 approval would have been evidence-only at `artifacts/lock-free-qualification/009-final-r5-pr/{summary.json,sync-probe.json}`, `artifacts/lock-free-qualification/009-final-r5-nightly/{summary.json,sync-probe.json}`, `artifacts/lock-free-qualification/009-final-r5-release/{summary.json,sync-probe.json,os-validation.json}`, `artifacts/lock-free-os-validation/009-final-r5-linux-x64.json`, and `artifacts/lock-free-os-validation/009-final-r5-linux-x64.evidence/linux-tiny-performance.json`; T118 records its rejection and T121 is the only current terminal freeze
- [X] T109 Preserve the rejected first immutable candidate, correct the PR SC-017 directory-generation count from 46 to the source-owned 50-transition minimum, make the test assert its machine-readable transition-count contract, validate every PR/nightly/release tier against that count even in validation-only mode, reject a one-below-source negative case, and require exact start, 50 unique transition-pass markers, summed repetitions, completion, and zero corruption/false-miss/wrong-generation/leak evidence. Pass focused Release execution, PR/release validation-only checks, a complete non-final PR diagnostic, and independent harness review without changing production or hot-path code.
- [X] T110 Preserve the rejected non-final PR diagnostic and harden the raw mapped-atomic litmus controller without changing its 10,000 barriers or the agents' 30-second stall bound: replace the observed 45-second throughput cliff with a 120-second parent ceiling, launch incrementally under cleanup ownership, drain output from process start, capture role/PID/exit/output and mapped state before and after a timeout stop, report process/mapping cleanup failures, and pass repeated focused Windows execution plus mapped-atomic, Integration, full-solution, Windows/Linux atomic, and complete non-final PR diagnostics.
- [X] T111 Record the unbound full-solution diagnostic observation (no named immutable artifact or hash was emitted) that missed an instrumented reservation checkpoint before exercising its helper-ordering assertions; prove the production path and current mapped-atomic diff were uninvolved, replace the shared test-only 50-millisecond pre-checkpoint budget with a 2-second finite budget and the 150-millisecond post-checkpoint expiry delay with 2.25 seconds for all affected schedules, and pass focused repetition, the complete cancellation-race class, full UnitTests, full solution, and independent harness review without changing production code.
- [X] T112 Preserve the rejected `009-r2-pre-final2-pr` diagnostic whose full suite and both churn tests passed but whose focused churn importer correctly rejected two rows against a stale one-row contract. Prove production is uninvolved; bind both churn owner/leak mappings to the exact case-sensitive SC-016 collision-workload FQN and reject their joint drift to the existing fixed-key sibling; bind the source namespace, top-level class, and one direct `[Fact]` method as a single contract; keep the sibling regression in the full suite; label the configured environment value truthfully as total cycles; and make validation-only positive/negative cases exercise real XML parsing, wrapper failure-state mutation/cleanup, and rejection of role swaps, nested types/methods, distinct, missing, alternate-existing, extra-sibling, wrong, duplicate, or non-passed evidence. Pass PR/release validation-only checks, the exact focused churn test, the full solution, a new complete non-final PR diagnostic, and independent harness review without changing production or workload code.
- [X] T113 Preserve the rejected `009-r2-pre-final3-pr` diagnostic whose full suite, performance, capacity, and 52 other steady-state checkpoints (104/108 checkpoint-workload rows) passed before checkpoints 62-63 exposed two deterministic late-suspension contract gaps. Require `InvalidReservation` only for the checkpoint-62 pause protocol that deliberately begins reservation abort, preserve the primary reserve outcome across cleanup, and reject every other resumed outcome for that case. At checkpoint 63, keep the public finite remove probe and move the ownership budget check after the instrumentable post-lease-scan window so an expired participant remains universally helpable in `RemoveRequested` instead of claiming `Reclaiming` with a stale deadline. Add a deterministic two-second finite-deadline/2.25-second pause regression proving `RemovePending`, zero `Reclaiming`, cooperative same-key republish, and full capacity; pass focused checkpoint-62/63 suspension across distributed-key and mixed-churn workloads at both one-second and release 30-second pauses, the full solution, a new complete non-final PR diagnostic, and independent source/harness review.
- [X] T114 Freeze the second immutable candidate at non-overwriting `009-final-r2-*` paths after T109-T113 pass. Preserve the original `009-final-linux-x64` pass and every rejected final/non-final diagnostic as evidence only. This checkbox froze the then-current Linux x64, Windows PR, nightly, and release commands and all SC-001..SC-018, exact-tree, log-binding, completion-revalidation, dual-platform, and no-unresolved-High-or-Medium-review predicates; it did not assert that ignored final artifacts existed. The R2 Linux invocation was later rejected and superseded by T115 without overwriting it.
- [X] T115 Preserve the rejected `009-final-r2-linux-x64` attempt whose report correctly identified a Windows host: required native/Python rows were not qualified and Linux-only tiny/`strace`/SIGSTOP rows were optional and inapplicable after the Linux command was mistakenly launched by Windows PowerShell, so the report could not satisfy the intended Linux contract. Prove the store and completed rows were uninvolved; validate an explicit Ubuntu login-shell invocation as Linux x64 with all structural self-tests passed; freeze new non-overwriting `009-final-r3-*` paths and require that invocation before the unchanged full Linux, PR, nightly, release, SC-001..SC-018, common-provenance, and no-post-run-edit contract can qualify.
- [X] T116 Preserve the rejected `009-final-r3-linux-x64` attempt that reached Linux x64 but stopped before workloads because Ubuntu's package-managed SDK 10.0.109 consumed stale user-local workload-set 10.0.102 metadata and `dotnet --info` failed. Repair the empty-workload manifest set with `dotnet workload update` to 10.0.109.1, prove `dotnet --info`, workload listing, Docker/tool resolution, clean restore/build, and 45/45 architecture tests in an executable Linux diagnostic, add a non-artifact-consuming Linux prerequisite command before the final script, and freeze new non-overwriting `009-final-r4-*` paths under the unchanged full qualification contract.
- [X] T117 Preserve the R4 Linux pass and rejected R4 PR result without overwriting either artifact. Classify the sole PR failure as a disposal-test contract violation: the concurrent theory selected current-process lease recovery despite the override's process-wide quiescence precondition, while its reservation sibling carried the same latent defect. Use the normal concurrent-safe `false` mode for both recovery operations, stop dereferencing borrowed projection bytes because racing disposal can invalidate their lifetime, and retain exact content assertions on the unaffected live handle and dedicated data-path suites. Record the unbound local observation of 50 repeated 15-row disposal theories separately from durable evidence; pass 29/29 disposal-class tests, Integration 302/302, the full solution 1,014/1,014, independent H0/M0/L0 diff review, and the clean provenance-bound `009-r5-pre-final-pr` diagnostic with all 24 gates passed. Freeze new non-overwriting `009-final-r5-*` paths under the unchanged SC-001..SC-018, common-provenance, immutable-evidence, and no-post-run-edit contract; the R4 artifacts remain diagnostic only.
- [X] T118 Preserve the successful R5 Linux, PR, and nightly artifacts and the incomplete non-convergent R5 release directory without overwriting any of them. Diagnose the first Legacy mixed-churn trial through live process/CPU and read-only mapped-sequence evidence as progressing through the known named-semaphore baseline rather than deadlocked, and prove that three Legacy 100,000,000-operation trials plus Legacy 100,000-frame ingest and the remaining matrix cannot fit the six-hour whole-probe deadline. Reject R5 as a final sequence without changing product code or weakening SC-001/SC-009.
- [X] T119 Make benchmark completion policy explicit and convergent: config schema 5 selects only `LockFree` as count-bound; Legacy mixed-churn and large-ingest remain three 10-second-warm-up/60-second comparison trials; every LockFree mixed trial retains 100,000,000 operations and every LockFree large-ingest trial retains 100,000 frames. Emit schema 8 `CountBoundProfiles`, `OperationTarget`, and `FrameTarget`, 30-second progress heartbeats, and a controller-enforced warm-up-plus-duration-plus-exact-60-second-grace deadline armed before store setup. Use an atomic monotonic-deadline latch so delayed timer dispatch cannot accept overdue cleanup; on timeout, give tracked child-tree termination a bounded 100 ms budget and then unconditionally fail-fast the isolated probe process rather than unwinding an in-flight infinite store operation. Preserve standalone `--count-bound-profiles` default behavior as `both`.
- [X] T120 Fail closed on completion evidence in both importers: reject missing, dual, inherited, swapped, one-below, or unmet targets; independently require positive operations, usable early/late samples, and the configured measured duration for every ordinary duration row; update Linux raw fixtures to schema 8. Extract pure target resolution and pass 9/9 direct policy cases plus 5/5 direct watchdog cases, including deliberately delayed timer dispatch, a completing thread crossing the deadline, and a real timer forcing `Completing -> TimedOut` while that thread is blocked, and the release-runner and OS validation-only positive/negative suites, including short-duration and tampered-policy cases.
- [X] T121 Freeze the terminal R6 one-shot paths `009-final-r6-linux-x64`, `009-final-r6-pr`, `009-final-r6-nightly`, and `009-final-r6-release` on one clean commit. Conditional approval remains entirely machine-derived from the exact schema-8 target/timing evidence, schema-4 summaries, schema-3 OS reports, immutable manifests/log bindings, identical source provenance, all SC-001..SC-018 gates, and independent H0/M0 review; no tracked post-run edit or checkbox change may promote a result.

**Final Checkpoint**: All correctness/package gates pass, required benchmark
evidence is recorded for qualified environments, review has no unresolved High
or Medium finding, and no repeated non-convergence condition remains.

---

## Dependencies and Execution Order

### Phase dependencies

- Phase 1 has no dependencies.
- Phase 2 depends on Phase 1 and blocks every user story.
- User Story 2 depends on Phase 2 and provides committed v2 values.
- User Story 1 depends on the User Story 2 publication checkpoint for its public
  independent test.
- User Story 3 depends on User Stories 1 and 2 because safe removal requires
  published values and leases.
- User Story 4 depends on the minimal publication/lease/removal lifecycle from
  User Stories 1-3.
- User Story 5 depends on all engine behavior whose diagnostics/contracts it
  exposes.
- Phase 8 depends on all five story checkpoints.
- Phase 9 depends on Phase 8 and closes only after the convergence regressions,
  clean diagnostic qualification, and independent review pass.

### Within each phase

1. Add and run the listed tests first; verify they fail for the intended missing
   behavior, not because the test cannot compile for an unrelated reason.
2. Implement the smallest protocol slice that satisfies the tests.
3. Run the story's existing regression set before advancing.
4. Never leave a claimed descriptor/slot/lease merely to return cancellation or
   `StoreBusy`.

### Parallel opportunities

- `[P]` contract/unit test files in a phase may be authored together.
- Layout codec work T015 can proceed beside facade/lifecycle work after their
  failing tests exist.
- Story documentation/fixtures with `[P]` may proceed after the relevant public
  contract is stable.
- Raw OS tracing T078 and BenchmarkDotNet T080 may proceed beside the main
  multi-process harness after all stories pass.
- Tasks that touch `MemoryStore.cs`, `LockFreeStoreEngine.cs`, or shared protocol
  state are deliberately sequential.

## Requirement Coverage

| Requirement group | Primary tasks |
|---|---|
| KV-only scope, exact keys, duplicate publication (FR-001..FR-007) | T020-T029, T071-T073 |
| Direct reservation/commit/lifetime (FR-008..FR-013) | T022-T030 |
| Shared zero-copy reads/leases (FR-014..FR-019) | T031-T040 |
| Logical remove/reclaim/reuse (FR-020..FR-024) | T042-T051 |
| Lock-free/no global owner/bounded retry (FR-025..FR-031) | T010, T020, T024-T029, T077-T084 |
| Failure scope/recovery/incarnations/no worker (FR-032..FR-036, FR-046) | T052-T061, T071-T073, T117 |
| Generation-tagged stale-helper fencing, spill-summary ABA prevention, cancellation, publication intent, exact-reference revalidation, PID-namespace recovery fencing, and v2 slot ceiling (FR-047..FR-053, LC-015..LC-016, SC-017..SC-018) | T007, T015, T020-T021, T026-T029, T043, T046, T048, T067, T082-T107 |
| Linux owner-anchor liveness, bounded cold-path convergence, and conservative orphan repair (FR-054) | T107-T108 |
| Persistent mapped-corruption latch and fail-closed propagation (FR-055) | T107-T108 |
| Physical-creator-only cold-open transaction and existing-unpublished fail-closed behavior (FR-056, LC-009) | T107-T108 |
| Linux OFD same-PID exclusion, stable lock inode, and synchronization-before-region teardown (FR-057) | T107-T108 |
| Allocation/diagnostics/disposal (FR-037..FR-041) | T030, T040, T051, T056, T062-T066, T117 |
| Public compatibility/docs/sample (FR-042..FR-045, LC-001..LC-016) | T005-T019, T062-T074, T086-T087, T090, T106 |
| Performance and success criteria SC-001..SC-018 | T039-T041, T045-T046, T055, T073, T075-T121 |

## Implementation Strategy

The minimal proving slice is Phase 2 plus one-key User Stories 2, 1, and 3:
reserve, commit, acquire, remove, release, reclaim, reuse. Do not broaden to full
API/diagnostics/benchmarks until this slice passes every controlled schedule and
the mapped atomic gate. Then add collision overflow, multi-process scaling,
recovery, compatibility, and release evidence incrementally.

If a convergence gate repeats after two evidence-driven corrections, stop and
raise the exact invariant, minimal failing schedule, affected requirement, and
design choices needed from the user. Do not mark remaining tasks complete or
weaken the spec.
