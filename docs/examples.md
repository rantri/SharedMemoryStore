# Examples

These examples show consumer-owned workflows built on the current
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md).
Frame-shaped values follow the opaque-byte rules in the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).
Direct ingest examples follow the
[reservation API contract](../specs/003-zero-copy-ingest/contracts/reservation-api.md).

## Basic Values

```csharp
using SharedMemoryStore;

var options = SharedMemoryStoreOptions.Create(
    name: $"sms-basic-{Guid.NewGuid():N}",
    slotCount: 2,
    maxValueBytes: 64,
    maxDescriptorBytes: 16,
    maxKeyBytes: 16,
    leaseRecordCount: 4,
    enableLeaseRecovery: true);

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine(openStatus);
    return;
}

using (store)
{
    var key = new byte[] { 1, 2, 3 };
    Console.WriteLine(store.TryPublish(key, [4, 5, 6, 7], [9, 8]));
    Console.WriteLine(store.TryAcquire(key, out var lease));
    Console.WriteLine(lease.ValueLength);
    Console.WriteLine(lease.Release());
    Console.WriteLine(store.TryRemove(key));
}
```

Expected status path: `Success` for publish, acquire, release, and remove.

## Error Handling

```csharp
var status = store.TryPublish(key, [1]);
if (status == StoreStatus.DuplicateKey)
{
    _ = store.TryRemove(key);
    status = store.TryPublish(key, [1]);
}

if (status == StoreStatus.StoreBusy)
{
    // Apply caller-owned retry or backoff policy.
}
```

Expected operational failures return `StoreStatus` values. Use
[Errors and statuses](errors.md) for the full status list.

## Diagnostics Snapshot

```csharp
var status = store.TryGetDiagnostics(out var snapshot);
if (status == StoreStatus.Success)
{
    Console.WriteLine(snapshot.FreeSlotCount);
    Console.WriteLine(snapshot.GetFailureCount(StoreStatus.StoreFull));
}
```

Use this pattern in health checks and support capture paths. The package does
not choose logging or metrics infrastructure for the application.

## Language-Neutral Values

Keys, descriptors, and values are opaque byte sequences:

- key: consumer-defined identity bytes with exact byte equality.
- descriptor: optional consumer-defined metadata bytes.
- value: immutable payload bytes.
- lease: generation-protected read token for a published value.

Strings are a consumer convention. If a consumer uses strings, encode them to
bytes before calling the core store and decode them after reading.

## Allocation-Conscious Keys

For hot paths, write key bytes into a caller-owned span and pass the span to the
store. Prefix keys when different logical domains could otherwise collide.

```csharp
using System.Buffers.Binary;

const byte OrderKeyPrefix = 1;

Span<byte> key = stackalloc byte[1 + 4];
key[0] = OrderKeyPrefix;
BinaryPrimitives.WriteInt32LittleEndian(key[1..], orderId);

var status = store.TryAcquire(key, out var lease);
```

Use [Byte encoding](byte-encoding.md) for string, GUID, descriptor, and payload
encoding guidance. The
[Basic usage sample](../samples/BasicUsage/README.md) includes a small helper
class that demonstrates this pattern without adding a public package API.

## Frame-Shaped Values

A frame-shaped value is still just descriptor bytes plus payload bytes. The core
store does not parse width, height, pixel format, timestamps, sections, headers,
or metadata.

One consumer-owned layout can put frame metadata in the descriptor and frame
pixels in the value:

```csharp
var descriptor = new FrameDescriptor(
    Width: 1280,
    Height: 720,
    PixelBytes: frame.Length,
    TimestampTicks: DateTime.UtcNow.Ticks).ToBytes();

var publish = store.TryPublish([1], frame, descriptor);
```

The [Frame value sample](../samples/FrameValue/README.md) shows this pattern and
also publishes a non-frame value to show that the core lifecycle is identical.

## Direct Frame Ingest

When a frame header gives the payload length before the bytes are read, reserve
the store slot first and receive directly into writable reservation memory:

```csharp
var status = store.TryReserve([1], payloadLength, descriptor, out var reservation);
if (status == StoreStatus.Success)
{
    while (reservation.RemainingBytes > 0)
    {
        var receiveLength = Math.Min(4096, reservation.RemainingBytes);
        var target = reservation.DangerousGetMemory(receiveLength).Slice(0, receiveLength);
        var received = await socket.ReceiveAsync(target, SocketFlags.None);
        if (received == 0)
        {
            _ = reservation.Abort();
            break;
        }

        status = reservation.Advance(received);
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            break;
        }
    }

    if (status == StoreStatus.Success)
    {
        status = reservation.Commit();
    }
}
```

`DangerousGetMemory` exists for trusted direct-I/O adapters that require
`Memory<byte>`. Do not retain or use the returned memory after commit, abort,
recovery, disposal, store disposal, or slot reuse. Use `GetSpan` for ordinary
immediate writes.

The [zero-copy ingest sample](../samples/ZeroCopyIngest/README.md) demonstrates
direct chunked writes, a runnable length-prefixed stream adapter, abort cleanup,
reader acquire, remove, and segmented publication.

## Segmented Payloads

Already-buffered segments can be published without flattening:

```csharp
ReadOnlySequence<byte> payload = GetBufferedPayload();
var status = store.TryPublishSegments([2], payload, descriptor, out var copiedBytes);
```

The committed value is still one immutable contiguous payload for readers. The
input can be segmented; the public shared-memory value is not.

## Pipeline Adapter

`System.IO.Pipelines` remains an adapter layer over the store contract. Read the
frame with the pipeline, slice the payload as a `ReadOnlySequence<byte>`, and
publish that sequence:

```csharp
var read = await pipe.Reader.ReadAsync();
var reader = new SequenceReader<byte>(read.Buffer);
if (reader.TryReadLittleEndian(out int payloadLength)
    && read.Buffer.Length - 4 >= payloadLength)
{
    var payload = read.Buffer.Slice(reader.Position, payloadLength);
    var status = store.TryPublishSegments(key, payload, descriptor, out var copiedBytes);
}
```

This does not make `System.IO.Pipelines` part of the runtime package contract.

## Wait Policy

```csharp
var publish = store.TryPublish(key, payload, descriptor, StoreWaitOptions.NoWait);
if (publish == StoreStatus.StoreBusy)
{
    // The caller decides whether to retry, drop work, or report back pressure.
}
```

Use the [Usage](usage.md) guide for when to choose `Default`, `NoWait`, or
`Infinite`.
