using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Leasing;

internal sealed unsafe class LeaseRegistry
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly StoreLayout _layout;
    private readonly int _recordSize = Marshal.SizeOf<SharedLeaseRecord>();
    private int _nextSearch;

    public LeaseRegistry(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _region = region;
        _layout = layout;
    }

    public void Initialize()
    {
        for (var i = 0; i < _layout.LeaseRecordCount; i++)
        {
            ref var record = ref GetRecord(i);
            record.State = LayoutConstants.LeaseFree;
            record.LeaseRecordId = i;
            record.SlotIndex = -1;
            record.SlotGeneration = 0;
            record.OwnerProcessId = 0;
            record.AcquireSequence = 0;
        }
    }

    public bool TryActivate(int slotIndex, int generation, long sequence, out int leaseRecordId)
    {
        var start = unchecked(Interlocked.Increment(ref _nextSearch) & int.MaxValue);
        for (var step = 0; step < _layout.LeaseRecordCount; step++)
        {
            var candidate = (start + step) % _layout.LeaseRecordCount;
            ref var record = ref GetRecord(candidate);
            if (Volatile.Read(ref record.State) == LayoutConstants.LeaseActive)
            {
                continue;
            }

            record.LeaseRecordId = candidate;
            record.SlotIndex = slotIndex;
            record.SlotGeneration = generation;
            record.OwnerProcessId = Environment.ProcessId;
            record.AcquireSequence = sequence;
            Volatile.Write(ref record.State, LayoutConstants.LeaseActive);
            leaseRecordId = candidate;
            return true;
        }

        leaseRecordId = -1;
        return false;
    }

    public bool IsActive(int leaseRecordId, int slotIndex, int generation)
    {
        if ((uint)leaseRecordId >= (uint)_layout.LeaseRecordCount)
        {
            return false;
        }

        ref var record = ref GetRecord(leaseRecordId);
        return Volatile.Read(ref record.State) == LayoutConstants.LeaseActive
            && record.SlotIndex == slotIndex
            && record.SlotGeneration == generation;
    }

    public int ActiveCount()
    {
        var count = 0;
        for (var i = 0; i < _layout.LeaseRecordCount; i++)
        {
            if (Volatile.Read(ref GetRecord(i).State) == LayoutConstants.LeaseActive)
            {
                count++;
            }
        }

        return count;
    }

    public ref SharedLeaseRecord GetRecord(int leaseRecordId)
    {
        Debug.Assert((uint)leaseRecordId < (uint)_layout.LeaseRecordCount);
        return ref *(SharedLeaseRecord*)(_region.Pointer + _layout.LeaseRegistryOffset + ((long)leaseRecordId * _recordSize));
    }

    public int RecordCount => _layout.LeaseRecordCount;
}
