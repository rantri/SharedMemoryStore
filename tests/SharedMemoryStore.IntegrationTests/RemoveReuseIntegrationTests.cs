using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class RemoveReuseIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void PublishAcquireRemoveReleaseAndReuseSameSlot()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2]));
        var first = SharedMemoryLayoutReader.ReadFirstPublished(store);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, lease.Release());

        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [3, 4]));
        var second = SharedMemoryLayoutReader.ReadFirstPublished(store);
        Assert.Equal(first.SlotIndex, second.SlotIndex);
        Assert.Equal(first.Generation + 1, second.Generation);
    }
}
