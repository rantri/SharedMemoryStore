# Contract: Reservation Memory Lifetime

## Safe Public Writing Model

`ValueReservation` represents one pending reservation. Safe public writable
access is immediate and scoped to the active reservation.

Expected public shape:

```csharp
public struct ValueReservation : IDisposable
{
    public bool IsValid { get; }
    public int PayloadLength { get; }
    public int BytesWritten { get; }
    public int RemainingBytes { get; }

    public Span<byte> GetSpan(int sizeHint = 0);
    public StoreStatus Advance(int byteCount);
    public StoreStatus Commit();
    public StoreStatus Abort();
}
```

The production public API must not expose general retained writable
`Memory<byte>` for reservation payload storage.

## Lifetime Rules

- `GetSpan` returns writable bytes only while the reservation is active and the
  requested size fits within the remaining payload.
- `Advance` records the exact number of bytes written to the current reservation
  position.
- `Commit` succeeds only after the producer has advanced exactly the announced
  payload length.
- `Abort` removes the pending key and makes the slot reusable.
- `Dispose` aborts only if the reservation is still active.
- After commit, abort, dispose, recovery, store disposal, or slot reuse, retained
  safe public write access must not mutate current store contents.

## Outcomes

- Invalid or stale reservation token: `InvalidReservation`.
- Operation attempted after completion where completion is known:
  `ReservationAlreadyCompleted`.
- Commit before all bytes are advanced: `ReservationIncomplete`.
- Advance beyond the announced payload length:
  `ReservationWriteOutOfRange`.
- Store disposed before the token operation can run: `StoreDisposed`.

## Tests

Automated tests must retain every safe public write access path, complete the
reservation through commit, abort, dispose, recovery, and store disposal, then
reuse the slot for at least 10,000 cycles and verify:
- Committed reader-visible payload bytes remain immutable.
- Aborted or disposed reservation bytes do not affect future values.
- Stale reservation tokens cannot advance, commit, or abort a reused slot.
- Basic examples do not use advanced or trusted writable-memory APIs.

## Future Advanced API Rule

If a future release adds retained writable memory again, it must be a separate
advanced/trusted API. It must be excluded from quickstarts, documented as
caller-owned lifetime risk, and tested against commit, abort, dispose, recovery,
store disposal, and slot reuse.
