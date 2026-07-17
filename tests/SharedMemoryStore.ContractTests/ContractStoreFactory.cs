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
        int participantRecordCount = 64,
        bool enableRecovery = true)
    {
        return SharedMemoryStoreOptions.Create(
            $"sms-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount,
            OpenMode.CreateOrOpen,
            enableRecovery);
    }

    public static Store Create(SharedMemoryStoreOptions options)
    {
        var status = Store.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }
}
