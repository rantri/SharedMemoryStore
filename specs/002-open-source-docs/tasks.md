# Tasks: Open Source Documentation

**Input**: Design documents from `/specs/002-open-source-docs/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: This feature is documentation-only for runtime behavior. No new
runtime unit, contract, or integration tests are required by the specification.
Validation tasks cover documentation inventory, placeholder checks, internal
links, package metadata/readme alignment, sample commands, clean package
consumption, `dotnet test`, and `dotnet pack`.

**Organization**: Tasks are grouped by user story to enable independent
implementation and validation of each reader workflow.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and has no
  dependency on another incomplete task.
- **[Story]**: User story label for story phases only.
- Every task includes exact repository paths.

## Path Conventions

- **Root entry points**: `README.md`, `LICENSE`, `CHANGELOG.md`,
  `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `SECURITY.md`, `SUPPORT.md`
- **Documentation guides**: `docs/`
- **GitHub templates**: `.github/`
- **Samples**: `samples/BasicUsage/`, `samples/FrameValue/`
- **Validation scripts**: `scripts/`
- **Package project**: `src/SharedMemoryStore/SharedMemoryStore.csproj`
- **Behavior contracts**: `specs/001-frame-memory-store/contracts/`

---

## Phase 1: Setup (Shared Documentation Infrastructure)

**Purpose**: Establish the shared documentation and validation scaffolding used
by every user story.

