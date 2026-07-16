using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Versioned negative-cache token for one canonical primary bucket. A present
/// token requires overflow lookup; an empty token preserves the identity of the
/// insertion generation that was proved absent so delayed setters cannot ABA
/// through the initial zero value.
/// </summary>
internal readonly struct SpillSummary
{
    internal const int SlotIndexBits = 20;
    internal const int SlotGenerationBits = 33;
    internal const int PresentBitIndex = SlotIndexBits + SlotGenerationBits;

    private const ulong SlotIndexMask = (1UL << SlotIndexBits) - 1;
    private const ulong SlotGenerationMask = (1UL << SlotGenerationBits) - 1;
    private const ulong IdentityMask = (1UL << PresentBitIndex) - 1;
    private const ulong PresentMask = 1UL << PresentBitIndex;
    private const ulong EncodedMask = (1UL << (PresentBitIndex + 1)) - 1;

    private SpillSummary(ulong value, bool isPresent, int slotIndex, long generation)
    {
        Value = value;
        IsPresent = isPresent;
        SlotIndex = slotIndex;
        Generation = generation;
    }

    internal ulong Value { get; }

    internal bool IsPresent { get; }

    internal bool IsInitial => Value == 0;

    internal int SlotIndex { get; }

    internal long Generation { get; }

    internal ulong Binding => IsInitial ? 0 : IndexBinding.Encode(SlotIndex, Generation);

    internal ulong EmptyValue => Value & IdentityMask;

    internal static ulong EncodePresent(ulong binding) => Encode(binding) | PresentMask;

    internal static ulong EncodeEmpty(ulong binding) => Encode(binding);

    internal static SpillSummary Decode(ulong raw)
    {
        if (raw == 0)
        {
            return default;
        }

        if ((raw & ~EncodedMask) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        ulong indexPlusOne = raw & SlotIndexMask;
        ulong generation = (raw >> SlotIndexBits) & SlotGenerationMask;
        if (indexPlusOne == 0
            || indexPlusOne > LayoutV2Constants.MaximumSlotCount
            || generation == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(raw));
        }

        return new SpillSummary(
            raw,
            (raw & PresentMask) != 0,
            checked((int)indexPlusOne - 1),
            checked((long)generation));
    }

    private static ulong Encode(ulong binding)
    {
        IndexBinding decoded = IndexBinding.Decode(binding);
        if ((uint)decoded.SlotIndex >= LayoutV2Constants.MaximumSlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(binding));
        }

        return ((ulong)decoded.Generation << SlotIndexBits)
            | checked((uint)(decoded.SlotIndex + 1));
    }
}
