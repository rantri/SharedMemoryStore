using SharedMemoryStore.IntegrationTests.TestSupport;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class MultiStoreLifecycleIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void MultipleNamedStoresRemainIsolated()
    {
        using var first = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options());
        using var second = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, first.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, second.TryPublish([1], [2]));

        Assert.Equal(StoreStatus.Success, first.TryAcquire([1], out var firstLease));
        Assert.Equal(StoreStatus.Success, second.TryAcquire([1], out var secondLease));
        Assert.Equal(1, firstLease.ValueSpan[0]);
        Assert.Equal(2, secondLease.ValueSpan[0]);
        firstLease.Dispose();
        secondLease.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DisposingOneHandleDoesNotMakeSameNamedStoreUnavailable()
    {
        var createOptions = IntegrationStoreFactory.Options(slotCount: 2, maxValueBytes: 8, maxDescriptorBytes: 2, maxKeyBytes: 2, leaseRecordCount: 4);
        var openOptions = new SharedMemoryStoreOptions
        {
            Name = createOptions.Name,
            OpenMode = OpenMode.OpenExisting,
            SlotCount = createOptions.SlotCount,
            MaxValueBytes = createOptions.MaxValueBytes,
            MaxDescriptorBytes = createOptions.MaxDescriptorBytes,
            MaxKeyBytes = createOptions.MaxKeyBytes,
            LeaseRecordCount = createOptions.LeaseRecordCount,
            EnableLeaseRecovery = createOptions.EnableLeaseRecovery,
            TotalBytes = createOptions.TotalBytes
        };

        var first = IntegrationStoreFactory.Create(createOptions);
        Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(openOptions, out var second));
        Assert.NotNull(second);

        Assert.Equal(StoreStatus.Success, first.TryPublish([1], [10]));
        first.Dispose();

        using (second)
        {
            Assert.Equal(StoreStatus.Success, second.TryAcquire([1], out var lease));
            Assert.Equal(10, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.Equal(StoreStatus.Success, second.TryRemove([1]));
            Assert.Equal(StoreStatus.Success, second.TryPublish([2], [20]));
            Assert.Equal(StoreStatus.Success, second.TryRemove([2]));
        }
    }
}
