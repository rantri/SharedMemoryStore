# Errors and Statuses

SharedMemoryStore reports expected operational outcomes as status values. The
complete status source is the
[error-taxonomy.md](../specs/001-frame-memory-store/contracts/error-taxonomy.md).
Reservation-specific diagnostics and outcomes are covered by
[diagnostics-and-errors.md](../specs/003-zero-copy-ingest/contracts/diagnostics-and-errors.md),
and wait outcomes are covered by
[contention-configuration-contract.md](../specs/005-api-production-readiness/contracts/contention-configuration-contract.md).

Expected pressure, lookup, validation, contention, and lifecycle cases should be
handled by checking `StoreOpenStatus` or `StoreStatus`, not by catching
exceptions.

## Open Statuses

| Status | Meaning | Safe action |
|--------|---------|-------------|
| `Success` | The named store was created or opened. | Continue. |
| `AlreadyExists` | `OpenMode.CreateNew` found an existing mapping. | Choose another name or use `OpenExisting`/`CreateOrOpen`. |
| `NotFound` | `OpenMode.OpenExisting` could not find the mapping. | Start the producer/owner first or use `CreateOrOpen`. |
| `InvalidOptions` | Options are empty, out of range, or otherwise invalid. | Call `SharedMemoryStoreOptions.Validate` and fix configuration. |
| `IncompatibleLayout` | Existing mapping differs by size, maxima, or layout version. | Use matching options, migrate, or choose a new name. |
| `UnsupportedPlatform` | Platform does not support the requested named shared-memory behavior. | Follow [Portability](portability.md) and platform support guidance. |
| `InsufficientCapacity` | `TotalBytes` cannot contain the requested layout. | Recalculate with `CalculateRequiredBytes`. |
| `AccessDenied` | The process lacks mapping access. | Review process identity and OS permissions. |
| `MappingFailed` | The runtime failed to create or open the memory mapping. | Capture OS, runtime, options, and failure context for support. |
| `StoreBusy` | Shared synchronization was not acquired within the wait policy. | Retry according to caller policy or use a longer wait. |
| `OperationCanceled` | Cancellation was observed before synchronization was acquired. | Honor caller cancellation. |

## Operation Statuses

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `Success` | Operation completed. | Expected path. |
| `DuplicateKey` | Key maps to a published, pending-removal, or pending-reservation value. | Producer attempted to overwrite without removal. |
| `NotFound` | Key is absent or no longer published. | Reader looked up a missing, pending, removed, or recovered key. |
| `InvalidKey` | Key is empty or otherwise invalid. | Caller supplied empty key bytes. |
| `KeyTooLarge` | Key exceeds `MaxKeyBytes`. | Configuration or encoding mismatch. |
| `ValueTooLarge` | Payload exceeds `MaxValueBytes`. | Capacity is too small for the payload. |
| `DescriptorTooLarge` | Descriptor exceeds `MaxDescriptorBytes`. | Metadata is too large for configured capacity. |
| `StoreFull` | No reusable value slot is available. | Published, pending-removal, or pending-reservation values occupy all slots. |
| `LeaseTableFull` | No reusable lease record is available. | Too many concurrent readers or leaked leases. |
| `InvalidLease` | Lease token does not match an active record. | Default token, stale token, or recovered/reclaimed record. |
| `LeaseAlreadyReleased` | Lease was already released. | Repeated release or dispose after release. |
| `RemovePending` | Removal was requested while active readers still hold leases. | Readers need to release before slot reuse. |
| `UnsupportedPlatform` | Operation is unsupported on this platform. | Platform or owner-liveness capability mismatch. |
| `StoreDisposed` | Store handle has been disposed. | Operation used a closed handle or raced with disposal. |
| `CorruptStore` | Unsafe shared-memory state was detected. | Inconsistent metadata or external mutation. |
| `AccessDenied` | Process lacks required access. | OS permissions or mapping access issue. |
| `UnknownFailure` | Unexpected runtime failure occurred. | Capture diagnostics and reproduction details. |
| `InvalidReservation` | Reservation token does not match a pending slot generation. | Default, stale, committed, aborted, disposed, or recovered token. |
| `ReservationIncomplete` | Commit was attempted before exact announced length was advanced. | Producer has not written all bytes. |
| `ReservationAlreadyCompleted` | Reservation already committed, aborted, disposed, or recovered. | Repeated completion path. |
| `ReservationWriteOutOfRange` | Advance would move outside announced payload length. | Producer wrote too many bytes or used wrong count. |
| `StoreBusy` | Shared synchronization was not acquired within the wait policy. | Contention under `NoWait` or bounded timeout. |
| `OperationCanceled` | Cancellation was observed before synchronization was acquired. | Caller canceled the operation. |

## Common Symptoms

Duplicate key:

```csharp
var first = store.TryPublish(key, [1]);
var second = store.TryPublish(key, [2]);
```

Expected statuses: `Success`, then `DuplicateKey`. Remove the key first or use
a new key.

Missing key:

```csharp
var status = store.TryAcquire([99], out var lease);
```

Expected status: `NotFound`. Check producer success, key bytes, removal, and
pending reservation state.

Full store:

```csharp
// Configure SlotCount = 1, publish one value, then publish another key.
```

Expected status for the second publish: `StoreFull`. Inspect
`FreeSlotCount`, `PendingRemovalCount`, and `ActiveReservationCount`.

Lease pressure:

```csharp
var status = store.TryAcquire(key, out var lease);
```

Expected status under lease-record pressure: `LeaseTableFull`. Increase
`LeaseRecordCount` or release leases sooner.

Invalid release:

```csharp
ValueLease lease = default;
var status = lease.Release();
```

Expected status: `InvalidLease`.

Reservation incomplete:

```csharp
var reserve = store.TryReserve(key, 4, default, out var reservation);
var commit = reservation.Commit();
```

Expected commit status: `ReservationIncomplete`. Finish with `Advance(4)` or
abort the reservation.

Reservation out of range:

```csharp
var status = reservation.Advance(reservation.RemainingBytes + 1);
```

Expected status: `ReservationWriteOutOfRange`. Advance only the exact bytes
written into the current span.

Disposal race:

Public store methods and token methods racing with disposal complete normally
when they entered first, or return `StoreDisposed`, an invalid token outcome, an
already-completed token outcome, or an empty span projection. Callers should not
see internal mapped-memory, mutex, or object-disposal exceptions from documented
public boundaries.

Unsupported platform:

Opening or recovery on unsupported platforms returns `UnsupportedPlatform` or
unsupported recovery counts. See [Portability](portability.md) and
[SUPPORT.md](../SUPPORT.md).

Version mismatch:

Opening an existing mapping with incompatible layout size, maxima, or major
layout version returns `IncompatibleLayout`. See
[Portability](portability.md) and
[shared-memory-layout.md](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

Corruption signal:

`CorruptStore` means the process detected unsafe shared state. Stop unsafe
access, capture options, operation, status, platform, package version, and
diagnostics, then follow [SUPPORT.md](../SUPPORT.md).

## Diagnostics To Inspect

Use [Diagnostics](diagnostics.md) to inspect:

- `LastFailureStatus`.
- `GetFailureCount(StoreStatus.SomeStatus)`.
- `CapacityPressureCount`.
- `FreeSlotCount`, `PendingRemovalCount`, and `ActiveReservationCount`.
- lease and reservation recovery counts.
- key-index tombstone and probe fields.

These signals help distinguish validation mistakes, live capacity pressure,
lease leaks, reservation leaks, key churn, unsupported platform behavior, and
unsafe shared state.
