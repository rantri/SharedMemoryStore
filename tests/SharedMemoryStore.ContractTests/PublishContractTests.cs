namespace SharedMemoryStore.ContractTests;

public sealed class PublishContractTests
{
    [Fact]
    public void TryPublishReturnsDocumentedStatuses()
    {
        var options = ContractStoreFactory.Options(slotCount: 1, maxValueBytes: 2, maxDescriptorBytes: 1, maxKeyBytes: 2);
        using var store = ContractStoreFactory.Create(options);

        Assert.Equal(StoreStatus.KeyTooLarge, store.TryPublish([1, 2, 3], [1]));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryPublish([1], [1, 2, 3]));
        Assert.Equal(StoreStatus.DescriptorTooLarge, store.TryPublish([1], [1], [1, 2]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2], [9]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [2]));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([2], [2]));
    }
}
