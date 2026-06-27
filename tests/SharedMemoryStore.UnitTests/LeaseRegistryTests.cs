using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LeaseRegistryTests
{
    [Fact]
    public void AcquireFailsDeterministicallyWhenLeaseTableIsFull()
    {
        var options = StoreTestNames.Options(slotCount: 1, leaseRecordCount: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        Assert.True(first.IsValid);

        Assert.Equal(StoreStatus.LeaseTableFull, store.TryAcquire([1], out var second));
        Assert.False(second.IsValid);

        Assert.Equal(StoreStatus.Success, first.Release());
    }

    [Fact]
    public void ReleaseValidatesGenerationAndDoubleRelease()
    {
        var options = StoreTestNames.Options(slotCount: 1, leaseRecordCount: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.LeaseAlreadyReleased, lease.Release());
    }
}
