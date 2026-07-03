# Feature Specification: Store Reliability Hardening

**Feature Branch**: `004-store-reliability-hardening`

**Created**: 2026-07-02

**Status**: Draft

**Input**: User description: "Add a feature to address issues found in review. Do a smart analysis if those fixes are needed, and create a spec."

## Review Analysis & Scope

The review identified four reliability issues that should be addressed before
adding more capability on top of the store:

- **Lease recovery ownership is required**: explicit recovery must never reclaim
  a lease owned by another live process when the caller only requested
  current-process recovery. This is a correctness and data-safety issue because
  early slot reuse can invalidate active readers.
- **Disposal concurrency hardening is required**: public operations are
  documented as thread-safe and deterministic. A race with disposal must return
  a documented store-disposed outcome, not surface raw lifecycle exceptions from
  internal synchronization or mapped-memory resources.
- **Long-running lifecycle rollover hardening is required**: a hot
  shared-memory service must remain safe after large numbers of reserve,
  acquire, release, remove, and reuse cycles. Probe cursors and generation
  identifiers must not produce invalid indexes, runtime overflow failures, or
  stale handle validation.
- **Tombstone pressure management is required, but should be evidence-led**:
  high churn over many unique keys can turn missing-key and insert probes into
  near full-table scans. The first step is diagnostic visibility and a churn
  benchmark; any new public maintenance API must be justified by that evidence.

The following review notes are intentionally out of scope for this feature:

- A first-class store-backed writer or pipeline adapter is a performance
  convenience, not necessary to correct the reliability defects.
- Versioned replacement semantics are useful future behavior, but they change
  publication semantics and should receive their own specification.
- Updating the completed zero-copy ingest spec status from Draft is documentation
  hygiene and can be handled separately from runtime reliability hardening.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Recover Only Eligible Leases (Priority: P1)

An operator or owning service invokes explicit lease recovery to clean up stale
reader leases without invalidating readers that are still alive in other
processes.

**Why this priority**: Incorrect recovery can allow storage reuse while another
process still holds a valid reader lease. That breaks the store's core safety
contract.

**Independent Test**: Open the same store from multiple process owners, acquire
leases from both owners, run recovery with current-process recovery enabled in
one owner, and verify only eligible leases are recovered while other live-owner
leases remain valid and continue protecting their storage.

**Acceptance Scenarios**:

1. **Given** a live reader in another process holds a lease, **When** the current
   process runs explicit recovery with current-process lease recovery enabled,
   **Then** the other process's live lease remains valid and its slot is not
   reclaimed or reused before release.
2. **Given** the current process holds an active lease and explicitly opts into
   current-process recovery, **When** recovery runs, **Then** that lease is
   recovered and future use of the old lease reports a deterministic released or
   invalid outcome.
3. **Given** a lease belongs to a process that can no longer be verified as
   alive, **When** recovery runs, **Then** the store either recovers the stale
   lease or reports that the lease could not be evaluated safely without
   changing visible value contents.
4. **Given** lease recovery is disabled for the store, **When** recovery is
   requested, **Then** no active lease is changed and the operation returns the
   documented unsupported outcome.

---

### User Story 2 - Return Deterministic Outcomes During Disposal Races (Priority: P2)

A service can dispose a store handle while other threads are concurrently
publishing, reserving, acquiring, removing, recovering, reading diagnostics, or
releasing leases, and each operation completes with a documented outcome.

**Why this priority**: Consumers rely on the package for shared infrastructure.
Racing disposal should be a normal lifecycle boundary, not a source of
unhandled exceptions or corrupted state.

**Independent Test**: Repeatedly run concurrent operations against a store while
disposing the store handle from another thread, and verify every operation
either completes before disposal or returns a documented store-disposed or
already-completed result after disposal.

**Acceptance Scenarios**:

1. **Given** one or more operations are about to enter the store, **When** the
   store handle is disposed concurrently, **Then** no public operation exposes an
   internal disposed-resource exception to the caller.
