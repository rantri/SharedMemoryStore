using SharedMemoryStore.Layout;

namespace SharedMemoryStore.UnitTests.TestSupport;

internal static class RolloverTestHooks
{
    public static void SeedSlotCursorNearIntBoundary(MemoryStore store)
    {
        store.SetSlotSearchCursorForTesting(int.MaxValue - 2);
    }

    public static void SeedLeaseCursorNearIntBoundary(MemoryStore store)
    {
        store.SetLeaseSearchCursorForTesting(int.MaxValue - 2);
    }

    public static void SeedSlotLifecycleNearGenerationBoundary(MemoryStore store, int slotIndex)
    {
        store.SetSlotLifecycleForTesting(slotIndex, new SlotLifecycleId(int.MaxValue, 0));
    }
}
