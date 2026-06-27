using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

internal static class BenchmarkStoreFactory
{
    public static SharedMemoryStoreOptions Options(
        int slotCount = 8,
        int maxValueBytes = 1024,
        int maxDescriptorBytes = 64,
        int maxKeyBytes = 16,
        int leaseRecordCount = 16,
        bool enableRecovery = true)
    {
        return new SharedMemoryStoreOptions
        {
            Name = $"sms-bench-{Guid.NewGuid():N}",
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

    public static Store Create(
        int slotCount = 8,
        int maxValueBytes = 1024,
        int maxDescriptorBytes = 64,
        int maxKeyBytes = 16,
        int leaseRecordCount = 16,
        bool enableRecovery = true)
    {
        var options = Options(
            slotCount,
            maxValueBytes,
            maxDescriptorBytes,
            maxKeyBytes,
            leaseRecordCount,
            enableRecovery);

        return Create(options);
    }

    public static Store Create(SharedMemoryStoreOptions options)
    {
        var status = Store.TryCreateOrOpen(options, out var store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            throw new InvalidOperationException($"Failed to open benchmark store: {status}");
        }

        return store;
    }
}
