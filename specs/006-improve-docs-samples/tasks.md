# Tasks: Documentation and Samples Excellence

**Input**: Design documents from `specs/006-improve-docs-samples/`

**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Tests**: This feature is documentation-only and sample-only. No runtime behavior changes are planned. Validation tasks cover documentation inventory, links, placeholders, public API/status drift, sample README contracts, sample builds/runs, package consumption, solution tests, and package creation.

**Organization**: Tasks are grouped by user story so each reader workflow can be implemented and validated independently.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel because it touches different files and has no dependency on another incomplete task.
- **[Story]**: User story label from [spec.md](spec.md), used only in user story phases.
- Every task includes exact repository paths.

## Path Conventions

- **Root entry points**: `README.md`, `CHANGELOG.md`, `CONTRIBUTING.md`, `SUPPORT.md`, `SECURITY.md`
- **Public guides**: `docs/`
- **Samples**: `samples/BasicUsage/`, `samples/FrameValue/`, `samples/ZeroCopyIngest/`, `samples/HostedServiceIntegration/`
- **Validation scripts**: `scripts/validate-docs.ps1`, `scripts/validate-package-consumption.ps1`
- **Package project**: `src/SharedMemoryStore/SharedMemoryStore.csproj`
- **Behavior contracts**: `specs/001-frame-memory-store/contracts/`, `specs/003-zero-copy-ingest/contracts/`, `specs/004-store-reliability-hardening/contracts/`, `specs/005-api-production-readiness/contracts/`

---

## Phase 1: Setup

**Purpose**: Establish implementation tracking so documentation updates can be reviewed against the feature contracts.

- [X] T001 Create current documentation inventory and reusable-content notes in specs/006-improve-docs-samples/documentation-inventory.md
- [X] T002 [P] Create workflow, outcome, sample, and maintainer coverage matrix in specs/006-improve-docs-samples/documentation-coverage.md
- [X] T003 [P] Create sample command and README validation matrix in specs/006-improve-docs-samples/sample-validation.md
- [X] T004 [P] Create public API, status, and contract reference map for documentation review in specs/006-improve-docs-samples/public-reference-map.md

---

## Phase 2: Foundational

**Purpose**: Shared validation and source-of-truth mapping that must exist before story documentation is finalized.

**Critical**: No user story documentation should be signed off until these validation foundations are in place.

- [X] T005 Add new required guide inventory for docs/concepts.md, docs/samples.md, docs/architecture.md, and docs/maintainers.md in scripts/validate-docs.ps1
- [X] T006 Expand public documentation placeholder and relative-link validation to include all required docs, sample READMEs, root policy files, and contract links in scripts/validate-docs.ps1
- [X] T007 Add required cross-link checks for README.md, docs/index.md, feature guides, sample READMEs, and contract references in scripts/validate-docs.ps1
- [X] T008 Add sample README contract checks for audience, concepts, prerequisites, run command, expected output, cleanup, related docs, non-success statuses, and non-goals in scripts/validate-docs.ps1
- [X] T009 Add public API, option, method, type, and StoreStatus reference drift checks against src/SharedMemoryStore/ and docs/ in scripts/validate-docs.ps1
- [X] T010 Add package metadata, README, CHANGELOG.md, docs/releases.md, and docs/packaging.md alignment checks in scripts/validate-docs.ps1
- [X] T011 [P] Align clean consumer validation with the documented first-use workflow in scripts/validate-package-consumption.ps1
- [X] T012 [P] Align public XML documentation examples and status wording in src/SharedMemoryStore/MemoryStore.cs, src/SharedMemoryStore/SharedMemoryStoreOptions.cs, src/SharedMemoryStore/StoreStatus.cs, src/SharedMemoryStore/Ingest/ValueReservation.cs, and src/SharedMemoryStore/ValueLease.cs

**Checkpoint**: Validation can detect missing guides, broken links, placeholder text, stale public names, incomplete sample READMEs, and package metadata drift.

---

## Phase 3: User Story 1 - Start Successfully as a New User (Priority: P1) MVP

**Goal**: A first-time consumer can land on the repository, understand the package, run the smallest useful workflow, and know where to go next.

**Independent Test**: A developer who has not used the package can start from README.md and complete the documented first-use workflow in under 10 minutes using only public docs and the linked sample.

### Implementation for User Story 1

