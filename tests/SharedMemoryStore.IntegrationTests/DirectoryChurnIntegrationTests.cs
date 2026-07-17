using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class DirectoryChurnIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void HighChurnPreservesProtectedValuesReservationsAndDirectoryHealth()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(
            slotCount: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 8));
        Assert.Equal(StoreStatus.Success, store.TryPublish([99], [99]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([99], out var protectedLease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([99]));
        Assert.Equal(StoreStatus.Success, store.TryReserve([88], 1, default, out var pendingReservation));
        Assert.True(pendingReservation.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([88], out _));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([88], [1]));

        for (var iteration = 0; iteration < 32; iteration++)
        {
            byte[] key = BitConverter.GetBytes(iteration);
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)iteration]));
            Assert.Equal(StoreStatus.Success, store.TryRemove(key));
        }

        DiagnosticsSnapshot diagnostics = store.GetDiagnostics();
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), diagnostics.ProtocolInfo);
        Assert.InRange(
            diagnostics.PrimaryDirectoryOccupancy,
            0,
            diagnostics.SlotCount);
        Assert.InRange(
            diagnostics.OverflowDirectoryOccupancy,
            0,
            diagnostics.SlotCount);
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
