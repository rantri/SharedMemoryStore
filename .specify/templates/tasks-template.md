---

description: "Task list template for feature implementation"
---

# Tasks: [FEATURE NAME]

**Input**: Design documents from `/specs/[###-feature-name]/`

**Prerequisites**: plan.md (required), spec.md (required for user stories), research.md, data-model.md, contracts/

**Tests**: Tests are REQUIRED for behavior-changing library work. Include unit,
contract, integration, and relevant concurrency/resource tests before
implementation tasks.

**Organization**: Tasks are grouped by user story to enable independent implementation and testing of each story.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: Which user story this task belongs to (e.g., US1, US2, US3)
- Include exact file paths in descriptions

## Path Conventions

- **Library source**: `src/SharedMemoryStore/`
- **Unit tests**: `tests/SharedMemoryStore.UnitTests/`
- **Contract tests**: `tests/SharedMemoryStore.ContractTests/`
- **Integration tests**: `tests/SharedMemoryStore.IntegrationTests/`
- **Consumer docs/examples**: `docs/`
- **Future language bindings**: `bindings/cpp/`, `bindings/python/` only when planned

<!--
  ============================================================================
  IMPORTANT: The tasks below are SAMPLE TASKS for illustration purposes only.

  The /speckit-tasks command MUST replace these with actual tasks based on:
  - User stories from spec.md (with their priorities P1, P2, P3...)
  - Feature requirements from plan.md
  - Entities from data-model.md
  - Library contracts from contracts/

  Tasks MUST be organized by user story so each story can be:
  - Implemented independently
  - Tested independently
  - Delivered as an MVP increment

  DO NOT keep these sample tasks in the generated tasks.md file.
  ============================================================================
-->

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Project initialization and basic structure

- [ ] T001 Create library project structure per implementation plan
- [ ] T002 Initialize `src/SharedMemoryStore/SharedMemoryStore.csproj` targeting .NET 10
- [ ] T003 [P] Configure formatting, analyzers, nullable reference types, and XML documentation generation
- [ ] T004 [P] Create unit, contract, and integration test projects under `tests/`
- [ ] T005 Configure NuGet package metadata and deterministic release build settings

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Core infrastructure that MUST be complete before ANY user story can be implemented

**⚠️ CRITICAL**: No user story work can begin until this phase is complete

Examples of foundational tasks (adjust based on your project):

- [ ] T006 Define public API namespaces, contracts, and lifecycle rules
- [ ] T007 [P] Configure shared test fixtures and package consumption test harness
- [ ] T008 [P] Establish error, diagnostics, and resource cleanup patterns
- [ ] T009 Document semantic version impact and compatibility expectations
- [ ] T010 Identify portability constraints for future C++ and Python implementations

**Checkpoint**: Foundation ready - user story implementation can now begin in parallel

---

## Phase 3: User Story 1 - [Title] (Priority: P1) 🎯 MVP

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 1

> **NOTE: Write these tests FIRST, ensure they FAIL before implementation**

- [ ] T011 [P] [US1] Unit test for [core behavior] in `tests/SharedMemoryStore.UnitTests/[Feature]Tests.cs`
- [ ] T012 [P] [US1] Contract test for [public API/package behavior] in `tests/SharedMemoryStore.ContractTests/[Feature]ContractTests.cs`
- [ ] T013 [P] [US1] Integration test for [library scenario] in `tests/SharedMemoryStore.IntegrationTests/[Feature]IntegrationTests.cs`

### Implementation for User Story 1

- [ ] T014 [P] [US1] Create [public type/value object] in `src/SharedMemoryStore/[Path]/[Type].cs`
- [ ] T015 [US1] Implement [library behavior] in `src/SharedMemoryStore/[Path]/[Service].cs`
- [ ] T016 [US1] Add validation, deterministic error behavior, and cleanup handling
- [ ] T017 [US1] Add consumer-controlled diagnostics for user story 1 operations
- [ ] T018 [US1] Add XML documentation and usage example for new public APIs

**Checkpoint**: At this point, User Story 1 should be fully functional and testable independently

---

## Phase 4: User Story 2 - [Title] (Priority: P2)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 2

- [ ] T019 [P] [US2] Unit test for [core behavior] in `tests/SharedMemoryStore.UnitTests/[Feature]Tests.cs`
- [ ] T020 [P] [US2] Contract test for [public API/package behavior] in `tests/SharedMemoryStore.ContractTests/[Feature]ContractTests.cs`
- [ ] T021 [P] [US2] Integration test for [library scenario] in `tests/SharedMemoryStore.IntegrationTests/[Feature]IntegrationTests.cs`

### Implementation for User Story 2

- [ ] T022 [P] [US2] Create [public type/value object] in `src/SharedMemoryStore/[Path]/[Type].cs`
- [ ] T023 [US2] Implement [library behavior] in `src/SharedMemoryStore/[Path]/[Service].cs`
- [ ] T024 [US2] Integrate with User Story 1 components without breaking existing contracts
- [ ] T025 [US2] Update XML documentation and examples for changed public APIs