- [X] T013 [P] [US1] Rewrite the repository entry path with purpose, supported scenarios, non-goals, install/reference path, minimal workflow, documentation map, package status, support, and validation path in README.md
- [X] T014 [P] [US1] Rework goal-based navigation and simple-to-advanced reader routes in docs/index.md
- [X] T015 [P] [US1] Update the first-use package workflow, run command, expected output, and next steps in docs/getting-started.md
- [X] T016 [P] [US1] Align the minimal sample README with the getting-started workflow, expected output, cleanup, and next links in samples/BasicUsage/README.md
- [X] T017 [P] [US1] Refresh minimal sample output text and status checks to match documented first-use expectations in samples/BasicUsage/Program.cs
- [X] T018 [US1] Align package-facing README and package identity statements between README.md and src/SharedMemoryStore/SharedMemoryStore.csproj
- [X] T019 [US1] Record first-use review results and any remaining entry-point gaps in specs/006-improve-docs-samples/documentation-coverage.md

**Checkpoint**: User Story 1 is independently reviewable from README.md, docs/index.md, docs/getting-started.md, and samples/BasicUsage/README.md.

---

## Phase 4: User Story 2 - Learn Every Public Feature in Context (Priority: P2)

**Goal**: A package consumer can move from basic usage to complete public feature coverage with ownership, outcomes, examples, and troubleshooting guidance.

**Independent Test**: A reviewer can choose any public feature from the package surface and find a user-facing explanation, expected statuses, ownership rules, and an example or sample link within two navigation steps from docs/index.md.

### Implementation for User Story 2

- [X] T020 [P] [US2] Create concept-first package vocabulary for store, name, key, descriptor, payload, slot, lease, reservation, segmented publish, wait policy, status, diagnostics snapshot, recovery, capacity pressure, lifecycle, portability, and package contract in docs/concepts.md
- [X] T021 [P] [US2] Reorganize create/open, options, capacity, publish, acquire, descriptor/payload, release, remove/reuse, reservation, segmented publish, waits, diagnostics, recovery, disposal, and package-consumption workflows in docs/usage.md
- [X] T022 [P] [US2] Expand task-oriented examples for basic values, frame-shaped values, direct reservation ingest, segmented payloads, diagnostics, recovery, waits, and error handling in docs/examples.md
- [X] T023 [P] [US2] Expand outcome taxonomy for validation failures, capacity failures, duplicate/missing keys, lease failures, reservation failures, contention/timeouts, disposed stores, unsupported platforms, cleanup/recovery, corruption, and version mismatch in docs/errors.md
- [X] T024 [P] [US2] Expand diagnostics snapshot fields, failure counts, troubleshooting signals, support evidence, and caller-owned observability boundaries in docs/diagnostics.md
- [X] T025 [P] [US2] Expand store handle, published value, lease, reservation, reader, producer, diagnostics, recovery, disposal, abnormal termination, and cleanup ownership rules in docs/lifecycle.md
- [X] T026 [P] [US2] Tighten optional hosting and lifecycle integration guidance without implying core package dependencies or broad service abstractions in docs/integration.md
- [X] T027 [P] [US2] Align package consumption, local package source, package README, release notes, and metadata guidance in docs/packaging.md
- [X] T028 [US2] Add behavior contract links from docs/concepts.md, docs/usage.md, docs/examples.md, docs/errors.md, docs/diagnostics.md, and docs/lifecycle.md to specs/001-frame-memory-store/contracts/, specs/003-zero-copy-ingest/contracts/, specs/004-store-reliability-hardening/contracts/, and specs/005-api-production-readiness/contracts/
- [X] T029 [US2] Update feature-learning routes and two-step feature reachability in docs/index.md
- [X] T030 [US2] Complete workflow and outcome coverage rows for User Story 2 in specs/006-improve-docs-samples/documentation-coverage.md

**Checkpoint**: User Story 2 covers every workflow in FR-004 and every outcome category in FR-006 with guide, status, ownership, example/sample, and contract traceability.

---

## Phase 5: User Story 3 - Progress Through Runnable Samples (Priority: P3)

**Goal**: Learners can follow a sample ladder from minimal usage through realistic advanced scenarios, with each sample runnable from a clean checkout.

**Independent Test**: Every sample in the documented learning path can be run from a clean checkout by following its README, and each result can be matched to the expected output without source inspection.

### Implementation for User Story 3

