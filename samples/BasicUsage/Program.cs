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
    Console.WriteLine($"open failed: {openStatus}");
    return 1;
}

using (store)
{
    var key = new byte[] { 1, 2, 3 };
    var descriptor = new byte[] { 9, 8 };
    var payload = new byte[] { 4, 5, 6, 7 };

    Console.WriteLine(store.TryPublish(key, payload, descriptor));
    Console.WriteLine(store.TryAcquire(key, out var lease));
    Console.WriteLine($"value bytes: {BitConverter.ToString(lease.ValueSpan.ToArray())}");
    Console.WriteLine(lease.Release());
    Console.WriteLine(store.TryRemove(key));
    Console.WriteLine(store.TryPublish(key, [10]));
    Console.WriteLine($"free slots: {store.GetDiagnostics().FreeSlotCount}");
}

return 0;
