# Examples

These examples show consumer-owned workflows built on the current
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md).
Frame-shaped values follow the opaque-byte rules in the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

## Basic Workflow

```csharp
using SharedMemoryStore;
using Store = SharedMemoryStore.SharedMemoryStore;

var options = new SharedMemoryStoreOptions
{
    Name = $"sms-basic-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = 64,
    MaxDescriptorBytes = 16,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, 64, 16, 16, 4)
};

var openStatus = Store.TryCreateOrOpen(options, out var store);
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
```

Expected operational failures return `StoreStatus` values. Use
[Errors and statuses](errors.md) for the full status list.

## Language-Neutral Values

Keys, descriptors, and values are opaque byte sequences:

- key: consumer-defined identity bytes with exact byte equality.
- descriptor: optional consumer-defined metadata bytes.
- value: immutable payload bytes.
- lease: generation-protected read token for a published value.

Strings are a consumer convention. If a consumer uses strings, encode them to
bytes before calling the core store and decode them after reading.

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
the store slot first and receive directly into the writable reservation memory:

```csharp
var status = store.TryReserve([1], payloadLength, descriptor, out var reservation);
if (status == StoreStatus.Success)
{
    while (reservation.RemainingBytes > 0)
    {
        var target = reservation.GetMemory(Math.Min(4096, reservation.RemainingBytes));
        var received = await socket.ReceiveAsync(target);
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

The [zero-copy ingest sample](../samples/ZeroCopyIngest/README.md) demonstrates
direct chunked writes, a runnable length-prefixed stream adapter, abort cleanup,
reader acquire, remove, and segmented publication.

## Pipelines Adapter

`System.IO.Pipelines` remains an adapter layer over the store contract. Read the
frame with the pipeline, slice the payload as a `ReadOnlySequence<byte>`, and
publish that sequence without flattening it:

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

This copies the pipeline-owned segments into one committed store value. It does
not make `System.IO.Pipelines` part of the runtime package contract.

## Segmented Buffered Frames

Already-buffered segments can be published without flattening:

```csharp
ReadOnlySequence<byte> frame = GetBufferedFrame();
var status = store.TryPublishSegments([2], frame, descriptor, out var copiedBytes);
```

The committed value is still one immutable contiguous payload for readers.
