using System.Threading;
using SharedMemoryStore.Layout;
using SharedMemoryStore.Slots;

namespace SharedMemoryStore.Leasing;

internal static class LeaseRelease
{
    public static StoreStatus Release(
        LeaseRegistry registry,
        ReusableSlotTable slots,
        SlotReclaimer reclaimer,
        int slotIndex,
        SlotLifecycleId lifecycleId,
        int leaseRecordId)
    {
        if ((uint)leaseRecordId >= (uint)registry.RecordCount)
        {
            return StoreStatus.InvalidLease;
        }

        ref var record = ref registry.GetRecord(leaseRecordId);
        var state = Volatile.Read(ref record.State);
        if (state is LayoutConstants.LeaseReleased or LayoutConstants.LeaseAbandoned)
        {
            return StoreStatus.LeaseAlreadyReleased;
        }

        if (state != LayoutConstants.LeaseActive
            || record.SlotIndex != slotIndex
            || !lifecycleId.Matches(record.SlotGeneration, record.SlotReuseEpoch))
        {
            return StoreStatus.InvalidLease;
        }

        ref var slot = ref slots.GetSlot(slotIndex);
        if (!lifecycleId.Matches(slot.Generation, slot.ReuseEpoch))
        {
            return StoreStatus.InvalidLease;
        }

        Volatile.Write(ref record.State, LayoutConstants.LeaseReleased);
        var remaining = Interlocked.Decrement(ref slot.UsageCount);
        if (remaining < 0)
        {
            Volatile.Write(ref slot.State, LayoutConstants.SlotFree);
            return StoreStatus.CorruptStore;
        }

        return remaining == 0
            ? reclaimer.ReclaimAfterFinalRelease(slotIndex, lifecycleId)
            : StoreStatus.Success;
    }
}