2. **Given** disposal has completed, **When** any public mutating or read
   operation is invoked on the disposed handle, **Then** it returns the
   documented store-disposed outcome or an empty diagnostic snapshot as
   documented.
3. **Given** a reader lease or reservation token outlives the owning store
   handle, **When** the token is inspected, advanced, committed, aborted,
   released, or disposed, **Then** it reports a deterministic invalid,
   already-completed, or store-disposed outcome and does not expose mapped
   memory.
4. **Given** multiple callers dispose the same store handle concurrently,
   **When** disposal completes, **Then** the operation is idempotent and all
   subsequent store operations observe the disposed lifecycle state.

---

### User Story 3 - Preserve Safety Across Long-Running Rollover (Priority: P3)

A long-running shared-memory service can continue reserving slots, activating
leases, removing values, and reusing storage after very large operation counts
without arithmetic rollover producing invalid indexes or stale handles.

**Why this priority**: The store is intended for hot production services.
Rollover defects may not appear in short tests but can become severe after
extended uptime.

**Independent Test**: Drive probe cursors and slot lifecycle identifiers through
their rollover boundaries in controlled tests, then continue normal store
operations and verify indexes stay within bounds and stale leases or
reservations never regain validity.

**Acceptance Scenarios**:

1. **Given** slot search has advanced through its rollover boundary, **When**
   the store reserves additional values, **Then** every candidate slot considered
   is within configured capacity and reservation either succeeds or reports
   capacity pressure deterministically.
2. **Given** lease-record search has advanced through its rollover boundary,
   **When** readers continue to acquire and release values, **Then** every lease
   record considered is within configured capacity and full tables return the
   documented full outcome.
3. **Given** a slot has been reused enough times to reach a lifecycle identifier
   boundary, **When** older leases or reservations are used, **Then** stale
   handles are not accepted for the current slot contents.
4. **Given** a rollover boundary is reached under concurrent publish, acquire,
   remove, release, reserve, commit, abort, and recovery activity, **When** the
   workload continues, **Then** the store remains available or reports a
   documented deterministic failure without corrupting visible values.

---

### User Story 4 - Detect and Control Tombstone Pressure (Priority: P4)

A service owner can see when key-index tombstones are degrading churn-heavy
workloads and can rely on bounded index-health behavior without guessing from
application latency alone.

**Why this priority**: Tombstone buildup is a production performance risk rather
than an immediate correctness failure. It should be measured and bounded before
adding larger public API surface.

**Independent Test**: Run a high-churn workload that repeatedly inserts,
removes, and probes many unique keys, verify diagnostics expose tombstone
pressure, and verify the store keeps lookup and insert behavior within the
defined success criteria after pressure management.

**Acceptance Scenarios**:

1. **Given** many unique keys have been inserted and removed, **When** a
   diagnostic snapshot is requested, **Then** the owner can distinguish live
   entries, tombstone pressure, and remaining usable key-index capacity.
2. **Given** tombstone pressure exceeds the documented health threshold, **When**
   the store continues serving missing-key lookups and new inserts, **Then** it
   avoids sustained near full-table probe behavior.
3. **Given** committed values and active reader leases exist while tombstone
   pressure is managed, **When** index health is restored, **Then** visible
   values, duplicate-key detection, and lease protection remain correct.
4. **Given** benchmark evidence does not justify a new public maintenance
   operation, **When** this feature is planned, **Then** the solution remains
   internal or diagnostic-only rather than expanding the public API surface.

### Edge Cases

- Current-process recovery is enabled while another live process holds a lease
  for the same or a different slot.
- Owner liveness cannot be evaluated safely because the platform does not
  expose the needed process information or the owner identity is ambiguous.
- A process exits after acquiring a lease, and recovery runs while values are
  concurrently removed or republished.
- Lease recovery observes a record whose slot generation no longer matches the
  current slot metadata.
