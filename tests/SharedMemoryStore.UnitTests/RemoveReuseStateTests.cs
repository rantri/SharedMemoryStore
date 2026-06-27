using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class RemoveReuseStateTests
{
    [Fact]
    public void RemoveWithoutLeaseReclaimsSlotImmediately()
    {
        var options = StoreTestNames.Options(slotCount: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(1, diagnostics.FreeSlotCount);
        Assert.Equal(0, diagnostics.PublishedSlotCount);
    }

    [Fact]
    public void RemoveWhileLeasedBlocksDuplicateUntilFinalRelease()
    {
        var options = StoreTestNames.Options(slotCount: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2, 3]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [4]));
        Assert.Equal(new byte[] { 1, 2, 3 }, lease.ValueSpan.ToArray());

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [4]));
    }
}
