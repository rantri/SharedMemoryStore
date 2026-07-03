# Byte Encoding

SharedMemoryStore stores keys, descriptors, and payloads as opaque bytes. The
package compares keys by exact byte equality and does not parse strings,
numbers, GUIDs, frame headers, JSON, or application schemas.

Use this guide to choose canonical bytes before calling the core store API. The
API accepts `ReadOnlySpan<byte>`, so hot paths should usually write into a
caller-owned `Span<byte>` instead of allocating a new `byte[]`.

Behavior claims on this page trace to the
[public API contract](../specs/001-frame-memory-store/contracts/public-api.md)
and the
[shared-memory layout contract](../specs/001-frame-memory-store/contracts/shared-memory-layout.md).

## Encoding Rules

- Pick one byte layout for each logical key family and keep it stable.
- Include a small prefix when different entity types could otherwise collide.
- Use fixed-width binary for numeric keys.
- Use a documented byte order for cross-language data. Little-endian is common
  for .NET-only layouts; big-endian is useful when matching network or
  language-neutral conventions.
- Use UTF-8 for string keys and descriptors that are text.
- Cache encoded keys when the same string key is reused often.
- Keep keys compact because each key is stored inline in the shared index and
  must fit `MaxKeyBytes`.
- Keep descriptors small and structured. Descriptors are metadata, not a second
  payload.
- Keep payloads in their natural byte form. Use reservation ingest or segmented
  publish when creating a temporary full payload array would be wasteful.

## Key Helpers

Prefer helpers that write into a destination span:

```csharp
using System.Buffers.Binary;
using System.Text;

static class StoreByteEncoding
{
    public const int Int32ByteCount = 4;
    public const int GuidByteCount = 16;

    public static void WriteInt32LittleEndian(int value, Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, value);
    }

    public static void WriteGuidBigEndian(Guid value, Span<byte> destination)
    {
        if (!value.TryWriteBytes(destination, bigEndian: true, out var bytesWritten)
            || bytesWritten != GuidByteCount)
        {
            throw new ArgumentException("Destination must have room for 16 bytes.", nameof(destination));
        }
    }

    public static int GetUtf8ByteCount(string value)
    {
        return Encoding.UTF8.GetByteCount(value);
    }

    public static bool TryWriteUtf8(string value, Span<byte> destination, out int bytesWritten)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > destination.Length)
        {
            bytesWritten = 0;
            return false;
        }

        bytesWritten = Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        return true;
    }
}
```

For an integer key:

```csharp
Span<byte> key = stackalloc byte[StoreByteEncoding.Int32ByteCount];
StoreByteEncoding.WriteInt32LittleEndian(42, key);

var status = store.TryAcquire(key, out var lease);
```

For a GUID key:

```csharp
Span<byte> key = stackalloc byte[StoreByteEncoding.GuidByteCount];
StoreByteEncoding.WriteGuidBigEndian(customerId, key);

var status = store.TryRemove(key);
```

For a string key:

```csharp
var byteCount = StoreByteEncoding.GetUtf8ByteCount(name);
Span<byte> key = byteCount <= 256
    ? stackalloc byte[byteCount]
    : new byte[byteCount];

if (!StoreByteEncoding.TryWriteUtf8(name, key, out var bytesWritten))
{
    throw new InvalidOperationException("Key destination was too small.");
}

var status = store.TryAcquire(key[..bytesWritten], out var lease);
```

If a string key is reused many times, encode it once and keep the resulting
array with the application object:

```csharp
byte[] cachedKey = Encoding.UTF8.GetBytes(name);

var status = store.TryPublish(cachedKey, payload, descriptor);
```

## Composite Keys

Raw numeric keys can collide across logical domains. Prefix the key with a
stable type or namespace byte:

```csharp
const byte OrderKeyPrefix = 1;

Span<byte> key = stackalloc byte[1 + StoreByteEncoding.Int32ByteCount];
key[0] = OrderKeyPrefix;
StoreByteEncoding.WriteInt32LittleEndian(orderId, key[1..]);

var status = store.TryPublish(key, payload, descriptor);
```

For larger composites, write each field in a fixed order and fixed width when
possible. Avoid culture-sensitive string formatting for keys.

## Descriptor Bytes

Use descriptors for compact metadata that helps readers interpret the payload.
For example:

```csharp
Span<byte> descriptor = stackalloc byte[12];
BinaryPrimitives.WriteInt32LittleEndian(descriptor[0..4], schemaVersion);
BinaryPrimitives.WriteInt64LittleEndian(descriptor[4..12], timestampTicks);

var status = store.TryPublish(key, payload, descriptor);
```

Use `ReadOnlySpan<byte>.Empty` when there is no descriptor:

```csharp
var status = store.TryPublish(key, payload, ReadOnlySpan<byte>.Empty);
```

## Payload Bytes

If the payload already exists as contiguous bytes, pass it directly:

```csharp
var status = store.TryPublish(key, payloadBytes, descriptor);
```

If the payload length is known before all bytes are available, reserve
store-owned memory and write chunks into the reservation:

```csharp
var reserve = store.TryReserve(key, payloadLength, descriptor, out var reservation);
```

If the payload already exists as multiple segments, publish the sequence without
first flattening it into a caller-owned array:

```csharp
var publish = store.TryPublishSegments(key, payloadSequence, descriptor, out var copiedBytes);
```

## Allocating Convenience Methods

Allocating helpers can be useful in setup paths, tests, and non-hot code:

```csharp
static byte[] ToInt32Key(int value)
{
    var key = new byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(key, value);
    return key;
}
```

Keep allocating helpers clearly named and documented. In hot paths, prefer the
span-writing helpers above.

## Related Samples

- [Basic usage sample](../samples/BasicUsage/README.md): uses small helper
  methods for an integer key and fixed binary descriptor.
- [Frame value sample](../samples/FrameValue/README.md): uses descriptor bytes
  for frame metadata.
- [Zero-copy ingest sample](../samples/ZeroCopyIngest/README.md): keeps payload
  bytes out of temporary full arrays by using reservation and segmented publish
  workflows.
