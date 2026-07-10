---

description: "Dependency-ordered implementation tasks for native and Python interoperability"
---

# Tasks: Native and Python Implementations

**Input**: Design documents from `specs/008-cpp-python-implementations/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: The specification explicitly requires automated conformance,
interoperability, lifecycle, packaging, and regression tests. Test tasks precede
their corresponding implementation tasks.

**Organization**: Tasks are grouped by user story so each slice has an explicit
independent validation checkpoint.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel with adjacent tasks because it owns different files
- **[Story]**: Maps the task to a prioritized user story in spec.md
- Every task names its primary file or directory

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Establish build, package, source, test, and protocol roots without
moving the existing C# implementation.

- [x] T001 Create the root native build and install skeleton in CMakeLists.txt and cmake/SharedMemoryStoreConfig.cmake.in
- [x] T002 [P] Create the C++ public/internal source skeleton in src/cpp/include/shared_memory_store/ and src/cpp/src/
- [x] T003 [P] Create the Python source-package skeleton in pyproject.toml and src/python/shared_memory_store/
- [x] T004 [P] Create native, Python, and interoperability test roots in tests/cpp/, tests/python/, tests/SharedMemoryStore.InteropAgent/, and tests/SharedMemoryStore.InteropTests/
- [x] T005 [P] Create protocol and sample roots in protocol/, samples/CppBasicUsage/, and samples/PythonBasicUsage/

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Make layout v1.2, status values, resource names, and ownership rules
precise before implementing any runtime behavior.

**Critical**: No user-story implementation begins until this phase passes its
static conformance checks.

- [x] T006 Correct the stale layout-minor statement and publish canonical version boundaries in specs/003-zero-copy-ingest/contracts/ingest-layout.md and protocol/README.md
- [x] T007 [P] Document exact mapped records, lifecycle states, and layout calculations in protocol/layout-v1.2.md
- [x] T008 [P] Document Windows/Linux naming, locking, permissions, owner sidecars, and cleanup in protocol/resource-naming-v1.md
- [x] T009 Create canonical layout, hash, status, and resource-name vectors in protocol/fixtures/v1.2/manifest.json
- [x] T010 Define fixed-width enums, versioned structs, opaque handles, export macros, and required symbols in src/cpp/include/shared_memory_store/c_api.h
- [x] T011 Implement packed records, static ABI assertions, checked layout arithmetic, FNV-1a hashing, UTF conversion, and SHA-256 helpers in src/cpp/src/layout.hpp and src/cpp/src/protocol.cpp
- [x] T012 Add failing-then-passing static conformance tests for every manifest vector in tests/cpp/protocol_tests.cpp and tests/python/test_protocol_manifest.py

**Checkpoint**: The native records compile to exact sizes and all offline
language-neutral vectors agree with the managed baseline.

---

## Phase 3: User Story 1 - Exchange Values Across Runtimes (Priority: P1) — MVP

**Goal**: C#, C++, and Python can create/open the same store and exchange,
acquire, release, remove, and replace exact bytes.

**Independent Test**: Run every ordered producer-consumer pairing for create,
publish, acquire/read/release, remove, and republish with arbitrary binary bytes.

### Tests for User Story 1

- [x] T013 [P] [US1] Add native create/open and publish/acquire/remove/reuse contract tests in tests/cpp/store_tests.cpp
- [x] T014 [P] [US1] Add C ABI null, bounds, ownership, and status contract tests in tests/cpp/c_api_tests.cpp
- [x] T015 [P] [US1] Add Python basic API and context-manager tests in tests/python/test_store.py
- [x] T016 [P] [US1] Add JSON-lines agent protocol tests in tests/SharedMemoryStore.InteropTests/AgentProtocolTests.cs
- [x] T017 [US1] Add the ordered 3x3 core exchange matrix tests in tests/SharedMemoryStore.InteropTests/CoreExchangeMatrixTests.cs

### Implementation for User Story 1

- [x] T018 [P] [US1] Implement Windows named mapping, mutex, UTF-8 naming, error mapping, and process-local gating in src/cpp/src/platform_windows.cpp
- [x] T019 [P] [US1] Implement Linux mapped files, fcntl byte locks, per-path local mutexes, permissions, owner sidecars, and cleanup in src/cpp/src/platform_linux.cpp
- [x] T020 [US1] Implement option validation, mapping initialization/validation, key index, slots, leases, publish, acquire, release, remove, and reuse in src/cpp/src/store.cpp
- [x] T021 [US1] Implement store/lease opaque-handle operations and exception containment in src/cpp/src/c_api.cpp
- [x] T022 [P] [US1] Implement move-only C++ store and lease wrappers in src/cpp/include/shared_memory_store/store.hpp
- [x] T023 [US1] Implement ctypes declarations, enums, loader, store, and lease wrappers in src/python/shared_memory_store/
- [x] T024 [US1] Implement equivalent C#, C++, and Python JSON-lines participants in tests/SharedMemoryStore.InteropAgent/, tests/cpp/interop_agent.cpp, and tests/python/interop_agent.py

**Checkpoint**: User Story 1 works independently on Windows and Linux; exact
bytes pass through all nine ordered runtime pairings.

---

## Phase 4: User Story 2 - Complete Store Lifecycle (Priority: P1)

**Goal**: All runtimes support bounded waits, segmented publication, direct
reservations, pending removal, crash recovery, and safe token invalidation.

**Independent Test**: Run mixed-runtime pending-reservation visibility,
foreign-lease removal, final release/reuse, bounded contention, crash recovery,
and three-owner cleanup scenarios.

### Tests for User Story 2

- [x] T025 [P] [US2] Add native reservation, segmented publish, bounded-wait, recovery, and lifecycle tests in tests/cpp/lifecycle_tests.cpp
- [x] T026 [P] [US2] Add Python reservation memoryview, segmented publish, timeout, recovery, and invalidation tests in tests/python/test_lifecycle.py
- [x] T027 [P] [US2] Add mixed-runtime lease/remove/reuse and reservation/commit/abort tests in tests/SharedMemoryStore.InteropTests/MixedLifecycleTests.cs
- [x] T028 [P] [US2] Add mixed-runtime lock contention, crash recovery, and Linux owner-cleanup tests in tests/SharedMemoryStore.InteropTests/RecoveryAndOwnershipTests.cs

### Implementation for User Story 2

- [x] T029 [US2] Implement bounded/infinite waits, segmented publish, reservations, progress, commit, abort, and token lifetime control in src/cpp/src/store.cpp
- [x] T030 [US2] Implement PID-based lease/reservation recovery and reports compatible with layout v1.2 in src/cpp/src/store.cpp
- [x] T031 [US2] Extend the C ABI with segments, reservation, wait, recovery, and borrowed-buffer functions in src/cpp/include/shared_memory_store/c_api.h and src/cpp/src/c_api.cpp
- [x] T032 [P] [US2] Extend the C++ RAII API with reservation, wait, segmented publish, and recovery types in src/cpp/include/shared_memory_store/store.hpp
- [x] T033 [US2] Extend Python with owned memoryviews, reservations, waits, segments, and recovery in src/python/shared_memory_store/store.py and src/python/shared_memory_store/_native.py
- [x] T034 [US2] Extend all three interop agents and orchestration helpers with lifecycle, contention, recovery, close, and crash commands in tests/cpp/interop_agent.cpp, tests/python/interop_agent.py, tests/SharedMemoryStore.InteropAgent/Program.cs, and tests/SharedMemoryStore.InteropTests/TestSupport/

**Checkpoint**: Mixed-runtime lifecycle and crash behavior matches the existing
public contract without partial visibility, stale token acceptance, or live
resource deletion.

---

## Phase 5: User Story 3 - Independent Distribution Consumption (Priority: P2)

**Goal**: Native and Python users can build/install one distribution cleanly and
see an explicit compatibility declaration.

**Independent Test**: Install the CMake package into an external native sample
and install a built wheel into a clean virtual environment; run both without
repository-source imports.

### Tests for User Story 3

- [x] T035 [P] [US3] Add a clean external find_package consumer test in tests/cpp/package_consumer/
- [x] T036 [P] [US3] Add installed-wheel loading and package-content tests in tests/python/test_installed_package.py

### Implementation for User Story 3

- [x] T037 [US3] Complete CMake targets, visibility, install/export, package config, CTest, and sample integration in CMakeLists.txt and cmake/SharedMemoryStoreConfig.cmake.in
- [x] T038 [US3] Configure scikit-build-core to bundle the correct native shared library and platform wheel tag in pyproject.toml
- [x] T039 [P] [US3] Implement minimal native and Python consumer samples in samples/CppBasicUsage/main.cpp and samples/PythonBasicUsage/main.py
- [x] T040 [P] [US3] Publish package/ABI/layout/resource compatibility metadata in protocol/compatibility.json
- [x] T041 [US3] Add reproducible native, wheel, and clean-consumer wrappers in scripts/validate-native.ps1 and scripts/validate-python.ps1

**Checkpoint**: CMake install consumption and Python wheel consumption pass from
clean locations with no undeclared runtime dependency.

---

## Phase 6: User Story 4 - Consistent Diagnostics (Priority: P3)

**Goal**: Native and Python callers inspect equivalent shared capacity and
lifecycle facts plus caller-owned local failure accounting.

**Independent Test**: Create known mixed-runtime store states and compare shared
diagnostic fields from every participant while verifying no library console I/O.

### Tests for User Story 4

- [x] T042 [P] [US4] Add native and C ABI diagnostics contract tests in tests/cpp/diagnostics_tests.cpp
- [x] T043 [P] [US4] Add Python and mixed-runtime diagnostics comparison tests in tests/python/test_diagnostics.py and tests/SharedMemoryStore.InteropTests/DiagnosticsInteropTests.cs

### Implementation for User Story 4

- [x] T044 [US4] Implement shared counts, index health, compaction, recovery counters, and local failure accounting in src/cpp/src/diagnostics.cpp and src/cpp/src/store.cpp
- [x] T045 [P] [US4] Expose versioned diagnostics value types through the C and C++ APIs in src/cpp/include/shared_memory_store/c_api.h and src/cpp/include/shared_memory_store/store.hpp
- [x] T046 [US4] Expose immutable Python diagnostics snapshots and agent responses in src/python/shared_memory_store/store.py and tests/python/interop_agent.py

**Checkpoint**: Shared facts agree across runtimes and all diagnostics remain
caller-controlled.

---

## Phase 7: Polish & Cross-Cutting Validation

**Purpose**: Finish protocol fixtures, documentation, CI, stress evidence, and
all existing release gates.

- [x] T047 Generate representative offline v1.2 binary fixtures and normalized snapshots in protocol/fixtures/v1.2/ and add their hashes to protocol/fixtures/v1.2/manifest.json
- [x] T048 Add the host/Docker all-language orchestration and stress entry point in scripts/validate-interoperability.ps1 and tests/SharedMemoryStore.InteropTests/StressInteropTests.cs
- [x] T049 [P] Add Windows and Linux native/Python/interoperability CI jobs in .github/workflows/ci.yml
- [x] T050 [P] Update README.md, docs/architecture.md, docs/portability.md, docs/getting-started.md, docs/packaging.md, docs/samples.md, docs/maintainers.md, and docs/releases.md for delivered native/Python support
- [x] T051 [P] Update CHANGELOG.md, src/SharedMemoryStore/SharedMemoryStore.csproj release notes, SECURITY.md, and SUPPORT.md with compatibility and trusted-boundary impact
- [x] T052 Run protocol, native, Python, wheel, CMake-consumer, and full cross-runtime validation from specs/008-cpp-python-implementations/quickstart.md and fix every failure
- [x] T053 Run pwsh ./scripts/validate-docs.ps1, dotnet build/test/pack, package consumption, samples, cross-platform, and Docker regression gates and fix every failure
- [x] T054 Audit git diff/status for generated artifacts, accidental binaries, public API drift, missing licenses, and user-owned changes without committing or pushing

---

## Dependencies & Execution Order

### Phase Dependencies

- Setup has no dependencies.
- Foundational depends on Setup and blocks every user story.
- User Story 1 depends on Foundational and creates the usable cross-runtime MVP.
- User Story 2 depends on the store/lease core from User Story 1.
- User Story 3 depends on the native and Python public surfaces from User Stories
  1 and 2 but is independently verifiable through clean consumers.
- User Story 4 depends on shared core state from User Stories 1 and 2 and can be
  implemented in parallel with packaging after those phases.
- Polish depends on all desired story phases.

### User Story Dependency Graph

```text
Setup -> Foundation -> US1 -> US2 ->+-> US3 -+
                                    +-> US4 -+-> Polish
