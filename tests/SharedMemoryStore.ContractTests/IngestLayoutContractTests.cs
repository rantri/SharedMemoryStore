using SharedMemoryStore.Layout;

namespace SharedMemoryStore.ContractTests;

public sealed class IngestLayoutContractTests
{
    [Fact]
    public void LayoutVersionAndSlotStateValuesMatchIngestContract()
    {
        Assert.Equal(1, LayoutConstants.LayoutMajorVersion);
        Assert.Equal(1, LayoutConstants.LayoutMinorVersion);
        Assert.Equal(0, LayoutConstants.SlotFree);
        Assert.Equal(1, LayoutConstants.SlotPublishing);
        Assert.Equal(2, LayoutConstants.SlotPublished);
        Assert.Equal(3, LayoutConstants.SlotRemoveRequested);
        Assert.Equal(4, LayoutConstants.SlotReclaiming);
    }

    [Fact]
    public void ReservationProgressUsesReservedSlotMetadata()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, [2], out var reservation));
        ref var slot = ref FindPublishingSlot(store);
        Assert.Equal(LayoutConstants.SlotPublishing, slot.State);
        Assert.Equal(4, slot.ValueLength);
        Assert.Equal(0, slot.Reserved);

        Assert.Equal(StoreStatus.Success, reservation.Advance(3));
        Assert.Equal(3, slot.Reserved);
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    private static ref SharedSlotMetadata FindPublishingSlot(SharedMemoryStore store)
    {
        for (var i = 0; i < store.Layout.SlotCount; i++)
        {
            ref var slot = ref store.GetSlotForTesting(i);
            if (slot.State == LayoutConstants.SlotPublishing)
            {
                return ref slot;
            }
        }

        throw new InvalidOperationException("No publishing slot found.");
    }
}
