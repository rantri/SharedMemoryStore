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

    [Fact]
    public void TryPublishRemainsCompatibleBesideReservations()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options(slotCount: 2));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, [9], out var reservation));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [3], [4]));

        new byte[] { 5, 6 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new byte[] { 5, 6 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 9 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
    }
}
