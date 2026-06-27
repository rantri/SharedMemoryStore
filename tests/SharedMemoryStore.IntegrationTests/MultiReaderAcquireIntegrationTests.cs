using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class MultiReaderAcquireIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void MultipleReadersObserveIdenticalBytesUntilFinalRelease()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1, leaseRecordCount: 8));
        var value = new byte[] { 7, 8, 9, 10 };

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], value));
        var leases = new ValueLease[4];
        for (var i = 0; i < leases.Length; i++)
        {
            Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out leases[i]));
            Assert.True(value.AsSpan().SequenceEqual(leases[i].ValueSpan));
        }

        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(4, store.GetDiagnostics().ActiveLeaseCount);

        for (var i = 0; i < leases.Length; i++)
        {
            Assert.Equal(StoreStatus.Success, leases[i].Release());
        }

        Assert.Equal(1, store.GetDiagnostics().FreeSlotCount);
    }
}