```

### Parallel Opportunities

- Setup skeletons T002-T005 own separate directories.
- Foundational protocol narratives T007-T008 can proceed in parallel.
- US1 platform adapters T018-T019 and public-wrapper tests T013-T016 are
  independent before store-core integration.
- US2 native, Python, mixed-lifecycle, and recovery tests T025-T028 are separate.
- After US2, packaging US3 and diagnostics US4 can proceed concurrently.
- Documentation, CI, and release metadata T049-T051 own separate files.

## Parallel Examples

### User Story 1

```text
Task T018: Implement Windows platform adapter.
Task T019: Implement Linux platform adapter.
Task T015: Define Python public behavior tests.
Task T016: Define language-neutral agent protocol tests.
```

### User Story 2

```text
Task T025: Native lifecycle tests.
Task T026: Python lifecycle tests.
Task T027: Mixed lease/reservation tests.
Task T028: Contention/recovery/owner tests.
```

### User Stories 3 and 4

```text
Task group T035-T041: Distribution packaging and consumption.
Task group T042-T046: Diagnostics contracts and implementation.
```

## Implementation Strategy

### MVP First

1. Complete Setup and Foundational phases.
2. Complete User Story 1.
3. Validate all nine basic ordered runtime pairings on Windows and Linux.
4. Do not claim feature completion until lifecycle, packaging, diagnostics, and
   regression phases also pass.

### Incremental Delivery

1. Canonical protocol and ABI.
2. Core cross-runtime byte exchange.
3. Complete lifecycle and recovery.
4. Clean ecosystem consumption.
5. Diagnostics and stress evidence.
6. Documentation and existing release gates.

## Notes

- Tests are written before the corresponding implementation and must fail for
  the expected missing behavior before they pass.
- The coarse shared lock is preserved until conformance is proven; optimization
  must not change visibility or lifecycle semantics.
- Layout v1.2 recovery remains PID-based; adding process-start identity is a
  future versioned layout feature.
- Do not commit or push any change for this user request.
