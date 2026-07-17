# Usage and lifecycle rules

SharedMemoryStore exposes one SMS2 protocol through idiomatic C#, C++, and
Python APIs. The names differ by language, but the ordering points, statuses,
mapped bytes, token lifetimes, recovery rules, and capacity limits are the same.

Normative behavior is defined by the current
[public API](../specs/010-lock-free-only-multilang/contracts/public-api.md) and
[protocol conformance](../specs/010-lock-free-only-multilang/contracts/protocol-conformance.md)
contracts.

## Configure capacity once

A physical store has fixed dimensions:

- `SlotCount`: maximum simultaneously allocated value generations;
- `MaxValueBytes`, `MaxDescriptorBytes`, and `MaxKeyBytes`;
- `LeaseRecordCount`: maximum simultaneously active lease records; and
- `ParticipantRecordCount`: maximum open handles, default `64`.

Use the language helper to calculate the exact SMS2 region size. C#:

```csharp
SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
    "frames",
    slotCount: 1024,
    maxValueBytes: 1_048_576,
    maxDescriptorBytes: 128,
    maxKeyBytes: 64,
    leaseRecordCount: 4096,
    participantRecordCount: 64,
    openMode: OpenMode.CreateOrOpen,
    enableLeaseRecovery: true);
```

C++ uses `store_options::create(...)`; Python uses
`StoreOptions.create(...)`. Processes opening the same public name must provide
identical dimensions and recovery settings. A mismatch returns
`IncompatibleLayout` before payload projection.

Every successful handle owns one participant record. Do not size the
participant table as a process count unless each process has exactly one handle.
Closed records are generation-fenced before reuse.

## Open modes

| Mode | Meaning |
|---|---|
| `CreateNew` / `CREATE_NEW` | Create only; return `AlreadyExists` if a physical store exists. |
| `OpenExisting` / `OPEN_EXISTING` | Open only; return `NotFound` if none exists. |
| `CreateOrOpen` / `CREATE_OR_OPEN` | Create when absent, otherwise validate and open. |

Only the physical creator initializes the region. An opener never treats a zero,
partial, noncurrent, or malformed header as an empty store.

## Publish a complete value

Keys, descriptors, and values are opaque byte sequences. Keys must be nonempty.

```csharp
StoreStatus status = store.TryPublish(
    key: [0x01, 0x00, 0x02],
    value: [0x10, 0x00, 0x20],
    descriptor: [0x01]);
```

Publication copies descriptor and payload bytes into the mapped slot, validates
the exact key and generation, and makes the value visible only at the final
publication ordering point. Readers never observe a partially published value.

Important results:

- `Success`: the complete value is published;
- `DuplicateKey`: the exact key is already published, pending removal, or held
  by a pending reservation;
- `StoreFull`: no reusable slot generation is currently available;
- `StoreBusy`: the bounded retry/help budget expired before publication;
- size and key validation statuses for invalid caller input.

## Publish segments

Segmented publication accepts an ordered `ReadOnlySequence<byte>` in C#, a span
of byte spans in C++, or an iterable of bytes-like values in Python. It performs
one logical publication and reports the copied byte count.

```csharp
StoreStatus status = store.TryPublishSegments(
    key,
    sequence,
    descriptor,
    out int copiedBytes);
```

The total segment length must fit `MaxValueBytes`. No payload-sized temporary
buffer is required by the store.

## Direct reservation ingest

Reservations publish data directly into the future mapped payload area:

```csharp
StoreStatus reserved = store.TryReserve(
    key,
    payloadLength,
    descriptor,
    out ValueReservation reservation);

if (reserved == StoreStatus.Success)
{
    try
    {
        while (reservation.RemainingBytes > 0)
        {
            Span<byte> target = reservation.GetSpan(reservation.RemainingBytes);
            int written = FillFromSource(target);
            StoreStatus advanced = reservation.Advance(written);
            if (advanced != StoreStatus.Success)
            {
                throw new InvalidOperationException($"Advance failed: {advanced}");
            }
        }

        StoreStatus committed = reservation.Commit();
        if (committed != StoreStatus.Success)
        {
            throw new InvalidOperationException($"Commit failed: {committed}");
        }
    }
    finally
    {
        if (reservation.IsValid)
        {
            _ = reservation.Abort();
        }
    }
}
```

Reservation rules:

- the announced payload length is immutable;
- progress must advance monotonically and never beyond that length;
- exactly one producer owns a reservation token;
- the key is unavailable to readers until commit orders publication;
- duplicate publication is rejected while the reservation is pending;
- commit requires exact progress; and
- abort or recovery unlinks the key and cooperatively reclaims the generation.

