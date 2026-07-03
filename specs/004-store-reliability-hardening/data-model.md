# Data Model: Store Reliability Hardening

## LeaseOwner

Represents the process or store-handle identity recorded for a reader lease.

**Fields**:
- `OwnerProcessId`: process id captured when the lease is activated.
- `OwnerKind`: `CurrentProcess`, `OtherLiveProcess`, `StaleProcess`,
  `Unknown`, or `Unsupported`.
- `ObservedAtSequence`: store sequence or scan order used for diagnostics.

**Relationships**:
- Belongs to one active `LeaseRecord`.
- Is evaluated by explicit lease recovery.
- May be reported through `LeaseRecoveryReport` and diagnostics.

**Validation Rules**:
- current-process identity is recoverable only when the caller opts into
  current-process recovery.
- other live process identity is never recoverable by current-process recovery.
- stale owners may be recovered when liveness can be evaluated safely.
- unsupported or ambiguous owner checks must not mutate active leases.

## LeaseRecord

Shared lease registry entry that protects one slot lifecycle identity.

**Fields**:
- `LeaseRecordId`: zero-based lease record index.
- `State`: `Free`, `Active`, `Released`, or `Abandoned`.
- `SlotIndex`: protected slot index.
- `SlotLifecycleId`: lifecycle identity captured during acquire.
- `Owner`: lease owner identity.
- `AcquireSequence`: sequence value captured during acquire.

**Relationships**:
- Points to one `SlotLifecycle`.
- Is referenced by one `ValueLease` token.
- Prevents storage reuse while active.

**Validation Rules**:
- release validates record id, slot index, and full lifecycle identity.
- recovery may mark an active record abandoned only after owner policy allows
  it.
- recovery must decrement slot usage only for records it actually recovered.
- mismatched slot lifecycle identity is reported as failed or unsafe and does
  not change visible values.

## LeaseRecoveryReport

Consumer-visible summary returned by explicit stale lease recovery.

**Fields**:
- `ScannedRecordCount`: lease records inspected.
- `RecoveredLeaseCount`: active records reclaimed.
- `ActiveLeaseCount`: active records skipped because the owner is still live or
  not eligible under the requested policy.
- `UnsupportedLeaseCount`: records whose owner liveness could not be evaluated
  safely on the current platform.
- `FailedRecoveryCount`: records rejected because shared state was inconsistent
  or unsafe to mutate.

**Relationships**:
- Produced by `TryRecoverLeases`.
- Feeds caller-owned logging, metrics, and release evidence.
- Updates recovery diagnostics without writing to console.

**Validation Rules**:
- reports must distinguish active live-owner skips from unsupported checks.
- disabled recovery returns a deterministic unsupported outcome and does not
  mutate active records.
- report counts must add up to the scanned active and inactive record decisions
  documented by the contract.

## StoreLifecycleState

State observed by public operations and token operations around disposal.

**Fields**:
- `State`: `Open`, `Disposing`, or `Disposed`.
- `DisposeStartedSequence`: optional sequence captured when disposal begins.
- `DisposeCompleted`: indicates all owned resources were released.

**Relationships**:
- Owned by one `SharedMemoryStore` handle.
- Governs public store methods, lease tokens, reservation tokens, and
  diagnostics.
- Protects access to the mapped region and synchronization primitives.

**Validation Rules**:
- disposal is idempotent under repeated or concurrent calls.
- operations that enter before disposal either complete normally or observe a
  documented disposal outcome.
- operations after disposal return `StoreDisposed`, invalid, already-completed,
  or an empty diagnostic snapshot according to their contract.
- no public operation exposes internal disposed-resource exceptions.

## OperationLifecycleOutcome

Documented result category for a public operation racing with disposal.

**Fields**:
- `OperationName`: public method or token method.
- `BeforeDisposeOutcome`: normal documented statuses.
- `DuringDisposeOutcome`: success if completed first, otherwise
  `StoreDisposed`, invalid, or already-completed.
- `AfterDisposeOutcome`: deterministic post-disposal result.

**Relationships**:
- Described in lifecycle and error contracts.
- Covered by disposal race stress tests.

**Validation Rules**:
- span and memory accessors return empty views after disposal.
- lease release after store disposal returns `StoreDisposed` when the owning
  handle can report it, or `InvalidLease` for default tokens.
- reservation advance, commit, and abort after store disposal return
  `StoreDisposed` when the owning handle can report it, or invalid outcomes for
  default tokens.

## ProbeCursor

Long-running search position for bounded slot and lease-record allocation.

**Fields**:
- `CursorValue`: monotonic integer used to distribute search starts.
- `TableCapacity`: slot count or lease record count.
- `CandidateIndex`: bounded candidate produced for each probe step.

**Relationships**:
- Used by reusable slot allocation.
- Used by lease record activation.
- Exercised by rollover boundary tests.

