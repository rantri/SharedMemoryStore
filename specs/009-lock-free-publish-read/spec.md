# Feature Specification: Lock-Free Shared-Memory Key-Value Store

**Feature Branch**: `codex/lock-free-csharp`

**Created**: 2026-07-12

**Status**: Draft

**Input**: User description: "Keep SharedMemoryStore a key-value store over
shared memory. Producer-consumer processing is only one use case, and an
external message broker load-balances work by sending keys to workers. Workers
and other processes need independent access to stored data. Create a C#-first
lock-free implementation so one process cannot lock the store and harm the
performance or progress of all other processes."

## Product Scope

This feature preserves SharedMemoryStore as a bounded, named, general-purpose
key-value store. Opaque byte keys address immutable values in shared memory.
Any authorized process can publish, acquire, release, or remove data according
to the existing key-value lifecycle; the library does not assign work to
processes and does not track message acknowledgement.

In the primary producer-worker use case, a message broker outside this library
sends keys to 6-12 workers. A worker acquires the value for its assigned key and
reads it without copying. Other processes may independently acquire the same key
or unrelated keys for monitoring, enrichment, diagnostics, or other application
workflows. Multiple valid read leases for one published value remain supported.

The feature replaces store-wide steady-state serialization with lock-free
progress. Lock-free means that suspending or terminating any participant during
a steady-state data operation cannot prevent all other eligible participants
from completing operations while relevant data or capacity exists. It does not
promise that every individual operation is wait-free or immune to starvation
under adversarial same-key contention.

Existing layout-v1.2 mappings and their public behavior remain a supported
compatibility profile. The new lock-free shared-memory contract has a distinct
compatibility identity so old and new participants can never mutate one mapping
under incompatible synchronization assumptions.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Read Values Concurrently by Key (Priority: P1)

Many processes independently receive or choose keys and acquire the matching
shared value. Workers may receive keys from an external broker, while unrelated
processes may read the same values or different values for their own purposes.
No reader is required to coordinate through the store with other readers.

**Why this priority**: Key-addressed zero-copy reads are the library's core
value. The store must remain useful beyond one producer-consumer topology, and a
slow or paused reader must not serialize all other readers.

**Independent Test**: Publish a known set of keyed values, then run 6 and 12
worker processes plus independent observer processes. Send keys to workers from
a test broker, let observers acquire overlapping keys, and verify exact bytes,
concurrent lease validity, progress, allocation behavior, and the absence of a
store-wide operation lock.

**Acceptance Scenarios**:

1. **Given** one published key, **When** several processes acquire it
   concurrently, **Then** every successful caller receives a valid lease over
   the same immutable value generation.
2. **Given** an external broker sends different keys to different workers,
   **When** workers acquire those keys, **Then** the store returns values by
   exact key and does not participate in worker selection, delivery, or
   acknowledgement.
3. **Given** workers are reading assigned values, **When** an unrelated process
   acquires one of the same keys, **Then** that additional lease is allowed and
   does not invalidate worker leases.
4. **Given** one reader pauses while holding a lease, **When** other processes
   acquire the same or unrelated published keys, **Then** they continue making
   progress and observe complete immutable values.
5. **Given** no current value exists for a key, **When** a process acquires it,
   **Then** it receives the documented not-found outcome rather than a false
   contention or corruption result.

---

### User Story 2 - Publish Directly Into Shared Memory (Priority: P1)

A producer reserves bounded capacity for an opaque key, descriptor, and complete
payload length, fills store-owned memory directly, and commits the value without
first creating an intermediate full-payload buffer. Other producers may publish
unrelated keys, and readers continue acquiring already committed values while a
reservation is being filled.

**Why this priority**: The primary high-throughput use case depends on zero-copy
ingest, but publication cannot achieve that goal if it holds a global lock that
delays every reader and unrelated writer.

**Independent Test**: Run direct frame ingestion concurrently with readers and
with publication of unrelated keys. Verify exact-byte commit, invisibility
before commit, duplicate-key behavior, zero-copy access, progress on unrelated
keys, and no per-value steady-state allocation.

**Acceptance Scenarios**:

1. **Given** free capacity and a valid new key, **When** a producer reserves,
   fills, accounts for exactly the announced payload length, and commits, **Then**
   one complete immutable value becomes visible atomically under that key.
2. **Given** a reservation is still being filled, **When** any process acquires
   its key, **Then** the reservation and all partial payload bytes remain
   invisible.
3. **Given** a producer is paused while filling one reservation, **When** other
   processes read committed keys or publish unrelated keys, **Then** those
   operations remain able to complete while capacity permits.
4. **Given** two producers race to publish the same absent key, **When** their
   publications contend, **Then** no more than one value generation becomes
   current and every caller receives a deterministic documented result.
5. **Given** the announced payload length is not exactly accounted for, **When**
   commit is attempted, **Then** the value remains invisible and no partial or
   overrun bytes become published.

---

### User Story 3 - Remove and Reuse Values Without Stalling the Store (Priority: P1)

Any authorized process can logically remove a key. New acquisitions stop seeing
the removed generation, existing leases remain readable, and storage is reused
only after the final protecting lease is released. Removal or reclamation of one
key does not globally pause operations on other keys.

**Why this priority**: A key-value store needs safe churn. Lock-free publication
and lookup are insufficient if remove, final release, reuse, or index maintenance
can still stop every process.

**Independent Test**: Continuously publish, acquire, remove, release, and reuse a
large rotating key set from independent processes. Pause participants at every
observable lifecycle stage and verify linearizable same-key outcomes, immutable
existing leases, stale-token rejection, unrelated-key progress, and bounded
capacity behavior.

**Acceptance Scenarios**:

1. **Given** a published value with no active lease, **When** removal succeeds,
   **Then** new acquisitions return not found and its capacity becomes safely
   reusable.
2. **Given** one or more active leases, **When** removal wins, **Then** new
   acquisitions cannot lease that generation, existing leases keep reading the
   exact bytes they acquired, and reclamation waits only for those leases.
3. **Given** acquire and remove race for the same generation, **When** both
   finish, **Then** either acquisition established a valid protecting lease
   before logical removal or removal won and acquisition did not expose the
   value.
4. **Given** a removed key is awaiting final release, **When** processes operate
   on unrelated keys, **Then** they continue without waiting for the retained
   lease.
5. **Given** a stale reservation or lease token survives storage reuse, **When**
   it attempts to commit, abort, project, release, or remove, **Then** it cannot
   affect the current generation.
6. **Given** a process pauses while helping publish or remove a key and the
   original value slot is later reclaimed and reused, **When** the paused process
   resumes, **Then** its older directory work cannot alter, unlink, or complete
   the newer value generation.

---

### User Story 4 - Survive Participant Pauses and Failures (Priority: P2)

The store remains available when a publisher, reader, remover, or diagnostic
caller is paused or terminated. An authorized caller can explicitly recover
eligible abandoned reservations and leases without treating a live owner as
dead or allowing a former owner to modify reused state.

