# Feature Specification: Native and Python Implementations

**Feature Branch**: `codex/cpp-python-implementations`

**Created**: 2026-07-10

**Status**: Draft

**Input**: User description: "Add interoperable Python and C++ implementations in the same repository, with a shared protocol boundary, and keep working until the complete cross-language feature works."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Exchange Values Across Runtimes (Priority: P1)

An application using any supported runtime can create or open a named store,
publish opaque keys, descriptors, and payloads, and exchange those values with
applications using either of the other supported runtimes on the same host.

**Why this priority**: Cross-runtime exchange is the central user value. A
second implementation that cannot participate in the same store would be a
separate product rather than an interoperable SharedMemoryStore participant.

**Independent Test**: Start one participant as the store creator and producer,
start a different participant as the consumer, and verify the consumer can
acquire, inspect, release, remove, and replace the value. Repeat for every
ordered producer-consumer pairing.

**Acceptance Scenarios**:

1. **Given** a store created by one supported runtime, **When** another runtime opens it with matching capacities, **Then** both participants observe the same store and values.
2. **Given** opaque keys and binary descriptors and payloads containing zero and non-text bytes, **When** one participant publishes them, **Then** every other participant reads the exact bytes without reinterpretation.
3. **Given** several participants racing to publish the same key, **When** their operations complete, **Then** exactly one succeeds and all others receive the documented duplicate outcome.

---

### User Story 2 - Use the Complete Store Lifecycle (Priority: P1)

Native and Python application developers can use the same create/open modes,
bounded waits, publication, acquisition, lease release, removal, reuse,
reservation, commit, abort, recovery, and status semantics already documented
for SharedMemoryStore.

**Why this priority**: Partial lifecycle support would create unsafe combinations
in which one participant can create shared state that another cannot manage or
recover correctly.

**Independent Test**: Run the public lifecycle contract suite against each
runtime independently, then run cross-runtime lease, removal, reservation, and
recovery scenarios.

**Acceptance Scenarios**:

1. **Given** a reader holding a lease, **When** another participant removes the key, **Then** removal remains pending until the lease is released and the slot is subsequently reusable.
2. **Given** an incomplete direct-write reservation, **When** the producer commits too early, aborts, or terminates, **Then** no partial value is exposed and an authorized participant can apply the documented recovery policy.
3. **Given** an unavailable shared lock, **When** a caller selects no-wait or a bounded wait, **Then** the operation returns the documented busy outcome within the selected bound.

---

### User Story 3 - Install and Use Each Distribution Independently (Priority: P2)

Developers can build, test, and consume the managed, native, and Python
distributions independently while still knowing which shared protocol versions
they can safely use together.

**Why this priority**: Each ecosystem has its own release and consumption
workflow, but those independent releases must not obscure interoperability.

**Independent Test**: Build each distribution from a clean checkout, consume it
from a minimal external sample, and verify that its advertised protocol version
matches the interoperability matrix.

**Acceptance Scenarios**:

1. **Given** a clean checkout with a supported toolchain, **When** a developer follows one distribution's documented build steps, **Then** only that distribution and its declared build dependencies are required.
2. **Given** independently versioned distributions, **When** a developer reviews compatibility information, **Then** the common on-memory protocol version is explicit and unambiguous.
3. **Given** a consuming application, **When** it uses a lease or writable reservation view, **Then** the distribution prevents or clearly detects use after release, abort, commit, recovery, or store closure.

---

### User Story 4 - Diagnose Capacity and Lifecycle State Consistently (Priority: P3)

Operators can inspect capacity, slot, lease, reservation, index-health, recovery,
and failure information without the libraries writing directly to application
output or starting hidden maintenance work.

**Why this priority**: Consistent diagnostics make mixed-runtime deployments
supportable without coupling the core store to a logging or hosting framework.

**Independent Test**: Produce known store states from multiple participants and
verify that diagnostics from every runtime report equivalent shared facts and
runtime-local failure information according to the documented contract.

**Acceptance Scenarios**:

1. **Given** published, pending-removal, and pending-reservation slots, **When** diagnostics are requested, **Then** every participant reports equivalent shared-state counts.
2. **Given** expected operation failures, **When** diagnostics are requested, **Then** failures are available to the caller and no library writes directly to console output.

### Edge Cases

- Empty keys, oversized keys, descriptors, or payloads return their documented
  outcomes without mutating the store.
- Store options whose calculated layout overflows or exceeds the supplied region
  are rejected before a mapping is used.
- Existing mappings with a different layout major version, record size, section
  offset, capacity, or unsupported state are rejected deterministically.
- Names containing Unicode, separators, punctuation, scope prefixes, or the
  maximum supported length resolve to the same platform resources in every
  implementation.
- Abrupt participant termination does not cause a live store to be deleted while
  another participant still owns it.
- Linux resource-owner records use the available process-start identity so PID
  reuse does not cause deletion of a region that still has a live owner.
- Slot generation rollover advances the full lifecycle identity and never makes
  an old lease or reservation valid again.
- Zero-length payloads and descriptors remain valid where the existing public
  contract permits them.
- Closing a store invalidates borrowed views without closing or corrupting other
  live participants.
- Unsupported operating systems and insufficient permissions produce documented
  outcomes rather than undefined behavior.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST deliver independently consumable managed,
  native, and Python distributions under one shared compatibility policy.
- **FR-002**: Every distribution MUST conform to one canonical, versioned
  on-memory layout, byte order, alignment, numeric state assignment, hashing,
  probing, lifecycle identity, and visibility contract.
