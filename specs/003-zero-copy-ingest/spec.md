# Feature Specification: Zero-Copy Frame Ingest

**Feature Branch**: `003-zero-copy-ingest`

**Created**: 2026-06-27

**Status**: Draft

**Input**: User description: "I'm using System.IO.Piplines to read from a socket. I have a defined wire protocol. I was used MemoryPool to avoid allocations and copying. Now I need to access the data from different processes so I decide to use shared memory. Please design a way to allow me to use the store efficiently. Performance is most important."

## Clarifications

### Session 2026-06-27

- Q: When is the complete frame payload length known? -> A: Frame header gives the complete payload length before payload bytes are read.
- Q: How are stale reservations reclaimed after producer failure? -> A: Stale reservations are reclaimed only by an explicit recovery operation.
- Q: When are descriptor bytes known for direct ingest? -> A: Descriptor bytes are known and fixed when the reservation is created.
- Q: What trust boundary applies to processes that open and use the store? -> A: Only trusted same-host services can open and use the store.
- Q: What level of pipeline/socket integration is required in this feature? -> A: Core reservation API is required; pipeline/socket usage is documented with examples.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Ingest Frames Directly into Shared Memory (Priority: P1)

A producer reads length-delimited frames from a socket protocol, derives the
complete payload length and descriptor bytes from the frame header, and places
each complete frame into store-owned shared memory without first building a
separate payload buffer.

**Why this priority**: This is the primary performance requirement. The store is
not efficient enough for high-rate socket ingestion if producers must allocate
or copy a full frame into an intermediate buffer before publication.

**Independent Test**: A producer reads a stream of length-delimited frames using
the documented socket or pipeline example, reserves store capacity for each
announced frame length, fills the reserved storage, commits the frame, and
verifies readers can acquire the committed frame with matching bytes while the
producer reports no per-frame payload allocation or application-level payload
copy before publication.

**Acceptance Scenarios**:

1. **Given** a valid frame header that announces a frame length within store
   limits and supplies fixed descriptor bytes, **When** the producer reserves
   storage, fills exactly the announced payload bytes, and commits the
   reservation, **Then** the frame becomes visible by key as one complete
   immutable value.
2. **Given** a frame is being filled in reserved storage, **When** another
   process tries to acquire the frame key before commit, **Then** the frame is
   not visible and no partial bytes are exposed.
3. **Given** the producer has completed store warm-up, **When** it ingests a
   steady stream of valid frames, **Then** each frame can be published without a
   per-frame payload allocation in producer-owned memory.

---

### User Story 2 - Publish Already Buffered Segmented Frames Efficiently (Priority: P2)

A producer that already received frame data in segmented buffers can publish the
frame without flattening those segments into a temporary contiguous array.

**Why this priority**: Some socket readers expose data as multiple segments.
Those producers still need an efficient path even when direct store-backed
receive is not possible for a particular frame or protocol stage.

**Independent Test**: Feed a frame split across multiple segments into the
publication path and verify the stored value matches the concatenated frame
bytes while allocation tracking shows no temporary full-frame array.

**Acceptance Scenarios**:

1. **Given** a valid frame is available across multiple read segments, **When**
   the producer publishes it by key, **Then** the store writes the frame into one
   committed value without requiring a full-frame temporary allocation.
2. **Given** a frame is available in one contiguous read segment, **When** the
   producer publishes it through the same workflow, **Then** the frame is stored
   with no extra producer allocation and remains readable by other processes.

---

### User Story 3 - Abort and Recover Incomplete Frame Writes (Priority: P3)

A producer can abandon a reservation when a socket read fails, a protocol frame
is malformed, or the process shuts down, and the store can reclaim the reserved
capacity safely.

**Why this priority**: Direct writes into store-owned memory are only safe for
production when incomplete writes cannot leak capacity or become visible as
corrupt frames.

**Independent Test**: Reserve capacity, write a partial frame, abort the
reservation, and verify the key is not visible, capacity returns to the free
pool, diagnostics record the abort, and a later valid frame can reuse the
storage.

**Acceptance Scenarios**:

1. **Given** a producer has an active reservation, **When** it aborts before
   commit, **Then** the key is not visible to readers and the reserved storage
   becomes reusable.
2. **Given** a producer exits or disposes a reservation without committing,
   **When** an owner invokes explicit stale reservation recovery according to
   documented lifecycle rules,
   **Then** the incomplete value is not exposed and recoverable capacity is
   reclaimed.
3. **Given** a producer attempts to commit after an abort, failed validation, or
   disposal, **When** it completes the operation, **Then** the store returns a
   deterministic failure and does not publish the frame.

---

### User Story 4 - Preserve Reader Safety and Existing Store Workflows (Priority: P4)

Existing consumers can continue using the byte-oriented publish and read-lease
workflow, while high-performance producers use the new ingest workflow without
weakening reader protection or removal semantics.

**Why this priority**: The store is already a reusable value store. Efficient
ingest must be additive and compatible with current immutable value, acquire,
release, remove, and reuse behavior.