**Why this priority**: The principal reason for removing the global lock is to
prevent one unhealthy process from harming every healthy process. Crash recovery
must restore capacity without recreating global blocking or corrupting active
data.

**Independent Test**: Suspend and terminate processes during reservation,
commit, lookup, lease registration, removal, release, diagnostics, and recovery.
Continue operations from healthy processes, then perform explicit recovery and
verify progress, owner classification, capacity restoration, and stale-owner
fencing.

**Acceptance Scenarios**:

1. **Given** a process is suspended at any steady-state operation transition,
   **When** other live processes operate on suitable keys and capacity, **Then**
   at least one eligible operation continues to complete without the suspended
   process resuming.
2. **Given** a publisher terminates with an incomplete reservation, **When** an
   authorized caller confirms it is recoverable, **Then** partial bytes never
   become visible and recoverable capacity is restored.
3. **Given** a reader terminates with a lease, **When** authorized recovery
   confirms that exact owner and lease incarnation are stale, **Then** the lease
   is released safely and cannot later release a reused generation.
4. **Given** owner liveness is unknown or still live, **When** recovery is
   attempted, **Then** the store preserves the protected state and reports the
   documented unsupported or active-owner outcome.
5. **Given** a formerly stalled owner resumes after its reservation or lease was
   recovered, **When** it uses an old token, **Then** the operation is rejected
   without mutating current state.

---

### User Story 5 - Operate and Upgrade the Store Safely (Priority: P3)

An operator can understand capacity, contention, active reservations, leases,
pending removals, recovery, and key-index health while live operations continue.
Existing users retain the documented public API and can distinguish legacy and
lock-free mappings during deployment or rollback.

**Why this priority**: Lock-free concurrency is production-ready only when its
pressure and recovery behavior are observable and incompatible participants are
fenced before accessing memory.

**Independent Test**: Exercise mixed operations, pressure, participant churn,
diagnostics, legacy opening, lock-free opening, incompatible opening, packaging,
and rollback. Verify diagnostic bounds, public statuses, compatibility outcomes,
and unchanged key-value semantics.

**Acceptance Scenarios**:

1. **Given** a live store under mixed load, **When** diagnostics are requested,
   **Then** operators can distinguish capacity exhaustion, local contention,
   retained leases, pending removals, abandoned state, recovery, and index
   pressure without globally pausing data operations.
2. **Given** an existing layout-v1.2 mapping, **When** an upgraded client opens
   it through the supported compatibility profile, **Then** current key-value,
   lease, and result-status behavior remains available.
3. **Given** a client does not support the lock-free mapping contract, **When**
   it attempts to open that mapping, **Then** it fails closed with a documented
   non-success compatibility/open outcome before projecting payload memory.
4. **Given** a service uses an external broker to distribute keys, **When** it
   adopts the lock-free store, **Then** broker topology and acknowledgement
   semantics remain outside the store and require no migration into the library.
5. **Given** a live managed Linux owner whose PID is hidden from another
   process by a different PID-namespace view, **When** that process performs a
   cold lifecycle operation, **Then** the live mapping is retained without
   adding synchronization to any key-value operation.
6. **Given** a physical region whose header is still unpublished and zero,
   **When** any same-profile or opposite-profile client attempts to open it,
   **Then** the opener leaves every byte unchanged and returns `AlreadyExists`
   for `CreateNew`, `StoreBusy` for `CreateOrOpen`, or `IncompatibleLayout` for
   `OpenExisting`.

### Edge Cases

- A key is empty, oversized, hash-colliding, concurrently inserted, concurrently
  removed, or repeatedly reused across many generations.
- Two or more processes publish the same absent key while other processes search
  through the same collision chain.
- A spill-summary setter or clearer pauses after exact validation while helpers
  finish that lifecycle, preserve a versioned Empty identity, and later publish
  a different overflow generation for the same canonical bucket.
- A producer reserves a key and pauses before writing, during writing, after all
  bytes are written, or immediately before or after commit visibility.
- A producer writes fewer or more bytes than announced, commits twice, aborts
  after commit, or retains writable memory after its lifetime.
- A zero-length value or descriptor is published and acquired.
- Acquisition races publication, removal, final lease release, recovery, index
  maintenance, store disposal, and generation rollover.
- Many processes repeatedly acquire one hot key while other processes access a
  broad key set.
- One process retains a lease indefinitely; only that value generation's
  reclamation and bounded capacity are affected.
- Multiple handles in one process and handles in different processes operate on
  the same and different keys concurrently.
- Capacity is one, all slots are occupied, all lease-tracking capacity is used,
  or capacity is retained entirely by removed generations with active leases.
- The configured participant table is full, one process owns several store
  handles, a participant terminates immediately after claiming shared state, or
  a participant record approaches its incarnation limit.
- High churn creates index pressure while missing-key lookup and new publication
  continue.
- A process is suspended after reserving shared state but before returning its
  public token.
- A publisher, reader, remover, diagnostic caller, or recovery caller terminates
  at each observable state transition.
- Owner process identifiers are reused or liveness cannot be established.
- A managed Linux owner's PID is not visible to another process while both see
  the same shared-memory resources; the owner exits normally or is terminated.
- A stale reservation, lease, recovery, or removal token acts after generation,
  incarnation, sequence, or storage-position reuse.
- A participant pauses after observing an in-progress directory mutation, other
  participants finish it and reuse the value slot repeatedly, and the paused
  participant later resumes with observations from the older generation.
- Lifecycle or identity counters approach their supported numeric limits.
- A bounded operation is canceled or expires at the same instant its target
  state becomes available.
- A store handle is disposed while another local operation is active, while
  other process handles remain live, or while borrowed memory is retained.
- A creator pauses after exposing a physical region but before publishing its
  header, including an older client that mapped before entering cold
  coordination; another opener must not infer initialization ownership from
  open mode, dimensions, profile, or zero bytes.
- Diagnostics observe counters and gauges while they change concurrently and
  therefore may be moment-in-time rather than transactionally exact.
- A broker delivers a missing, removed, duplicate, or delayed key; the store
  answers only the current key-value lookup and does not acknowledge the message.
- An untrusted process modifies mapped bytes; protection from malicious mapped
  writers remains outside the trust boundary.

## Concurrency Outcome Contract

The following outcome sets make same-key and lifecycle races testable. A caller
may additionally receive its documented input-validation, disposed,
incompatible, access-denied, unsupported-platform, or corruption outcome when
that condition genuinely applies. Steady-state `StoreStatus.StoreBusy` is
allowed only when the caller's bounded local retry budget is exhausted; it does
not mean another process owns a global store lock. Cold
`StoreOpenStatus.StoreBusy` may additionally report cold-gate contention or an
existing unpublished region whose initialization ownership cannot be proven.

