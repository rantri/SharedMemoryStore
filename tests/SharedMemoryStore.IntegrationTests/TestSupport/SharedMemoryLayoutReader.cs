using SharedMemoryStore.Layout;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.IntegrationTests.TestSupport;

internal static class SharedMemoryLayoutReader
{
    public static PublishedSlot ReadFirstPublished(Store store)
    {
        for (var i = 0; i < store.Layout.SlotCount; i++)
        {
            ref var slot = ref store.GetSlotForTesting(i);
            if (slot.State is LayoutConstants.SlotPublished or LayoutConstants.SlotRemoveRequested)
            {
                return new PublishedSlot(i, slot.Generation, slot.KeyLength, slot.DescriptorLength, slot.ValueLength, slot.UsageCount);
            }
        }

        throw new InvalidOperationException("No published slot found.");
    }
}

internal readonly record struct PublishedSlot(
    int SlotIndex,
    int Generation,
    int KeyLength,
    int DescriptorLength,
    int ValueLength,
    int UsageCount);
