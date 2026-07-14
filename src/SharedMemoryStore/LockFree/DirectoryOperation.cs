namespace SharedMemoryStore.LockFree;

internal readonly struct DirectoryOperation
{
    private const ulong IndexMask = (1UL << 22) - 1;
    private const ulong GenerationMask = (1UL << 33) - 1;
    private const ulong UsedBitsMask = (1UL << 62) - 1;

    private DirectoryOperation(ulong value, int intent, int phase, int kind, long index, long generation)
    {
        Value = value;
        Intent = intent;
        Phase = phase;
        Kind = kind;
        Index = index;
        Generation = generation;
    }

    public ulong Value { get; }
    public int Intent { get; }
    public int Phase { get; }
    public int Kind { get; }
    public long Index { get; }
    public long Generation { get; }

    public static ulong Encode(int intent, int phase, int targetKind, long targetIndex, long generation)
    {
        if (intent is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(intent));
        }

        if (phase is < 0 or > 5)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (targetKind is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(targetKind));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        if ((ulong)targetIndex > IndexMask)
        {
            throw new ArgumentOutOfRangeException(nameof(targetIndex));
        }

        if (intent == 0 && phase == 0 && targetKind == 0 && targetIndex == 0 && generation == 0)
        {
            return 0;
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        if ((ulong)generation > GenerationMask)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return (uint)intent
            | ((ulong)(uint)phase << 2)
            | ((ulong)(uint)targetKind << 5)
            | ((ulong)targetIndex << 7)
            | ((ulong)generation << 29);
    }

    public static DirectoryOperation Decode(ulong raw)
    {
        if (raw == 0)
        {
            return default;
        }

        var generation = (raw >> 29) & GenerationMask;
        if (generation == 0 || (raw & ~UsedBitsMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        return new DirectoryOperation(
            raw,
            (int)(raw & 0x3),
            (int)((raw >> 2) & 0x7),
            (int)((raw >> 5) & 0x3),
            checked((long)((raw >> 7) & IndexMask)),
            checked((long)generation));
    }
}
