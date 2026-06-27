using System.Runtime.InteropServices;
using System.Threading;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.Layout;

internal sealed unsafe class SharedKeyIndex
{
    private readonly MemoryMappedStoreRegion _region;
    private readonly StoreLayout _layout;
    private readonly int _entryHeaderSize = Marshal.SizeOf<SharedIndexEntryHeader>();

    public SharedKeyIndex(MemoryMappedStoreRegion region, StoreLayout layout)
    {
        _region = region;
        _layout = layout;
    }

    public bool TryFind(ReadOnlySpan<byte> key, ulong hash, out int slotIndex, out int generation)
    {
        slotIndex = -1;
        generation = 0;
        var start = ProbeStart(hash);

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexEmpty)
            {
                return false;
            }

            if (state == LayoutConstants.IndexOccupied
                && entry.KeyHash == hash
                && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
            {
                slotIndex = entry.SlotIndex;
                generation = entry.SlotGeneration;
                return true;
            }
        }

        return false;
    }

    public bool TryInsert(ReadOnlySpan<byte> key, ulong hash, int slotIndex, int generation)
    {
        var start = ProbeStart(hash);
        var firstTombstone = -1;

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexOccupied)
            {
                if (entry.KeyHash == hash
                    && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
                {
                    return false;
                }

                continue;
            }

            if (state == LayoutConstants.IndexTombstone)
            {
                firstTombstone = firstTombstone < 0 ? entryIndex : firstTombstone;
                continue;
            }

            WriteEntry(firstTombstone >= 0 ? firstTombstone : entryIndex, key, hash, slotIndex, generation);
            return true;
        }

        if (firstTombstone >= 0)
        {
            WriteEntry(firstTombstone, key, hash, slotIndex, generation);
            return true;
        }

        return false;
    }

    public bool TryRemove(ReadOnlySpan<byte> key, ulong hash)
    {
        var start = ProbeStart(hash);

        for (var step = 0; step < _layout.IndexEntryCount; step++)
        {
            var entryIndex = (start + step) & (_layout.IndexEntryCount - 1);
            ref var entry = ref Entry(entryIndex);
            var state = Volatile.Read(ref entry.State);

            if (state == LayoutConstants.IndexEmpty)
            {
                return false;
            }

            if (state == LayoutConstants.IndexOccupied
                && entry.KeyHash == hash
                && StoreKey.Equals(KeyPointer(entryIndex), entry.KeyLength, key))
            {
                Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
                return true;
            }
        }

        return false;
    }

    public bool TryRemoveSlot(int slotIndex, int generation)
    {
        for (var entryIndex = 0; entryIndex < _layout.IndexEntryCount; entryIndex++)
        {
            ref var entry = ref Entry(entryIndex);
            if (Volatile.Read(ref entry.State) == LayoutConstants.IndexOccupied
                && entry.SlotIndex == slotIndex
                && entry.SlotGeneration == generation)
            {
                Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
                return true;
            }
        }

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

    private void WriteEntry(int entryIndex, ReadOnlySpan<byte> key, ulong hash, int slotIndex, int generation)
    {
        ref var entry = ref Entry(entryIndex);
        Volatile.Write(ref entry.State, LayoutConstants.IndexTombstone);
        entry.KeyHash = hash;
        entry.KeyLength = key.Length;
        entry.SlotIndex = slotIndex;
        entry.SlotGeneration = generation;
        var destination = new Span<byte>(KeyPointer(entryIndex), _layout.MaxKeyBytes);
        destination.Clear();
        key.CopyTo(destination);
        Volatile.Write(ref entry.State, LayoutConstants.IndexOccupied);
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
}
