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

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        ValueLease staleLease = lease;
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.False(staleLease.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [3, 4]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out ValueLease replacement));
        Assert.Equal(new byte[] { 3, 4 }, replacement.ValueSpan.ToArray());
        Assert.Equal(StoreStatus.Success, replacement.Release());
        Assert.False(staleLease.IsValid);
    }
}
