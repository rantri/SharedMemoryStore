namespace SharedMemoryStore.Layout;

internal readonly record struct SlotLifecycleId(int Generation, long ReuseEpoch)
{
    public static SlotLifecycleId Initial => new(1, 0);

    public bool IsValid => Generation > 0 && ReuseEpoch >= 0;

    public bool Matches(int generation, long reuseEpoch)
    {
        return Generation == generation && ReuseEpoch == reuseEpoch;
    }

    public SlotLifecycleId Advance()
    {
        return TryAdvance(out var next)
            ? next
            : throw new InvalidOperationException("Slot lifecycle identity cannot advance.");
    }

    public bool TryAdvance(out SlotLifecycleId next)
    {
        if (Generation == int.MaxValue)
        {
            if (ReuseEpoch == long.MaxValue)
            {
                next = default;
                return false;
            }

            next = new SlotLifecycleId(1, ReuseEpoch + 1);
            return true;
        }

        next = new SlotLifecycleId(Generation + 1, ReuseEpoch);
        return true;
    }

    public static SlotLifecycleId FromSlot(in SharedSlotMetadata slot)
    {
        return new SlotLifecycleId(slot.Generation, slot.ReuseEpoch);
    }

    public static SlotLifecycleId FromLease(in SharedLeaseRecord record)
    {
        return new SlotLifecycleId(record.SlotGeneration, record.SlotReuseEpoch);
    }

    public static SlotLifecycleId FromIndex(in SharedIndexEntryHeader entry)
    {
        return new SlotLifecycleId(entry.SlotGeneration, entry.SlotReuseEpoch);
    }
}
