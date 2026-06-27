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
}