| Concurrent actions | Allowed observable outcome |
|---|---|
| Two atomic convenience publications of the same absent key | At most one publication returns `Success`, ordered at its exact `Reserved(AtomicPublication) -> Published` transition. Another caller returns `DuplicateKey` only after observing a current `Published`/`RemoveRequested` duplicate witness, `StoreFull` if no physical candidate slot is reusable, or `StoreBusy` if its bounded retry budget expires while the winner remains tentative. `Initializing` and `Reserved(AtomicPublication)` alone never justify `DuplicateKey`. Two current generations are never visible. |
| Two explicit reservations of the same absent key | At most one reserve returns `Success`, ordered at its exact `Initializing -> Reserved(ExplicitReservation)` transition. Another caller returns `DuplicateKey` after observing that exact-key explicit-reservation witness, `StoreFull` if no physical candidate slot is reusable before duplicate arbitration, or `StoreBusy` if its bounded retry budget expires before it can determine the winner. |
| Atomic convenience publication and explicit reservation of the same absent key | `Reserved(ExplicitReservation)` is an ordered duplicate-key witness. `Reserved(AtomicPublication)` is tentative: a contender MUST help/revalidate and may return `DuplicateKey` only after the atomic publication becomes `Published`, may proceed if that tentative lifecycle aborts, may return physical `StoreFull` before acquiring a candidate slot, or may return `StoreBusy` after bounded retry exhaustion. |
| Commit and acquire of the reservation's key | If acquire orders first, it returns `NotFound`; if commit orders first, acquire returns `Success` with the complete committed generation. Acquire may return `StoreBusy` only after bounded retry exhaustion. Partial bytes are never returned. |
| Acquire and remove of one published generation | If acquire orders first, it returns `Success` with a protecting lease and removal returns `RemovePending`. If logical removal orders first, acquire returns `NotFound` and removal returns `Success` or `RemovePending` according to established leases or bounded post-removal classification/reclaim work. Either caller may return `StoreBusy` only before logical removal after bounded retry exhaustion. |
| Final lease release and reclamation | The valid final release returns `Success` and causes exactly one safe reclamation. A duplicate or stale release returns `LeaseAlreadyReleased` or `InvalidLease` and cannot reclaim a later generation. |
| Remove and republish of the same key | Publication returns `DuplicateKey` while the published or pending-removal generation still owns the key. Publication may return `Success` only after that lifecycle is safely reclaimed and the new publication wins ownership. |
| Normal recovery and a live reservation/lease action | With the applicable current-process overrides disabled, recovery remains safe concurrently with normal resource activity and preserves every resource whose exact participant is live Active. An explicit reserve orders at `Initializing -> Reserved(ExplicitReservation)`; an atomic convenience publication orders at `Reserved(AtomicPublication) -> Published`. Normal recovery neither invalidates those live workflows nor undoes a committed value or releases a later incarnation. Stale-process and exact `Closing`/`Recovering` ownership remain independently recoverable. |
| Current-process lease-recovery override and current-process lease activity | `RecoverCurrentProcessLeases: true` is an administrative test/controlled-shutdown override, not a concurrent lease operation. Before invoking it, the caller MUST quiesce all current-process lease acquisition, projection, borrowed-span use, and release across every handle attached to the mapping, and MUST maintain that quiescence until recovery returns. Concurrent use of this override with that activity is outside the supported contract; no process-local hot-path gate is added to enforce the precondition. |
| Current-process reservation-recovery override and current-process publication activity | `RecoverCurrentProcessReservations: true` is an administrative test/controlled-shutdown override, not a concurrent publication operation. Before invoking it, the caller MUST quiesce reservation and publication creation, writable projection/use, progress, commit, abort, reservation disposal, and store-handle disposal across every handle attached to the mapping, and MUST maintain that quiescence until recovery returns. Concurrent use of this override with that activity is outside the supported contract; no process-local hot-path gate is added to enforce the precondition. |
| Cancellation/deadline and target-state availability | If the operation orders before cancellation/deadline observation, it returns its normal result. Otherwise it returns `OperationCanceled` or `StoreBusy` and leaves no newly owned reservation or lease. |
| Local disposal and an operation on that handle | If the operation orders first, it returns its normal result and disposal then invalidates local borrowed access. If disposal orders first, the operation returns `StoreDisposed`. Other process handles remain unaffected, and neither path exposes synchronization or mapped-memory exceptions. |
| Index maintenance and lookup of a stable published key | Lookup returns `Success` with the exact key/value or `StoreBusy` after bounded retry exhaustion. It does not return `NotFound` solely because unrelated maintenance is moving through an intermediate state. |

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST remain a bounded named key-value store in which
  opaque byte keys address immutable shared-memory values.
- **FR-002**: The library MUST NOT assign keys to workers, load-balance work,
  track message delivery, acknowledge broker messages, or impose exclusive work
  claims as part of the key-value contract.
- **FR-003**: Keys MUST retain exact byte equality, non-empty validation,
  configured size limits, and deterministic collision handling.
- **FR-004**: At most one published current value generation MAY exist for one
  exact key at a time.
- **FR-005**: Processes MUST be able to publish unrelated keys concurrently,
  subject only to bounded capacity and conflicts involving shared internal state.
- **FR-006**: Concurrent publication of the same absent key MUST make at most one
  generation current and MUST return deterministic documented outcomes to every
  participant.
- **FR-007**: The existing simple and segmented publication workflows MUST
  preserve their documented byte, descriptor, duplicate-key, and visibility
  semantics. Each is one atomic convenience publication whose public success
  orders only at the exact `Reserved(AtomicPublication) -> Published`
  transition; its preceding `Initializing` and `Reserved` states remain
  tentative internal work.
- **FR-008**: A producer MUST be able to reserve bounded capacity for a key,
  fixed descriptor, and complete non-negative payload length before visibility.
  This explicit-reservation workflow MUST carry
  `PublicationIntent=ExplicitReservation` and public reservation MUST order only
  at the exact `Initializing -> Reserved(ExplicitReservation)` transition. An
  `Initializing` lifecycle is tentative and MAY be physically discoverable for
  helping, but MUST NOT alone establish reserved-key ownership or justify
  `DuplicateKey`.
- **FR-009**: A reservation MUST expose store-owned writable payload memory so
  the producer can fill it without an intermediate full-payload buffer.
- **FR-010**: Commit MUST succeed only after exactly the announced payload length
  has been explicitly accounted for; incomplete or overrun commit attempts MUST
  remain invisible and return deterministic outcomes.
- **FR-011**: Commit MUST make the complete key, descriptor, payload, and value
  generation visible atomically; readers MUST observe either no value or one
  complete immutable generation. Publication of either intent orders at the
  exact transition to `Published`; for an explicit reservation this is the
  later commit operation, while for an atomic convenience workflow it is the
  sole public operation's success point.
- **FR-012**: Aborting or disposing an incomplete reservation MUST keep it
  invisible and restore safely reusable capacity.
- **FR-013**: Writable and read-only mapped-memory views MUST be borrowed for a
  precise documented lifetime. Public safe access MUST stop projecting a view
  after its lifetime; explicitly unsafe escaped references remain outside the
  library's protection boundary.
- **FR-014**: Any authorized process MUST be able to acquire the current value
  for an exact key without obtaining exclusive processing ownership.
- **FR-015**: Multiple threads and processes MUST be able to hold simultaneous
  valid read leases for the same published value generation.