- Disposal races with each public operation, including recovery and diagnostic
  snapshot creation.
- Disposal races with lease release, reservation advance, reservation commit,
  reservation abort, and token disposal.
- The store is disposed while callers still hold spans or memory views from
  leases or reservations.
- Slot capacity or lease-record capacity is one, forcing every operation to
  revisit the same record.
- Probe cursors roll over while the table is empty, full, and partially full.
- Slot lifecycle identifiers reach a boundary while older leases or
  reservations still exist.
- The key index contains only empty entries, only tombstones, a mix of occupied
  and tombstone entries, and no empty entries.
- A removed key is inserted again after tombstone pressure management.
- Missing-key lookups run against a churned index where the key has never
  existed.
- Diagnostics are requested while tombstone pressure management is active.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The library MUST recover only leases that are eligible under the
  requested owner policy.
- **FR-002**: The library MUST NOT recover or decrement ownership for a lease
  held by another live owner when the caller only opted into current-process
  lease recovery.
- **FR-003**: The library MUST preserve slot reuse protection for every live
  lease that is skipped by recovery.
- **FR-004**: Lease recovery MUST distinguish recovered leases, still-active
  leases, unsupported owner checks, and unsafe or inconsistent records through
  consumer-visible reporting or diagnostics.
- **FR-005**: Lease recovery MUST keep disabled or unsupported recovery
  deterministic and MUST NOT mutate active leases in those cases.
- **FR-006**: Every public store operation MUST handle a concurrent dispose
  boundary without exposing internal disposed-resource exceptions to callers.
- **FR-007**: After disposal completes, public operations MUST return documented
  disposed, invalid, empty, or already-completed outcomes according to the
  operation's contract.
- **FR-008**: Store disposal MUST be idempotent under repeated or concurrent
  calls.
- **FR-009**: Reader leases and reservation tokens MUST become unable to expose
  store memory after the owning store handle is disposed.
- **FR-010**: Slot probing MUST remain within configured slot capacity across
  long-running cursor rollover.
- **FR-011**: Lease-record probing MUST remain within configured lease-record
  capacity across long-running cursor rollover.
- **FR-012**: Slot lifecycle identifiers MUST prevent stale leases and
  reservations from becoming valid again after any reuse or rollover boundary.
- **FR-013**: Rollover behavior MUST be covered by deterministic tests that can
  reach boundary conditions without requiring impractical wall-clock runtimes.
- **FR-014**: The library MUST expose enough key-index health information for a
  consumer to identify tombstone pressure separately from live-entry capacity
  pressure.
- **FR-015**: The feature MUST include a high-churn benchmark that measures
  missing-key lookup behavior and insert behavior before and after tombstone
  pressure management.
- **FR-016**: The library MUST prevent tombstone accumulation from causing
  sustained near full-table scans for normal missing-key lookups and inserts.
- **FR-017**: Tombstone pressure management MUST preserve visible values,
  duplicate-key detection, reader lease protection, and slot reuse behavior.
- **FR-018**: Any new public maintenance operation for tombstone management MUST
  be added only if benchmark evidence shows diagnostics or internal management
  are insufficient to meet the success criteria.
- **FR-019**: The feature MUST preserve existing documented publish, reserve,
  acquire, remove, release, recovery, diagnostics, and package-consumption
  behavior except for correcting unsafe outcomes described in this spec.
- **FR-020**: Public documentation MUST describe the corrected recovery owner
  policy, disposal lifecycle outcomes, rollover safety guarantees, tombstone
  diagnostics, and any compatibility impact.

### Library Contract & Compatibility *(mandatory)*

- **LC-001**: The corrected lease recovery contract MUST state which owner
  categories may be recovered, which must be skipped, and how unsupported owner
  checks are reported.
- **LC-002**: Disposal lifecycle documentation MUST state the outcomes callers
  can expect before disposal, during a disposal race, and after disposal.
- **LC-003**: Rollover behavior MUST be documented as part of the store's
  long-running safety contract, including stale handle invalidation rules.
