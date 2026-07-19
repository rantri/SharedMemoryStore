# Feature Specification: Lock-Free-Only Multi-Language Store

**Feature Branch**: `codex/010-lock-free-only-multilang`

**Created**: 2026-07-16

**Status**: Draft

**Input**: User description: "Remove the legacy layout, commit to the lock-free
layout, and provide one interoperable protocol implemented by C#, C++, and
Python. Run the complete Spec-Kit flow and keep working until the full test suite
passes."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Open One Canonical Store from Any Runtime (Priority: P1)

An application developer can create a named store from any supported
distribution and open that same store from either of the other distributions
without selecting a profile or reasoning about multiple mapped layouts.

**Why this priority**: A single protocol is the central product decision. If a
runtime creates a different store or requires a compatibility profile, the
repository still contains multiple products rather than one interoperable one.

**Independent Test**: Create a store with each distribution in turn, open it
from all three distributions, and prove that every handle reports the same
protocol identity, capacities, and lifecycle state without a profile option.

**Acceptance Scenarios**:

1. **Given** no mapping exists, **When** any supported distribution creates a
   store, **Then** every supported distribution can open the same physical
   store using the same public name and matching capacities.
2. **Given** a mapping uses the retired layout, **When** a current distribution
   attempts to open it, **Then** the attempt fails deterministically before any
   payload projection or mutation.
3. **Given** an ordinary options helper is used without an implementation
   selector, **When** required capacity is calculated and the store is opened,
   **Then** the canonical lock-free protocol is used.

---

### User Story 2 - Exchange Values and Lifetimes Across Runtimes (Priority: P1)

Publishers and readers using different supported distributions can exchange
opaque keys, descriptors, and payloads while preserving zero-copy leases,
exclusive direct-write reservations, logical removal, and bounded slot reuse.

**Why this priority**: Byte exchange alone is insufficient; safe reservation,
lease, and removal lifetimes are the core value of the package.

**Independent Test**: Run every ordered producer-consumer pair and reverse
mutation ownership through publish, reserve/commit, acquire/release,
remove/final-release, and republish while checking exact bytes and statuses.

**Acceptance Scenarios**:

1. **Given** arbitrary binary key, descriptor, and payload bytes published by
   one runtime, **When** another runtime acquires the key, **Then** it observes
   exactly the committed bytes and can safely release the lease.
2. **Given** a writable reservation owned by one runtime, **When** it is partly
   filled, advanced, and committed, **Then** no runtime observes the value
   before exact completion and all runtimes observe it afterward.
3. **Given** one or more foreign leases are active, **When** another runtime
   removes the key, **Then** new acquires fail, existing borrowed views remain
   valid, and the slot is reclaimed only after the final valid release.
4. **Given** multiple runtimes race to publish the same key, **When** all calls
   finish, **Then** exactly one value becomes visible and every other outcome
   is from the documented non-corrupt result set.

---

### User Story 3 - Survive Contention, Pauses, and Crashes (Priority: P1)

Healthy participants continue making bounded progress when unrelated
participants contend, pause, close, or terminate, and an owner can explicitly
recover only state proven abandoned.

**Why this priority**: Implementations that share bytes but disagree about
atomic transitions or owner identity can corrupt later generations and are not
safe interoperability partners.

**Independent Test**: Pause or terminate each runtime at every documented
publication, directory, lease, removal, and recovery transition while other
runtimes continue on the same and unrelated keys, then recover and reuse all
eligible capacity.

**Acceptance Scenarios**:

1. **Given** one participant is paused during a same-key mutation, **When**
   healthy participants help or finish the operation, **Then** the paused
   participant cannot mutate a later slot generation after resuming.
2. **Given** a participant terminates with a reservation or lease record,
   **When** explicit recovery proves its exact incarnation abandoned, **Then**
   eligible capacity is restored without exposing partial data or reclaiming a
   live owner.
3. **Given** a caller selects no-wait or a finite wait, **When** contention,
   capacity, or recovery uncertainty prevents success, **Then** the call returns
   a documented outcome within the selected bound and leaks no ownership.
4. **Given** one participant is suspended, **When** healthy participants operate
   on unrelated keys while capacity permits, **Then** they continue without a
   process-owned store-wide operation lock.

---

### User Story 4 - Consume and Diagnose Each Distribution Independently (Priority: P2)

Developers can build, install, and consume the managed, native, and Python
distributions independently, and operators can obtain equivalent protocol,
capacity, lifecycle, retry, help, and recovery diagnostics from each one.