**Checkpoint**: At this point, User Stories 1 AND 2 should both work independently

---

## Phase 5: User Story 3 - [Title] (Priority: P3)

**Goal**: [Brief description of what this story delivers]

**Independent Test**: [How to verify this story works on its own]

### Tests for User Story 3

- [ ] T026 [P] [US3] Unit test for [core behavior] in `tests/SharedMemoryStore.UnitTests/[Feature]Tests.cs`
- [ ] T027 [P] [US3] Contract test for [public API/package behavior] in `tests/SharedMemoryStore.ContractTests/[Feature]ContractTests.cs`
- [ ] T028 [P] [US3] Integration test for [library scenario] in `tests/SharedMemoryStore.IntegrationTests/[Feature]IntegrationTests.cs`

### Implementation for User Story 3

- [ ] T029 [P] [US3] Create [public type/value object] in `src/SharedMemoryStore/[Path]/[Type].cs`
- [ ] T030 [US3] Implement [library behavior] in `src/SharedMemoryStore/[Path]/[Service].cs`
- [ ] T031 [US3] Update XML documentation and examples for changed public APIs

**Checkpoint**: All user stories should now be independently functional

---

[Add more user story phases as needed, following the same pattern]

---

## Phase N: Polish & Cross-Cutting Concerns

**Purpose**: Improvements that affect multiple user stories

- [ ] TXXX [P] Documentation updates in docs/
- [ ] TXXX Code cleanup and refactoring
- [ ] TXXX Performance optimization across all stories
- [ ] TXXX [P] Additional unit, contract, integration, concurrency, or resource tests
- [ ] TXXX Security hardening
- [ ] TXXX Validate NuGet package creation with `dotnet pack`
- [ ] TXXX Validate clean-project package consumption
- [ ] TXXX Run quickstart.md validation

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies - can start immediately
- **Foundational (Phase 2)**: Depends on Setup completion - BLOCKS all user stories
- **User Stories (Phase 3+)**: All depend on Foundational phase completion
  - User stories can then proceed in parallel (if staffed)
  - Or sequentially in priority order (P1 → P2 → P3)
- **Polish (Final Phase)**: Depends on all desired user stories being complete

### User Story Dependencies

- **User Story 1 (P1)**: Can start after Foundational (Phase 2) - No dependencies on other stories
- **User Story 2 (P2)**: Can start after Foundational (Phase 2) - May integrate with US1 but should be independently testable
- **User Story 3 (P3)**: Can start after Foundational (Phase 2) - May integrate with US1/US2 but should be independently testable

### Within Each User Story

- Tests MUST be written and FAIL before implementation
- Public contracts before implementation internals
- Core implementation before diagnostics and documentation updates
- Contract compatibility before integration
- Story complete before moving to next priority

### Parallel Opportunities

- All Setup tasks marked [P] can run in parallel
- All Foundational tasks marked [P] can run in parallel (within Phase 2)
- Once Foundational phase completes, all user stories can start in parallel (if team capacity allows)
- All tests for a user story marked [P] can run in parallel
- Independent types within a story marked [P] can run in parallel
- Different user stories can be worked on in parallel by different team members

---

## Parallel Example: User Story 1

```bash
# Launch all tests for User Story 1 together:
Task: "Unit test for [core behavior] in tests/SharedMemoryStore.UnitTests/[Feature]Tests.cs"
Task: "Contract test for [public API/package behavior] in tests/SharedMemoryStore.ContractTests/[Feature]ContractTests.cs"
Task: "Integration test for [library scenario] in tests/SharedMemoryStore.IntegrationTests/[Feature]IntegrationTests.cs"

# Launch independent implementation files for User Story 1 together:
Task: "Create [public type/value object] in src/SharedMemoryStore/[Path]/[Type].cs"
Task: "Implement [library behavior] in src/SharedMemoryStore/[Path]/[Service].cs"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup
2. Complete Phase 2: Foundational (CRITICAL - blocks all stories)
3. Complete Phase 3: User Story 1
4. **STOP and VALIDATE**: Test User Story 1 independently
5. Pack and demonstrate consumer usage if ready

### Incremental Delivery

1. Complete Setup + Foundational → Foundation ready
2. Add User Story 1 → Test independently → Pack/Demo (MVP!)
3. Add User Story 2 → Test independently → Pack/Demo
4. Add User Story 3 → Test independently → Pack/Demo
5. Each story adds value without breaking previous stories

### Parallel Team Strategy

With multiple developers:

1. Team completes Setup + Foundational together
2. Once Foundational is done:
   - Developer A: User Story 1
   - Developer B: User Story 2
   - Developer C: User Story 3
3. Stories complete and integrate independently

---

## Notes

- [P] tasks = different files, no dependencies
- [Story] label maps task to specific user story for traceability
- Each user story should be independently completable and testable
- Verify tests fail before implementing
- Commit after each task or logical group
- Stop at any checkpoint to validate story independently
- Avoid: vague tasks, same file conflicts, cross-story dependencies that break independence
