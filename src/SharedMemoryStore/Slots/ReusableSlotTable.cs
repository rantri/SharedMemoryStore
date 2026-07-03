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
            slot.Generation = SlotLifecycleId.Initial.Generation;
            slot.ReuseEpoch = SlotLifecycleId.Initial.ReuseEpoch;
            slot.UsageCount = 0;
            slot.KeyLength = 0;
            slot.DescriptorLength = 0;
            slot.ValueLength = 0;
            slot.PublisherProcessId = 0;
            slot.Reserved = 0;
            slot.KeyHash = 0;
            slot.DescriptorOffset = _layout.DescriptorStorageOffset + ((long)i * _layout.DescriptorStride);
            slot.PayloadOffset = _layout.PayloadStorageOffset + ((long)i * _layout.PayloadStride);
            slot.CommittedSequence = 0;
        }
    }

    public bool TryReserve(out int slotIndex)
    {
        var start = unchecked((uint)Interlocked.Increment(ref _nextSearch));
        for (var step = 0; step < _layout.SlotCount; step++)
        {
            var candidate = (int)((start + (uint)step) % (uint)_layout.SlotCount);
            ref var slot = ref GetSlot(candidate);
            if (Volatile.Read(ref slot.State) == LayoutConstants.SlotFree)
            {
                Volatile.Write(ref slot.State, LayoutConstants.SlotPublishing);
                slot.UsageCount = 0;
                slot.PublisherProcessId = Environment.ProcessId;
                slot.Reserved = 0;
                slot.KeyHash = 0;
                slot.KeyLength = 0;
                slot.DescriptorLength = 0;
                slot.ValueLength = 0;
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
        slot.PublisherProcessId = 0;
        slot.Reserved = 0;
        slot.CommittedSequence = 0;
        Volatile.Write(ref slot.State, LayoutConstants.SlotFree);
    }

    public void PrepareReservation(int slotIndex, ulong keyHash, int keyLength, int descriptorLength, int valueLength)
    {
        ref var slot = ref GetSlot(slotIndex);
        slot.KeyHash = keyHash;
        slot.KeyLength = keyLength;
        slot.DescriptorLength = descriptorLength;
        slot.ValueLength = valueLength;
        slot.PublisherProcessId = Environment.ProcessId;
        slot.Reserved = 0;
        slot.CommittedSequence = 0;
    }

    public StoreStatus AdvanceReservation(int slotIndex, SlotLifecycleId lifecycleId, int byteCount)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return StoreStatus.InvalidReservation;
        }

        ref var slot = ref GetSlot(slotIndex);
        if (!lifecycleId.Matches(slot.Generation, slot.ReuseEpoch))
        {
            return StoreStatus.InvalidReservation;
        }

        if (Volatile.Read(ref slot.State) != LayoutConstants.SlotPublishing)
        {
            return StoreStatus.ReservationAlreadyCompleted;
        }

        var remaining = slot.ValueLength - slot.Reserved;
        if (byteCount < 0 || byteCount > remaining)
        {
            return StoreStatus.ReservationWriteOutOfRange;
        }

        slot.Reserved += byteCount;
        return StoreStatus.Success;
    }

    public StoreStatus ValidatePendingReservation(int slotIndex, SlotLifecycleId lifecycleId, out SharedSlotMetadata slot)
    {
        slot = default;
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return StoreStatus.InvalidReservation;
        }

        ref var current = ref GetSlot(slotIndex);
        slot = current;
        if (!lifecycleId.Matches(current.Generation, current.ReuseEpoch))
        {
            return StoreStatus.InvalidReservation;
        }

        return Volatile.Read(ref current.State) == LayoutConstants.SlotPublishing
            ? StoreStatus.Success
            : StoreStatus.ReservationAlreadyCompleted;
    }

    public bool IsPendingReservation(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return false;
        }

        ref var slot = ref GetSlot(slotIndex);
        return Volatile.Read(ref slot.State) == LayoutConstants.SlotPublishing
            && lifecycleId.Matches(slot.Generation, slot.ReuseEpoch);
    }

    public void Commit(int slotIndex, ulong keyHash, int keyLength, int descriptorLength, int valueLength, long sequence)
    {
        ref var slot = ref GetSlot(slotIndex);
        slot.KeyHash = keyHash;
        slot.KeyLength = keyLength;
        slot.DescriptorLength = descriptorLength;
        slot.ValueLength = valueLength;
        slot.PublisherProcessId = Environment.ProcessId;
        slot.Reserved = 0;
        slot.CommittedSequence = sequence;
        Volatile.Write(ref slot.State, LayoutConstants.SlotPublished);
    }

    public StoreStatus Reclaim(int slotIndex)
    {
        ref var slot = ref GetSlot(slotIndex);
        Volatile.Write(ref slot.State, LayoutConstants.SlotReclaiming);
        if (!SlotLifecycleId.FromSlot(slot).TryAdvance(out var next))
        {
            return StoreStatus.CorruptStore;
        }

        slot.KeyHash = 0;
        slot.KeyLength = 0;
        slot.ValueLength = 0;
        slot.DescriptorLength = 0;
        slot.PublisherProcessId = 0;
        slot.UsageCount = 0;
        slot.Reserved = 0;
        slot.CommittedSequence = 0;
        slot.Generation = next.Generation;
        slot.ReuseEpoch = next.ReuseEpoch;

        Volatile.Write(ref slot.State, LayoutConstants.SlotFree);
        return StoreStatus.Success;
    }

    public ref SharedSlotMetadata GetSlot(int slotIndex)
    {
        Debug.Assert((uint)slotIndex < (uint)_layout.SlotCount);
        return ref *(SharedSlotMetadata*)(_region.Pointer + _layout.SlotMetadataOffset + ((long)slotIndex * _slotSize));
    }

    public bool IsPublishedGeneration(int slotIndex, SlotLifecycleId lifecycleId)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return false;
        }

        ref var slot = ref GetSlot(slotIndex);
        var state = Volatile.Read(ref slot.State);
        return state is LayoutConstants.SlotPublished or LayoutConstants.SlotRemoveRequested
            && lifecycleId.Matches(slot.Generation, slot.ReuseEpoch);
    }

    internal void SetNextSearchForTesting(int nextSearch)
    {
        Volatile.Write(ref _nextSearch, nextSearch);
    }

    public (int Free, int Published, int PendingRemoval, int ActiveReservations) CountStates()
    {
        var free = 0;
        var published = 0;
        var pending = 0;
        var activeReservations = 0;

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
                case LayoutConstants.SlotPublishing:
                    activeReservations++;
                    break;
            }
        }

        return (free, published, pending, activeReservations);
    }

    public int SlotCount => _layout.SlotCount;
}