**Validation Rules**:
- candidate indexes are always within `0 <= index < TableCapacity`.
- arithmetic cannot throw overflow exceptions during normal operation.
- full tables return documented full statuses.
- capacity one is valid and must keep probing the only record deterministically.

## SlotLifecycleId

Stale-proof identity for a slot's current contents.

**Fields**:
- `SlotIndex`: zero-based slot index.
- `Generation`: per-slot generation component.
- `ReuseEpoch`: rollover component used when generation reaches its boundary.

**Relationships**:
- Captured by key index entries, lease records, reservation tokens, and
  `ValueLease` tokens.
- Advances before a slot becomes reusable after reclaim.
- Distinguishes current contents from stale handles.

**Validation Rules**:
- stale leases and reservations fail validation after any reclaim or lifecycle
  boundary transition.
- lifecycle advancement must not throw overflow exceptions.
- if the full identity cannot advance safely, the slot is not reused and the
  operation returns a deterministic failure.
- layout compatibility impact must be documented when shared records change.

## KeyIndexEntry

Open-addressed index entry that maps opaque keys to slot lifecycle identities.

**Fields**:
- `State`: `Empty`, `Occupied`, or `Tombstone`.
- `KeyHash`: stable hash of the key.
- `KeyLength`: key byte length.
- `SlotIndex`: referenced slot.
- `SlotLifecycleId`: lifecycle identity captured when the key was inserted.
- `KeyBytes`: inline key bytes.

**Relationships**:
- Points to published, pending-removal, or pending-reservation slot state.
- Tombstones preserve probe chains after removal.
- Rebuilt or compacted by index health management.

**Validation Rules**:
- duplicate detection requires hash and exact key equality.
- missing-key lookup stops only at an empty entry or after a bounded full-table
  probe.
- tombstones may be reused for new inserts.
- compaction must preserve occupied keys and their current slot lifecycle
  identities.

## IndexHealthSnapshot

Consumer-visible diagnostics for key-index health and tombstone pressure.

**Fields**:
- `IndexEntryCount`: configured index entry count.
- `OccupiedIndexEntryCount`: live occupied entries.
- `TombstoneIndexEntryCount`: tombstone entries.
- `EmptyIndexEntryCount`: entries available to terminate probe chains.
- `TombstonePressureRatio`: tombstones divided by index entries.
- `UsableIndexCapacity`: entries usable for new inserts before pressure.
- `LastObservedProbeLength`: most recent observed probe length for diagnostics.
- `MaxObservedProbeLength`: maximum observed probe length since handle open.
- `IndexCompactionCount`: number of synchronous internal compactions.

**Relationships**:
- Extends `DiagnosticsSnapshot`.
- Informs benchmark evidence and operational alerts.
- Does not expose key, descriptor, or payload bytes.

**Validation Rules**:
- snapshot creation is caller-controlled and allocation-conscious.
- diagnostics must distinguish live capacity pressure from tombstone pressure.
- disposed store diagnostics return a safe empty or last-known shape without
  accessing disposed mapped memory.

## TombstonePressurePolicy

Rules used to decide whether index health management is required.

**Fields**:
- `PressureThreshold`: tombstone or probe-cost threshold selected from
  benchmarks.
- `CompactionMode`: `None`, `DiagnosticOnly`, or `SynchronousInternal`.
- `LastCompactionSequence`: sequence at which synchronous compaction last ran.

**Relationships**:
- Consumes `IndexHealthSnapshot`.
- May trigger bounded synchronous index compaction under the store lock.
- Must not start background work.

**Validation Rules**:
- maintenance preserves visible values, duplicate-key detection, pending
  reservations, pending removals, active leases, and slot reuse rules.
- no public maintenance API is added unless benchmark evidence shows internal
  management is insufficient.
- compaction failure returns deterministic statuses and leaves the prior index
  state valid.

## ChurnWorkload

Benchmark workload used to validate index health under repeated unique-key
insert, remove, and lookup activity.

**Fields**:
- `ConfiguredCapacity`: slot count and index entry count.
- `UniqueKeyCount`: number of distinct keys exercised.
- `InsertCount`: total inserts attempted.
- `RemoveCount`: total removes attempted.
- `MissingLookupCount`: missing-key probes attempted.
- `CleanIndexBaseline`: measured latency with no tombstone pressure.
- `ManagedPressureLatency`: measured latency after pressure management.

**Relationships**:
- Drives `TombstonePressureBenchmarks`.
- Produces release evidence for the selected maintenance policy.

**Validation Rules**:
- missing-key lookup and new-key insert latency after pressure management stays
  within 2x of a clean-index baseline at the same configured capacity.
- diagnostics identify pressure before the benchmark reaches 75% of measured
  worst-case probe cost.
