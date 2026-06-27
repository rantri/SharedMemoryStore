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