- **LC-004**: Tombstone diagnostics MUST be consumer-controlled and MUST NOT
  require direct console output, hidden background workers, or global mutable
  configuration.
- **LC-005**: Semantic version impact SHOULD be treated as a patch-level
  reliability fix when public surface stays compatible, or a minor pre-1.0
  package update if additive diagnostics or reporting members are required.
- **LC-006**: Future C++ and Python implementations or bindings MUST follow the
  same owner recovery, disposal, rollover, and tombstone-health semantics.
- **LC-007**: Any changed status, report, or diagnostic behavior MUST be covered
  by contract tests and release notes so consumers can update intentionally.

### Key Entities *(include if feature involves data)*

- **Lease Owner**: The process or store handle identity recorded for a reader
  lease and used to decide whether explicit recovery may reclaim that lease.
- **Lease Recovery Report**: Consumer-visible summary of records scanned,
  recovered, skipped as active, unsupported, or rejected as unsafe.
- **Store Lifecycle State**: The handle state before disposal, during disposal,
  and after disposal that determines whether public operations may access store
  resources.
- **Probe Cursor**: The long-running search position used to distribute slot
  and lease-record allocation attempts across bounded tables.
- **Slot Lifecycle Identifier**: The value that distinguishes current slot
  contents from stale leases or reservations created for earlier contents.
- **Key Index Tombstone**: A removed key-index entry that preserves probe chains
  but can increase future lookup and insert work.
- **Index Health Snapshot**: Consumer-visible diagnostics that describe live
  entry count, tombstone pressure, usable capacity, and pressure thresholds.
- **Churn Workload**: Repeated insert, remove, and missing-key lookup activity
  over many unique keys used to validate index health over time.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Across at least 10,000 multi-owner recovery cycles, 100% of leases
  held by other live owners remain valid after current-process recovery runs.
- **SC-002**: Across at least 10,000 stale-owner recovery cycles, eligible stale
  leases are either recovered or reported as unsupported or unsafe with no
  observed premature slot reuse for active leases.
- **SC-003**: Across at least 100,000 concurrent dispose-race operations, no
  public operation exposes an internal disposed-resource exception, memory access
  failure, or undocumented lifecycle outcome.
- **SC-004**: Boundary tests drive slot probing, lease-record probing, and slot
  lifecycle identifiers through rollover conditions and then complete at least
  1,000,000 additional operations without invalid indexes, arithmetic overflow
  failures, or stale handle acceptance.
- **SC-005**: In the churn benchmark, missing-key lookup and new-key insert
  latency after tombstone pressure management stays within 2x of a clean-index
  baseline at the same configured capacity.
- **SC-006**: Tombstone diagnostics identify pressure before the churn benchmark
  reaches 75% of the measured worst-case probe cost for the configured index.
- **SC-007**: Existing publish, reserve, acquire, remove, release, recovery,
  diagnostics, package, and documentation validation continues to pass after the
  reliability hardening is applied.
- **SC-008**: Release notes and public documentation allow a package consumer to
  understand the corrected recovery policy and disposal outcomes in under 10
  minutes without reading implementation internals.

## Assumptions

- The store remains a trusted same-host library; defending against a malicious
  process that can directly mutate shared memory is outside this feature.
- Owner liveness checks may be platform-dependent. When owner liveness cannot
  be evaluated safely, preserving live data safety is more important than
  aggressive recovery.
- The feature may add diagnostics or report detail if needed, but it should not
  add broader writer, pipeline, or versioned replacement capabilities.
- Tombstone management can be internal, explicit, or diagnostic-led; planning
  must choose the smallest option that meets the measurable outcomes.
- Existing zero-copy ingest behavior remains in scope only where it exercises
  shared lease, reservation, disposal, rollover, or diagnostics contracts.
- The completed zero-copy ingest spec status note is tracked as separate
  documentation cleanup unless planning deliberately batches it with release
  notes.
