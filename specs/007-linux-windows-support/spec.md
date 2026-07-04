# Feature Specification: Linux, Windows, and Docker Support

**Feature Branch**: `007-linux-windows-support`

**Created**: 2026-07-03

**Status**: Draft

**Input**: User description: "The package should have full support on Linux and Windows. Both on development and running." Updated request: "add to the spec the ability to use the shared memory between Docker containers."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Run the Store on Linux and Windows (Priority: P1)

A package consumer can use the same public store workflows on Linux and Windows
without platform-specific application code for ordinary create, open, publish,
acquire, release, remove, reuse, reservation, segmented publish, diagnostics,
and cleanup operations.

**Why this priority**: Runtime support is the package's primary value. Linux
users cannot adopt the package if valid store operations deterministically report
unsupported platform or require different consumer behavior than Windows.

**Independent Test**: On a clean Linux environment and a clean Windows
environment, run the same package-consumption workflow that creates a store,
publishes and reads values, exercises reservation and segmented publishing, and
verifies all documented statuses match on both platforms.

**Acceptance Scenarios**:

1. **Given** a valid store configuration on Linux, **When** a consumer creates
   or opens a store, **Then** the store opens successfully and does not return an
   unsupported-platform outcome.
2. **Given** the same valid store configuration on Windows, **When** a consumer
   creates or opens a store, **Then** the observable store behavior matches the
   Linux result for the same workflow.
3. **Given** values are published, acquired by multiple readers, released,
   removed, and republished, **When** the workflow is run on Linux and Windows,
   **Then** reader visibility, lease protection, removal, and slot reuse follow
   the same public contract on both platforms.
4. **Given** direct reservation ingest and segmented publishing are used,
   **When** the producer completes, aborts, or recovers work, **Then** committed
   values and failure outcomes are equivalent on Linux and Windows.

---

### User Story 2 - Develop and Validate on Linux and Windows (Priority: P1)

A contributor can clone the repository on Linux or Windows, follow the
documented development workflow, run validation, and produce package artifacts
without needing a different feature set or hidden platform-specific setup.

**Why this priority**: Full platform support includes maintainability. If the
repository can only be built, tested, packaged, or documented from one operating
system, future changes can regress the other platform unnoticed.

**Independent Test**: From clean Linux and Windows checkouts, follow the
documented maintainer workflow through restore, build, tests, sample execution,
documentation validation, package-consumption validation, and package creation.

**Acceptance Scenarios**:

1. **Given** a contributor starts from a clean Linux checkout, **When** they
   follow the repository development instructions, **Then** the full validation
   workflow completes without platform-specific failures.
2. **Given** a contributor starts from a clean Windows checkout, **When** they
   follow the same documented validation intent, **Then** the full workflow
   completes with equivalent coverage and artifacts.
3. **Given** validation scripts or commands are documented, **When** they are
   run on Linux and Windows, **Then** command names, paths, cleanup behavior, and
   temporary artifacts are portable or clearly paired with equivalent commands.
4. **Given** a validation failure occurs on either platform, **When** the
   contributor reviews the output, **Then** the failure identifies the affected
   workflow and platform clearly enough to diagnose without source changes.

---

### User Story 3 - Share Stores Between Docker Containers (Priority: P2)

A service owner can run cooperating producer and reader processes in separate
Docker containers on the same host and use the package's shared-memory store
between those containers with the same public workflows used by ordinary
same-host processes.

**Why this priority**: Containerized deployments are a common production shape.
Linux and Windows support is incomplete for many users if shared-memory
participation only works for processes outside containers or only inside one
container.

**Independent Test**: Start two clean Docker containers on the same host with
the required shared-resource capabilities exposed, publish values from one
container, read and release them from the other container, and verify
diagnostics, cleanup, recovery, and failure outcomes match the documented
container support contract.

**Acceptance Scenarios**:

1. **Given** two containers on the same host are configured for shared store
   participation, **When** one container creates a store and publishes values,
   **Then** another container can open the same store by name and acquire those
   values.