**Why this priority**: Independent packaging and caller-controlled diagnostics
are required for the protocol to be usable outside this repository.

**Independent Test**: Build and install each distribution from a clean checkout,
run its minimal external consumer, and compare diagnostics over shared states
created by each of the other distributions.

**Acceptance Scenarios**:

1. **Given** a clean supported host, **When** a developer follows one
   distribution's documented build and install path, **Then** the example runs
   without relying on source-tree loading or undeclared runtime dependencies.
2. **Given** known free, reserved, published, leased, pending-removal, and
   recovery states, **When** diagnostics are requested from each distribution,
   **Then** shared facts agree and runtime-local counters are clearly identified.
3. **Given** a lease, reservation, or store has ended, **When** its borrowed view
   is used again, **Then** the distribution prevents or clearly rejects the
   invalid lifetime rather than silently accessing reused mapped memory.

### Edge Cases

- Empty keys and oversized keys, descriptors, payloads, participant tables, or
  store dimensions are rejected without poisoning the mapping.
- An existing retired-layout mapping is never converted in place and is never
  interpreted as an empty canonical store.
- A mapping with an unknown major version, incompatible required-feature mask,
  malformed offsets, impossible state, or misaligned atomic field fails closed
  before projection.
- Opposite create/open modes racing across runtimes agree on exactly one physical
  creator and never initialize the same region twice.
- Participant-table exhaustion rejects only the new handle and does not impede
  existing participants.
- Exact hash collisions preserve configured value capacity and exact-key
  equality before and after spill churn.
- Abrupt termination before, during, and after participant registration,
  reservation publication, lease activation, removal, and final close cannot
  authorize deletion or recovery of a live owner.
- Process identifier reuse and Linux PID-namespace differences cannot make a
  later process appear to be an abandoned earlier participant.
- Cancellation immediately before or after an ordering point produces only the
  documented outcome set and leaves helpable or completed shared state.
- Closing one language wrapper invalidates only its local borrowed objects and
  does not close or corrupt other live handles.
- Unsupported architectures, kernels, filesystems, or permissions return an
  explicit unsupported or access outcome without falling back to the retired
  protocol.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The repository MUST define exactly one creatable and readable
  current shared-memory protocol for all supported distributions.
- **FR-002**: Current managed, native, and Python public APIs MUST NOT expose a
  legacy/profile choice; their ordinary sizing and creation helpers MUST select
  the canonical protocol.
- **FR-003**: Product source, generated packages, samples, tests, benchmarks,
  manifests, and current protocol documentation MUST remove executable support
  for the retired mapped layout and its operation synchronization model.
- **FR-004**: Every current distribution MUST deterministically reject retired,
  unknown, malformed, or unsupported mappings before payload access and without
  creating a parallel store under the same public name.
- **FR-005**: Retired mappings MUST require an explicit drain, close, recreate,
  and application-owned republish process; in-place conversion and automatic
  fallback MUST remain unsupported.
- **FR-006**: All distributions MUST conform to one canonical byte order,
  alignment, layout arithmetic, record topology, hashing, exact-key comparison,
  numeric state assignment, required-feature set, and resource identity.
- **FR-007**: Every shared control transition and visibility point MUST have one
  language-neutral atomicity and ordering contract that every implementation
  follows exactly.
- **FR-008**: Every distribution MUST calculate required capacity and support
  create-new, open-existing, and create-or-open with equivalent validation and
  deterministic open outcomes.
- **FR-009**: Every distribution MUST support contiguous and segmented atomic
  publication without exposing partial descriptor or payload bytes.
- **FR-010**: Every distribution MUST support exclusive announced-length
  reservations, writable projection, exact advancement, commit, abort, and
  stale-token rejection.
- **FR-011**: Every distribution MUST support concurrent zero-copy acquisition,
  immutable descriptor/payload projection, lease release, and local lifetime
  invalidation.
- **FR-012**: Every distribution MUST support logical removal, rejection of new
  acquires after removal ordering, preservation of active borrowed views, and
  cooperative exact-once physical reclamation.
- **FR-013**: Directory operations MUST preserve exact-key behavior and full
  configured value capacity under arbitrary collisions, helping, cancellation,
  participant pause, removal, and slot reuse.
- **FR-014**: Every persistent reference to a reusable record MUST carry enough
  lifecycle identity to prevent stale helpers and stale public tokens from
  mutating later generations.
