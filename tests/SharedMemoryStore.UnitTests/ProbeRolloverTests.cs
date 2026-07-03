using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class ProbeRolloverTests
{
    [Fact]
    public void SlotProbeCursorRolloverProducesValidCandidates()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2, leaseRecordCount: 2));
        RolloverTestHooks.SeedSlotCursorNearIntBoundary(store);

        for (var i = 0; i < 16; i++)
        {
            var key = ChurnKeyFactory.Key(i);
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [1]));
            Assert.Equal(StoreStatus.Success, store.TryRemove(key));
        }
    }

    [Fact]
    public void LeaseProbeCursorRolloverProducesValidCandidates()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1, leaseRecordCount: 1));
        RolloverTestHooks.SeedLeaseCursorNearIntBoundary(store);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));

        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
            Assert.True(lease.LeaseRecordIdForTesting >= 0);
            Assert.Equal(StoreStatus.Success, lease.Release());
        }
    }
}
