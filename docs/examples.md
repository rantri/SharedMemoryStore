# Examples

These examples use the one SMS2 protocol. Every store is participant-aware and
reports protocol identity `(2, 0, 2, 7, 0)`.

Token lifetimes and outcomes follow the current
[public API contract](../specs/010-lock-free-only-multilang/contracts/public-api.md).

## C#: publish, lease, remove, reuse

```csharp
using SharedMemoryStore;

SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
    $"example-{Guid.NewGuid():N}",
    slotCount: 4,
    maxValueBytes: 1024,
    maxDescriptorBytes: 32,
    maxKeyBytes: 32,
    leaseRecordCount: 8,
    participantRecordCount: 4,
    openMode: OpenMode.CreateNew,
    enableLeaseRecovery: true);

if (MemoryStore.TryCreateOrOpen(options, out MemoryStore? store)
    != StoreOpenStatus.Success || store is null)
{
    throw new InvalidOperationException("Could not create the store.");
}

using (store)
{
    byte[] key = [0x01, 0x00, 0x02];
    byte[] descriptor = [0x01];

    if (store.TryPublish(key, [0x10, 0x00, 0x20], descriptor)
        != StoreStatus.Success)
    {
        throw new InvalidOperationException("Publish failed.");
    }

    if (store.TryAcquire(key, out ValueLease lease) != StoreStatus.Success)
    {
        throw new InvalidOperationException("Acquire failed.");
    }

    using (lease)
    {
        Console.WriteLine(Convert.ToHexString(lease.ValueSpan));
    }

    if (store.TryRemove(key) != StoreStatus.Success)
    {
        throw new InvalidOperationException("Remove failed.");
    }

    if (store.TryPublish(key, [0x30]) != StoreStatus.Success)
    {
        throw new InvalidOperationException("Generation reuse failed.");
    }
}
```

If removal races a live lease, accept `RemovePending`, release the lease, and
retry only if the application needs to observe physical reclamation.

## C#: direct reservation ingest

```csharp
byte[] key = "frame-17"u8.ToArray();
ReadOnlyMemory<byte> source = GetFrame();

StoreStatus status = store.TryReserve(
    key,
    source.Length,
    descriptor: default,
    out ValueReservation reservation);

if (status == StoreStatus.Success)
{
    try
    {
        source.Span.CopyTo(reservation.GetSpan(source.Length));
        if (reservation.Advance(source.Length) != StoreStatus.Success)
        {
            throw new InvalidOperationException("Advance failed.");
        }

        if (reservation.Commit() != StoreStatus.Success)
        {
            throw new InvalidOperationException("Commit failed.");
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

The writable span is valid only for the exact reservation lifetime. Do not use
it after advance, commit, abort, recovery, or store close.

## C#: segmented publication

```csharp
using System.Buffers;

ReadOnlySequence<byte> segments = BuildSequence(header, body, trailer);
StoreStatus published = store.TryPublishSegments(
    "frame-18"u8,
    segments,
    descriptor: default,
    out int copiedBytes);
```

Segment order is preserved. The store performs one logical publication and
does not require a payload-sized concatenation buffer.

## C#: bounded wait and cancellation

```csharp
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var wait = new StoreWaitOptions(TimeSpan.FromMilliseconds(250), cancellation.Token);

StoreStatus status = store.TryAcquire(key, wait, out ValueLease lease);
switch (status)
{
    case StoreStatus.Success:
        using (lease) Consume(lease.ValueSpan);
        break;
    case StoreStatus.NotFound:
        break;
    case StoreStatus.StoreBusy:
        ScheduleRetry();
        break;
    case StoreStatus.OperationCanceled:
        throw new OperationCanceledException(cancellation.Token);
}
```

`StoreBusy` means the local bounded retry/help budget expired. It does not imply
that a store-wide hot mutex was held.

## C++: publish and lease

```cpp
#include <shared_memory_store/store.hpp>
#include <array>

using namespace shared_memory_store;

auto options = store_options::create(
    "cpp-example", 4, 1024, 32, 32, 8, 4,
    open_mode::create_new, true);

memory_store store;
if (memory_store::try_create_or_open(options, store) != open_status::success) {
    return 1;
}

const std::array<std::byte, 2> key{std::byte{1}, std::byte{2}};
const std::array<std::byte, 3> value{std::byte{3}, std::byte{4}, std::byte{5}};
if (store.try_publish(key, value) != status::success) return 2;

value_lease lease;
if (store.try_acquire(key, lease) != status::success) return 3;
auto borrowed = lease.value();
Consume(borrowed);
return lease.release() == status::success ? 0 : 4;
```

Stores, leases, reservations, and cancellation sources are move-only. Explicit
lifecycle methods give deterministic statuses; destructors are non-throwing
best-effort cleanup.

## Python: context-managed lifecycle

```python
from shared_memory_store import MemoryStore, OpenMode, StoreOpenStatus, StoreOptions, StoreStatus

options = StoreOptions.create(
    "python-example",
    slot_count=4,
    max_value_bytes=1024,
    max_descriptor_bytes=32,
    max_key_bytes=32,
    lease_record_count=8,
    participant_record_count=4,
    open_mode=OpenMode.CREATE_NEW,
    enable_lease_recovery=True,
)

opened, store = MemoryStore.open(options)
if opened is not StoreOpenStatus.SUCCESS or store is None:
    raise RuntimeError(f"open failed: {opened}")

with store:
    if store.publish(b"key\x00", b"value\x00", b"schema-1") is not StoreStatus.SUCCESS:
        raise RuntimeError("publish failed")
    acquired, lease = store.acquire(b"key\x00")
    if acquired is not StoreStatus.SUCCESS or lease is None:
        raise RuntimeError(f"acquire failed: {acquired}")
    with lease:
        Consume(bytes(lease.value), bytes(lease.descriptor))
    if store.remove(b"key\x00") is not StoreStatus.SUCCESS:
        raise RuntimeError("remove failed")
```

Direct Python `memoryview` values and any derived views share the exact token
lifetime. Convert to `bytes` when data must outlive the lease.

## Explicit recovery

Enable recovery at creation. In C#:

```csharp
StoreStatus leaseStatus = store.TryRecoverLeases(
    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
    out LeaseRecoveryReport leases);

StoreStatus reservationStatus = store.TryRecoverReservations(
    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
    out ReservationRecoveryReport reservations);
```

Use the language-equivalent API in C++ or Python. Do not infer that an
unsupported or changing owner is stale.

## Cross-language key encoding

The store does not serialize application keys. For a composite key, define a
canonical byte schema. Example:

```text
byte 0       namespace
bytes 1..4   uint32 little-endian identifier
bytes 5..12  uint64 little-endian sequence
```

C# `BinaryPrimitives`, C++ fixed-width integers with explicit little-endian
encoding, and Python `int.to_bytes(..., "little")` can produce identical bytes.
Always include a schema/version discriminator when formats may evolve.

## Repository samples

- [C# basic usage](../samples/BasicUsage/README.md)
- [C++ basic usage](../samples/CppBasicUsage/README.md)
- [Python basic usage](../samples/PythonBasicUsage/README.md)
- [Frame value](../samples/FrameValue/README.md)
- [Zero-copy ingest](../samples/ZeroCopyIngest/README.md)
- [Broker-key dispatch](../samples/LockFreeBrokerKeys/README.md)
- [Docker shared memory](../samples/DockerSharedMemory/README.md)

## Related guides

- [Getting started](getting-started.md)
- [Usage](usage.md)
- [Errors](errors.md)
- [Diagnostics](diagnostics.md)