- **FR-016**: A successful lease MUST expose the exact immutable payload and
  descriptor directly from shared memory without a mandatory payload copy.
- **FR-017**: A lease MUST protect its exact value generation from storage reuse
  until released or safely recovered.
- **FR-018**: Projecting or inspecting an already valid lease MUST NOT require a
  store-wide exclusive operation lock for each property or memory view.
- **FR-019**: Releasing or disposing a valid lease MUST relinquish only that
  lease incarnation and MUST NOT affect other leases for the same generation.
- **FR-020**: Successful logical removal MUST prevent new leases for the removed
  generation at one observable point while preserving every lease established
  before that point.
- **FR-021**: Reclamation and reuse MUST occur only after logical removal and the
  release or safe recovery of every protecting lease for that generation.
- **FR-022**: Acquire/remove and release/reclaim races MUST produce documented
  outcomes consistent with one ordering of their observable completion points.
- **FR-023**: A key awaiting reclamation MUST retain the existing duplicate-key
  behavior until its lifecycle permits documented reuse.
- **FR-024**: A stable published key MUST NOT spuriously return not found because
  an unrelated key is being published, removed, reclaimed, or maintained.
- **FR-025**: Suspending or terminating any participant at any steady-state
  publish, reserve, commit, abort, acquire, project, release, remove, reclaim, or
  index-maintenance transition MUST NOT prevent another live eligible
  participant from completing an operation while suitable keys or capacity
  exist.
- **FR-026**: Steady-state progress MUST NOT depend on process-owned or globally
  exclusive synchronization state.
- **FR-027**: Every steady-state success and normal-failure path for simple and
  segmented publication, reservation access and progress, commit, abort,
  reservation disposal, acquisition, lease projection, release, lease disposal,
  removal, final reclamation, and index maintenance MUST NOT acquire a named
  cross-process lock or any other globally exclusive operation owner.
- **FR-028**: Every successful publication, commit, acquisition, logical removal,
  and release MUST have a single documented externally observable ordering
  point for same-key race analysis.
- **FR-029**: An individual operation that cannot complete within the caller's
  selected contention or wait bound MUST return a deterministic busy, canceled,
  timeout, capacity, or lifecycle outcome without reporting a false success or
  corrupting state.
- **FR-030**: Optional waiting for a key-state, capacity, or recovery transition
  MAY use bounded notification, but successful fast-path operations MUST NOT pay
  a mandatory globally exclusive synchronization acquisition.
- **FR-031**: The store MUST remain fixed-capacity and MUST return distinguishable
  outcomes for value-slot exhaustion, lease-tracking exhaustion, duplicate key,
  local contention exhaustion, pending reclamation, and lock-free-profile
  participant/open-handle tracking exhaustion. `StoreFull` is a physical
  capacity outcome: every non-`Free` slot, including tentative `Initializing`
  and `Reserved(AtomicPublication)`, is unavailable until safely reclaimed. A
  sequential allocation scan is only a candidate because a reusable slot can
  move behind it. Public `StoreFull` therefore requires two same-order collects
  of every structurally valid slot control, with every control non-`Free` and
  the second collect exactly equal to the first. Slot controls MUST progress
  monotonically for a generation—failed claims use
  `Initializing -> Aborting -> Reclaiming -> Free(next generation)` and MUST NOT
  roll back to the same `Free` word—so exact equality cannot hide ABA. The
  structural check MUST require a nonzero bounded generation, a structurally
  valid configured participant token in `Initializing`/`Reserved`, participant
  zero in `Free`/`Published`/`RemoveRequested`/`Aborting`/`Reclaiming`/`Retired`,
  and the terminal generation in `Retired`. An invalid state/generation/owner
  shape MUST return `CorruptStore`, even when two malformed words compare equal.
  The confirmed ordering point is the candidate instant after the first collect
  and before the second. A free slot, changed control, or concurrent use of the
  process-local proof buffer is contention, not capacity; `NoWait` returns
  `StoreBusy`, while finite/infinite calls retry under their existing budget.
  A publication/reservation operation whose initial same-key lookup was absent MAY
  therefore return `StoreFull` at its later candidate claim before final
  arbitration with a raced same-key lifecycle; duplicate-key status has no
  precedence over genuine physical exhaustion in that race. Lease allocation
  scan exhaustion is likewise provisional because a reusable lease record can
  rotate behind the scanner. Public `LeaseTableFull` requires two same-order
  collects of every lease control, with both passes structurally valid,
  non-`Free`, and exactly equal. Owner-controlled states MUST carry a
  structurally valid configured participant token; unowned states MUST have no
  participant, and invalid state/incarnation/owner shapes MUST return
  `CorruptStore`. Lease incarnation advance or terminal retirement prevents
  control-word ABA between collects. A free or changed lease control, or
  concurrent use of that handle's lease-proof buffer, is contention:
  `NoWait` returns `StoreBusy`, while finite/infinite calls retry under the
  operation-wide budget. Only the confirmed candidate instant between the
  collects may order `LeaseTableFull`.
- **FR-032**: A stalled or failed reservation owner MAY retain only its key, one
  reservation lifecycle, and its configured slot capacity; a stalled or failed
  lease owner MAY retain only its lease record and protected value generation.
  Neither MAY retain global authority required for unrelated operations or
  additional readers of an already published value to progress.
- **FR-033**: Explicit reservation recovery MUST discard incomplete bytes,
  preserve committed values, restore only safely recoverable capacity, and fence
  recovered tokens from later mutation. Recovery MUST reclaim stranded bounded
  resources rather than restore global steady-state progress. Normal recovery
  MUST preserve an owner-controlled lifecycle whose exact participant is live
  Active. Current-process reservation override MUST require documented
  process-wide writer and writable-view quiescence; racing that override with
  current-process publication activity is outside the supported result contract.
- **FR-034**: Explicit lease recovery MUST distinguish live, stale, unsupported,
  and inconsistent owners before relinquishing a lease and MUST reject a stale
  release after recovery or reuse.
- **FR-035**: Owner and token identity MUST distinguish later incarnations even
  when a process identifier, participant record, lease record, key, or storage
  position is reused. The first atomic claim of a value slot or lease record MUST
  already identify a recoverable participant incarnation; ownership identity
  MUST NOT depend on a later non-atomic write by the claimant.
- **FR-036**: The library MUST NOT rely on hidden background workers for
  publication, lookup, reclamation, index maintenance, recovery, diagnostics,
  retries, or cleanup.
- **FR-037**: Steady-state publish/reserve/commit and acquire/project/release
  paths, including expected duplicate, missing, full, and local-contention
  outcomes, MUST avoid per-operation runtime heap allocation after
  initialization and warm-up. Each open lock-free handle MAY eagerly reserve one
  process-local `Int64[SlotCount]` StoreFull proof buffer and one
  `Int64[LeaseRecordCount]` LeaseTableFull proof buffer (approximately eight
  bytes per configured record: 1 KiB for 128 lease records, 64 KiB for 8,192,
  and 8 MiB for 1,048,576); the proof paths MUST perform no per-operation
  allocation and MUST place no proof counter or buffer in shared memory.
