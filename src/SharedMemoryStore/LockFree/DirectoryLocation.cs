namespace SharedMemoryStore.LockFree;

internal readonly struct DirectoryLocation
{
    private const ulong IndexMask = (1UL << 22) - 1;
    private const ulong GenerationMask = (1UL << 33) - 1;
    private const ulong UsedBitsMask = (1UL << 57) - 1;

    private DirectoryLocation(ulong value, int kind, long index, long generation)
    {
        Value = value;
        Kind = kind;
        Index = index;
        Generation = generation;
    }

    public ulong Value { get; }
    public int Kind { get; }
    public long Index { get; }
    public long Generation { get; }

    public static ulong Encode(int kind, long index, long generation)
    {
        if (kind is < 1 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        if ((ulong)index > IndexMask)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        if ((ulong)generation > GenerationMask)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return (uint)kind | ((ulong)index << 2) | ((ulong)generation << 24);
    }

    public static DirectoryLocation Decode(ulong raw)
    {
        if (raw == 0)
        {
            return default;
        }

        var kind = (int)(raw & 0x3);
        var generation = (raw >> 24) & GenerationMask;
        if (kind is < 1 or > 2 || generation == 0 || (raw & ~UsedBitsMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        return new DirectoryLocation(
            raw,
            kind,
            checked((long)((raw >> 2) & IndexMask)),
            checked((long)generation));
    }
}
