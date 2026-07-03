using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.ContractTests;

internal static class ContractStoreFactory
{
    public static SharedMemoryStoreOptions Options(
        int slotCount = 3,
        int maxValueBytes = 64,
        int maxDescriptorBytes = 16,
        int maxKeyBytes = 16,
        int leaseRecordCount = 3,
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