2. **Given** a reader in one container holds an active lease, **When** a writer
   in another container removes or republishes values, **Then** lease protection
   and slot reuse follow the same public contract as non-container processes.
3. **Given** a container exits unexpectedly while holding a lease or incomplete
   reservation, **When** another container runs explicit recovery, **Then** stale
   work is recovered or reported safely according to documented outcomes.
4. **Given** containers are not configured with the required shared-resource
   capabilities, **When** a container attempts cross-container store
   participation, **Then** the package reports a documented environment or
   unsupported-capability outcome rather than corrupting data or silently using
   an isolated store.

---

### User Story 4 - Trust Cross-Platform Reliability and Recovery (Priority: P2)

A service owner can rely on synchronization, owner recovery, disposal races,
long-running reuse, diagnostics, and corruption detection to preserve the same
data-safety guarantees on Linux, Windows, and supported same-host Docker
container deployments.

**Why this priority**: A store that runs across supported environments but
weakens recovery or lifecycle safety in one environment would create production
data-risk and support burden.

**Independent Test**: Run the reliability, lifecycle, contention, recovery,
reuse, and diagnostics scenarios on Linux, Windows, and supported same-host
Docker container deployments and compare public outcomes, reports, and visible
value contents.

**Acceptance Scenarios**:

1. **Given** multiple processes participate in the same store, **When** readers,
   producers, and cleanup operations run concurrently, **Then** active reader
   leases protect storage on Linux, Windows, and supported same-host Docker
   container deployments.
2. **Given** an owner exits unexpectedly, **When** explicit recovery is run,
   **Then** stale work is recovered or reported safely according to the same
   public categories across supported environments.
3. **Given** operations race with disposal, **When** public operations complete,
   **Then** callers receive documented lifecycle outcomes and no platform leaks
   internal runtime failures.
4. **Given** high-churn and long-running reuse workloads execute, **When** they
   complete on Linux, Windows, and supported same-host Docker container
   deployments, **Then** index health, stale handle rejection, and diagnostics
   remain within documented bounds.

---

### User Story 5 - Learn Supported Platform Behavior from Public Docs (Priority: P3)

A consumer or maintainer can read the public documentation and understand that
Linux, Windows, and supported same-host Docker container deployments are
first-class supported environments, including prerequisites, known limitations,
validation coverage, sample expectations, and unsupported scenarios.

**Why this priority**: Platform support must be visible and precise. Stale
Windows-first wording or incomplete Linux guidance would undermine adoption and
make support expectations unclear.

**Independent Test**: Review README, portability guidance, samples, maintainer
docs, package metadata, release notes, and troubleshooting material to confirm
they consistently describe Linux, Windows, and supported Docker container usage
as supported runtime and development targets.

**Acceptance Scenarios**:

1. **Given** a Linux consumer opens the repository, **When** they read the
   platform guidance, **Then** they can identify supported workflows, expected
   setup, limitations, and sample commands within two navigation steps.
2. **Given** a Windows consumer opens the repository, **When** they read the
   same guidance, **Then** Windows support remains equally clear and no longer
   depends on outdated Windows-first caveats.
3. **Given** a containerized service owner opens the repository, **When** they
   read the container guidance, **Then** they can identify the supported
   same-host container sharing model, required deployment responsibilities,
   expected outcomes, and unsupported container scenarios within two navigation
   steps.
4. **Given** a platform-specific limitation remains, **When** it is documented,
   **Then** the limitation is scoped to the affected scenario and does not
   contradict the claim of full ordinary runtime and development support.
5. **Given** package metadata or release notes mention platform support, **When**
   the package is prepared for release, **Then** those statements match the
   validated Linux, Windows, and container behavior.

### Edge Cases

- Linux and Windows store names include characters that are valid for the public
  naming contract but awkward for platform-visible shared resources.
- Two unrelated users or services on the same host choose the same store name.
- A store is created by one process and opened by another process on the same
  platform with different process lifetime and permission boundaries.
