namespace SharedMemoryStore.LockFree;

internal readonly struct IndexBinding
{
    private const ulong IndexMask = 0x7fff_ffffUL;
    private const ulong GenerationMask = 0x1_ffff_ffffUL;

    private IndexBinding(ulong value, int slotIndex, long generation)
    {
        Value = value;
        SlotIndex = slotIndex;
        Generation = generation;
    }

    public ulong Value { get; }
    public int SlotIndex { get; }
    public long Generation { get; }

    public static ulong Encode(int slotIndex, long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotIndex, int.MaxValue - 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, checked((long)GenerationMask));
        return ((ulong)generation << 31) | checked((uint)(slotIndex + 1));
    }

    public static IndexBinding Decode(ulong raw)
    {
        var indexPlusOne = raw & IndexMask;
        var generation = raw >> 31;
        if (indexPlusOne == 0 || generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        return new IndexBinding(raw, checked((int)indexPlusOne - 1), checked((long)generation));
    }
}
