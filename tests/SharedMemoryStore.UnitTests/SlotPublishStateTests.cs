using SharedMemoryStore.Layout;
using SharedMemoryStore.UnitTests.TestSupport;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.UnitTests;

public sealed class SlotPublishStateTests
{
    [Fact]
    public void PublishCommitsSlotMetadataAndRejectsDuplicateKey()
    {
        var options = StoreTestNames.Options(slotCount: 2);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([10], [1, 2, 3], [4]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([10], [9]));

        ref var slot = ref FindPublishedSlot(store);
        Assert.Equal(LayoutConstants.SlotPublished, slot.State);
        Assert.Equal(1, slot.Generation);
        Assert.Equal(3, slot.ValueLength);
        Assert.Equal(1, slot.DescriptorLength);
    }

    [Fact]
    public void RemoveAndRepublishAdvancesGeneration()
    {
        var options = StoreTestNames.Options(slotCount: 1);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        ref var slot = ref store.GetSlotForTesting(0);
        var firstGeneration = slot.Generation;

        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [2]));

        Assert.Equal(firstGeneration + 1, slot.Generation);
        Assert.Equal(LayoutConstants.SlotPublished, slot.State);
    }

    private static ref SharedSlotMetadata FindPublishedSlot(Store store)
    {
        for (var i = 0; i < store.Layout.SlotCount; i++)
        {
            ref var slot = ref store.GetSlotForTesting(i);
            if (slot.State == LayoutConstants.SlotPublished)
            {
                return ref slot;
            }
        }

        throw new InvalidOperationException("No published slot found.");
    }
}