- A producer or reader process exits while holding synchronization, a lease, or
  an incomplete reservation.
- Cleanup or recovery runs when owner liveness cannot be evaluated with the same
  precision across supported environments.
- Shared resources remain after abnormal process termination and a later process
  creates or opens a store with the same name.
- Two Docker containers on the same host are configured for shared store
  participation but run under different identities or permission boundaries.
- A Docker container is restarted while another container still has active
  readers, pending reservations, or cleanup work for the same store.
- Container names, lifecycle, and cleanup differ from host processes, but public
  store names and resource ownership must remain deterministic.
- A Docker deployment isolates the required shared-resource capabilities even
  though the host operating system is otherwise supported.
- Temporary validation artifacts, package artifacts, and generated consumer
  projects are cleaned up safely on case-sensitive and case-insensitive file
  systems.
- Documentation or sample commands use path separators, shell names, quoting, or
  cleanup behavior that works on only one platform.
- Concurrent tests that pass on one scheduler expose timing or contention
  assumptions on the other platform.
- Restricted host environments block named shared-memory or same-host
  synchronization even though the operating system family is supported.
- Container resource limits are too small for the requested store capacity.
- Existing unsupported-platform statuses remain valid for platforms outside the
  Linux, Windows, and supported same-host Docker container support scope.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The package MUST support ordinary runtime store usage on Linux,
  Windows, and supported same-host Docker container deployments for valid
  create, open, publish, acquire, release, remove, reuse, reservation, segmented
  publish, diagnostics, recovery, and disposal workflows.
- **FR-002**: Valid Linux, Windows, and supported Docker container store
  creation or opening MUST NOT return unsupported-platform outcomes when
  required same-host capabilities are available.
- **FR-003**: The same public API, option model, status taxonomy, diagnostics
  model, ownership rules, and lifecycle rules MUST apply to Linux and Windows
  consumers unless a documented platform-specific limitation is explicitly tied
  to a non-ordinary scenario.
- **FR-004**: Cross-process store visibility MUST work on Linux and Windows for
  same-host participants using the same store name and compatible options.
- **FR-005**: Shared synchronization MUST provide equivalent mutual exclusion,
  timeout, cancellation, busy, abandoned-owner, and disposal-race outcomes on
  Linux and Windows.
- **FR-006**: Lease ownership and recovery MUST preserve active-reader safety on
  Linux, Windows, and supported same-host Docker container deployments and MUST
  report unsupported or unsafe owner decisions through documented public
  categories rather than reclaiming storage aggressively.
- **FR-007**: Reservation ownership, commit, abort, recovery, and retained-memory
  safety MUST follow the same public contract on Linux and Windows.
- **FR-008**: Diagnostics snapshots and failure counts MUST report the same
  categories and meanings on Linux and Windows for equivalent workloads.
- **FR-009**: Store names, resource cleanup, and process lifetime behavior MUST
  be documented and validated for both supported platforms.
- **FR-010**: The repository development workflow MUST be executable from clean
  Linux and Windows checkouts, including build, test, sample execution,
  documentation validation, package-consumption validation, and package
  creation.
- **FR-011**: Validation scripts and repository commands MUST use portable path
  handling, shell invocation, cleanup safety, and artifact locations or provide
  documented equivalent commands for Linux and Windows.
- **FR-012**: Samples MUST run successfully on Linux and Windows or explicitly
  skip only scenarios that are outside the supported ordinary runtime scope.
- **FR-013**: Package-consumption validation MUST prove that a clean consumer can
  install or reference the package and complete the documented first-use and
  advanced workflows on Linux, Windows, and supported same-host Docker container
  deployments.
- **FR-014**: Automated tests MUST cover Linux, Windows, and supported Docker
  container behavior for public API contracts, cross-process visibility,
  synchronization contention, owner recovery, reservation recovery, disposal
  races, long-running reuse, diagnostics, and package consumption.
- **FR-015**: Public documentation MUST replace Windows-first support wording
  with Linux-and-Windows support wording wherever the validated behavior changes.
