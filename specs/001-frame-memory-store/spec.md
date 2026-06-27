# Feature Specification: Shared Memory Value Store

**Feature Branch**: `001-frame-memory-store`

**Created**: 2026-06-26

**Status**: Draft

**Input**: User description: "Develop SharedMemoryStore as a general reusable
library for high-rate shared-memory data exchange. The first production use case
is a computation server that receives frames of about 1.3 MB each, where each
frame contains header, metadata, and binary data. The core library must store
keyed binary values in shared memory with zero allocations, provide very fast
access for other services, track usage with reference counts or leases, remove
values cleanly, and reuse memory without allocations. Start with C# and support
future C++ and Python clients."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Publish Values at High Rate (Priority: P1)

A producer publishes binary values into a shared memory store so that each value
is available by key immediately after publication without per-value runtime heap
allocation after initialization.

**Why this priority**: Publishing keyed values is the core capability of the
library. If data cannot enter shared memory predictably, downstream processing
services cannot rely on the store.

**Independent Test**: Initialize a store with enough capacity, publish a
sequence of 1.3 MB values with unique keys, and verify each value becomes
available with matching bytes and optional descriptor data while reporting no
per-value runtime heap allocation after warm-up.

**Acceptance Scenarios**:

1. **Given** an initialized store with free capacity, **When** a producer
   publishes a valid binary value with a unique key, **Then** the value is
   visible by that key and its bytes and descriptor match the submitted data.
2. **Given** the store has completed initialization and warm-up, **When** a
   producer publishes a steady stream of valid values, **Then** publication does
   not allocate runtime heap memory per value and does not grow store memory
   beyond configured capacity.

---

### User Story 2 - Acquire Values from Processing Services (Priority: P2)

A processing service acquires a value by key and reads the value directly from
shared memory while the store prevents that memory from being reused until the
service releases its lease.

**Why this priority**: The store is useful only when independent services can
read shared data quickly and safely without copying payload bytes into private
memory.

**Independent Test**: Publish a value, acquire it from multiple simulated
services, verify all readers observe identical bytes, release each reader, and
verify the value remains protected until the final release.

**Acceptance Scenarios**:

1. **Given** a value exists in the store, **When** a service acquires it by key,
   **Then** the service receives a valid read lease and the active usage count
   increases.
2. **Given** several services hold read leases for the same value, **When** one
   service releases its lease, **Then** the value remains available for the
   remaining services and is not eligible for memory reuse.

---

### User Story 3 - Remove Values and Reuse Memory (Priority: P3)

A producer or store owner removes values that are no longer needed, and the
store reuses released memory for future values without allocating new value
storage.

**Why this priority**: High-rate data streams require bounded memory. The store
must cleanly recycle memory or it will not be usable in long-running production
systems.

**Independent Test**: Publish values until the store has used multiple storage
slots, remove values after readers release them, publish new values, and verify
released slots are reused without increasing memory usage.

**Acceptance Scenarios**:

1. **Given** a value has no active readers, **When** the value is removed,
   **Then** its key is no longer resolvable and its storage is available for a
   future value.
2. **Given** a value is marked for removal while readers still hold leases,
   **When** the final reader releases its lease, **Then** the value is removed
   and its storage becomes reusable.

---

### User Story 4 - Use Frame Data as a Store Value (Priority: P4)

A computation server stores each incoming frame as one shared-memory value, with
the frame's header, metadata, and payload represented by the consumer's chosen
value layout.

**Why this priority**: Frames are the first important production use case, but
they must not force the core library to become frame-specific.

**Independent Test**: Publish frame-shaped values of about 1.3 MB, have multiple
services acquire and read them, release all leases, remove the values, and
verify memory reuse without requiring frame-specific store operations.

**Acceptance Scenarios**:

1. **Given** a producer has a frame containing header, metadata, and payload,
   **When** it publishes the frame as a binary value with descriptor data,
   **Then** processing services can acquire the value and interpret the frame
   layout outside the core store.