- **FR-015**: Every successfully opened handle MUST own one exact participant
  incarnation before it can claim a value or lease record, and handle capacity
  exhaustion MUST be explicit.
- **FR-016**: Every distribution MUST support explicit reservation and lease
  recovery using equivalent conservative liveness and exact-incarnation rules;
  recovery MUST never reclaim state that may still have a live owner.
- **FR-017**: Healthy steady-state publish, reserve, commit, acquire, release,
  remove, reclaim, recovery-help, and diagnostics operations MUST NOT require a
  process-owned or globally exclusive store-wide operation lock.
- **FR-018**: Cold create/open/close coordination MAY use bounded platform
  synchronization, but every runtime MUST use the same resource ordering,
  creation authority, ownership evidence, and final-cleanup policy.
- **FR-019**: Every operation MUST honor no-wait, finite, infinite, cancellation,
  capacity, and contention outcomes without hidden workers or unbounded
  ownership leakage.
- **FR-020**: Persistent structural corruption MUST become a shared terminal
  condition only after revalidation; caller input, capacity, contention,
  cancellation, and legal lifecycle races MUST NOT poison a store.
- **FR-021**: Every distribution MUST expose equivalent protocol identity and
  bounded diagnostics for capacity, participant occupancy, directory health,
  retries, helping, recovery, and terminal corruption without direct console
  output.
- **FR-022**: Borrowed lease and reservation memory MUST be usable only during
  the lifetime of its exact store handle and token; completion, recovery,
  release, or close MUST invalidate local access.
- **FR-023**: The native ABI MUST use versioned fixed-width structures, opaque
  handles, explicit byte lengths, and non-throwing status returns, and MUST NOT
  expose language-runtime object ownership across its boundary.
- **FR-024**: The native distribution MUST provide ownership-safe wrappers for
  stores, leases, and reservations while preserving the canonical statuses and
  byte semantics.
- **FR-025**: The Python distribution MAY include a bundled native component but
  MUST require no third-party runtime package and MUST load only its packaged
  platform artifact rather than an arbitrary working-directory library.
- **FR-026**: The Python distribution MUST expose context-managed stores,
  leases, reservations, read-only borrowed value views, writable reservation
  views, and explicit status outcomes equivalent to the canonical contract.
- **FR-027**: Automated conformance tests MUST pin the protocol header, every
  shared record size and offset, layout calculations, control-word codecs,
  state/status numbers, hash and naming vectors, required features, and binary
  fixtures in all distributions.
- **FR-028**: Automated interoperability tests MUST cover every ordered runtime
  pair plus mixed-runtime publication, reservation, lease, removal, collision,
  cancellation, participant lifecycle, crash, recovery, and corruption cases
  on every supported host platform.
- **FR-029**: Each distribution MUST include a clean-consumer packaging test,
  minimal runnable example, current compatibility declaration, and complete
  public documentation.
- **FR-030**: Removing public selectors, changing defaults, retiring a mapped
  layout, and changing a native ABI MUST be released as documented breaking
  changes with explicit migration notes and independently versioned package,
  ABI, layout, and resource identities.
- **FR-031**: Existing status numeric assignments that remain meaningful under
  the canonical protocol MUST remain stable across distributions; removed
  profile-only symbols MUST not leave ambiguous placeholder behavior.
- **FR-032**: Runtime libraries MUST avoid global mutable configuration, hidden
  maintenance threads, direct console output, application-specific brokers, and
  undeclared runtime dependencies.
- **FR-033**: The security boundary MUST remain trusted same-host participants;
  cross-host sharing, persistence guarantees, application-schema interpretation,
  and protection from malicious authorized writers MUST remain out of scope.

### Key Entities

- **Canonical Protocol**: The sole current mapped layout, required-feature set,
  atomic state machine, resource identity, and compatibility contract.
- **Distribution**: One independently versioned managed, native, or Python
  package that implements the canonical protocol.
- **Store Handle**: A process-local owner of one mapped view, one participant
  incarnation, and its cold-lifecycle resources.
- **Participant Incarnation**: An exact reusable-record identity that
  distinguishes one handle lifetime from process, namespace, token, and record
  reuse.
- **Published Value Generation**: One immutable descriptor and payload bound to
  an exact key and reusable slot generation.
- **Reservation**: An exclusive writable capability for one announced value
  generation before visibility.
- **Lease**: A shared read-only capability protecting one published generation
  from physical reuse.
- **Directory Mutation**: A helpable, generation-fenced operation that binds or
  unbinds an exact key and value generation.
