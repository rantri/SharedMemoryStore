# Reservation API Contract

## Package Impact

- Package id remains `SharedMemoryStore`.
- Target framework remains `net10.0`.
- Runtime dependencies remain .NET BCL only.
- Semantic version impact: additive minor feature while package is pre-1.0.
- Existing `TryPublish`, `TryAcquire`, `TryRemove`, `TryRecoverLeases`,
  `GetDiagnostics`, `ValueLease`, and options behavior remains compatible.

## Public API Additions

Required members on `SharedMemoryStore`:

```csharp
StoreStatus TryReserve(
    ReadOnlySpan<byte> key,
    int payloadLength,
    ReadOnlySpan<byte> descriptor,
    out ValueReservation reservation);

StoreStatus TryPublishSegments(
    ReadOnlySpan<byte> key,
    in ReadOnlySequence<byte> payload,
    ReadOnlySpan<byte> descriptor,
    out long copiedBytes);

StoreStatus TryRecoverReservations(
    in ReservationRecoveryOptions options,
    out ReservationRecoveryReport report);
```

Required public types:

```csharp
public struct ValueReservation : IDisposable
{
    public bool IsValid { get; }
    public int PayloadLength { get; }
    public int BytesWritten { get; }
    public int RemainingBytes { get; }
    public Span<byte> GetSpan(int sizeHint = 0);
    public Memory<byte> DangerousGetMemory(int sizeHint = 0);
    public StoreStatus Advance(int byteCount);
    public StoreStatus Commit();
    public StoreStatus Abort();
    public void Dispose();
}

public readonly record struct ReservationRecoveryOptions(
    bool RecoverCurrentProcessReservations);

public readonly record struct ReservationRecoveryReport(
    int ScannedReservationCount,
    int RecoveredReservationCount,
    int ActiveReservationCount,
    int UnsupportedReservationCount,
    int FailedRecoveryCount);
```

The final implementation may add overloads for convenience, but the allocation
and compatibility contract belongs to the span, memory, and sequence APIs above.

## Reservation Lifecycle

1. `TryReserve` validates key, payload length, descriptor length, store state,
   duplicate key, and capacity.
2. On success, the store reserves one free slot, copies descriptor bytes, inserts
   a pending index entry, records owner process id and slot generation, and
   returns a `ValueReservation`.
3. While pending, `TryAcquire` for the key returns `NotFound`; duplicate publish
   or reserve attempts return `DuplicateKey`.
4. The producer writes into `GetSpan` or, for trusted direct-I/O adapters that
   require `Memory<byte>`, `DangerousGetMemory`, and calls `Advance` with the
   number of bytes actually written.
5. `Commit` succeeds only when `BytesWritten == PayloadLength`; it publishes the
   slot atomically and makes the value acquirable by key.
6. `Abort` removes the pending index entry and returns the slot to the free
   pool without exposing payload bytes.
7. `Dispose` aborts an active pending reservation and is a no-op for completed
   reservations.

## Writable Memory Rules

- `GetSpan` exposes the remaining unwritten payload region for immediate
  writes.
- `DangerousGetMemory` exposes the remaining unwritten payload region for
  trusted stream or socket adapters that require retained-capable
  `Memory<byte>`.
- The returned view length must never exceed `RemainingBytes`.
- A positive `sizeHint` larger than `RemainingBytes` returns an empty view or a
  deterministic out-of-range status through the next `Advance`; it must not
  expose bytes outside the reservation.
- Views are valid only while the reservation is pending and the store handle is
  open.
- Only the reserving producer may mutate the writable region.
- Consumers must not retain or use writable views after commit, abort, dispose,
  recovery, store disposal, or slot reuse. This is especially important for
  `DangerousGetMemory` because `Memory<byte>` is retained-capable by design.

## Commit and Abort Rules

- Commit is atomic with respect to readers: readers observe either no value or a
  complete committed value.
- Commit after abort, successful commit, disposal, stale recovery, or generation
  mismatch returns `ReservationAlreadyCompleted` or `InvalidReservation`.
- Commit before all payload bytes are advanced returns `ReservationIncomplete`
  and leaves the reservation pending so the producer can finish or abort.
- Advance beyond the reserved payload length returns
  `ReservationWriteOutOfRange` and does not change publication state.
- Abort after successful commit returns `ReservationAlreadyCompleted`.
- Abort after stale recovery or generation mismatch returns
  `InvalidReservation`.

## Segmented Publish Rules

- `TryPublishSegments` applies the same pending-to-published safety rules as a
  reservation while holding one shared-synchronization acquisition.
- `ReadOnlySequence<byte>.Length` must fit in `int` and not exceed
  `MaxValueBytes`.
- The helper copies segments in order into one reserved payload region and
  publishes only after the copied byte count equals the sequence length.
- If segment copy or validation fails, the helper reclaims the unpublished slot
  before releasing shared synchronization.
- The helper must not allocate a temporary contiguous full-payload array.
- The stored value bytes must equal the logical concatenation of all segments.

## Existing Publish Compatibility

`TryPublish(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value,
ReadOnlySpan<byte> descriptor = default)` remains part of the public contract.
It may be internally implemented as reserve, copy, advance, commit, but it must
preserve existing documented statuses, allocation behavior, descriptor behavior,
reader visibility, and remove/reuse semantics.

## Threading and Ownership

- Store methods remain thread-safe for concurrent producers and readers.
- A single `ValueReservation` is owned by one producer workflow; concurrent
  calls on copies of the same reservation token are invalid unless explicitly
  synchronized by the caller.
- Slot generation and pending state validation prevent stale token reuse.
- Reservation recovery is explicit and may race with a producer only according
  to documented owner-liveness policy.
- The store never writes diagnostics directly to console, trace, or logs.

## XML Documentation Requirements

Public XML documentation must describe:
- reservation lifetime and disposal behavior.
- exact byte-count requirement.
- descriptor immutability.
- visibility rule before commit.
- invalid and repeated operation outcomes.
- writable memory lifetime and ownership.
- segmented publish allocation contract.
- trusted same-host service boundary.