2. **Given** a non-frame payload is published with the same store operations,
   **When** a consumer acquires it by key, **Then** the store provides the same
   lease, lifetime, and memory reuse behavior as it does for frame-shaped data.

---

### User Story 5 - Consume as a General Library (Priority: P5)

A developer installs the library package into another project and uses
documented operations to create or open a store, publish values, acquire values,
release leases, remove values, and observe failure conditions.

**Why this priority**: The project is intended to become shared production
infrastructure, not a one-off component embedded in one computation server.

**Independent Test**: Create a clean consumer project, install the package, run
the documented example, and verify the example publishes, acquires, releases,
removes, and reuses value storage successfully.

**Acceptance Scenarios**:

1. **Given** a clean consumer project, **When** the package is installed and the
   basic usage example is run, **Then** the example completes without requiring
   implementation internals.
2. **Given** a consumer encounters a full store, invalid key, missing value, or
   release error, **When** the documented operation is performed, **Then** the
   library returns a deterministic documented result or error.

### Edge Cases

- A value is larger than the configured maximum value size.
- A key is duplicated while the previous value is still present.
- A service tries to acquire a missing, removed, or expired key.
- A value is removed while one or more services still hold read leases.
- A service releases the same lease more than once.
- The store reaches configured capacity while new values continue arriving.
- Value length, descriptor length, or stored layout information is inconsistent
  with the submitted data.
- Multiple producers or consumers operate concurrently on the same key or on
  adjacent storage slots.
- A process holding a lease terminates or disconnects before releasing it.
- The target platform does not provide the required shared-memory capability or
  denies access to the shared memory region.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Library MUST allow a consumer to create or open a named shared
  memory store with bounded capacity and documented maximum value size limits.
- **FR-002**: Library MUST store opaque binary values as key-value entries where
  each key resolves to one complete stored value.
- **FR-003**: Library MUST allow each value to include optional descriptor data
  so consumers can describe layouts such as header, metadata, and payload
  without making that layout part of the core store model.
- **FR-004**: Library MUST support values of at least 1.3 MB when configured
  with sufficient capacity.
- **FR-005**: Library MUST avoid runtime heap allocation per publish, acquire,
  release, remove, and reuse operation after initialization and warm-up.
- **FR-006**: Library MUST expose value bytes to readers without copying the
  value payload into reader-owned memory.
- **FR-007**: Library MUST keep published value contents immutable for readers
  until the value is removed and storage is reused.
- **FR-008**: Library MUST increment value usage when a reader acquires a lease
  and decrement value usage when the reader releases it.
- **FR-009**: Library MUST prevent storage reuse for a value while its usage
  count is greater than zero.
- **FR-010**: Library MUST support removing a value by key and reusing its
  storage after no readers hold the value.
- **FR-011**: Library MUST provide deterministic outcomes for duplicate keys,
  missing keys, oversized values, full store capacity, invalid releases, and
  unsupported platforms.
- **FR-012**: Library MUST support concurrent producer and consumer activity
  without data corruption, use-after-release, or usage count underflow.
- **FR-013**: Library MUST provide consumer-controlled diagnostics for store
  state, operation failures, capacity pressure, and unreleased leases without
  writing directly to the console.
- **FR-014**: Library MUST document the lifecycle responsibilities for store
  owners, producers, and readers, including cleanup after normal and abnormal
  termination.
- **FR-015**: Library MUST include package metadata, public documentation, and
  runnable examples for creation, publication, acquisition, release, removal,
  and memory reuse.

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: Public API surface MUST describe operations for creating/opening a
  store, publishing a value, acquiring a value by key, releasing a value lease,
  removing a value, observing store capacity, and disposing resources.
- **LC-002**: NuGet packaging impact is an initial public package contract for
  SharedMemoryStore, including package identity, versioning, XML documentation,
  release notes, and a clean-project consumption example.