**Independent Test**: In one store, publish values through both the existing
workflow and the ingest workflow, acquire them from multiple readers, remove
them while readers hold leases, and verify all values remain protected until the
final release.

**Acceptance Scenarios**:

1. **Given** a frame was committed through the ingest workflow, **When** multiple
   processes acquire it, **Then** each reader observes identical immutable bytes
   until its lease is released.
2. **Given** a committed frame is removed while readers still hold leases,
   **When** the final reader releases the frame, **Then** the storage becomes
   reusable and is not reused earlier.
3. **Given** an existing consumer uses the current simple publish workflow,
   **When** the ingest feature is added to the package, **Then** that consumer's
   documented behavior does not change.

### Edge Cases

- A frame header announces a length larger than the configured maximum value
  size.
- A frame header announces a valid length, but the socket stream ends before the
  full frame arrives.
- A producer writes fewer bytes or more bytes than the reserved frame length.
- A producer reserves storage and then aborts, disposes, or exits before commit.
- A producer attempts to commit the same reservation more than once.
- A producer attempts to abort after a successful commit.
- A key is duplicated while a committed value exists or while another producer
  has a pending reservation for that key.
- The store reaches configured capacity while one or more reservations are
  pending.
- Readers attempt to acquire a key whose reservation has not committed yet.
- Removal is requested for a committed frame while readers hold active leases.
- Multiple producers reserve, fill, commit, abort, and remove different frames
  concurrently.
- A frame arrives across many non-contiguous read segments.
- Descriptor or protocol metadata is missing, too large, or inconsistent with
  the announced payload length before reservation.
- Unsupported platforms, access denial, mapping failures, stale reservations,
  and abandoned process state occur during high-rate ingestion.
- A process outside the trusted same-host service boundary attempts to open or
  mutate the store; deployment access control is responsible for preventing this
  scenario.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Library MUST let a producer reserve bounded store capacity for one
  future value using a key, payload length, and fixed optional descriptor bytes
  before the value is visible to readers.
- **FR-002**: Library MUST expose the reserved payload storage to the producer as
  writable store-owned memory so the producer can fill the value without first
  allocating a full-frame payload buffer outside the store.
- **FR-003**: Library MUST keep reserved storage invisible to readers until the
  producer explicitly commits the reservation.
- **FR-004**: Library MUST publish a committed reservation atomically so readers
  observe either no value for the key or the complete committed value, never a
  partially written value.
- **FR-005**: Library MUST allow a producer to abort a reservation and reclaim
  the reserved capacity without making the partially written bytes visible.
- **FR-006**: Library MUST reclaim or report incomplete reservations through an
  explicit recovery operation when a producer fails to commit or abort normally.
- **FR-007**: Library MUST support publishing a frame already available across
  multiple read segments without requiring a temporary contiguous full-frame
  allocation.
- **FR-008**: Library MUST support the direct ingest path for frames whose
  header gives the complete payload length and descriptor bytes before payload
  bytes are read.
- **FR-009**: Library MUST preserve existing immutable value, acquire, release,
  remove, diagnostics, and slot reuse semantics for values committed through the
  ingest workflow.
- **FR-010**: Library MUST reject oversized values, invalid lengths, invalid
  keys, duplicate keys, full capacity, invalid reservation states, and
  unsupported platform conditions with deterministic documented outcomes.
- **FR-011**: Library MUST avoid per-frame runtime heap allocation in the
  steady-state direct ingest path after initialization and warm-up.
- **FR-012**: Library MUST avoid per-frame temporary full-payload allocation in
  the segmented publish path after initialization and warm-up.
- **FR-013**: Library MUST provide consumer-controlled diagnostics for active
  reservations, committed frames, aborted reservations, failed commits, capacity
  pressure, stale or abandoned reservations, and allocation-sensitive ingest
  measurements.
- **FR-014**: Library MUST document producer responsibilities for reservation
  lifetime, exact byte counts, commit, abort, cleanup, and error handling.
- **FR-015**: Library MUST include examples that show a length-prefixed socket
  frame workflow, segmented buffered frame workflow, reader acquisition, removal,
  lease release, and reservation cleanup.
- **FR-016**: Library MUST document that direct writable reservations are
  intended for trusted same-host services and that deployment access control is
  responsible for preventing untrusted processes from opening or mutating the
  store.
- **FR-017**: Library MUST provide the core reservation contract as the required
  ingest capability and document socket and pipeline usage as examples or
  adapters over that contract.

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: Public API surface MUST describe the reservation lifecycle,
  writable payload access, fixed descriptor handling, commit behavior, abort
  behavior, segmented publish behavior, result statuses, diagnostics, disposal
  rules, and examples.
- **LC-002**: NuGet packaging impact is an additive package feature that updates
  package documentation, XML documentation, examples, release notes, and
  compatibility guidance.
- **LC-003**: Semantic version impact is minor while the package remains
  pre-1.0 because the feature adds public capabilities without changing
  documented behavior for existing simple publish and acquire workflows.
