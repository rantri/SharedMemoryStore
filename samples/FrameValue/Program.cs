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

    Console.WriteLine(store.TryPublish(frameKey, frame, descriptor));
    Console.WriteLine(store.TryAcquire(frameKey, out var firstReader));
    Console.WriteLine(store.TryAcquire(frameKey, out var secondReader));

    var parsed = FrameDescriptor.FromBytes(firstReader.DescriptorSpan);
    Console.WriteLine($"frame {parsed.Width}x{parsed.Height}, bytes {parsed.PixelBytes}, readers equal {firstReader.ValueSpan.SequenceEqual(secondReader.ValueSpan)}");

    Console.WriteLine(store.TryRemove(frameKey));
    firstReader.Dispose();
    secondReader.Dispose();

    Console.WriteLine(store.TryPublish(otherKey, [1, 2, 3]));
    Console.WriteLine(store.TryAcquire(otherKey, out var other));
    Console.WriteLine($"non-frame bytes: {other.ValueLength}");
    other.Dispose();
}

return 0;