- **LC-003**: Semantic version impact is major for the first stable public
  contract if released as 1.0.0; before that release, any breaking change MUST
  still be documented in feature notes and migration notes.
- **LC-004**: Future C++ and Python portability considerations MUST include a
  language-neutral value layout description, key rules, lifecycle semantics,
  lease/reference counting rules, error taxonomy, and memory ownership contract.
- **LC-005**: Diagnostics and resource ownership expectations MUST state which
  party owns store lifetime, value lifetime, read leases, removal decisions,
  stale-lease handling, and cleanup after process exit.
- **LC-006**: Frame-specific header, metadata, and payload interpretation MUST
  remain outside the core store contract unless introduced later as a separate
  adapter or helper package.

### Key Entities *(include if feature involves data)*

- **Shared Memory Store**: A bounded named memory region that stores keyed value
  entries and tracks capacity, keys, usage counts, and reusable slots.
- **Value Entry**: One stored binary value containing a key, payload bytes,
  optional descriptor data, size information, lifecycle state, and usage count.
- **Store Key**: A unique identifier used by producers and services to locate a
  value while it remains in the store.
- **Value Lease**: A reader's temporary access token for a value. Holding a
  lease keeps the value storage protected from reuse.
- **Reusable Slot**: A portion of store capacity that can hold one value entry
  and can return to the free pool after removal and release.
- **Value Descriptor**: Optional consumer-defined metadata that describes how to
  interpret the value bytes without changing store ownership or lifecycle rules.
- **Store Owner**: The consumer responsible for creating the store, configuring
  capacity, and coordinating lifecycle cleanup.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After initialization and warm-up, publish, acquire, release,
  remove, and reuse operations report 0 bytes of runtime heap allocation per
  operation in a documented steady-state benchmark.
- **SC-002**: The store sustains at least 500 publishes per second for 1.3 MB
  values for 60 seconds on documented local benchmark hardware without data
  corruption or memory growth beyond configured capacity.
- **SC-003**: In a benchmark with one producer and four concurrent readers,
  100,000 publish/acquire/release/remove cycles complete with no usage count
  underflow, leaked active leases, or use-after-release detection failures.
- **SC-004**: After one million publish/remove/reuse cycles, total committed
  store memory remains within 1% of the configured capacity plus documented
  fixed overhead.
- **SC-005**: A full store, oversized value, duplicate key, missing key, invalid
  release, or unsupported platform returns a documented outcome with p95 latency
  of 1 ms or less, and reports maximum observed latency, in the steady-state
  benchmark.
- **SC-006**: A clean consumer project can install the package and run the basic
  publish/acquire/release/remove example in under 5 minutes using only public
  documentation.
- **SC-007**: A frame-shaped value containing header, metadata, and payload can
  be stored, acquired by multiple readers, released, removed, and have its
  storage reused using only the general value-store contract.
- **SC-008**: The public contract documentation identifies every lifecycle,
  memory ownership, compatibility, and future C++/Python portability rule needed
  by a non-.NET client implementer.

## Assumptions

- The initial consumer services run on the same host and communicate through
  shared memory rather than network transport.
- The first implementation is a C#/.NET 10 NuGet package; C++ and Python
  clients are future work but their compatibility needs influence the public
  contract now.
- The first production benchmark uses frame-shaped values of about 1.3 MB, but
  frames are not a core store concept.
- Value contents are immutable once published. Updating data means publishing a
  new value entry under an allowed key.
- Store capacity and maximum value size are configured by the consumer during
  store creation.
- The first production use case operates within a trusted service boundary; OS
  permissions and process isolation are handled by deployment policy.
- Direct producer writing into reserved store memory may be considered during
  planning, but this specification only requires allocation-free steady-state
  publication and reader access without payload copies.
- Exact benchmark hardware and release thresholds may be tightened during
  planning, but the allocation, bounded memory, lease/reference counting, and
  reuse outcomes are mandatory.