The writable span, C++ span, or Python `memoryview` is borrowed. It ends when the
reservation advances beyond that range, commits, aborts, is recovered, or its
store closes.

## Acquire a zero-copy lease

```csharp
StoreStatus acquired = store.TryAcquire(key, out ValueLease lease);
if (acquired == StoreStatus.Success)
{
    using (lease)
    {
        ReadOnlySpan<byte> descriptor = lease.DescriptorSpan;
        ReadOnlySpan<byte> value = lease.ValueSpan;
        Consume(descriptor, value);
    }
}
```

The lease binds the exact store, participant incarnation, lease-record
incarnation, slot index, and slot generation. Releasing it is idempotent only in
the documented sense: a second release returns `LeaseAlreadyReleased`; a stale
or structurally invalid token never releases a later incarnation.

Do not retain a span, C++ `std::span`, Python `memoryview`, or derived view after
release, recovery, or store close. Copy bytes into application-owned memory when
they must outlive the lease.

## Remove and reuse

`TryRemove` has two phases:

1. make the key logically absent; and
2. physically reclaim its slot generation when no lease protects it and
   bounded cleanup completes.

```csharp
StoreStatus removed = store.TryRemove(key);
```

- `Success` means logical removal and physical reclamation completed.
- `RemovePending` means logical removal completed but a lease or bounded helper
  step still delays reuse.
- `NotFound` means no published value was found at the call's lookup ordering
  point.

Once logically removed, new acquires return `NotFound`; an existing lease may
continue reading its exact generation. Its final release may complete
reclamation. A later publish of the same key succeeds only after the old
generation is safely unlinked and reusable.

## Explicit recovery

Recovery scans are caller-invoked; there is no background thread. Enable
recovery in the store options and call:

```csharp
StoreStatus leases = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out LeaseRecoveryReport leaseReport);

StoreStatus reservations = store.TryRecoverReservations(
    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
    out ReservationRecoveryReport reservationReport);
```

Recovery validates the exact participant and record incarnation, classifies the
owner, revalidates unchanged state, and then performs an exact compare/exchange.
It never reclaims a record that may still have a live owner. Reports distinguish
scanned, recovered, active, unsupported, and failed/inconsistent records.

Current-process recovery is opt-in because it invalidates tokens owned by the
calling process. A recovered local lease or reservation must subsequently
report its stale/completed outcome rather than touching a reused generation.

## Wait policies and cancellation

C# exposes:

```csharp
StoreWaitOptions.NoWait
StoreWaitOptions.Default
StoreWaitOptions.Infinite
new StoreWaitOptions(timeout, cancellationToken)
```

C++ and Python expose equivalent no-wait, finite, infinite, and cancellation
values. One budget covers the whole call.

Hot operations do not wait on an OS-owned store lock. They may retry failed
CAS operations, revalidate versioned records, help incomplete transitions, scan
bounded candidates, and back off locally. `StoreBusy` means that work did not
order within the caller's budget. It is safe to retry according to application
policy.

Cold create/open/close may wait on platform lifecycle coordination and can also
return `StoreBusy` or `OperationCanceled`. Holding that cold resource must not
delay an already-open handle's hot operations.

Cancellation is checked before ordering points and during bounded cleanup. It
does not undo a result already visible to other participants.

## Same-handle concurrency and close

A store handle accepts concurrent operations. Local lifetime accounting admits
entered calls while the handle is open. Closing the handle:

1. prevents new operations from entering;
2. waits for already-entered local calls to drain;
3. invalidates local lease/reservation access;
4. releases the participant record; and
5. performs bounded platform-owner cleanup.

Calls that lose the race to close return `StoreDisposed` or the operation's
already ordered status. Close does not become mapped or process-wide operation
synchronization.

Lease and reservation tokens cannot outlive their exact store handle. C++ move
operations and Python context managers preserve this ownership rule.

## Diagnostics

`TryGetDiagnostics` performs a bounded structural scan and combines it with
process-local counters. It is observational: correctness never depends on a
diagnostics call. See [diagnostics.md](diagnostics.md).

## Deployment replacement

There is no mapped conversion path. To replace a noncurrent deployment, stop
new work, drain tokens, close all handles, remove or replace the physical store,
create fresh SMS2 resources, and republish from application-owned authoritative
data. A current client rejects a noncurrent mapping before reading its values.

## Related guides

- [Errors and statuses](errors.md)
- [Diagnostics](diagnostics.md)
- [Examples](examples.md)
- [Architecture](architecture.md)
- [Portability](portability.md)
