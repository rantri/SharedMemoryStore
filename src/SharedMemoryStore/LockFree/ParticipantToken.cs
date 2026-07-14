namespace SharedMemoryStore.LockFree;

internal readonly struct ParticipantToken
{
    private ParticipantToken(ulong value, int recordIndex, int generation)
    {
        Value = value;
        RecordIndex = recordIndex;
        Generation = generation;
    }

    public ulong Value { get; }
    public int RecordIndex { get; }
    public int Generation { get; }

    public static ulong Encode(int recordIndex, int generation, int participantCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(participantCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(participantCount, 1_048_575);
        ArgumentOutOfRangeException.ThrowIfNegative(recordIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(recordIndex, participantCount);

        var indexBits = RequiredBits(participantCount + 1);
        var generationBits = 28 - indexBits;
        var maximumGeneration = (1 << generationBits) - 1;
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, maximumGeneration);
        return ((ulong)(uint)generation << indexBits) | (uint)(recordIndex + 1);
    }

    public static ParticipantToken Decode(ulong raw, int participantCount)
    {
        var indexBits = RequiredBits(checked(participantCount + 1));
        var indexMask = (1UL << indexBits) - 1;
        var indexPlusOne = raw & indexMask;
        var generation = raw >> indexBits;
        if (indexPlusOne == 0 || indexPlusOne > (ulong)participantCount || generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        return new ParticipantToken(raw, checked((int)indexPlusOne - 1), checked((int)generation));
    }

    /// <summary>
    /// Validates the complete 28-bit wire token without throwing. Capacity
    /// classifiers use this on shared owner fields so an out-of-range encoded
    /// record index cannot be mistaken for ordinary occupancy.
    /// </summary>
    public static bool IsStructurallyValid(ulong raw, int participantCount)
    {
        if (participantCount is < 1 or > 1_048_575
            || raw is 0 or > 0x0fff_ffffUL)
        {
            return false;
        }

        int indexBits = RequiredBits(participantCount + 1);
        ulong indexMask = (1UL << indexBits) - 1;
        ulong indexPlusOne = raw & indexMask;
        ulong generation = raw >> indexBits;
        ulong maximumGeneration = (1UL << (28 - indexBits)) - 1;
        return indexPlusOne is >= 1
            && indexPlusOne <= (ulong)participantCount
            && generation is >= 1
            && generation <= maximumGeneration;
    }

    private static int RequiredBits(int distinctValues)
    {
        var bits = 0;
        var value = distinctValues - 1;
        do
        {
            bits++;
            value >>= 1;
        }
        while (value != 0);

        return bits;
    }
}
