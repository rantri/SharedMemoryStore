using SharedMemoryStore.Layout;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class CorruptStoreTests
{
    [Fact]
    public void OperationsReturnCorruptStatusAfterSafeErrorMode()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        store.Header.StoreState = LayoutConstants.StoreCorrupt;

        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.CorruptStore, store.TryRemove([1]));
    }

    [Fact]
    public void DetectedUsageUnderflowMovesStoreIntoSafeErrorMode()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        ref var slot = ref store.GetSlotForTesting(lease.SlotIndexForTesting);
        slot.UsageCount = 0;

        Assert.Equal(StoreStatus.CorruptStore, lease.Release());
        Assert.Equal(LayoutConstants.StoreCorrupt, store.Header.StoreState);
        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([2], [2]));
    }
}