- **FR-038**: Diagnostics MUST expose capacity, active reservations, published
  values, active leases, pending removals, reclamation, local contention,
  recovery, invalid-token outcomes, and key-index health.
- **FR-039**: Diagnostics MAY be moment-in-time rather than transactionally
  exact, but requesting them MUST be safe during live operations and MUST NOT
  impose a global exclusive data-path pause or require data operations to wait
  for the diagnostic caller.
- **FR-040**: Disposing one local handle MUST invalidate its borrowed views and
  tokens without corrupting or globally blocking other attached handles.
- **FR-041**: Disposal races with every public operation MUST produce only
  documented normal or disposed outcomes and MUST NOT expose invalid mapped
  memory or synchronization exceptions.
- **FR-042**: Existing key-addressed `TryPublish`, reservation,
  `TryPublishSegments`, `TryAcquire`, lease release, `TryRemove`, recovery,
  diagnostics, wait-policy, and disposal semantics MUST remain publicly
  recognizable and behaviorally compatible in the lock-free profile.
- **FR-043**: Unsupported clients MUST fail closed on the lock-free mapped
  representation before projecting payload memory or mutating shared state.
  Current clients that can validate the header MUST report the documented
  incompatible-layout outcome; already released clients MAY surface an existing
  non-success mapping/open outcome when their requested view prevents header
  validation.
- **FR-044**: Documentation and samples MUST show one zero-copy producer, 6-12
  broker-directed workers, simultaneous non-worker readers, removal with active
  leases, bounded pressure, disposal, and explicit recovery while keeping broker
  responsibilities outside the store.
- **FR-045**: The trusted same-host process boundary MUST remain explicit; the
  feature MUST NOT claim cross-host access, persistence, or protection from
  malicious processes with mapped-memory write access.
- **FR-046**: Explicit recovery MAY coordinate ownership classification and the
  exact records being recovered, but healthy steady-state data operations MUST
  NOT acquire or wait for a global recovery lock.
- **FR-047**: Every durable in-progress key-directory mutation and every durable
  directory-location reference in the lock-free profile MUST identify the exact
  value generation it belongs to. Work that resumes after the referenced slot
  has been reclaimed or reused MUST be unable to match, alter, unlink, or
  complete the newer generation. Generation-mismatch cleanup MUST be
  directional: generation `G` MAY exact-CAS its own tagged word or a strictly
  older residue, but MUST preserve every observed word tagged with a generation
  greater than `G` because that word belongs to a reused lifecycle. Publication
  from an unversioned empty word MUST be postvalidated and compensating cleanup
  MUST compare only against the publisher's exact generation-tagged value.
- **FR-048**: The lock-free profile MUST support between 1 and 1,048,575 value
  slots. Creation requests outside that range MUST fail validation before a
  mapping is created or opened. This limit applies only to the lock-free
  profile and MUST NOT silently change the legacy profile's validation contract.
- **FR-049**: Overflow lookup in the lock-free profile MUST use a one-word
  exact-generation versioned spill summary. An exact current insert MUST publish
  Present before its overflow-cell CAS. A helper MAY publish logical Empty only
  by exact full-word CAS after a stable empty bounded scan and exact canonical
  mutation revalidation, and MUST preserve a nonrepeating version identity so a
  delayed setter or clearer cannot ABA through empty. Budget, instability, or
  malformed state MUST NOT create a false-negative overflow decision. A stable
  full scan that finds a different exact current spill witness MAY full-word-CAS
  Present to that witness under the same revalidated canonical mutation; a
  Present identity may recur only while its exact nonwrapping binding remains
  current, which cannot revive an older stable-empty clear authorization.
- **FR-050**: A legal reservation transition from `Initializing` or `Reserved`
  to the unowned `Aborting`/`Reclaiming` cleanup lifecycle MAY race any insert
  helper phase. After every validation window, a helper MUST distinguish this
  cancellation state from structural corruption and either complete exact
  cancellation cleanup or stop benignly. In particular, a delayed
  `BindingChanged` helper MUST NOT report corruption merely because its exact
  `Initializing` slot can no longer become `Reserved`, and an exact versioned
  `Empty(binding)` published by cancellation MUST suppress an older overflow
  setter without being reclassified as malformed state. A helper that resumes
  after validating `Insert` MAY exact-clear that insert's target after
  cancellation has handed the same canonical mutation to `Unlink/Prepared`.
  A delayed unlink location publisher that revalidates the canceling
  `Aborting`/`Reclaiming` lifecycle MUST treat an empty target or a structurally
  valid different in-range binding as legal progress and MUST preserve the
  replacement; a stable malformed or mapping-out-of-range target remains
  `CorruptStore`. A helper that resumes
  after cancellation, reclaim, or reuse MUST remain generation-fenced and MUST
  NOT alter the later lifecycle. Losing `Initializing -> Reserved` to exact
  `Aborting`/`Reclaiming`, terminal retirement, or a strictly later generation
  means the tentative reservation was legally canceled and returns
  `InvalidReservation` after helping cleanup; a lower generation or an
  impossible same-generation state remains fail-closed `CorruptStore`. Winning
  `Initializing -> Reserved(ExplicitReservation)` is the public explicit-reserve
  ordering point. The same transition for `AtomicPublication` remains tentative
  for the outer operation. Supported normal recovery preserves the live owner;
  an administrative current-process override is permitted only after the
  quiescence required by FR-033.
- **FR-051**: Every claimed lock-free value-slot lifecycle MUST carry one
  immutable 32-bit `PublicationIntent` ordinary-metadata value at byte offset
  52: `None=0`, `ExplicitReservation=1`, or `AtomicPublication=2`. The exclusive
  `Initializing` owner MUST write a nonzero known intent with the key, lengths,
  descriptor, and other ordinary lifecycle metadata before release-publishing
  the exact current-generation `Insert/Prepared` directory operation. That word
  MUST be the metadata-ready marker and MUST precede canonical mutation and
  directory-cell binding publication. Reclaim MAY leave the ordinary bytes
  stale and the next exclusive claimant MUST overwrite them. `Free`, `Retired`,
  and pre-metadata `Initializing`—operation zero with no exact current mutation
  or directory-cell reference—MUST ignore stale/`None` intent;
  direct unreferenced cleanup of such a safely recovered claim MUST remain
  possible without interpreting those ordinary bytes;
  every current discoverable lifecycle with an unknown intent MUST fail closed
  as `CorruptStore`; a current mutation/cell reference without its required
  operation marker is also corruption. `Reserved(ExplicitReservation)`, `Published`, and
  `RemoveRequested` are duplicate-key witnesses; `Initializing` and
  `Reserved(AtomicPublication)` are not.
