using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class TombstonePressureIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void HighChurnCompactionPreservesValuesAndDuplicateDetection()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 8, maxKeyBytes: 8, leaseRecordCount: 8));
        Assert.Equal(StoreStatus.Success, store.TryPublish([99], [99]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([99], out var protectedLease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([99]));
        Assert.Equal(StoreStatus.Success, store.TryReserve([88], 1, default, out var pendingReservation));
        Assert.True(pendingReservation.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([88], out _));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([88], [1]));

        for (var i = 0; i < 6; i++)
        {
            var key = BitConverter.GetBytes(i);
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)i]));
            Assert.Equal(StoreStatus.Success, store.TryRemove(key));
        }

        var diagnostics = store.GetDiagnostics();
        Assert.True(diagnostics.IndexCompactionCount > 0);
        Assert.Equal(99, protectedLease.ValueSpan[0]);
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([99], [1]));
        Assert.True(pendingReservation.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([88], out _));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([88], [1]));
        Assert.Equal(StoreStatus.Success, pendingReservation.Abort());
        Assert.Equal(StoreStatus.Success, protectedLease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([100], [100]));
    }
}
