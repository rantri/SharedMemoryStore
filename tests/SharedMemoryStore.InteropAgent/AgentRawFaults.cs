using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.InteropAgent;

internal readonly record struct AgentRawFaultResult(
    string Target,
    int ParticipantIndex,
    int OriginalProcessId,
    int ReplacementProcessId,
    ulong OriginalPidNamespaceId,
    ulong ReplacementPidNamespaceId,
    long OriginalRaw,
    long ReplacementRaw);

/// <summary>Test-only raw SMS2 mutations used to prove conservative failure paths.</summary>
internal static unsafe class AgentRawFaults
{
    internal static AgentRawFaultResult InjectDirectoryMutation(
        SharedMemoryStoreOptions options)
    {
        using MemoryMappedStoreRegion region = OpenRaw(options);
        StoreLayoutV2 layout = Validate(region, options);
        ref long mutation = ref *(long*)(region.Pointer + layout.PrimaryDirectoryOffset + sizeof(long));
        long original = AtomicControlWord.LoadAcquire(ref mutation);
        long malformed = unchecked((long)IndexBinding.Encode(layout.SlotCount, generation: 1));
        AtomicControlWord.StoreRelease(ref mutation, malformed);
        return new AgentRawFaultResult(
            "directoryMutation",
            -1,
            0,
            0,
            0,
            0,
            original,
            malformed);
    }

    internal static AgentRawFaultResult ReplaceParticipantProcessId(
        SharedMemoryStoreOptions options,
        int targetProcessId,
        int replacementProcessId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetProcessId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(replacementProcessId);
        using MemoryMappedStoreRegion region = OpenRaw(options);
        StoreLayoutV2 layout = Validate(region, options);
        ParticipantMatch match = FindParticipant(region, layout, targetProcessId);
        ref ParticipantRecordV2 participant = ref Participant(region, layout, match.Index);
        long original = match.Control;
        ulong raw = unchecked((ulong)original);
        int state = (int)(raw & 0x7UL);
        int generation = checked((int)((raw >> 3) & 0x0fff_ffffUL));
        long replacement = unchecked((long)AtomicControlWord.EncodeParticipant(
            state,
            generation,
            replacementProcessId));
        AtomicControlWord.StoreRelease(ref participant.Control, replacement);
        return new AgentRawFaultResult(
            "participantProcessId",
            match.Index,
            targetProcessId,
            replacementProcessId,
            participant.PidNamespaceId,
            participant.PidNamespaceId,
            original,
            replacement);
    }

    internal static AgentRawFaultResult ReplaceParticipantNamespace(
        SharedMemoryStoreOptions options,
        int targetProcessId,
        ulong replacementPidNamespaceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetProcessId);
        using MemoryMappedStoreRegion region = OpenRaw(options);
        StoreLayoutV2 layout = Validate(region, options);
        ParticipantMatch match = FindParticipant(region, layout, targetProcessId);
        ref ParticipantRecordV2 participant = ref Participant(region, layout, match.Index);
        long original = match.Control;
        ulong originalNamespace = participant.PidNamespaceId;
        participant.PidNamespaceId = replacementPidNamespaceId;
        return new AgentRawFaultResult(
            "participantNamespace",
            match.Index,
            targetProcessId,
            targetProcessId,
            originalNamespace,
            replacementPidNamespaceId,
            original,
            original);
    }

    internal static AgentRawFaultResult ReplaceHeaderNamespace(
        SharedMemoryStoreOptions options,
        ulong replacementPidNamespaceId)
    {
        using MemoryMappedStoreRegion region = OpenRaw(options);
        _ = Validate(region, options);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        ulong original = header.PidNamespaceId;
        header.PidNamespaceId = replacementPidNamespaceId;
        return new AgentRawFaultResult(
            "headerNamespace",
            -1,
            0,
            0,
            original,
            replacementPidNamespaceId,
            0,
            0);
    }

    internal static AgentRawFaultResult ReplaceLayoutMajorVersion(
        SharedMemoryStoreOptions options,
        ushort replacementLayoutMajorVersion)
    {
        using MemoryMappedStoreRegion region = OpenRaw(options);
        _ = Validate(region, options);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        ushort original = header.LayoutMajorVersion;
        header.LayoutMajorVersion = replacementLayoutMajorVersion;
        return new AgentRawFaultResult(
            "layoutMajorVersion",
            -1,
            0,
            0,
            0,
            0,
            original,
            replacementLayoutMajorVersion);
    }

    internal static AgentRawFaultResult ReplaceRequiredFeatures(
        SharedMemoryStoreOptions options,
        ulong replacementRequiredFeatures)
    {
        using MemoryMappedStoreRegion region = OpenRaw(options);
        _ = Validate(region, options);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        ulong original = header.RequiredFeatures;
        header.RequiredFeatures = replacementRequiredFeatures;
        return new AgentRawFaultResult(
            "requiredFeatures",
            -1,
            0,
            0,
            0,
            0,
            unchecked((long)original),
            unchecked((long)replacementRequiredFeatures));
    }

    private static MemoryMappedStoreRegion OpenRaw(SharedMemoryStoreOptions options)
    {
        var rawOptions = new SharedMemoryStoreOptions
        {
            Name = options.Name,
            OpenMode = OpenMode.OpenExisting,
            TotalBytes = options.TotalBytes,
            SlotCount = options.SlotCount,
            MaxValueBytes = options.MaxValueBytes,
            MaxDescriptorBytes = options.MaxDescriptorBytes,
            MaxKeyBytes = options.MaxKeyBytes,
            LeaseRecordCount = options.LeaseRecordCount,
            ParticipantRecordCount = options.ParticipantRecordCount,
            EnableLeaseRecovery = options.EnableLeaseRecovery
        };
        StoreOpenStatus open = MemoryMappedStoreRegion.TryOpen(rawOptions, out MemoryMappedStoreRegion? region);
        return open == StoreOpenStatus.Success && region is not null
            ? region
            : throw new InvalidOperationException($"Raw mapping open failed: {open}.");
    }

    private static StoreLayoutV2 Validate(
        MemoryMappedStoreRegion region,
        SharedMemoryStoreOptions options)
    {
        StoreLayoutV2 layout = StoreLayoutV2.FromOptions(options);
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        if (!layout.MatchesHeader(header))
        {
            throw new InvalidOperationException("Raw mapping does not match the requested SMS2 layout.");
        }

        return layout;
    }

    private static ParticipantMatch FindParticipant(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        int processId)
    {
        for (var index = 0; index < layout.ParticipantRecordCount; index++)
        {
            ref ParticipantRecordV2 participant = ref Participant(region, layout, index);
            long control = AtomicControlWord.LoadAcquire(ref participant.Control);
            ulong raw = unchecked((ulong)control);
            int state = (int)(raw & 0x7UL);
            int observedProcessId = checked((int)(raw >> 31));
            if (observedProcessId == processId
                && state is LayoutV2Constants.ParticipantRegistering
                    or LayoutV2Constants.ParticipantActive
                    or LayoutV2Constants.ParticipantClosing)
            {
                return new ParticipantMatch(index, control);
            }
        }

        throw new InvalidOperationException(
            $"No live participant record owned by PID {processId} was found.");
    }

    private static ref ParticipantRecordV2 Participant(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        int index) =>
        ref *(ParticipantRecordV2*)(
            region.Pointer + layout.ParticipantOffset + ((long)index * layout.ParticipantStride));

    private readonly record struct ParticipantMatch(int Index, long Control);
}