- **FR-003**: Every distribution MUST resolve a public store name to mutually
  compatible platform mapping, synchronization, ownership, and lifecycle
  resources on supported operating systems.
- **FR-004**: Native and Python consumers MUST be able to calculate required
  capacity and create, create-or-open, or open an existing store with the same
  validation and deterministic open outcomes as existing consumers.
- **FR-005**: Native and Python consumers MUST support publish, acquire, byte
  access, lease release, remove, and slot reuse with status semantics equivalent
  to the existing public contract.
- **FR-006**: Native and Python consumers MUST support announced-length writable
  reservations, progress advancement, exact commit, abort, and invalidation of
  completed reservation views.
- **FR-007**: Native and Python consumers MUST support segmented publication as
  one contiguous committed value without exposing partially copied data.
- **FR-008**: Every distribution MUST preserve pending-removal semantics while
  leases exist and reclaim the slot only after the final valid release.
- **FR-009**: Every distribution MUST support explicit, policy-controlled stale
  lease and reservation recovery without reclaiming records owned by another
  live participant.
- **FR-010**: Every distribution MUST expose caller-controlled bounded and
  no-wait synchronization outcomes and MUST NOT introduce hidden retry workers.
- **FR-011**: Every distribution MUST expose diagnostics covering shared
  capacity and lifecycle facts plus its own observed failures, without direct
  console output or a required telemetry framework.
- **FR-012**: Borrowed payload, descriptor, and writable reservation memory MUST
  remain valid only for the documented lifetime of its owning lease,
  reservation, and store handle.
- **FR-013**: The existing managed public API and behavior MUST remain compatible
  unless a separately reviewed semantic-version change is documented.
- **FR-014**: Distribution versions MAY advance independently, but each release
  MUST declare the shared protocol versions it can open and produce.
- **FR-015**: Automated conformance tests MUST pin exact record sizes, field
  offsets, state and status numbers, layout arithmetic, hash vectors, resource
  naming vectors, and representative binary fixtures.
- **FR-016**: Automated interoperability tests MUST cover every ordered
  producer-consumer pairing and mixed-runtime lease, removal, reservation,
  contention, owner-lifecycle, and recovery scenarios on each supported host
  platform.
- **FR-017**: Each distribution MUST include a minimal runnable example and a
  clean-consumer validation path.
- **FR-018**: Runtime libraries MUST avoid global mutable configuration, hidden
  background work, direct console output, and undeclared runtime dependencies.
- **FR-019**: The security documentation MUST preserve the trusted same-host
  participant boundary and MUST NOT imply protection from malicious writers
  that can legitimately access the shared resources.
- **FR-020**: Cross-host sharing, persistence guarantees, application-schema
  parsing, and distributed-cache behavior MUST remain outside this feature.

### Key Entities

- **Shared Protocol Version**: The major and minor compatibility identity for
  mapped bytes, state transitions, hashing, synchronization participation, and
  resource ownership behavior.
- **Distribution**: One independently versioned consumable package exposing the
  store contract to a supported application runtime.
- **Store Handle**: A process-local owner of one mapped-region view and its
  synchronization and lifecycle resources.
- **Lease**: A bounded-lifetime read capability tied to one slot lifecycle
  identity and one active shared lease record.
- **Reservation**: A bounded-lifetime write capability for an announced payload,
  tied to one pending slot lifecycle identity.
- **Conformance Fixture**: A versioned set of canonical values and mapped bytes
  used to prove that implementations interpret the protocol identically.
- **Compatibility Matrix**: The declared relationship between independently
  released distribution versions and supported shared protocol versions.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All nine ordered producer-consumer combinations exchange at least
  1,000 values containing arbitrary key, descriptor, and payload bytes without
  a byte mismatch or undocumented status.
- **SC-002**: Every supported participant passes 100% of the shared conformance
  vectors for layout, hashing, resource naming, lifecycle, and status semantics.
- **SC-003**: Mixed-participant contention tests complete within the
  caller-selected wait limit plus 250 milliseconds in at least 99.9% of 10,000
  bounded-wait operations.
- **SC-004**: Each supported host environment completes 10,000 mixed-participant
  removal, final-release, reservation-abort, and recovery cycles without stale
  handle acceptance, partial-value visibility, or live-resource deletion.
- **SC-005**: A developer can build and run the basic example for any one
  distribution from a clean checkout by following that distribution's
  documentation, with every required dependency and command stated explicitly.
- **SC-006**: Existing managed contract, unit, integration, sample, documentation,
  package-consumption, build, test, and pack validations remain passing.
- **SC-007**: Reviewers can determine whether any two released distributions are
  interoperable from one compatibility matrix without comparing source code.

## Assumptions

- The implementations participate in the same named store and are not merely
  separate APIs with similar behavior.
- Linux and Windows remain the supported same-host targets. Linux-based
  same-host containers remain a deployment profile rather than a new protocol.
- The existing layout major version 1, minor version 2 is the initial shared
  baseline; this feature does not intentionally alter its mapped record layout.
- Lease and reservation recovery preserves the current layout-v1.2 PID-based
  liveness semantics; adding a process-start identity to those records requires
  a separately versioned layout change.
- Native and Python distributions may expose ecosystem-appropriate names and
  lifetime constructs while preserving equivalent outcomes and byte semantics.
- Python consumption may rely on a native component shipped with or built for
  the Python distribution; a separately maintained copy of the state machine is
  not required.
- Build-only tooling is allowed when documented and license-compatible, but the
  runtime dependency surface remains minimal and deliberate.
- Performance claims remain bounded to measured same-host scenarios and do not
  promise persistence, cross-host sharing, or resistance to malicious in-scope
  writers.