- [X] T031 [P] [US3] Create ordered sample ladder, audience guidance, run commands, expected outcomes, cleanup summary, and deeper links in docs/samples.md
- [X] T032 [P] [US3] Complete required sample contract sections for basic usage in samples/BasicUsage/README.md
- [X] T033 [P] [US3] Complete required sample contract sections for frame-shaped values in samples/FrameValue/README.md
- [X] T034 [P] [US3] Complete required sample contract sections for zero-copy ingest and segmented publish in samples/ZeroCopyIngest/README.md
- [X] T035 [P] [US3] Complete required sample contract sections for optional hosted service integration in samples/HostedServiceIntegration/README.md
- [X] T036 [P] [US3] Refresh frame sample output, descriptor explanation, and status handling to match its README in samples/FrameValue/Program.cs and samples/FrameValue/FrameDescriptor.cs
- [X] T037 [P] [US3] Refresh zero-copy ingest sample output, reservation lifecycle, segmented publish path, and status handling to match its README in samples/ZeroCopyIngest/Program.cs
- [X] T038 [P] [US3] Refresh hosted service sample output, lifecycle, health, shutdown, cleanup, and recovery behavior to match its README in samples/HostedServiceIntegration/Program.cs
- [X] T039 [US3] Add sample ladder links from docs/samples.md to docs/examples.md, docs/usage.md, docs/integration.md, docs/getting-started.md, docs/index.md, and every sample README under samples/
- [X] T040 [US3] Complete sample build/run and README contract results in specs/006-improve-docs-samples/sample-validation.md

**Checkpoint**: User Story 3 is independently valid when all four sample READMEs describe purpose, prerequisites, command, expected output, cleanup, non-success statuses, and related docs.

---

## Phase 6: User Story 4 - Understand Internals as a Maintainer (Priority: P4)

**Goal**: Maintainers can understand architecture, design boundaries, invariants, performance rules, portability constraints, validation, and release responsibilities without reverse-engineering implementation files.

**Independent Test**: A maintainer can start from docs/index.md and find internal concept, architecture, performance, validation, and release guidance within two navigation steps, then use that guidance to review a documentation-only or public-contract change.

### Implementation for User Story 4

- [X] T041 [P] [US4] Create maintainer architecture guide covering package responsibility boundaries, source areas, storage model, lifecycle model, synchronization, recovery, diagnostics, and current implementation details in docs/architecture.md
- [X] T042 [P] [US4] Create maintainer guide covering public contract boundaries, changeable internals, validation commands, documentation update rules, release impact, and review questions in docs/maintainers.md
- [X] T043 [P] [US4] Expand measured results, design expectations, benchmark methodology, capacity assumptions, platform assumptions, and unvalidated scenarios in docs/performance.md
- [X] T044 [P] [US4] Expand .NET 10 baseline, Windows-first validation scope, unsupported platform behavior, same-host boundary, and future C++/Python portability wording in docs/portability.md
- [X] T045 [P] [US4] Align release responsibilities, package metadata review, changelog impact, validation commands, support/security review, and documentation-only change handling in docs/releases.md
- [X] T046 [US4] Link maintainer internals from README.md and docs/index.md without presenting changeable implementation details as public compatibility guarantees
- [X] T047 [US4] Add source-area and invariant references from docs/architecture.md to src/SharedMemoryStore/Layout/, src/SharedMemoryStore/Ingest/, src/SharedMemoryStore/Leasing/, src/SharedMemoryStore/Diagnostics/, and src/SharedMemoryStore/Lifecycle/
- [X] T048 [US4] Add contract boundary links from docs/maintainers.md to specs/001-frame-memory-store/contracts/, specs/003-zero-copy-ingest/contracts/, specs/004-store-reliability-hardening/contracts/, and specs/005-api-production-readiness/contracts/
- [X] T049 [US4] Complete maintainer-internals review rows for User Story 4 in specs/006-improve-docs-samples/documentation-coverage.md

**Checkpoint**: User Story 4 is independently reviewable through docs/architecture.md, docs/maintainers.md, docs/performance.md, docs/portability.md, docs/releases.md, README.md, and docs/index.md.

---

## Phase 7: User Story 5 - Keep Documentation Trustworthy Over Time (Priority: P5)

**Goal**: Maintainers can repeatedly validate that documentation, samples, package metadata, release notes, XML documentation, and contract references remain aligned with current package behavior.

**Independent Test**: A documentation review can run the documented validation workflow and verify that links, sample commands, public API references, contract references, status names, package metadata, release notes, and known limitations are current.

### Implementation for User Story 5

