using SharedMemoryStore.Layout;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class SlotLifecycleIdentifierTests
{
    [Fact]
    public void ReclaimAdvancesLifecycleIdentityAcrossGenerationBoundary()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1, leaseRecordCount: 1));
        RolloverTestHooks.SeedSlotLifecycleNearGenerationBoundary(store, 0);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new SlotLifecycleId(int.MaxValue, 0), lease.LifecycleIdForTesting);
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, lease.Release());

        ref var slot = ref store.GetSlotForTesting(0);
        Assert.Equal(1, slot.Generation);
        Assert.Equal(1, slot.ReuseEpoch);
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [2]));
        Assert.NotEqual(StoreStatus.Success, new ValueLease(store, 0, new SlotLifecycleId(int.MaxValue, 0), 0).Release());
    }

    [Fact]
    public void ReservationTokenDoesNotRegainValidityAfterBoundaryReclaim()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1));
        RolloverTestHooks.SeedSlotLifecycleNearGenerationBoundary(store, 0);

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        Assert.Equal(new SlotLifecycleId(int.MaxValue, 0), reservation.LifecycleIdForTesting);
        Assert.Equal(StoreStatus.Success, reservation.Abort());
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var next));

        Assert.False(reservation.IsValid);
        Assert.Equal(new SlotLifecycleId(1, 1), next.LifecycleIdForTesting);
    }
}
