using FrameValue;
using SharedMemoryStore;

var frame = new byte[1_300_000];
for (var i = 0; i < frame.Length; i++)
{
    frame[i] = (byte)(i % 251);
}

var descriptor = new FrameDescriptor(Width: 1280, Height: 720, PixelBytes: frame.Length, TimestampTicks: DateTime.UtcNow.Ticks).ToBytes();
var options = new SharedMemoryStoreOptions
{
    Name = $"sms-frame-{Guid.NewGuid():N}",
    OpenMode = OpenMode.CreateOrOpen,
    SlotCount = 2,
    MaxValueBytes = frame.Length,
    MaxDescriptorBytes = descriptor.Length,
    MaxKeyBytes = 16,
    LeaseRecordCount = 4,
    EnableLeaseRecovery = true,
    TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(2, frame.Length, descriptor.Length, 16, 4)
};

var openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
if (openStatus != StoreOpenStatus.Success || store is null)
{
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    var frameKey = new byte[] { 1 };
    var otherKey = new byte[] { 2 };

    var publishFrame = store.TryPublish(frameKey, frame, descriptor);
    Console.WriteLine(publishFrame);
    if (publishFrame != StoreStatus.Success)
    {
        return 2;
    }

    var firstAcquire = store.TryAcquire(frameKey, out var firstReader);
    Console.WriteLine(firstAcquire);
    if (firstAcquire != StoreStatus.Success)
    {
        return 3;
    }

    var secondAcquire = store.TryAcquire(frameKey, out var secondReader);
    Console.WriteLine(secondAcquire);
    if (secondAcquire != StoreStatus.Success)
    {
        firstReader.Dispose();
        return 4;
    }

    var parsed = FrameDescriptor.FromBytes(firstReader.DescriptorSpan);
    Console.WriteLine($"frame {parsed.Width}x{parsed.Height}, bytes {parsed.PixelBytes}, readers equal {firstReader.ValueSpan.SequenceEqual(secondReader.ValueSpan)}");

    var removeFrame = store.TryRemove(frameKey);
    Console.WriteLine(removeFrame);
    if (removeFrame != StoreStatus.RemovePending)
    {
        firstReader.Dispose();
        secondReader.Dispose();
        return 5;
    }

    firstReader.Dispose();
    secondReader.Dispose();

    var publishOther = store.TryPublish(otherKey, [1, 2, 3]);
    Console.WriteLine(publishOther);
    if (publishOther != StoreStatus.Success)
    {
        return 6;
    }

    var acquireOther = store.TryAcquire(otherKey, out var other);
    Console.WriteLine(acquireOther);
    if (acquireOther != StoreStatus.Success)
    {
        return 7;
    }

    Console.WriteLine($"non-frame bytes: {other.ValueLength}");
    other.Dispose();
}

return 0;
