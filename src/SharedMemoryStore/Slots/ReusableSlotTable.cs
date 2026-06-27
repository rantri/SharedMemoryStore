using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Slots;

internal sealed unsafe class ReusableSlotTable
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly StoreLayout _layout;
    private readonly int _slotSize = Marshal.SizeOf<SharedSlotMetadata>();
    private int _nextSearch;

    public ReusableSlotTable(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _region = region;
        _layout = layout;
    }

    public void Initialize()
    {
        for (var i = 0; i < _layout.SlotCount; i++)
        {
            ref var slot = ref GetSlot(i);
            slot.State = LayoutConstants.SlotFree;
            slot.Generation = 1;
            slot.UsageCount = 0;
            slot.KeyLength = 0;
            slot.DescriptorLength = 0;
            slot.ValueLength = 0;
            slot.PublisherProcessId = 0;
            slot.KeyHash = 0;
            slot.DescriptorOffset = _layout.DescriptorStorageOffset + ((long)i * _layout.DescriptorStride);
            slot.PayloadOffset = _layout.PayloadStorageOffset + ((long)i * _layout.PayloadStride);
            slot.CommittedSequence = 0;
        }
    }

    public bool TryReserve(out int slotIndex)
    {
        var start = unchecked(Interlocked.Increment(ref _nextSearch) & int.MaxValue);
        for (var step = 0; step < _layout.SlotCount; step++)
        {
            var candidate = Math.Abs(start + step) % _layout.SlotCount;
            ref var slot = ref GetSlot(candidate);
            if (Volatile.Read(ref slot.State) == LayoutConstants.SlotFree)
            {
                Volatile.Write(ref slot.State, LayoutConstants.SlotPublishing);
                slot.UsageCount = 0;
                slot.PublisherProcessId = Environment.ProcessId;
                slot.CommittedSequence = 0;
                slotIndex = candidate;
                return true;
            }
        }

        slotIndex = -1;
        return false;
    }

    public void Abort(int slotIndex)
    {
        ref var slot = ref GetSlot(slotIndex);
        slot.KeyHash = 0;
        slot.KeyLength = 0;
        slot.ValueLength = 0;
        slot.DescriptorLength = 0;
        slot.UsageCount = 0;
        Volatile.Write(ref slot.State, LayoutConstants.SlotFree);
    }

    public void Commit(int slotIndex, ulong keyHash, int keyLength, int descriptorLength, int valueLength, long sequence)
    {
        ref var slot = ref GetSlot(slotIndex);
        slot.KeyHash = keyHash;
        slot.KeyLength = keyLength;
        slot.DescriptorLength = descriptorLength;
        slot.ValueLength = valueLength;
        slot.PublisherProcessId = Environment.ProcessId;
        slot.CommittedSequence = sequence;
        Volatile.Write(ref slot.State, LayoutConstants.SlotPublished);
    }

    public void Reclaim(int slotIndex)
    {
        ref var slot = ref GetSlot(slotIndex);
        Volatile.Write(ref slot.State, LayoutConstants.SlotReclaiming);
        slot.KeyHash = 0;
        slot.KeyLength = 0;
        slot.ValueLength = 0;
        slot.DescriptorLength = 0;
        slot.PublisherProcessId = 0;
        slot.UsageCount = 0;
        checked
        {
            slot.Generation++;
        }

        Volatile.Write(ref slot.State, LayoutConstants.SlotFree);
    }

    public ref SharedSlotMetadata GetSlot(int slotIndex)
    {
        Debug.Assert((uint)slotIndex < (uint)_layout.SlotCount);
        return ref *(SharedSlotMetadata*)(_region.Pointer + _layout.SlotMetadataOffset + ((long)slotIndex * _slotSize));
    }

    public bool IsPublishedGeneration(int slotIndex, int generation)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return false;
        }

        ref var slot = ref GetSlot(slotIndex);
        var state = Volatile.Read(ref slot.State);
        return state is LayoutConstants.SlotPublished or LayoutConstants.SlotRemoveRequested
            && slot.Generation == generation;
    }

    public (int Free, int Published, int PendingRemoval) CountStates()
    {
        var free = 0;
        var published = 0;
        var pending = 0;

        for (var i = 0; i < _layout.SlotCount; i++)
        {
            ref var slot = ref GetSlot(i);
            switch (Volatile.Read(ref slot.State))
            {
                case LayoutConstants.SlotFree:
                    free++;
                    break;
                case LayoutConstants.SlotPublished:
                    published++;
                    break;
                case LayoutConstants.SlotRemoveRequested:
                    pending++;
                    break;
            }
        }

        return (free, published, pending);
    }
}
