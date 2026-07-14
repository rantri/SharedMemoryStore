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
| `StoreBusy` | The selected wait policy expired before the operation ordered. Legacy uses a shared lock; lock-free v2 exhausts its local retry/revalidation/helping budget. | Retry according to caller policy or use a longer wait. |
| `OperationCanceled` | Cancellation was observed before the operation ordering point. | Honor caller cancellation. |

## Operation Statuses

| Status | Meaning | Typical cause |
|--------|---------|---------------|
| `Success` | Operation completed. | Expected path. |
| `DuplicateKey` | Key maps to a published or pending-removal value, or to `Reserved(ExplicitReservation)`. | Producer attempted to overwrite a public same-key lifecycle. Tentative `Initializing` and `Reserved(AtomicPublication)` alone do not qualify. |
| `NotFound` | Key is absent or no longer published. | Reader looked up a missing, pending, removed, or recovered key. |
| `InvalidKey` | Key is empty or otherwise invalid. | Caller supplied empty key bytes. |
| `KeyTooLarge` | Key exceeds `MaxKeyBytes`. | Configuration or encoding mismatch. |
| `ValueTooLarge` | Payload exceeds `MaxValueBytes`. | Capacity is too small for the payload. |
| `DescriptorTooLarge` | Descriptor exceeds `MaxDescriptorBytes`. | Metadata is too large for configured capacity. |
| `StoreFull` | No reusable value slot is available. | Published, pending-removal, explicit-reservation, tentative initialization/atomic-publication, cleanup, or retired lifecycles occupy all physical slots. |
| `LeaseTableFull` | Two exact, structurally valid lease-control collects confirm that no reusable lease record is available. | Too many concurrent readers or leaked leases. |
| `InvalidLease` | Lease token does not match an active record. | Default token, stale token, or recovered/reclaimed record. |
| `LeaseAlreadyReleased` | Lease was already released. | Repeated release or dispose after release. |
| `RemovePending` | The key is logically absent, but an active lease or incomplete bounded post-removal work delays physical reclamation. | Release readers or let later remove, release, or allocation-pressure helping finish reclamation. |
| `UnsupportedPlatform` | Operation is unsupported on this platform. | Platform or owner-liveness capability mismatch. |
| `StoreDisposed` | Store handle has been disposed. | Operation used a closed handle or raced with disposal. |
| `CorruptStore` | Unsafe shared-memory state was detected. | Inconsistent metadata or external mutation. |
| `AccessDenied` | Process lacks required access. | OS permissions or mapping access issue. |
| `UnknownFailure` | Unexpected runtime failure occurred. | Capture diagnostics and reproduction details. |
| `InvalidReservation` | Reservation token does not match a pending slot generation, or the tentative claim was legally canceled before explicit reservation ordered. | Default, stale, committed, aborted, disposed, or recovered token; or exact-generation cancellation before ordering. |
| `ReservationIncomplete` | Commit was attempted before exact announced length was advanced. | Producer has not written all bytes. |
| `ReservationAlreadyCompleted` | Reservation already committed, aborted, disposed, or recovered. | Repeated completion path. |
| `ReservationWriteOutOfRange` | Advance would move outside announced payload length. | Producer wrote too many bytes or used wrong count. |
| `StoreBusy` | The operation did not order within the wait policy. Legacy may be waiting for its shared lock; lock-free v2 exhausted bounded local retry/revalidation/helping. | Contention under `NoWait` or bounded timeout. |
| `OperationCanceled` | Cancellation was observed before the operation ordering point. | Caller canceled the operation. |

In lock-free v2, each lifecycle records a publication intent. `TryReserve`
orders at `Initializing -> Reserved(ExplicitReservation)`, which becomes a
duplicate-key witness. `TryPublish` and `TryPublishSegments` use
`AtomicPublication`; their internal `Initializing` and `Reserved` states are
tentative and the outer operation orders only at `Reserved -> Published`.
Tentative states are helpable and consume physical capacity, but they do not
alone justify `DuplicateKey`; bounded revalidation may instead return
`StoreBusy`. After an initial absent-key lookup, a raced operation may instead
return `StoreFull` at candidate claim before final duplicate arbitration;
duplicate status does not take precedence over genuine physical exhaustion in
that race. A missed allocation scan alone is not enough: v2 returns `StoreFull`
only after two same-order, structurally valid, all-non-Free control snapshots
match exactly. `Initializing`/`Reserved` require a structurally valid configured
participant token; `Free`/`Published`/`RemoveRequested`/`Aborting`/`Reclaiming`/
`Retired` require participant zero, every generation is nonzero and bounded, and
`Retired` is terminal. An invalid state/generation/owner shape returns
`CorruptStore`, even when two malformed words compare equal. A free or changing
slot, or contention for that handle's private proof buffer, follows the caller's
wait policy and can return `StoreBusy` instead. Normal recovery preserves a
lifecycle owned by an exact live Active participant. The current-process
reservation override is supported only after process-wide publication and
writable-view quiescence.

## Common Symptoms

Duplicate key:

```csharp
var first = store.TryPublish(key, [1]);
var second = store.TryPublish(key, [2]);
```

With candidate capacity available, expected statuses are `Success`, then
`DuplicateKey`. If every physical slot is already occupied, the second
concurrent operation may return `StoreFull` after its initial absent lookup and
before final duplicate arbitration.
Remove the key first, free capacity, or use a new key.

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
`LeaseRecordCount` or release leases sooner. An exhausted scan alone is not
enough: the lock-free profile confirms two identical, structurally valid,
all-non-Free snapshots. Movement or another operation using the handle-local
proof buffer follows the wait policy and may return `StoreBusy`; malformed lease
controls fail `CorruptStore`.

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

`CorruptStore` means a process proved unsafe persistent shared state. Layout v2
irreversibly latches that condition in the mapped store control, so subsequent
operations in every attached process fail before a new projection or mutation;
a later open reports `IncompatibleLayout`. Already borrowed spans cannot be
revoked. Stop access, capture options, operation, status, platform, package
version, and any diagnostics captured before the latch, then follow
[SUPPORT.md](../SUPPORT.md). Invalid caller input and ordinary concurrency,
capacity, cancellation, and token-history outcomes do not set the corruption
latch.

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
