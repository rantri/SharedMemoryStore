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
            record.SlotReuseEpoch = 0;
            record.OwnerProcessId = 0;
            record.AcquireSequence = 0;
        }
    }

    public bool TryActivate(int slotIndex, SlotLifecycleId lifecycleId, long sequence, out int leaseRecordId)
    {
        var start = unchecked((uint)Interlocked.Increment(ref _nextSearch));
        for (var step = 0; step < _layout.LeaseRecordCount; step++)
        {
            var candidate = (int)((start + (uint)step) % (uint)_layout.LeaseRecordCount);
            ref var record = ref GetRecord(candidate);
            if (Volatile.Read(ref record.State) == LayoutConstants.LeaseActive)
            {
                continue;
            }

            record.LeaseRecordId = candidate;
            record.SlotIndex = slotIndex;
            record.SlotGeneration = lifecycleId.Generation;
            record.SlotReuseEpoch = lifecycleId.ReuseEpoch;
            record.OwnerProcessId = Environment.ProcessId;
            record.AcquireSequence = sequence;
            Volatile.Write(ref record.State, LayoutConstants.LeaseActive);
            leaseRecordId = candidate;
            return true;
        }

        leaseRecordId = -1;
        return false;
    }

    public bool IsActive(int leaseRecordId, int slotIndex, SlotLifecycleId lifecycleId)
    {
        if ((uint)leaseRecordId >= (uint)_layout.LeaseRecordCount)
        {
            return false;
        }

        ref var record = ref GetRecord(leaseRecordId);
        return Volatile.Read(ref record.State) == LayoutConstants.LeaseActive
            && record.SlotIndex == slotIndex
            && lifecycleId.Matches(record.SlotGeneration, record.SlotReuseEpoch);
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

    internal void SetNextSearchForTesting(int nextSearch)
    {
        Volatile.Write(ref _nextSearch, nextSearch);
    }
}
