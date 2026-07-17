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