- [X] T001 Create the required-file inventory and path constants for documentation validation in `scripts/validate-docs.ps1`
- [X] T002 [P] Create the complete documentation table-of-contents shell in `docs/index.md`
- [X] T003 [P] Create the GitHub issue template configuration baseline in `.github/ISSUE_TEMPLATE/config.yml`
- [X] T004 [P] Create the release preparation guide shell in `docs/releases.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Shared source-of-truth mapping, package metadata alignment, and
validation checks that must be in place before story documentation is finalized.

**CRITICAL**: No user story documentation should be finalized until this phase
is complete.

- [X] T005 Map package metadata values from `src/SharedMemoryStore/SharedMemoryStore.csproj` into package documentation notes in `docs/packaging.md`
- [X] T006 Map public API, error, and shared-memory behavior sources from `specs/001-frame-memory-store/contracts/public-api.md`, `specs/001-frame-memory-store/contracts/error-taxonomy.md`, and `specs/001-frame-memory-store/contracts/shared-memory-layout.md` into reference links in `docs/index.md`
- [X] T007 [P] Add unresolved placeholder scanning for `README.md`, `docs/`, `.github/`, root policy files, and `samples/` to `scripts/validate-docs.ps1`
- [X] T008 [P] Add relative Markdown link validation for `README.md`, `docs/`, root policy files, and `samples/` to `scripts/validate-docs.ps1`
- [X] T009 Add package readme, package license, release notes, and metadata alignment checks against `src/SharedMemoryStore/SharedMemoryStore.csproj` to `scripts/validate-docs.ps1`
- [X] T010 Document documentation-only semantic version impact, package readme inclusion, and no runtime dependency changes in `docs/packaging.md`

**Checkpoint**: Foundation ready - user story work can now begin in parallel.

---

## Phase 3: User Story 1 - Evaluate the Library from GitHub (Priority: P1) MVP

**Goal**: A first-time GitHub visitor can understand the package purpose,
maturity, license, supported status, first-use path, and key constraints without
reading implementation files.

**Independent Test**: Starting from `README.md`, a first-time reader can
identify purpose, package/runtime status, core use cases, non-goals, license,
installation path, and first runnable example in under 10 minutes.

### Validation for User Story 1

- [X] T011 [US1] Add README and documentation-index reachability checks for evaluator links in `scripts/validate-docs.ps1`

### Implementation for User Story 1

- [X] T012 [US1] Expand the project overview, audience, prerelease status, target framework, Windows-first validation scope, future C++/Python status, non-goals, and support path in `README.md`
- [X] T013 [P] [US1] Add MIT license text matching the package license expression in `LICENSE`
- [X] T014 [US1] Create the first-use installation path, local package source guidance, minimal workflow, and expected status outcomes in `docs/getting-started.md`
- [X] T015 [P] [US1] Create the basic sample guide with prerequisites, command, expected output, and cleanup notes in `samples/BasicUsage/README.md`
- [X] T016 [US1] Link getting started, usage, contracts, examples, lifecycle, packaging, support, security, contributing, license, changelog, and release notes from `README.md`
- [X] T017 [US1] Link evaluator, consumer, reviewer, contributor, and maintainer paths from `docs/index.md`

**Checkpoint**: User Story 1 is independently reviewable from repository entry
points.

---

## Phase 4: User Story 2 - Use the Library Correctly (Priority: P2)

**Goal**: A package consumer can follow public documentation to create/open a
store, publish values, acquire and release leases, remove values, handle common
failures, observe diagnostics, and clean up resources.

**Independent Test**: A clean consumer project follows only the public
documentation and completes create/open, publish, acquire, release, remove,
reuse, and dispose.

### Validation for User Story 2

- [X] T018 [US2] Ensure clean consumer validation covers documented create/open, publish, acquire, release, remove, reuse, and dispose in `scripts/validate-package-consumption.ps1`

### Implementation for User Story 2

- [X] T019 [P] [US2] Create the primary consumer workflow guide for create/open, publish, acquire, release, remove, reuse, diagnostics, and dispose in `docs/usage.md`
- [X] T020 [P] [US2] Create duplicate key, missing key, full store, oversized value, invalid release, unsupported platform, stale lease, cleanup failure, and version mismatch guidance in `docs/errors.md`
- [X] T021 [P] [US2] Create diagnostics snapshot, observability responsibility, and consumer-controlled troubleshooting guidance in `docs/diagnostics.md`
- [X] T022 [US2] Expand store owner, producer, reader, lease, removal, stale lease, abnormal termination, and cleanup responsibilities in `docs/lifecycle.md`
- [X] T023 [US2] Add basic workflow and error-handling examples that match the public sample behavior in `docs/examples.md`
- [X] T024 [US2] Align the basic sample README with the getting-started and usage workflow in `samples/BasicUsage/README.md`
- [X] T025 [US2] Cross-link usage, errors, diagnostics, lifecycle, and examples from `docs/index.md`

**Checkpoint**: User Story 2 can be validated by following public consumer docs
and the package-consumption script.

---

## Phase 5: User Story 3 - Understand Contracts and Compatibility (Priority: P3)

**Goal**: A production reviewer or future language implementer can trace public
behavior, lifecycle, memory ownership, diagnostics, compatibility, performance
scope, versioning, and portability claims to detailed documentation.

**Independent Test**: A reviewer can trace every major public behavior from
repository entry points to detailed contract documentation within two navigation
steps and without source inspection.

### Validation for User Story 3

- [X] T026 [US3] Add contract-reference link coverage checks for lifecycle, errors, diagnostics, portability, performance, and examples in `scripts/validate-docs.ps1`

### Implementation for User Story 3

- [X] T027 [P] [US3] Create performance scope documentation with measured versus unmeasured claims and no hardware guarantees in `docs/performance.md`
- [X] T028 [P] [US3] Expand .NET 10 baseline, Windows-first validation, future C++/Python audience, unsupported scenarios, and portability constraints in `docs/portability.md`
- [X] T029 [US3] Add public API and status/error contract traceability links to `docs/usage.md`
- [X] T030 [US3] Add detailed lifecycle, ownership, stale recovery, abnormal termination, and cleanup contract links to `docs/lifecycle.md`
- [X] T031 [US3] Add language-neutral key, value descriptor, opaque payload, lease, and frame-shaped value guidance in `docs/examples.md`
- [X] T032 [P] [US3] Create the frame-value sample guide with consumer-owned layout rules, command, expected output, and limitations in `samples/FrameValue/README.md`
- [X] T033 [US3] Cross-link contract and compatibility paths from `README.md` and `docs/index.md`

**Checkpoint**: User Story 3 can be reviewed as a public contract and
compatibility reference without relying on implementation inspection.

---

## Phase 6: User Story 4 - Contribute to the Project (Priority: P4)

**Goal**: A potential contributor can report issues, propose changes, run local
validation, follow conduct expectations, and submit a pull request maintainers
can review.

**Independent Test**: A first-time contributor can locate contribution guidance
from the repository root and identify issue, support, pull request, validation,
review, conduct, documentation update, and security disclosure expectations in
under 15 minutes.

### Validation for User Story 4

- [X] T034 [US4] Add contributor-path required-file and required-link checks for `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, `.github/ISSUE_TEMPLATE/`, and `.github/pull_request_template.md` to `scripts/validate-docs.ps1`

### Implementation for User Story 4