- **FR-052**: A directory lookup or maintenance reference MUST be treated as a
  cached exact-reference-word witness, not as permanent ownership of the decoded
  slot binding. For a primary lane or overflow cell the exact source word is the
  binding itself; for a versioned `Present(binding)` spill summary the exact
  source word is the complete encoded summary and the referenced binding is
  decoded separately. If later slot classification would return `CorruptStore`,
  the implementation MUST acquire-read that exact source word, take a fresh
  stable snapshot of the separately decoded slot binding, and acquire-read the
  same source word again. If either source read no longer equals the exact raw
  reference word, unlink/reclaim/reuse or summary replacement has overtaken the
  cached observation and the caller MUST perform a budgeted fresh lookup or
  maintenance retry instead of reporting corruption. Only an unchanged exact
  reference word around a repeated invalid slot snapshot may fail closed.
  Directory-location publication MUST apply the same rule to one joint tuple:
  canonical mutation, exact operation, current location, slot control,
  immutable directory binding, and every selected or competing target cell.
  Before returning `CorruptStore`, two stable acquire collects MUST be followed
  by exact no-op compare/exchange confirmation of the atomic tuple members and
  a fresh immutable-binding read; any loss is progress or retry, not corruption.
  For `Unlink/Prepared`, the first valid location publication wins arbitration;
  a losing helper MUST exact-clear only its distinct recovered old binding and
  preserve an empty or structurally valid replacement target. If
  `Unlink/TargetSelected` later finds another structurally valid
  same-generation location, it MUST exact-clean both old-binding witnesses and
  the alternate location while preserving any replacement it does not own.
  After a location CAS, loss of the exact unlink source MUST withdraw that
  helper's exact old target and location; an exact committed `Insert` successor
  or other valid replacement MUST remain untouched. A structurally valid older
  location is exact-cleanable residue. A future-generation location is benign
  reuse only when another member of the old tuple proves movement; if the exact
  old-generation tuple remains stable around that future location, the shape is
  corruption and the future word is preserved for diagnosis. Every
  slot control used to classify a live directory reference MUST also have a
  valid generation, state, and owner shape:
  `Initializing`/`Reserved` require a structurally valid configured participant
  token; `Free`, `Published`, `RemoveRequested`, `Aborting`, `Reclaiming`, and
  `Retired` require participant zero; and `Retired` requires the terminal
  generation. Only a structurally valid strictly newer control may classify an
  old binding as stale. This revalidation MUST add no shared counter, lock, or OS
  synchronization.
- **FR-053**: Layout v2 MUST store the creator's exact Linux PID-namespace
  numeric identity in the header and the registering process's identity in each
  participant record; Windows values MUST be zero. The header MUST also contain
  an aligned atomic recovery mode that begins `Enabled` only when the creator's
  identity is proven. A different or unproven Linux opener MUST release-publish
  irreversible `Mixed` before its first `Registering` CAS and MUST retain
  ordinary KV access. Recovery MUST snapshot participant control before acquire-
  loading the mode. In Mixed, a partial `Registering` identity MUST be
  Unsupported and preserved; a stable Active identity MAY be classified only
  after its per-record namespace exactly matches the caller's current namespace.
  Namespace mismatch or inability to prove the current namespace MUST become
  Unsupported before PID/start lookup. Closing/Recovering handoffs remain
  helpable. Header offsets 264/272 and participant offset 32 MUST be compatibility-
  fenced by required-feature bit 2 without changing existing offsets or strides.
- **FR-054**: Every current C# Linux mapped handle MUST derive a private
  mode-`0600` owner-liveness anchor from the unchanged GUID third field of its
  three-field `.owners` line, acquire an exclusive open-description `flock`
  before publishing that line, and hold it for the mapped-view lifetime. Under
  `.lifecycle`, a separately opened probe MUST classify a contended anchor as
  live regardless of PID visibility, an acquirable anchor as stale, a missing
  anchor by the existing PID/start-token fallback for older C#, C++, and Python,
  and access/symlink/directory/other ambiguity conservatively as live. A
  same-process registry MUST make local classification explicit. Cleanup MUST
  commit exact owner absence before deleting a stale anchor; orderly close MUST
  unmap before unlocking and MUST unlock only after exact sidecar absence or
  finalized exact-owner release-marker publication, while process death MUST
  release the lock automatically. The anchor mechanism MUST NOT change mapped
  layout, the owner-line format, the interoperable record locks, or any hot
  key-value path.
- **FR-055**: When a v2 operation proves persistent mapped structural
  corruption after the required exact-reference and race revalidation, it MUST
  atomically and irreversibly transition the shared store control from `Ready`
  to `Corrupt`. Every later public operation on every attached handle MUST
  acquire-check that control before projecting or mutating mapped data and
  return `CorruptStore`; opening the corrupt mapping MUST fail closed as
  `IncompatibleLayout`. Already borrowed spans cannot be revoked, but a later
  projection through an issued token MUST fail. The latch MUST NOT be set for
  malformed caller-owned input, documented token history, capacity,
  contention, cancellation, disposal, or a legal concurrent state change.
  Cleanup that discovers corruption MUST latch it without throwing and MUST
  preserve the malformed record. This per-operation check and the transition
  MUST use only mapped 64-bit atomics and MUST add no OS synchronization,
  process-held lock, or shared hot-path counter.
- **FR-056**: A cold create/open attempt MUST acquire the platform's required
  ordered lifecycle coordination before creating, opening, or mapping the
  physical region and MUST retain that coordination through header
  initialization or validation and handle registration. Only the attempt that
  proves it physically created the region MAY initialize an unpublished
  header; `OpenMode`, requested profile or dimensions, and observed zero bytes
  MUST NOT confer that authority. An already-existing zero header MUST remain
  byte-for-byte unchanged and return `AlreadyExists` for `CreateNew`,
  `StoreBusy` for `CreateOrOpen`, or `IncompatibleLayout` for `OpenExisting`.
  The caller's original wait and cancellation budget MUST cover the complete
  cold transaction. Ordered gates MUST be released before failed-open mapped
  resource cleanup that may re-enter outer lifecycle coordination, and no store
  handle may escape until resource ownership has been transferred exactly once.
- **FR-057**: Current C# and native Linux participants MUST use nonblocking
  open-file-description record locks on byte `[0,1)` for `.lock` and
  `.lifecycle`, fail closed as Unsupported when OFD locking is unavailable, and
  preserve exclusion across separately loaded managed assemblies and native
  modules in the same PID. Current cleanup MUST retain the empty mode-`0600`
  `.lock` inode as a stable rendezvous, and every successful, failed, initialized,
  or uninitialized teardown MUST retire ordinary synchronization after held gates
  are released but before mapped-region/owner cleanup can enter `.lifecycle`.
  Released traditional-record-lock participants MUST remain compatible across
  processes; concurrently mixing such an older implementation with a current
  implementation inside one OS process is explicitly unsupported because old
  process-associated locks can be released by closing any sibling descriptor.
  These rules MUST add no lock acquisition to a v2 steady-state key-value path.

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: The initial production-ready implementation MUST focus on the
  current C#/.NET package, including its public surface, tests, documentation,
  diagnostics, benchmarks, samples, and packaging.
- **LC-002**: The new lock-free mapped representation MUST use a compatibility
  identity distinct from layout v1.2; incompatible participants MUST fail before
  accessing payload memory.
