namespace SharedMemoryStore.ContractTests;

public sealed class RemoveReuseContractTests
{
    [Fact]
    public void TryRemoveReturnsSuccessNotFoundAndRemovePending()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options(slotCount: 1));

        Assert.Equal(StoreStatus.NotFound, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.NotFound, store.TryRemove([1]));
    }
}