- [X] T050 [US5] Add documentation maintenance checklist for public behavior, API names, statuses, samples, performance claims, platform support, diagnostics, package metadata, and release status in docs/maintainers.md
- [X] T051 [P] [US5] Update contributor documentation review expectations, validation commands, and release-impact requirements in CONTRIBUTING.md
- [X] T052 [P] [US5] Update changelog entry for documentation and samples excellence scope, validation expectations, and compatibility impact in CHANGELOG.md
- [X] T053 [P] [US5] Update documentation-only release review, known limitation review, and package release note alignment in docs/releases.md
- [X] T054 [P] [US5] Update package README, PackageReleaseNotes, license, changelog, release notes, and packaging guide alignment rules in docs/packaging.md
- [X] T055 [US5] Align PackageReleaseNotes and package-facing documentation pointers with the documentation feature scope in src/SharedMemoryStore/SharedMemoryStore.csproj
- [X] T056 [US5] Link maintainer validation guidance from docs/maintainers.md to scripts/validate-docs.ps1, scripts/validate-package-consumption.ps1, SharedMemoryStore.slnx, and specs/006-improve-docs-samples/quickstart.md
- [X] T057 [US5] Update release and maintenance routes from README.md and docs/index.md to docs/maintainers.md, docs/releases.md, docs/packaging.md, CHANGELOG.md, SUPPORT.md, and SECURITY.md
- [X] T058 [US5] Complete validation-review and maintenance-rule rows for User Story 5 in specs/006-improve-docs-samples/documentation-coverage.md

**Checkpoint**: User Story 5 is independently valid when maintainers can run the documented validation workflow and identify every affected documentation surface for future public behavior, metadata, sample, or release changes.

---

## Phase 8: Polish & Cross-Cutting Validation

**Purpose**: Final validation and cleanup across all documentation, samples, package metadata, and release artifacts.

- [X] T059 Run scripts/validate-docs.ps1 and fix validation failures in README.md, docs/, samples/, root policy files, and scripts/validate-docs.ps1
- [X] T060 Run dotnet build SharedMemoryStore.slnx -c Release and fix build or sample compile failures in SharedMemoryStore.slnx, src/SharedMemoryStore/, tests/, and samples/
- [X] T061 Run all sample commands from specs/006-improve-docs-samples/quickstart.md and fix output mismatches in samples/BasicUsage/README.md, samples/FrameValue/README.md, samples/ZeroCopyIngest/README.md, and samples/HostedServiceIntegration/README.md
- [X] T062 Run scripts/validate-package-consumption.ps1 and fix package-consumption drift in scripts/validate-package-consumption.ps1, docs/getting-started.md, and docs/packaging.md
- [X] T063 Run dotnet test SharedMemoryStore.slnx -c Release and fix documentation or sample regressions exposed through tests/SharedMemoryStore.ContractTests/, tests/SharedMemoryStore.IntegrationTests/, and tests/SharedMemoryStore.UnitTests/
- [X] T064 Run dotnet pack src/SharedMemoryStore/SharedMemoryStore.csproj -c Release -o artifacts/package and fix package metadata or README issues in src/SharedMemoryStore/SharedMemoryStore.csproj and README.md
- [X] T065 Review unsupported behavior, performance, platform, persistence, distributed-cache, hidden-background-work, and future-binding claims and fix wording in docs/performance.md, docs/portability.md, docs/integration.md, docs/architecture.md, and docs/maintainers.md
- [X] T066 Complete manual reader workflow review from specs/006-improve-docs-samples/quickstart.md and record final results in specs/006-improve-docs-samples/documentation-coverage.md

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 Setup**: No dependencies.
- **Phase 2 Foundational**: Depends on Phase 1 and blocks final user story sign-off.
- **User Stories**: Depend on Phase 2 validation scaffolding and source-of-truth mapping.
- **Phase 8 Polish**: Depends on all selected user stories.

### User Story Dependencies

- **US1 (P1)**: Starts after Phase 2. This is the MVP because it creates the first-use path.
- **US2 (P2)**: Starts after Phase 2. It can proceed independently, then link back to US1 entry points.
- **US3 (P3)**: Starts after Phase 2. It can proceed independently, then link samples back to US1 and US2 guides.
- **US4 (P4)**: Starts after Phase 2. It can proceed independently, then link internals from README.md and docs/index.md.
- **US5 (P5)**: Starts after Phase 2. It can proceed independently, but final validation depends on the selected docs and sample surfaces.

### Within Each User Story

- Inventory and contract mapping before public claims.
- Story-specific documents before cross-link updates.
- Sample README updates before sample command validation.
- Validation results recorded before story checkpoint sign-off.

---

## Parallel Opportunities

- Setup tasks T002, T003, and T004 can run in parallel after T001 starts.
- Foundational tasks T011 and T012 can run in parallel with validation-script work T005 through T010.
- US1 tasks T013 through T017 can run in parallel because they touch separate entry-point and sample files.
- US2 guide tasks T020 through T027 can run in parallel, with T028 through T030 following for cross-links and coverage.
- US3 sample README and sample source tasks T031 through T038 can run in parallel, with T039 and T040 following.
- US4 guide tasks T041 through T045 can run in parallel, with T046 through T049 following for links and coverage.
- US5 tasks T051 through T054 can run in parallel, with T055 through T058 following for package metadata, links, and coverage.