- **LC-003**: Layout-v1.2 mappings MUST remain usable through their supported
  compatibility profile; no in-place reinterpretation or mixed synchronization
  participation is permitted.
- **LC-004**: The public key-value model MUST remain unchanged: keys identify
  values, acquisitions are shared read leases, and the store does not become a
  queue, stream, broker, dispatcher, or exclusive-claim work pool.
- **LC-005**: Existing public status numeric assignments MUST remain stable;
  new contention, compatibility, incarnation, or recovery outcomes MUST be
  appended or separated without renumbering the legacy contract.
- **LC-006**: Public contracts MUST define key equality, duplicate publication,
  reservation lifetime, exact-byte commit, value visibility, lease lifetime,
  logical removal, reclamation, token incarnation, recovery, waiting, disposal,
  and same-key race outcomes.
- **LC-007**: Public documentation MUST define system-wide lock-free progress
  and explicitly state that the feature does not guarantee wait-free completion
  or equal progress for every caller under sustained same-key contention.
- **LC-008**: Public documentation MUST separate empty/missing, duplicate,
  capacity, pending removal, local contention, timeout, cancellation, disposed,
  incompatible, unsupported recovery, and corruption outcomes.
- **LC-009**: Resource ownership documentation MUST state who owns each cold
  create/open transaction and its physical-initialization authority, store
  handle, reservation, borrowed writable view, read lease, recovery decision,
  and mapped-memory lifetime, including successful ownership transfer and
  failed-open cleanup ordering.
- **LC-010**: C++ and Python implementation of the new layout is outside this
  feature, but the shared-memory visibility, ordering, identity, progress, and
  recovery contracts MUST be documented without relying on managed-object
  identity.
- **LC-011**: The implementation plan MUST classify the NuGet semantic-version
  impact separately from the new mapped-layout compatibility version and include
  deployment and rollback guidance.
- **LC-012**: The core package MUST retain its minimal runtime dependency model
  and MUST NOT require a message broker, dispatcher, scheduler, service host,
  logging framework, or hidden worker.
- **LC-013**: Package-consumption and compatibility tests MUST cover legacy-only,
  lock-free-only, incompatible mixed-version, upgrade, and rollback scenarios.
- **LC-014**: Performance documentation MUST distinguish uncontended latency,
  same-key contention, unrelated-key concurrency, capacity pressure, recovery,
  and large-payload zero-copy measurements.
- **LC-015**: The layout-v2 contract MUST publish the generation-fenced
  directory-reference representation and the 1,048,575-slot maximum as
  cross-runtime compatibility rules. Clients that do not implement the exact
  representation MUST reject the mapping rather than infer or reinterpret it.
- **LC-016**: Layout-v2 mappings using the versioned spill-summary codec,
  publication-intent metadata, and PID-namespace recovery identity MUST carry
  required-feature bits 0, 1, and 2, respectively, and the exact required-
  features mask MUST be 7. Earlier required-features-zero, bit-0-only, and
  mask-3 v2 binaries and current binaries MUST
  reject one another before payload projection; this pre-release compatibility
  fence MUST be reflected in executable constants, fixtures, and protocol
  documentation without changing the 2.0 topology.

### Key Entities *(include if feature involves data)*

- **Shared-Memory Key-Value Store**: One bounded named mapping containing the
  key index, value generations, reservations, leases, and reusable storage.
- **Key**: An opaque non-empty byte sequence compared by exact bytes and used
  solely to address the current visible value.
- **Publication Reservation**: Temporary ownership of bounded store capacity for
  one key, fixed descriptor, and announced payload length before visibility.
- **Publication Intent**: Immutable per-lifecycle ordinary metadata that
  distinguishes an explicitly returned reservation from the tentative internal
  reservation used by one atomic convenience publication.
- **Published Value Generation**: One complete immutable descriptor and payload
  currently addressable by a key and protected from reuse by its lifecycle
  identity and active leases.
- **Read Lease**: A shared, zero-copy, read-only protection token for one exact
  published value generation; several leases may coexist.
- **Pending Removal**: A logically absent value generation retained only until
  its protecting leases are released or safely recovered.
- **Participant Incarnation**: Identity that distinguishes a particular owner
  and token lifetime from later reuse of process identifiers or shared records.
- **Recovery Decision**: A caller-controlled classification and mutation that
  restores only safely abandoned reservation or lease state.
- **Diagnostics Snapshot**: Bounded consumer-requested measurements of store
  capacity, lifecycle state, contention, recovery, and index health.
- **External Key Dispatcher**: An application-owned broker or coordinator that
  may send keys to workers but has no ownership role inside the store contract.

## Success Criteria *(mandatory)*

### Benchmark Workload Matrix

Relative performance criteria use the same machine, operating system, runtime,
store capacities, process placement, warm-up, and measurement window for the
legacy and lock-free profiles. Throughput scenarios run three trials with at
least 10 seconds of warm-up and 60 seconds of measurement per trial; reported
results use the median trial. Participants receive one logical processor each
where the host permits it, without oversubscribing the host, and the report
records processor allocation, OS, runtime, payload size, key distribution,
lease duration, churn pattern, and final statuses.

| Workload | Participants | Data and access pattern | Primary measurements |
|---|---|---|---|
| Tiny operation baseline | 1, 2, 4, 8, and 12 independent processes | 8-byte keys, 1-byte payloads, distinct rotating keys; publish/remove and acquire/release cycles with immediate release | Aggregate operations/second and p50/p95/p99 latency |
| Same-key broadcast reads | 1, 2, 4, 6, 8, and 12 readers | One stable key, 256-byte payload, full-payload checksum, immediate release | Aggregate acquire/read/release throughput, p99, and checksum equality |
| Distributed-key reads | 1, 2, 4, 6, 8, and 12 readers | 256 stable keys selected uniformly, 256-byte payloads, full-payload checksum, immediate release | Scaling, p99, misses, and checksum equality |
| Primary broker-directed workload | One zero-copy producer, 1 or 12 broker-directed readers, and one observer | 1.3 MB frames, 16-byte descriptors, 256 rotating keys; broker sends each committed key to one reader, observer samples overlapping keys, cleanup removes only after assigned processing | Publication rate, end-to-end key availability, reader throughput, allocations, copies, and status outcomes |
| Mixed lifecycle churn | 12 readers plus 2 publisher/remover processes | At least 256 live keys, collision-heavy key set, random publish/acquire/release/remove/reuse operations for 10,000,000 cycles | Per-key correctness, throughput stability, early/late p99, and capacity recovery |
| Participant suspension | Same as distributed-key and mixed-churn workloads | Suspend one participant for 30 seconds at each observable steady-state transition | Healthy-process throughput, affected keys/capacity, and progress |
| Large-payload ingest | One producer plus 1 and 12 readers | 100,000 frames of 1.3 MB each using direct reservation, exact-byte commit, broker-directed acquire, and safe removal | Producer-owned allocation, library copies, throughput, and payload checksums |

### Measurable Outcomes