- **FR-016**: Public documentation MUST clearly identify unsupported platforms,
  restricted host environments, cross-host scenarios, persistence expectations,
  security boundaries, and any platform-specific limitations that remain.
- **FR-017**: Release notes and package metadata MUST state Linux, Windows, and
  supported Docker container runtime and development support accurately for the
  release that delivers this feature.
- **FR-018**: The feature MUST preserve existing public contracts for Windows
  consumers except where a documented compatibility change is required to make
  Linux and Windows behavior consistent.
- **FR-019**: The feature MUST preserve the shared-memory layout and public data
  semantics expected by future language implementations unless a deliberate
  compatibility change is documented and approved.
- **FR-020**: Failures caused by missing platform capabilities, permissions, or
  restricted host policies MUST be reported through documented outcomes that
  allow consumers to distinguish environment problems from invalid options or
  data corruption.
- **FR-021**: Cross-container store visibility MUST work between supported
  Docker containers on the same host when they use the same store name,
  compatible options, and required shared-resource capabilities.
- **FR-022**: Containerized producers and readers MUST observe the same lease,
  removal, reuse, reservation, diagnostics, recovery, and disposal contracts as
  non-container same-host participants.
- **FR-023**: Container configurations that isolate required shared-memory,
  synchronization, owner-liveness, permission, or cleanup capabilities MUST
  produce documented environment or unsupported-capability outcomes.
- **FR-024**: Public documentation and samples MUST include a Docker container
  validation path that demonstrates cross-container publish, acquire, release,
  cleanup, diagnostics, and recovery behavior.

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: Linux, Windows, and supported same-host Docker container
  deployments MUST be documented as first-class supported runtime and
  development targets for the package version that ships this feature.
- **LC-002**: Any public behavior that differs between Linux, Windows, and
  supported same-host Docker container deployments MUST be explicitly
  documented, tested, and justified as a platform or deployment limitation rather
  than an accidental gap.
- **LC-003**: Existing Windows consumers SHOULD observe compatible behavior for
  successful workflows, status meanings, diagnostics categories, memory lifetime,
  and package consumption.
- **LC-004**: If platform support requires changing public options, statuses,
  diagnostics, naming rules, or recovery reports, the change MUST include
  semantic-version review, migration guidance, contract tests, and release notes.
- **LC-005**: The same shared-memory layout, state values, key semantics, slot
  lifecycle rules, lease rules, reservation rules, and diagnostics meanings MUST
  apply to Linux and Windows.
- **LC-006**: Unsupported-platform outcomes remain part of the public contract
  for platforms outside Linux and Windows or environments that lack required
  same-host capabilities.
- **LC-007**: Public examples and package-consumption validation MUST represent
  the supported platform contract and MUST NOT rely on undocumented
  platform-specific behavior.
- **LC-008**: Docker container support MUST be documented as same-host
  shared-memory participation, not cross-host sharing, service discovery,
  persistence, orchestration, or distributed-cache behavior.
- **LC-009**: Container-specific setup requirements MUST be documented as
  deployment responsibilities and MUST NOT change the ordinary public store API
  unless semantic-version review approves a compatibility change.

### Key Entities *(include if feature involves data)*

- **Supported Platform**: An operating system family and environment where the
  package promises ordinary runtime and development workflows are validated and
  supported.
- **Platform Capability**: A same-host resource capability required for shared
  storage, synchronization, owner liveness, permissions, cleanup, and diagnostics
  to meet the public contract.
- **Container Participant**: A producer, reader, or maintenance process running
  inside a Docker container that participates in a same-host shared store.
- **Container Sharing Configuration**: The deployment conditions that allow
  separate Docker containers on the same host to see the same store resources and
  synchronization state.
- **Store Resource Identity**: The public store name and the platform-visible
  resources associated with it for same-host cross-process participation.
- **Cross-Platform Validation Matrix**: The supported-platform coverage record
  for runtime workflows, development workflows, samples, package consumption,
  reliability scenarios, and documentation checks.