---

## Parallel Example: User Story 1

```text
Task: "Rewrite the repository entry path with purpose, supported scenarios, non-goals, install/reference path, minimal workflow, documentation map, package status, support, and validation path in README.md"
Task: "Rework goal-based navigation and simple-to-advanced reader routes in docs/index.md"
Task: "Update the first-use package workflow, run command, expected output, and next steps in docs/getting-started.md"
Task: "Align the minimal sample README with the getting-started workflow, expected output, cleanup, and next links in samples/BasicUsage/README.md"
```

## Parallel Example: User Story 2

```text
Task: "Create concept-first package vocabulary for store, name, key, descriptor, payload, slot, lease, reservation, segmented publish, wait policy, status, diagnostics snapshot, recovery, capacity pressure, lifecycle, portability, and package contract in docs/concepts.md"
Task: "Reorganize create/open, options, capacity, publish, acquire, descriptor/payload, release, remove/reuse, reservation, segmented publish, waits, diagnostics, recovery, disposal, and package-consumption workflows in docs/usage.md"
Task: "Expand outcome taxonomy for validation failures, capacity failures, duplicate/missing keys, lease failures, reservation failures, contention/timeouts, disposed stores, unsupported platforms, cleanup/recovery, corruption, and version mismatch in docs/errors.md"
```

## Parallel Example: User Story 3

```text
Task: "Complete required sample contract sections for frame-shaped values in samples/FrameValue/README.md"
Task: "Complete required sample contract sections for zero-copy ingest and segmented publish in samples/ZeroCopyIngest/README.md"
Task: "Complete required sample contract sections for optional hosted service integration in samples/HostedServiceIntegration/README.md"
Task: "Refresh hosted service sample output, lifecycle, health, shutdown, cleanup, and recovery behavior to match its README in samples/HostedServiceIntegration/Program.cs"
```

## Parallel Example: User Story 4

```text
Task: "Create maintainer architecture guide covering package responsibility boundaries, source areas, storage model, lifecycle model, synchronization, recovery, diagnostics, and current implementation details in docs/architecture.md"
Task: "Create maintainer guide covering public contract boundaries, changeable internals, validation commands, documentation update rules, release impact, and review questions in docs/maintainers.md"
Task: "Expand measured results, design expectations, benchmark methodology, capacity assumptions, platform assumptions, and unvalidated scenarios in docs/performance.md"
```

## Parallel Example: User Story 5

```text
Task: "Update contributor documentation review expectations, validation commands, and release-impact requirements in CONTRIBUTING.md"
Task: "Update changelog entry for documentation and samples excellence scope, validation expectations, and compatibility impact in CHANGELOG.md"
Task: "Update documentation-only release review, known limitation review, and package release note alignment in docs/releases.md"
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete Phase 1 setup tracking.
2. Complete Phase 2 foundational validation and reference mapping.
3. Complete Phase 3 User Story 1.
4. Stop and validate first-use independently from README.md, docs/index.md, docs/getting-started.md, and samples/BasicUsage/README.md.

### Incremental Delivery

1. Deliver US1 so new users can start successfully.
2. Deliver US2 so consumers can learn every public feature and outcome.
3. Deliver US3 so sample projects become a runnable learning ladder.
4. Deliver US4 so maintainers have internals, architecture, performance, and release guidance.
5. Deliver US5 so validation and maintenance rules keep documentation trustworthy.
6. Complete Phase 8 release-grade validation.

### Parallel Team Strategy

1. One contributor owns validation scripts in scripts/.
2. One contributor owns consumer guides in docs/concepts.md, docs/usage.md, docs/examples.md, docs/errors.md, docs/diagnostics.md, and docs/lifecycle.md.
3. One contributor owns samples under samples/.
4. One contributor owns maintainer, performance, portability, release, and package docs.
5. Integrate through final cross-link, coverage, and quickstart validation tasks.

---

## Notes

- [P] tasks touch separate files and can run independently after phase prerequisites.
- Documentation must preserve current runtime behavior and public contracts.
- Public claims must link to package metadata, current docs, tests, or Spec Kit contract references.
- Performance wording must separate measured evidence from expectations and unsupported scenarios.
- Future C++ and Python wording must remain portability context, not delivered binding claims.
- Avoid unresolved placeholders, stale API/status names, unsupported platform promises, hidden background work, persistence guarantees, distributed-cache claims, and generated sample build output in reader-facing docs.
