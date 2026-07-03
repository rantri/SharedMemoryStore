using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests.TestSupport;

internal static class IntegrationStoreFactory
{
    public static SharedMemoryStoreOptions Options(
        int slotCount = 4,
        int maxValueBytes = 1024,
        int maxDescriptorBytes = 64,
        int maxKeyBytes = 32,
        int leaseRecordCount = 8,
        bool enableRecovery = true)
    {
        return new SharedMemoryStoreOptions
        {
            Name = $"sms-{Guid.NewGuid():N}",
            OpenMode = OpenMode.CreateOrOpen,
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

    public static Store Create(SharedMemoryStoreOptions options)
    {
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }
}
