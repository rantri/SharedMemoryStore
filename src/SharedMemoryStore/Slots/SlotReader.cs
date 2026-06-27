using System.Threading;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Slots;

internal sealed unsafe class SlotReader
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly ReusableSlotTable _slots;

    public SlotReader(MemoryMappedStoreRegion region, ReusableSlotTable slots)
    {
        _region = region;
        _slots = slots;
    }

    public int GetValueLength(int slotIndex, int generation)
    {
        if (!_slots.IsPublishedGeneration(slotIndex, generation))
        {
            return 0;
        }

        return _slots.GetSlot(slotIndex).ValueLength;
    }

    public int GetDescriptorLength(int slotIndex, int generation)
    {
        if (!_slots.IsPublishedGeneration(slotIndex, generation))
        {
            return 0;
        }

        return _slots.GetSlot(slotIndex).DescriptorLength;
    }

    public ReadOnlySpan<byte> GetValueSpan(int slotIndex, int generation)
    {
        if (!_slots.IsPublishedGeneration(slotIndex, generation))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ref var slot = ref _slots.GetSlot(slotIndex);
        Thread.MemoryBarrier();
        return new ReadOnlySpan<byte>(_region.Pointer + slot.PayloadOffset, slot.ValueLength);
    }

    public ReadOnlySpan<byte> GetDescriptorSpan(int slotIndex, int generation)
    {
        if (!_slots.IsPublishedGeneration(slotIndex, generation))
        {
            return ReadOnlySpan<byte>.Empty;
        }

        ref var slot = ref _slots.GetSlot(slotIndex);
        Thread.MemoryBarrier();
        return new ReadOnlySpan<byte>(_region.Pointer + slot.DescriptorOffset, slot.DescriptorLength);
    }
}
