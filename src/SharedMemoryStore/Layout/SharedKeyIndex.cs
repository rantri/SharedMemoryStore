using System.Runtime.InteropServices;
using System.Threading;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.Layout;

internal sealed unsafe class SharedKeyIndex
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly StoreLayout _layout;
    private readonly int _entryHeaderSize = Marshal.SizeOf<SharedIndexEntryHeader>();
    private int _lastObservedProbeLength;
    private int _maxObservedProbeLength;

    public SharedKeyIndex(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _region = region;
        _layout = layout;
    }

    public bool TryFind(ReadOnlySpan<byte> key, ulong hash, out int slotIndex, out SlotLifecycleId lifecycleId)
    {
        slotIndex = -1;
        lifecycleId = default;
        var start = ProbeStart(hash);
        var probes = 0;

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            probes++;
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexEmpty)
            {
                RecordProbeLength(probes);
                return false;
            }

            if (state == LayoutConstants.IndexOccupied
                && entry.KeyHash == hash
                && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
            {
                slotIndex = entry.SlotIndex;
                lifecycleId = SlotLifecycleId.FromIndex(entry);
                RecordProbeLength(probes);
                return true;
            }
        }

        RecordProbeLength(probes);
        return false;
    }

    public bool TryInsert(ReadOnlySpan<byte> key, ulong hash, int slotIndex, SlotLifecycleId lifecycleId)
    {
        var start = ProbeStart(hash);
        var firstTombstone = -1;
        var probes = 0;

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            probes++;
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexOccupied)
            {
                if (entry.KeyHash == hash
                && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
                {
                    RecordProbeLength(probes);
                    return false;
                }

                continue;
            }

            if (state == LayoutConstants.IndexTombstone)
            {
                firstTombstone = firstTombstone < 0 ? entryIndex : firstTombstone;
                continue;
            }

            WriteEntry(firstTombstone >= 0 ? firstTombstone : entryIndex, key, hash, slotIndex, lifecycleId);
            RecordProbeLength(probes);
            return true;
        }

        if (firstTombstone >= 0)
        {
            WriteEntry(firstTombstone, key, hash, slotIndex, lifecycleId);
            RecordProbeLength(probes);
            return true;
        }

        RecordProbeLength(probes);
        return false;
    }

    public bool TryRemove(ReadOnlySpan<byte> key, ulong hash)
    {
        var start = ProbeStart(hash);
        var probes = 0;

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            probes++;
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexEmpty)
            {
                RecordProbeLength(probes);
                return false;
            }

            if (state == LayoutConstants.IndexOccupied
                && entry.KeyHash == hash
                && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
            {
                Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
                RecordProbeLength(probes);
                return true;
            }
        }

        RecordProbeLength(probes);
        return false;
    }

    public bool TryRemoveSlot(int slotIndex, SlotLifecycleId lifecycleId)
    {
        var probes = 0;
        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            probes++;
            ref var entry = ref Entry(entryIndex);
            if (Volatile.Read(ref entry.State) == LayoutConstants.IndexOccupied
                && entry.SlotIndex == slotIndex
                && lifecycleId.Matches(entry.SlotGeneration, entry.SlotReuseEpoch))
            {
                Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
                RecordProbeLength(probes);
                return true;
            }
        }

        RecordProbeLength(probes);
        return false;
    }

    public int OccupiedCount()
    {
        var count = 0;
        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            if (Volatile.Read(ref Entry(entryIndex).State) == LayoutConstants.IndexOccupied)
            {
                count++;
            }
        }

        return count;
    }

    public IndexStateCounts CountStates()
    {
        var occupied = 0;
        var tombstone = 0;
        var empty = 0;

        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            switch (Volatile.Read(ref Entry(entryIndex).State))
            {
                case LayoutConstants.IndexOccupied:
                    occupied++;
                    break;
                case LayoutConstants.IndexTombstone:
                    tombstone++;
                    break;
                default:
                    empty++;
                    break;
            }
        }

        return new IndexStateCounts(
            _layout.IndexEntryCount,
            occupied,
            tombstone,
            empty,
            Volatile.Read(ref _lastObservedProbeLength),
            Volatile.Read(ref _maxObservedProbeLength));
    }

    public bool TryCompact()
    {
        var occupied = new List<IndexEntrySnapshot>(_layout.IndexEntryCount);
        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            ref var entry = ref Entry(entryIndex);
            if (Volatile.Read(ref entry.State) != LayoutConstants.IndexOccupied)
            {
                continue;
            }

            var key = new byte[entry.KeyLength];
            new ReadOnlySpan<byte>(KeyPointer(entryIndex), entry.KeyLength).CopyTo(key);
            occupied.Add(new IndexEntrySnapshot(
                key,
                entry.KeyHash,
                entry.SlotIndex,
                SlotLifecycleId.FromIndex(entry)));
        }

        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            ref var entry = ref Entry(entryIndex);
            Volatile.Write(ref entry.State, LayoutConstants.IndexEmpty);
            entry.KeyLength = 0;
            entry.KeyHash = 0;
            entry.SlotIndex = -1;
            entry.SlotGeneration = 0;
            entry.SlotReuseEpoch = 0;
            new Span<byte>(KeyPointer(entryIndex), _layout.MaxKeyBytes).Clear();
        }

        foreach (var snapshot in occupied)
        {
            if (!TryInsertWithoutProbeMetrics(snapshot.Key, snapshot.Hash, snapshot.SlotIndex, snapshot.LifecycleId))
            {
                return false;
            }
        }

        return true;
    }

    private void WriteEntry(int entryIndex, ReadOnlySpan<byte> key, ulong hash, int slotIndex, SlotLifecycleId lifecycleId)
    {
        ref var entry = ref Entry(entryIndex);
        Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
        entry.KeyHash = hash;
        entry.KeyLength = key.Length;
        entry.SlotIndex = slotIndex;
        entry.SlotGeneration = lifecycleId.Generation;
        entry.SlotReuseEpoch = lifecycleId.ReuseEpoch;
        var destination = new Span<byte>(KeyPointer(entryIndex), _layout.MaxKeyBytes);
        destination.Clear();
        key.CopyTo(destination);
        Volatile.Write(ref entry.State, LayoutConstants.IndexOccupied);
    }

    private bool TryInsertWithoutProbeMetrics(ReadOnlySpan<byte> key, ulong hash, int slotIndex, SlotLifecycleId lifecycleId)
    {
        var start = ProbeStart(hash);
        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            if (Volatile.Read(ref entry.State) == LayoutConstants.IndexEmpty)
            {
                WriteEntry(entryIndex, key, hash, slotIndex, lifecycleId);
                return true;
            }
        }

        return false;
    }

    private void RecordProbeLength(int probeLength)
    {
        Volatile.Write(ref _lastObservedProbeLength, probeLength);
        var current = Volatile.Read(ref _maxObservedProbeLength);
        while (probeLength > current)
        {
            var previous = Interlocked.CompareExchange(ref _maxObservedProbeLength, probeLength, current);
            if (previous == current)
            {
                break;
            }

            current = previous;
        }
    }

    private int ProbeStart(ulong hash)
    {
        return (int)(hash & (ulong)(_layout.IndexEntryCount - 1));
    }

    private ref SharedIndexEntryHeader Entry(int entryIndex)
    {
        return ref *(SharedIndexEntryHeader*)(_region.Pointer + _layout.IndexOffset + ((long)entryIndex * _layout.IndexEntrySize));
    }

    private byte* KeyPointer(int entryIndex)
    {
        return _region.Pointer + _layout.IndexOffset + ((long)entryIndex * _layout.IndexEntrySize) + _entryHeaderSize;
    }

    private readonly record struct IndexEntrySnapshot(byte[] Key, ulong Hash, int SlotIndex, SlotLifecycleId LifecycleId);
}

internal readonly record struct IndexStateCounts(
    int EntryCount,
    int OccupiedCount,
    int TombstoneCount,
    int EmptyCount,
    int LastObservedProbeLength,
    int MaxObservedProbeLength)
{
    public int UsableCapacity => EmptyCount + TombstoneCount;

    public double TombstonePressureRatio => EntryCount == 0 ? 0 : (double)TombstoneCount / EntryCount;
}
