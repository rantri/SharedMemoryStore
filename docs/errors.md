# Errors and Statuses

SharedMemoryStore reports expected operational outcomes as status values. The
complete status source is the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

Expected store pressure and lookup failures should be handled by checking
`StoreOpenStatus` or `StoreStatus`, not by catching exceptions.

## Open Statuses

- `Success`: the named store was created or opened.
- `AlreadyExists`: `OpenMode.CreateNew` found an existing mapping.
- `NotFound`: `OpenMode.OpenExisting` could not find the mapping.
- `InvalidOptions`: options are null, empty, out of range, or overflow layout
  calculation.
- `IncompatibleLayout`: an existing mapping does not match the supplied layout
  or layout version.
- `UnsupportedPlatform`: the current platform does not support the requested
  named memory-mapped-file behavior.
- `InsufficientCapacity`: `TotalBytes` cannot contain the requested layout.
- `AccessDenied`: the process lacks required mapping access.
- `MappingFailed`: the runtime failed to create or open the mapping.
- `StoreBusy`: shared synchronization was not acquired within the selected wait
  policy.
- `OperationCanceled`: cancellation was observed before shared synchronization
  was acquired.

## Operation Statuses

- `Success`: the operation completed.
- `DuplicateKey`: a key already maps to a published, pending-removal, or
  pending-reservation value.
- `NotFound`: the key is absent or no longer published.
- `InvalidKey`: the key is empty or otherwise invalid.
- `KeyTooLarge`: the key exceeds `MaxKeyBytes`.
- `ValueTooLarge`: the payload exceeds `MaxValueBytes`.
- `DescriptorTooLarge`: the descriptor exceeds `MaxDescriptorBytes`.
- `StoreFull`: no reusable value slot is available.
- `LeaseTableFull`: no reusable lease record is available.
- `InvalidLease`: the lease does not match an active record.
- `LeaseAlreadyReleased`: the lease was already released.
- `RemovePending`: removal was requested while active readers still hold leases.
- `UnsupportedPlatform`: the operation is not supported on the current platform.
- `StoreDisposed`: the handle has been disposed.
- `CorruptStore`: the store detected unsafe shared-memory state.
- `AccessDenied`: the process lacks required access.
- `UnknownFailure`: an unexpected runtime failure occurred.
- `InvalidReservation`: the reservation token does not match a pending slot
  generation.
- `ReservationIncomplete`: commit was attempted before the exact announced
  payload length was advanced.
- `ReservationAlreadyCompleted`: the reservation was already committed,
  aborted, disposed, or recovered.
- `ReservationWriteOutOfRange`: reservation progress would move outside the
  announced payload length.
- `StoreBusy`: shared synchronization was not acquired within the selected wait
  policy.
- `OperationCanceled`: cancellation was observed before shared synchronization
  was acquired.

## Common Situations

Duplicate key:

```csharp
var first = store.TryPublish(key, [1]);
var second = store.TryPublish(key, [2]);
```

Expected statuses: `Success`, then `DuplicateKey`.

Missing key:

```csharp
var status = store.TryAcquire([99], out var lease);
```

Expected status: `NotFound`.

Full store:

```csharp
// Configure SlotCount = 1, publish one value, then publish another key.
```

Expected status for the second publish: `StoreFull`.

Oversized value or descriptor:

```csharp
var status = store.TryPublish(key, new byte[options.MaxValueBytes + 1]);
```

Expected status: `ValueTooLarge`.

Invalid release:

```csharp
ValueLease lease = default;
var status = lease.Release();
```

Expected status: `InvalidLease`.

Unsupported platform:

```csharp
var open = MemoryStore.TryCreateOrOpen(options, out var store);
```

Expected status on unsupported platforms: `UnsupportedPlatform`.

Stale lease:

```csharp
var recovery = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out var report);
```

Expected status is `Success` when recovery is enabled and supported.
Unsupported owner-liveness checks return deterministic unsupported results
rather than background cleanup.

Cleanup failure:

If a store handle is already disposed, operations return `StoreDisposed`.
Release leases before disposal when release status matters.

Disposal races:

Public store methods and token methods racing with disposal complete normally
when they entered first, or return `StoreDisposed`, an invalid token outcome, an
already-completed token outcome, or an empty span projection. Callers
should not see internal mapped-memory, mutex, or object-disposal exceptions from
documented public boundaries.

Reservation lifecycle failure:

```csharp
var reserve = store.TryReserve(key, 4, default, out var reservation);
var commit = reservation.Commit();
```

Expected status for the commit before any `Advance()` call:
`ReservationIncomplete`. Abort or finish the reservation before reusing the key.

Version mismatch:

Opening an existing mapping with incompatible layout size, maxima, or major
layout version returns `IncompatibleLayout`. See
[Portability](portability.md) and the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

## Diagnostics

Non-success operation statuses increment diagnostic counters on the store
handle. Use [Diagnostics](diagnostics.md) for snapshot fields and observability
guidance.