- **SC-001**: A 100,000,000-operation mixed run with concurrent publication,
  reservation, commit, acquire, projection, release, remove, and reuse across at
  least 12 reader processes and additional publisher/remover processes completes
  with zero partial payloads, mixed generations, false successful removals,
  stale-token mutations, access violations, deadlocks, or global livelocks.
- **SC-002**: In the same-key broadcast-read workload, 6 reader processes
  achieve at least 4 times one-reader acquire/read/release throughput and 12
  readers achieve at least 7 times one-reader throughput.
- **SC-003**: In the distributed-key workload, 6 reader processes achieve at
  least 4.5 times one-reader throughput and 12 readers achieve at least 8 times
  one-reader throughput.
- **SC-004**: In the primary broker-directed workload, the zero-copy producer
  with 12 readers sustains at least 80% of its publication rate measured with
  one reader.
- **SC-005**: In each participant-suspension workload, pausing one participant
  for 30 seconds leaves the same set of healthy processes operating on unrelated
  suitable keys at least 90% of their own pre-suspension aggregate throughput
  while capacity permits; the paused participant is excluded from both sides of
  the comparison.
- **SC-006**: In the 8-process tiny-operation workload,
  the Windows lock-free profile delivers at least 4 times layout-v1.2 aggregate
  throughput and reduces p99 acquire/release and publish/remove latency by at
  least 80%. On Linux, for both acquire/release and publish/remove, lock-free
  one-process p99 is no greater than layout-v1.2 one-process p99, lock-free
  8-process aggregate throughput is no lower than layout v1.2, and lock-free
  8-process p99 is at most 3 times its own one-process p99 and at most 10
  microseconds absolute. Every sampled lock-free trial at both process counts
  has maximum latency at most 10 milliseconds. This separates intrinsic
  latency and contention amplification from the legacy file-lock incumbent's
  serialized completion distribution.
- **SC-007**: Instrumented steady-state validation records zero dependency on a
  process-owned or globally exclusive synchronization owner for successful
  publication, lookup, lease, removal, reclamation, and index-maintenance paths.
- **SC-008**: After initialization and warm-up in the primary and tiny-operation
  workloads, publication/reservation/commit and acquire/projection/release each
  report 0 bytes of runtime heap allocation per operation across at least
  1,000,000 operations.
- **SC-009**: In the large-payload ingest workload, all 100,000 direct publications
  complete without a producer-owned full-payload allocation, without a
  library-level full-payload copy after the producer fills reserved memory, and
  without any reader payload copy required by the library.
- **SC-010**: Across 10,000 injected reservation-owner and lease-owner
  termination cycles, explicit recovery exposes zero partial publications,
  reclaims zero live ownership, accepts zero stale token actions, and restores
  all capacity classified as safely recoverable.
- **SC-011**: Exhaustive controlled race tests for atomic-publish/atomic-publish,
  atomic-publish/explicit-reserve, explicit-reserve/explicit-reserve,
  commit/acquire, acquire/remove, release/reclaim, supported recovery/live
  activity, and disposal/operation produce only the documented outcome sets
  across at least 1,000,000 repetitions per race family.
- **SC-012**: Every bounded contention, capacity, recovery, cancellation, and
  wait test returns within the caller's selected limit plus 250 milliseconds and
  leaves no leaked reservation or lease ownership.
- **SC-013**: Existing layout-v1.2 contract tests, lock-free profile contract and
  integration tests, package-consumption tests, full release tests, and package
  creation all pass in the release configuration.
- **SC-014**: A consumer can follow the documented sample to publish directly,
  distribute keys through its own broker, read from 6-12 workers and an
  additional observer process, remove values safely, and recover a terminated
  participant without reading implementation source.
- **SC-015**: In a barrier-controlled test, 12 processes simultaneously lease
  one key and observe the same checksum; removal becomes pending, all 12 views
  remain valid, no new lease succeeds after removal, and exactly one safe
  reclamation occurs after the final release.
- **SC-016**: The 10,000,000-cycle mixed lifecycle churn workload
  completes without corruption or leaked safely recoverable capacity, and its
  late-run missing-key and publication p99 latency remains within 2 times its
  early-run p99 latency on the same environment.
- **SC-017**: In controlled collision-heavy tests, a participant is paused at
  every directory-mutation transition while other participants complete the
  operation, reclaim the referenced slot, and reuse it for a later generation.
  Across at least 1,000,000 repetitions, the resumed participant performs zero
  mutations against the later generation and all capacity remains recoverable.
- **SC-018**: In three trials of at least 10,000 complete collision-heavy spill
  churn cycles, diagnostics observe a real spill and nonzero real cleanup scans,
  then report logical spill count zero and overflow occupancy zero before the
  late missing-key window. That late window adds zero overflow scans, has zero
  correctness failures, and its p99 latency is at most 2 times the corresponding
  early missing-key p99 on the same environment.

## Assumptions

- SharedMemoryStore remains a key-value store. Load balancing, key delivery,
  retries, acknowledgements, and worker scheduling belong to an external broker
  or application layer.
- Multiple processes may acquire the same key concurrently. The store does not
  infer exclusive processing ownership from a read lease.
- The primary measured workload has one zero-copy producer and 6-12 workers, but
  the public store remains valid for additional readers, publishers, removers,
  diagnostic callers, and non-broker use cases.
- Values are immutable after publication. Updating a key continues to use the
  documented remove-and-republish lifecycle; atomic in-place mutation and an
  additive atomic-replace API are outside this feature.
- The store provides per-key operation semantics but no cross-key transaction,
  atomic batch, or total ordering guarantee.
- Capacity remains fixed at store creation. Lock-free progress does not imply
  unlimited storage, guaranteed success, or immunity from capacity retained by
  valid leases.
- Layout 2.0 has a configurable participant-record capacity, defaulting to 64
  open store handles. Each handle consumes one record so its identity is present
  in the first atomic slot/lease claim; exhaustion rejects a new open without
  affecting already-open handles or steady-state data progress.
- Layout 2.0 intentionally caps value-slot capacity at 1,048,575 so every
  persistent directory intent and location can carry enough value-generation
  identity to reject stale helpers after slot reuse using the portable atomic
  contract. Larger lock-free stores require a future mapped-layout version
  rather than weakening the generation fence.
- Lock-free is a system-wide progress guarantee, not a wait-free guarantee for
  each caller. Same-key contention may cause bounded retries or a documented
  contention outcome.
- Store creation, opening, and final cleanup are cold paths and may use bounded
  lifecycle coordination. Explicit recovery may coordinate owner classification
  and the records it changes, but neither lifecycle nor recovery coordination
  may become an operation lock required by healthy steady-state data access.
- No hidden background worker performs maintenance, reclamation, recovery,
  diagnostics, retries, or notification on behalf of callers.
- The deployment boundary remains trusted same-host processes. Cross-host
  behavior, persistence guarantees, and protection from malicious mapped-memory
  writers are outside scope.
- The first implementation targets the C#/.NET package on its currently
  supported Windows, Linux, and same-host container environments.
- C++ and Python support for the new mapped contract is deferred. Those clients
  must reject it until they implement the documented protocol.