- [X] T035 [P] [US4] Create setup, build, test, pack, sample, benchmark, documentation validation, compatibility review, and PR guidance in `CONTRIBUTING.md`
- [X] T036 [P] [US4] Create project-specific conduct expectations and maintainer-owned enforcement path in `CODE_OF_CONDUCT.md`
- [X] T037 [P] [US4] Create bug report fields for package version, OS, .NET SDK/runtime, store options, operation, status, reproduction, and logs in `.github/ISSUE_TEMPLATE/bug_report.yml`
- [X] T038 [P] [US4] Create documentation issue fields for affected file/link, observed problem, and expected change in `.github/ISSUE_TEMPLATE/documentation.yml`
- [X] T039 [P] [US4] Create feature request fields for use case, API impact, compatibility impact, dependency impact, and alternatives in `.github/ISSUE_TEMPLATE/feature_request.yml`
- [X] T040 [P] [US4] Create pull request checklist for summary, behavior/API/package impact, validation, docs, compatibility, security, support, and release notes in `.github/pull_request_template.md`
- [X] T041 [US4] Cross-link contributing, conduct, issue templates, pull request guidance, security disclosure, and support paths from `README.md`, `CONTRIBUTING.md`, and `docs/index.md`

**Checkpoint**: User Story 4 can be validated by following only repository root
and GitHub community files.

---

## Phase 7: User Story 5 - Maintain Releases and Support (Priority: P5)

**Goal**: A maintainer can publish releases with consistent package-facing
documentation, release notes, changelog entries, support policy, security
reporting guidance, compatibility statements, and known limitations.

**Independent Test**: A maintainer preparing a release can use the docs to
verify package description, license, release notes, changelog, support,
security, compatibility, and known limitations before publication.

### Validation for User Story 5

- [X] T042 [US5] Add release-readiness checks for support, security, changelog, package metadata, and release guide links in `scripts/validate-docs.ps1`

### Implementation for User Story 5

- [X] T043 [P] [US5] Create general questions, bugs, security reports, documentation issues, feature requests, unsupported scenarios, and best-effort prerelease support guidance in `SUPPORT.md`
- [X] T044 [P] [US5] Create private vulnerability reporting, supported versions, public disclosure avoidance, and prerelease security support guidance in `SECURITY.md`
- [X] T045 [P] [US5] Create reverse-chronological changelog entries for the documentation baseline, package version, compatibility impact, known limitations, and validation scope in `CHANGELOG.md`
- [X] T046 [US5] Complete release preparation checks for package description, release notes, compatibility, known limitations, support, security, license, and documentation links in `docs/releases.md`
- [X] T047 [US5] Verify package readme, package release notes, repository metadata, license metadata, and package description alignment in `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [X] T048 [US5] Expand package consumer metadata, clean package validation, release notes, support path, and package readme guidance in `docs/packaging.md`
- [X] T049 [US5] Cross-link support, security, changelog, release guide, package metadata, and package validation paths from `README.md` and `docs/index.md`

**Checkpoint**: User Story 5 can be validated by a release-readiness review
without changing runtime behavior.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Final validation and cleanup across all documentation paths.

- [X] T050 Run documentation inventory, placeholder, link, and metadata validation in `scripts/validate-docs.ps1`
- [X] T051 Run manual placeholder search across `README.md`, `docs/`, `.github/`, root policy files, `LICENSE`, and `samples/`
- [X] T052 Run package restore, build, and pack validation for `src/SharedMemoryStore/SharedMemoryStore.csproj`
- [X] T053 Run clean consumer package validation in `scripts/validate-package-consumption.ps1`
- [X] T054 [P] Run sample command validation for `samples/BasicUsage/BasicUsage.csproj` and `samples/FrameValue/FrameValue.csproj`
- [X] T055 Run full release test validation for test projects under `tests/`
- [X] T056 Complete reader workflow review from `specs/002-open-source-docs/quickstart.md` and fix any final public documentation gaps in `README.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies.
- **Phase 2 Foundational**: Depends on Phase 1 and blocks final user story
  documentation.
- **User Stories**: Depend on Phase 2 for validation and source-of-truth
  mapping.
- **Phase 8 Polish**: Depends on all desired user stories being complete.

### User Story Dependencies

- **User Story 1 (P1)**: Starts after Phase 2. This is the MVP.
- **User Story 2 (P2)**: Starts after Phase 2. It can be implemented
  independently, then cross-linked with US1 entry points.
- **User Story 3 (P3)**: Starts after Phase 2. It can be implemented
  independently, then cross-linked with US1 and US2 references.
- **User Story 4 (P4)**: Starts after Phase 2. It can be implemented
  independently, with final links to support and security once US5 exists.