- **LC-004**: Future C++ and Python portability considerations MUST include a
  language-neutral reservation state machine, commit visibility rule, abort and
  recovery rules, binary layout compatibility, error outcomes, and memory
  ownership expectations.
- **LC-005**: Diagnostics and resource ownership expectations MUST state which
  party owns store lifetime, reservation lifetime, writable memory lifetime,
  commit and abort decisions, reader leases, removal decisions, stale
  reservation handling, and cleanup after process exit.
- **LC-006**: Any .NET-specific socket or pipeline convenience helpers MUST be
  adapters over the language-neutral store contract, not the definition of the
  core shared-memory behavior.
- **LC-007**: Existing byte-oriented publication MUST remain available and its
  documented result statuses and reader semantics MUST remain compatible.
- **LC-008**: Security and access-control documentation MUST state the trusted
  same-host service boundary for this feature and avoid implying protection
  against malicious writers inside that boundary.
- **LC-009**: Socket and pipeline guidance MUST remain layered over the
  language-neutral reservation contract; a first-class store-backed pipeline
  memory pool is out of scope for this feature.

### Key Entities *(include if feature involves data)*

- **Ingest Reservation**: A pending store entry that owns capacity for one key,
  one announced payload length, and fixed optional descriptor bytes before the
  value is visible to readers.
- **Writable Payload Region**: The reserved store-owned bytes a producer fills
  before commit. The region is valid only while its reservation is active.
- **Committed Frame Value**: A value produced from a successful reservation
  commit and then governed by the same immutable value and lease rules as other
  published store entries.
- **Segmented Frame Source**: A producer-visible frame representation split
  across multiple read segments that must be copied into one store value without
  a temporary full-frame allocation.
- **Reservation State**: The lifecycle state of a reserved entry, including
  pending, committed, aborted, failed, stale, and reclaimed outcomes.
- **Frame Descriptor**: Optional metadata known before payload bytes are read
  and associated with the committed value, such as protocol version, header
  fields, payload type, or application-specific interpretation rules.
- **Store Reader Lease**: A reader's temporary protection over a committed value
  that prevents storage reuse until release.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After initialization and warm-up, direct frame ingest reports 0
  bytes of per-frame runtime heap allocation for payload storage across at least
  100,000 frames in the documented allocation benchmark.
- **SC-002**: Direct frame ingest performs no application-level payload copy
  before publication when the frame length is known before the payload is read,
  as verified by benchmark instrumentation and code review of the public
  example.
- **SC-003**: Segmented frame publication stores frames split across at least 16
  segments without allocating a temporary full-frame array, and the stored bytes
  match the logical concatenation of all segments for 100% of test frames.
- **SC-004**: Readers never observe partial reservation contents across
  1,000,000 reserve/fill/commit/acquire cycles with concurrent readers and
  producers.
- **SC-005**: Aborted, disposed, failed, and stale reservations are never
  visible to readers and leave no unreclaimed capacity after explicit recovery
  completes in 100,000 failure-injection cycles.
- **SC-006**: The direct ingest benchmark sustains at least the same frame rate
  as the existing simple publish benchmark for 1.3 MB values, and records the
  relative throughput improvement or regression in release notes.
- **SC-007**: Existing simple publish, acquire, release, remove, and reuse tests
  continue to pass unchanged after the ingest feature is added.
- **SC-008**: A clean consumer project can run the documented socket-frame
  ingest example and a separate reader example in under 10 minutes using only
  public package documentation.
- **SC-009**: Diagnostics identify active reservation count, aborted reservation
  count, failed commit count, capacity pressure, and stale reservation recovery
  results without direct console output from library code.

## Assumptions

- Frame producers can determine the complete payload length and descriptor bytes
  from the wire protocol header before reading any payload bytes.
- The first version stores each committed frame as one contiguous value entry;
  scatter/gather values spanning multiple independently leased store regions
  are out of scope for this feature.
- The unavoidable operating-system transfer from a socket into user-visible
  memory is outside the store's copy count; this feature targets avoiding
  additional application-level payload buffers and copies before publication.
- Existing immutable value and read-lease semantics remain the reader contract
  after a frame is committed.
- Stale reservation cleanup is explicit and consumer-controlled; the library
  does not rely on background cleanup timers for this feature.
- The initial deployment model is trusted same-host services; defense against
  malicious writers that can open the shared memory store is out of scope for
  this feature.
- The current byte-oriented publish workflow remains supported for callers that
  already have a contiguous value span.
- This feature requires the core reservation API and documented socket or
  pipeline examples; a first-class store-backed pipeline memory pool may be
  evaluated later if benchmarks show the examples are insufficient.
- The first implementation targets the current .NET package, while core
  reservation states and shared-memory visibility rules are documented in a
  language-neutral way for future C++ and Python consumers.
- Performance validation uses the existing 1.3 MB frame-shaped workload as the
  primary benchmark unless planning identifies a stricter production workload.
