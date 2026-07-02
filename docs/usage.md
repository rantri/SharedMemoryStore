# Usage

This guide describes the primary package consumer workflow. Detailed public API
rules are traced to the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md),
and deterministic status behavior is traced to the
[error taxonomy contract](../specs/001-frame-memory-store/contracts/error-taxonomy.md).

## Create or Open a Store

Configure explicit capacity limits and calculate the required mapped-region
size from those limits.

```csharp
using SharedMemoryStore;
using Store = SharedMemoryStore.SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = "sms-app-values",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 128,
    MaxValueBytes = 1_048_576,
    MaxDescriptorBytes = 256,
    MaxKeyBytes = 64,
    LeaseRecordCount = 256,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(128, 1_048_576, 256, 64, 256)
};

var open = Store.TryCreateOrOpen(options, out var store);
if (open != StoreOpenStatus.Success || store is null)
{
    // InvalidOptions, UnsupportedPlatform, AccessDenied, MappingFailed,
    // NotFound, AlreadyExists, IncompatibleLayout, or InsufficientCapacity.
    return;
}
```

`OpenMode.CreateNew` fails with `AlreadyExists` when the mapping exists.
`OpenMode.OpenExisting` fails with `NotFound` when it does not exist.
`OpenMode.CreateOrOpen` creates the mapping when needed and validates layout
compatibility when opening an existing mapping.

## Publish

Publish stores immutable payload bytes and optional descriptor bytes under an
opaque byte key.

```csharp
var key = new byte[] { 1, 2, 3 };
var descriptor = new byte[] { 9, 8 };
var payload = new byte[] { 4, 5, 6, 7 };

var publish = store.TryPublish(key, payload, descriptor);
```

Expected success returns `StoreStatus.Success`. Common non-success statuses are
`DuplicateKey`, `KeyTooLarge`, `ValueTooLarge`, `DescriptorTooLarge`,
`StoreFull`, `UnsupportedPlatform`, `StoreDisposed`, `CorruptStore`, and
`UnknownFailure`.

## Direct Reservation Ingest

Use `TryReserve` when the key, descriptor, and payload length are known before
payload bytes arrive. The returned `ValueReservation` exposes writable
store-owned memory. Readers cannot acquire the key until `Commit()` succeeds.

```csharp
var reserve = store.TryReserve(key, payloadLength: 4, descriptor, out var reservation);
if (reserve == StoreStatus.Success)
{
    new byte[] { 4, 5, 6, 7 }.CopyTo(reservation.GetSpan());
    var advance = reservation.Advance(4);
    var commit = advance == StoreStatus.Success
        ? reservation.Commit()
        : reservation.Abort();
}
```

`Commit()` succeeds only after `Advance()` has recorded exactly the announced
payload length. Commit before completion returns `ReservationIncomplete`.
Advancing past the remaining payload length returns
`ReservationWriteOutOfRange`. `Abort()` and active-reservation disposal remove
the pending key without exposing partial bytes.

Writable spans and memory are valid only while the reservation is pending and
the store handle remains open. Descriptor bytes are fixed at reservation time.

## Segmented Publish

Use `TryPublishSegments` when payload bytes already exist in one or more
segments and flattening them into a temporary full-payload array would be
wasteful.

```csharp
ReadOnlySequence<byte> payload = GetPayloadSequence();
var publish = store.TryPublishSegments(key, payload, descriptor, out var copiedBytes);
```

The helper reserves one contiguous store slot, copies each segment in order,
advances reservation progress, and commits only after the copied byte count
matches the sequence length. On copy, advance, or commit failure, it aborts the
active reservation before returning.

## Acquire and Read

Acquire returns a `ValueLease` that protects the slot generation while the
caller reads the descriptor and value spans.

```csharp
var acquire = store.TryAcquire(key, out var lease);
if (acquire == StoreStatus.Success)
{
    try
    {
        ReadOnlySpan<byte> descriptorSpan = lease.DescriptorSpan;
        ReadOnlySpan<byte> valueSpan = lease.ValueSpan;
    }
    finally
    {
        _ = lease.Release();
    }
}
```

Read spans are valid only while the lease is active and the store remains open.
The spans are read-only, and the core store never parses application-specific
payload formats.

## Release

Call `Release()` when the status matters. Use `Dispose()` when best-effort
release is enough.

```csharp
var release = lease.Release();
```

The first release of an active lease returns `Success`. Releasing the same lease
again returns `LeaseAlreadyReleased` or `InvalidLease` depending on the lease
record state.

## Remove and Reuse

Remove deletes the key from the public index and makes the slot reusable when no
active lease protects it.

```csharp
var remove = store.TryRemove(key);
```

If no readers hold a lease, expected status is `Success` and the slot can be
reused immediately. If active leases exist, expected status is `RemovePending`.
The final release advances the slot generation and allows reuse.

```csharp
_ = store.TryPublish(key, [10]);
```

Publishing the same key while a value is still published or pending removal
returns `DuplicateKey`.

## Diagnostics

`GetDiagnostics()` returns a snapshot for caller-owned formatting, logging, or
metrics export.

```csharp
var diagnostics = store.GetDiagnostics();
Console.WriteLine($"free slots: {diagnostics.FreeSlotCount}");
Console.WriteLine($"last failure: {diagnostics.LastFailureStatus}");
```

The library does not write diagnostics to the console and does not start hidden
background work.

## Stale Lease Recovery

When `EnableLeaseRecovery` is true, owners may call explicit recovery. Recovery
policy is caller-controlled and platform support is deterministic.

```csharp
var recovery = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out var report);
```

Use recovery for controlled owner workflows, not as a replacement for normal
lease disposal.

## Stale Reservation Recovery

Incomplete reservations are recovered explicitly by the owner. This keeps
cleanup policy visible and avoids hidden background work.

```csharp
var recovery = store.TryRecoverReservations(
    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
    out var report);
```

Recovery removes pending key index entries before reclaiming slots. Current
process recovery is intended for tests and controlled shutdown paths.
Diagnostics expose recovered, still-active, unsupported, and failed recovery
counts so callers can route cleanup outcomes to their own logs or metrics.

## Trusted Same-Host Boundary

Direct writable reservations are intended for trusted services on the same host.
Use operating-system permissions and deployment controls to prevent untrusted
processes from opening the mapping. The package does not protect against a
malicious process that is already inside that trust boundary and can mutate the
shared memory. See [Portability](portability.md) for the language-neutral
boundary and future binding guidance.

## Dispose

Dispose the store handle when finished.

```csharp
store.Dispose();
```

Disposal invalidates future operations on that handle and invalidates span
projections previously obtained from leases.