- **User Story 5 (P5)**: Starts after Phase 2. It can be implemented
  independently, with final links to contribution docs once US4 exists.

### Within Each User Story

- Validation checks before final documentation sign-off.
- Source-of-truth and metadata alignment before public claims.
- Story-specific documents before cross-link updates.
- Reader workflow review before moving to polish.

---

## Parallel Opportunities

- Setup tasks T002, T003, and T004 can run in parallel after T001 starts.
- Foundational tasks T007 and T008 can run in parallel with T005 and T006.
- Once Phase 2 completes, US1 through US5 can proceed in parallel by different
  contributors.
- Independent documents within a story marked [P] can be written in parallel.
- Sample README work can run in parallel with root policy and guide work when
  paths do not overlap.
- Final sample validation T054 can run in parallel with documentation review
  tasks after all sample docs are complete.

---

## Parallel Example: User Story 1

```text
Task: "Add MIT license text matching the package license expression in LICENSE"
Task: "Create the basic sample guide with prerequisites, command, expected output, and cleanup notes in samples/BasicUsage/README.md"
```

## Parallel Example: User Story 2

```text
Task: "Create the primary consumer workflow guide for create/open, publish, acquire, release, remove, reuse, diagnostics, and dispose in docs/usage.md"
Task: "Create duplicate key, missing key, full store, oversized value, invalid release, unsupported platform, stale lease, cleanup failure, and version mismatch guidance in docs/errors.md"
Task: "Create diagnostics snapshot, observability responsibility, and consumer-controlled troubleshooting guidance in docs/diagnostics.md"
```

## Parallel Example: User Story 3

```text
Task: "Create performance scope documentation with measured versus unmeasured claims and no hardware guarantees in docs/performance.md"
Task: "Expand .NET 10 baseline, Windows-first validation, future C++/Python audience, unsupported scenarios, and portability constraints in docs/portability.md"
Task: "Create the frame-value sample guide with consumer-owned layout rules, command, expected output, and limitations in samples/FrameValue/README.md"
```

## Parallel Example: User Story 4

```text
Task: "Create setup, build, test, pack, sample, benchmark, documentation validation, compatibility review, and PR guidance in CONTRIBUTING.md"
Task: "Create bug report fields for package version, OS, .NET SDK/runtime, store options, operation, status, reproduction, and logs in .github/ISSUE_TEMPLATE/bug_report.yml"
Task: "Create pull request checklist for summary, behavior/API/package impact, validation, docs, compatibility, security, support, and release notes in .github/pull_request_template.md"
```

## Parallel Example: User Story 5

```text
Task: "Create general questions, bugs, security reports, documentation issues, feature requests, unsupported scenarios, and best-effort prerelease support guidance in SUPPORT.md"
Task: "Create private vulnerability reporting, supported versions, public disclosure avoidance, and prerelease security support guidance in SECURITY.md"
Task: "Create reverse-chronological changelog entries for the documentation baseline, package version, compatibility impact, known limitations, and validation scope in CHANGELOG.md"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1: Setup.
2. Complete Phase 2: Foundational validation and source mapping.
3. Complete Phase 3: User Story 1.
4. Stop and validate the evaluator path from `README.md`.
5. Confirm `scripts/validate-docs.ps1` covers the MVP required links and
   placeholders.

### Incremental Delivery

1. Complete setup and foundational checks.
2. Deliver US1 so GitHub visitors can evaluate the project.
3. Deliver US2 so package consumers can use the library correctly.
4. Deliver US3 so production reviewers and future implementers can trace
   contracts.
5. Deliver US4 so contributors can participate predictably.
6. Deliver US5 so maintainers can release and support the package.
7. Run Phase 8 validation before merge.

### Parallel Team Strategy

1. One contributor owns validation script tasks in `scripts/`.
2. One contributor owns consumer and contract docs in `docs/`.
3. One contributor owns community files in root and `.github/`.
4. One contributor owns package metadata, release, and support alignment.
5. Integrate through final cross-link and validation tasks.

---

## Notes

- [P] tasks touch separate files and can run independently.
- Each user story has its own validation task and independently testable reader
  workflow.
- Documentation must distinguish implemented behavior, prerelease status,
  unsupported scenarios, and future C++/Python goals.
- Public claims must align with `src/SharedMemoryStore/SharedMemoryStore.csproj`
  and `specs/001-frame-memory-store/contracts/`.
- Avoid unresolved placeholders, stale commands, broken links, unsupported
  platform claims, unapproved support commitments, and undocumented package
  metadata changes.
