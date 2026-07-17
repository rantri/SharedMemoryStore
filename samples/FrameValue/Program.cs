using FrameValue;
using SharedMemoryStore;

var frame = new byte[1_300_000];
for (var i = 0; i < frame.Length; i++)
{
    frame[i] = (byte)(i % 251);
}

var descriptor = new FrameDescriptor(Width: 1280, Height: 720, PixelBytes: frame.Length, TimestampTicks: DateTime.UtcNow.Ticks).ToBytes();
var options = SharedMemoryStoreOptions.Create(
    name: $"sms-frame-{Guid.NewGuid():N}",
    slotCount: 2,
    maxValueBytes: frame.Length,
    maxDescriptorBytes: descriptor.Length,
    maxKeyBytes: 16,
    leaseRecordCount: 4,
    participantRecordCount: 4,
    openMode: OpenMode.CreateNew,
    enableLeaseRecovery: true);

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    if (store.ProtocolInfo != new StoreProtocolInfo(2, 0, 2, 7, 0))
    {
        Console.WriteLine($"unexpected protocol: {store.ProtocolInfo}");
        return 2;
    }

    var frameKey = new byte[] { 1 };
    var otherKey = new byte[] { 2 };

    var publishFrame = store.TryPublish(frameKey, frame, descriptor);
    Console.WriteLine(publishFrame);
    if (publishFrame != StoreStatus.Success)
    {
        return 3;
    }

    var firstAcquire = store.TryAcquire(frameKey, out var firstReader);
    Console.WriteLine(firstAcquire);
    if (firstAcquire != StoreStatus.Success)
    {
        return 4;
    }

    var secondAcquire = store.TryAcquire(frameKey, out var secondReader);
    Console.WriteLine(secondAcquire);
    if (secondAcquire != StoreStatus.Success)
    {
        firstReader.Dispose();
        return 5;
    }

    var parsed = FrameDescriptor.FromBytes(firstReader.DescriptorSpan);
    Console.WriteLine($"frame {parsed.Width}x{parsed.Height}, bytes {parsed.PixelBytes}, readers equal {firstReader.ValueSpan.SequenceEqual(secondReader.ValueSpan)}");

    var removeFrame = store.TryRemove(frameKey);
    Console.WriteLine(removeFrame);
    if (removeFrame != StoreStatus.RemovePending)
    {
        firstReader.Dispose();
        secondReader.Dispose();
        return 6;
    }

    firstReader.Dispose();
    secondReader.Dispose();

    var publishOther = store.TryPublish(otherKey, [1, 2, 3]);
    Console.WriteLine(publishOther);
    if (publishOther != StoreStatus.Success)
    {
        return 7;
    }

    var acquireOther = store.TryAcquire(otherKey, out var other);
    Console.WriteLine(acquireOther);
    if (acquireOther != StoreStatus.Success)
    {
        return 8;
    }

    Console.WriteLine($"non-frame bytes: {other.ValueLength}");
    other.Dispose();
}

return 0;
