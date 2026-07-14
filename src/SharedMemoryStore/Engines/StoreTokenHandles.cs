namespace SharedMemoryStore.Engines;

/// <summary>
/// Engine-neutral identity for one pending reservation lifecycle.
/// </summary>
/// <remarks>
/// The three unsigned words deliberately remain opaque to the public token.
/// Together they fence the mapping incarnation, participant incarnation, and
/// value-slot incarnation without exposing a layout-specific record type.
/// </remarks>
internal readonly record struct ReservationHandle
{
    internal ReservationHandle(
        ulong storeId,
        ulong participantToken,
        ulong slotBinding,
        int payloadLength)
    {
        StoreId = storeId;
        ParticipantToken = participantToken;
        SlotBinding = slotBinding;
        PayloadLength = payloadLength;
    }

    internal ulong StoreId { get; }

    internal ulong ParticipantToken { get; }

    internal ulong SlotBinding { get; }

    internal int PayloadLength { get; }

    internal bool IsDefault => StoreId == 0
        && ParticipantToken == 0
        && SlotBinding == 0
        && PayloadLength == 0;
}

/// <summary>
/// Engine-neutral identity for one active read-lease lifecycle.
/// </summary>
/// <remarks>
/// The handle fences the mapping, participant, value slot, and lease record.
/// Layout-specific index/generation splits are interpreted only by the engine
/// that created it.
/// </remarks>
internal readonly record struct LeaseHandle
{
    internal LeaseHandle(
        ulong storeId,
        ulong participantToken,
        ulong slotBinding,
        ulong leaseToken)
    {
        StoreId = storeId;
        ParticipantToken = participantToken;
        SlotBinding = slotBinding;
        LeaseToken = leaseToken;
    }

    internal ulong StoreId { get; }

    internal ulong ParticipantToken { get; }

    internal ulong SlotBinding { get; }

    internal ulong LeaseToken { get; }

    internal bool IsDefault => StoreId == 0
        && ParticipantToken == 0
        && SlotBinding == 0
        && LeaseToken == 0;
}