- **Recovery Decision**: A caller-controlled classification and exact mutation
  of state proven abandoned.
- **Conformance Fixture**: Canonical mapped bytes and vectors used to prove
  equivalent interpretation by every distribution.
- **Compatibility Manifest**: Independently versioned package, ABI, mapped
  protocol, resource protocol, platform, and required-feature support data.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: All nine ordered producer-consumer combinations exchange at least
  1,000 arbitrary binary values and complete acquire, release, remove, and reuse
  with zero byte mismatches, partial values, or undocumented outcomes.
- **SC-002**: Every supported distribution passes 100% of the canonical layout,
  control-word, hashing, naming, status, feature-mask, and binary-fixture vectors.
- **SC-003**: A mixed-runtime workload completes at least 1,000,000 publication,
  reservation, acquisition, release, removal, and reuse operations with zero
  corruption, stale-generation mutation, false successful removal, leaked
  safely recoverable capacity, or access violation.
- **SC-004**: At least 10,000 injected reservation-owner and lease-owner
  terminations distributed across all supported runtimes expose zero partial
  publications, reclaim zero live ownership, accept zero stale token actions,
  and restore all capacity classified as safely recoverable.
- **SC-005**: Controlled pause/reuse schedules cover every persistent directory,
  slot, lease, and participant mutation transition for every implementation and
  produce zero later-generation mutations across at least 1,000,000 total
  repetitions.
- **SC-006**: Every one of at least 10,000 finite-wait mixed-runtime operations
  returns within the selected limit plus 250 milliseconds and leaves no owner
  token after a non-success outcome.
- **SC-007**: Instrumented Windows x64 and Linux x64 runs observe zero
  process-owned or globally exclusive operation-lock acquisition during
  successful steady-state data operations in every distribution.
- **SC-008**: Twelve simultaneous readers implemented across the supported
  runtimes acquire the same published value, observe one checksum, survive
  pending removal, retain valid borrowed views, and cause exactly one safe
  reclamation after the final release.
- **SC-009**: Clean consumers build, install, import or link, and run the minimal
  example for all three distributions using only documented prerequisites and
  packaged runtime artifacts.
- **SC-010**: The full managed, native, Python, interop, package-consumption,
  sample, documentation, build, test, and packaging suite completes with zero
  failures in the release configuration on supported hosts.
- **SC-011**: Static product and package inspection finds one current mapped
  protocol, zero public profile selectors, zero creatable retired-layout paths,
  and one unambiguous compatibility declaration.
- **SC-012**: A developer can follow the migration guide to close and replace a
  retired store and republish application-owned data without reading
  implementation source; attempts to skip recreation fail closed.
- **SC-013**: On each qualified release host, eight-process acquire/release and
  publish/remove p99 is at most 10 microseconds on Linux x64 and 25 microseconds
  on Windows x64, aggregate throughput is at least 100,000 credited operations
  per second, eight-process p99 is at most three times one-process p99, and no
  successful raw operation stalls longer than 10 milliseconds on Linux x64 or
  250 milliseconds on Windows x64. The Windows raw maximum is a hard hang
  detector with scheduler grace; p99, scaling, duration-bound, and suspension
  gates remain the strict progress predicates.

## Assumptions

- The canonical protocol is the existing documented layout 2.0 with its current
  required-feature mask rather than a newly encoded layout 3.0.
- Breaking source, binary, native-ABI, and mapped-layout compatibility with
  legacy-only consumers is accepted; current package versions advance by the
  appropriate major version.
- No in-place migration is required because the sole consumer can drain all
  handles, recreate mappings, and republish application-owned values.
- Windows x64 and Linux x64, including supported same-host Linux containers,
  remain the qualified targets. Other architectures fail explicitly until a
  later qualification advertises them.
- Python may rely on the native implementation shipped inside its package to
  perform interprocess atomic operations; it does not maintain an independent
  pure-Python copy of the shared state machine.
- The exact protocol topology, state transitions, and memory ordering already
  documented for layout 2.0 remain authoritative unless research finds a
  catastrophic cross-language impossibility that requires a separately reviewed
  protocol revision.
- Historical feature specifications and source-control history may mention
  retired layouts; current product code, packages, samples, manifests, and
  current protocol guidance do not advertise or implement them.
- Each language may use ecosystem-appropriate names and ownership constructs,
  but status outcomes, mapped bytes, visibility, recovery, and lifetimes remain
  equivalent.
