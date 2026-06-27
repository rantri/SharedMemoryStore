using System.Threading;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Slots;

internal sealed class SlotReclaimer
{
    private readonly ReusableSlotTable _slots;
    private readonly SharedKeyIndex _index;

    public SlotReclaimer(ReusableSlotTable slots, SharedKeyIndex index)
    {
        _slots = slots;
        _index = index;
    }

    public StoreStatus RequestRemove(int slotIndex, int generation)
    {
        ref var slot = ref _slots.GetSlot(slotIndex);
        var state = Volatile.Read(ref slot.State);

        if (state == LayoutConstants.SlotRemoveRequested)
        {
            return StoreStatus.RemovePending;
        }

        if (state != LayoutConstants.SlotPublished || slot.Generation != generation)
        {
            return StoreStatus.NotFound;
        }

        if (Volatile.Read(ref slot.UsageCount) > 0)
        {
            Volatile.Write(ref slot.State, LayoutConstants.SlotRemoveRequested);
            return StoreStatus.RemovePending;
        }

        _index.TryRemoveSlot(slotIndex, generation);
        _slots.Reclaim(slotIndex);
        return StoreStatus.Success;
    }

    public StoreStatus ReclaimAfterFinalRelease(int slotIndex, int generation)
    {
        ref var slot = ref _slots.GetSlot(slotIndex);
        if (slot.Generation != generation)
        {
            return StoreStatus.InvalidLease;
        }

        if (Volatile.Read(ref slot.State) == LayoutConstants.SlotRemoveRequested
            && Volatile.Read(ref slot.UsageCount) == 0)
        {
            _index.TryRemoveSlot(slotIndex, generation);
            _slots.Reclaim(slotIndex);
        }

        return StoreStatus.Success;
    }
}
