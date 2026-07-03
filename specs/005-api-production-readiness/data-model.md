# Data Model: API Production Readiness

## Primary Store Identity

Represents the public name and namespace consumers use to create, open, and
operate a store.

**Fields and relationships**:
- `Namespace`: remains `SharedMemoryStore`.
- `PrimaryType`: becomes `MemoryStore`.
- `PackageId`: remains `SharedMemoryStore`.
- `MigrationFrom`: previous pre-release type `SharedMemoryStore`.
- `Examples`: docs, samples, and package-consumption tests that import the final
  namespace and reference `MemoryStore` directly.

**Validation rules**:
- Public examples must compile without namespace/type aliasing.
- Release notes must list every renamed type, namespace, member, and status.
- Contract tests must reject accidental reintroduction of
  `SharedMemoryStore.SharedMemoryStore` as the primary public type.

## Reservation Write Access

Temporary mutable access to bytes reserved for one pending value.

**Fields and relationships**:
- `Reservation`: `ValueReservation` token that owns the pending slot reference.
- `WritableSpan`: stack-scoped `Span<byte>` over remaining reservation bytes.
- `BytesWritten`: number of bytes advanced by the producer.
- `PayloadLength`: announced payload length.
- `LifecycleState`: active, committed, aborted, disposed, recovered, or
  store-disposed.
- `SlotLifecycleId`: internal generation and reuse epoch used to reject stale
  reservation tokens.

**Validation rules**:
- Writable access is available only while the reservation is active.
- Retained safe public handles must not mutate bytes after commit, abort,
  disposal, recovery, store disposal, or slot reuse.
- Commit succeeds only when `BytesWritten == PayloadLength`.
- Advance fails when it would exceed `PayloadLength`.
- General public API does not return retained writable `Memory<byte>`.

**State transitions**:
- Active -> Committed when all announced bytes are advanced and commit succeeds.
- Active -> Aborted when `Abort` succeeds.
- Active -> Disposed when `Dispose` aborts the reservation.
- Active -> Recovered when recovery reclaims stale pending reservation state.
- Any completed state -> InvalidReservation or ReservationAlreadyCompleted for
  later token operations, according to the operation contract.

## Operation Wait Policy

Caller-visible rules for synchronization waits.

**Fields and relationships**:
- `Timeout`: finite wait limit, zero for immediate try, or explicit infinite
  wait for legacy-style behavior.
- `CancellationToken`: optional cancellation signal.
- `DefaultPolicy`: one-second bounded default used by overloads without an
  explicit wait policy.
- `OperationFamily`: open/create, publish, reserve, segmented publish, acquire,
  remove, recover, diagnostics, lease release, reservation advance, reservation
  commit, and reservation abort.
- `ContentionOutcome`: `StoreBusy` or equivalent open status.
- `CancellationOutcome`: `OperationCanceled` or equivalent open status.

**Validation rules**:
- Undefined or negative non-infinite timeouts are invalid.
- Timeout must be observed before mutating shared state when synchronization was
  not acquired, with tests allowing at most 250 milliseconds of scheduler
  tolerance.
- Cancellation before acquisition returns only the documented cancellation
  outcome for that API family.
- Store disposal while waiting returns the documented disposed lifecycle outcome.

## Store Configuration

Consumer-provided values needed to create or open the bounded shared-memory
layout.

**Fields and relationships**:
- `Name`: OS-visible mapping name.
- `OpenMode`: create, open existing, or create-or-open.
- `SlotCount`, `LeaseRecordCount`, `MaxKeyBytes`, `MaxDescriptorBytes`,
  `MaxValueBytes`: logical capacities.
- `TotalBytes`: mapped-region size, either supplied or derived.
- `EnableLeaseRecovery`: explicit recovery capability flag.
- `ValidationResult`: public validation details.

**Validation rules**:
- Name must be nonempty, not whitespace, not contain null characters, and fit
  documented length limits.
- Undefined `OpenMode` values are invalid options.
- Required capacities must be positive except descriptor bytes, which may be
  zero.
- Required size calculations must detect overflow.
- `TotalBytes` must be at least the calculated layout size.
- Consumers can construct ordinary valid options without manually copying
  internal layout constants.

## Status Outcome

Documented result category returned from public operations.

**Fields and relationships**:
- Existing validation statuses: duplicate key, not found, key too large, value
  too large, descriptor too large.
- New or corrected statuses: invalid key, store busy, operation canceled.
- Lifecycle statuses: invalid lease, lease already released, invalid
  reservation, reservation incomplete, reservation already completed, store
  disposed.
- Capacity and platform statuses: store full, lease table full, unsupported
  platform, access denied, corrupt store, unknown failure.

**Validation rules**:
- Status names must describe the condition they represent.
- Contention outcomes are distinct from validation, capacity, lookup, and
  lifecycle outcomes.
- Empty keys return invalid key, not key too large.
- Canceled waits return cancellation status, not store busy.

## Diagnostics Failure Summary

Consumer-visible diagnostics for operation failures and store health.

**Fields and relationships**:
- `LastFailureStatus`: most recent non-success status observed by the handle.
- `GetFailureCount(StoreStatus)`: stable aggregate access by status.
- Capacity, slot, lease, reservation, index, recovery, and tombstone pressure
  summary properties.
- Pruned per-status convenience names that duplicate `GetFailureCount`.

**Validation rules**:
- Every non-success public status increments the aggregate count for that
  status exactly once per failed operation.
- Removed or obsolete convenience names must have migration notes.
- Diagnostics access does not write to console, start background work, or mutate
  store contents.

## Production Integration Surface

Optional service-oriented adapter boundary for hosting, health, graceful
shutdown, and cleanup or recovery workflows.

**Fields and relationships**:
- `CorePackage`: `SharedMemoryStore`, dependency-light and BCL-only.
- `IntegrationPackage`: optional adapter package or sample, separate from core.
- `HealthProbe`: narrow health result over diagnostics and open status.
- `LifecycleService`: optional start, stop, shutdown, dispose, and recovery
  behavior.
- `ConfigurationValidation`: hosted validation wrapper over core options.

**Validation rules**:
- Installing the core package must not install hosting dependencies.
- Any hosting adapter must be opt-in and separately packaged or sampled.
- No broad interface may mirror every method of the concrete store.
- Any interface must represent a focused consumer boundary such as health,
  lifecycle, read, or write behavior.