- **Development Workflow**: The documented contributor path from clean checkout
  through validation and package creation on each supported platform.
- **Runtime Workflow**: A consumer-visible store scenario such as creation,
  publishing, reading, removal, reservation ingest, recovery, diagnostics, or
  disposal.
- **Platform Limitation**: A documented scenario where an environment prevents a
  supported-platform guarantee, with expected outcome and consumer action.
- **Container Limitation**: A documented container deployment condition that
  prevents cross-container shared-store participation, with expected outcome and
  consumer action.
- **Compatibility Note**: Documentation that explains whether platform-support
  work changes public behavior, package semantics, or migration requirements.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: On Linux and Windows, a clean consumer completes the documented
  package first-use workflow with successful create, publish, acquire, release,
  remove, reuse, diagnostics, and disposal outcomes in under 10 minutes.
- **SC-002**: On Linux and Windows, the advanced package-consumption workflow
  completes reservation ingest, abort or recovery, segmented publish, and read
  validation with 100% matching documented status categories.
- **SC-003**: Cross-process integration tests pass on Linux and Windows for same
  store visibility, multiple readers, removal with active leases, republish after
  release, and incompatible open attempts.
- **SC-004**: Cross-container integration tests pass between at least two Docker
  containers on the same host for same store visibility, multiple readers,
  removal with active leases, republish after release, diagnostics, explicit
  recovery, and at least 10,000 cross-container publish/acquire/release/remove
  cycles.
- **SC-005**: Synchronization contention tests prove that bounded waits,
  cancellation, busy outcomes, and disposal races return documented results on
  Linux, Windows, and supported same-host Docker container deployments within
  the caller-selected limit plus 250 milliseconds.
- **SC-006**: Recovery tests cover at least 10,000 stale-owner or
  current-owner recovery cycles on Linux, Windows, and supported same-host
  Docker container deployments with zero premature reuse of storage protected by
  an active live-owner lease.
- **SC-007**: Long-running reuse and churn validation completes at least
  1,000,000 publish, acquire, release, remove, reserve, commit, abort, or reuse
  operations on Linux and Windows without stale handle acceptance, layout
  corruption, or undocumented failures.
- **SC-008**: The full repository validation workflow completes from clean Linux
  and Windows checkouts with zero platform-specific command, path, shell,
  cleanup, package, or sample failures.
- **SC-009**: Public documentation review finds zero remaining claims that Linux
  or supported same-host Docker container usage is unsupported, unvalidated,
  future-only, or outside ordinary runtime and development support for the
  shipping package version.
- **SC-010**: Package metadata and release notes identify Linux, Windows, and
  supported Docker container support, remaining unsupported scenarios, and any
  compatibility impact in a form a consumer can understand in under 10 minutes
  without reading source code.
- **SC-011**: Existing Windows runtime, contract, package-consumption, sample,
  and documentation validation remains passing after Linux and Docker support are
  added.

## Assumptions

- Linux support means mainstream same-host Linux environments that provide the
  required shared-resource, synchronization, process, permission, and cleanup
  capabilities for ordinary package usage.
- Windows support remains in scope and must not regress while Linux support is
  added.
- Docker container support means Linux-based same-host Docker containers
  configured so cooperating participants can access the same required
  shared-memory and synchronization capabilities. Windows containers and
  cross-host container sharing remain out of scope for this feature.
- macOS, cross-host sharing between different machines, distributed-cache
  behavior, persistence across machine restart, and malicious in-boundary
  writers remain out of scope unless a later feature explicitly adds them.
- Restricted containers or locked-down hosts may prevent required same-host
  shared-resource capabilities; those cases must fail with documented
  environment outcomes rather than being treated as ordinary support failures.
- Future C++ and Python audiences remain portability considerations, not
  delivered bindings, unless a later feature explicitly adds them.
- Planning may choose the smallest implementation strategy that provides the
  public Linux and Windows guarantees while preserving package contracts and
  dependency discipline.
