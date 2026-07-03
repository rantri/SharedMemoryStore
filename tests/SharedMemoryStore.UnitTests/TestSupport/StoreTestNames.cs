using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.UnitTests.TestSupport;

internal static class StoreTestNames
{
    public static string Create() => $"sms-{Guid.NewGuid():N}";

    public static SharedMemoryStoreOptions Options(
        int slotCount = 4,
        int maxValueBytes = 256,
        int maxDescriptorBytes = 32,
        int maxKeyBytes = 32,
        int leaseRecordCount = 4,
        OpenMode mode = OpenMode.CreateOrOpen,
        bool enableRecovery = true)
    {
        return new SharedMemoryStoreOptions
        {
            Name = Create(),
            OpenMode = mode,
            SlotCount = slotCount,
            MaxValueBytes = maxValueBytes,
            MaxDescriptorBytes = maxDescriptorBytes,
            MaxKeyBytes = maxKeyBytes,
            LeaseRecordCount = leaseRecordCount,
            EnableLeaseRecovery = enableRecovery,
            TotalBytes = SharedMemoryStoreOptions.CalculateRequiredBytes(
                slotCount,
                maxValueBytes,
                maxDescriptorBytes,
                maxKeyBytes,
                leaseRecordCount)
        };
    }

    public static Store CreateStore(SharedMemoryStoreOptions options)
    {
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }
}
