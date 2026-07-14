using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeConflictCleanupAndAtomicAbortTests
{
    [Fact]
    public void NoWaitStopsBeforeFreshLookupAfterSuccessfullyReclaimingRemovedGeneration()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore("fresh-lookup-budget");
        byte[] key = [0x41];
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation publisher));

        ReservationHandle removedHandle = publisher.HandleForEngine;
        IndexBinding removedBinding = IndexBinding.Decode(removedHandle.SlotBinding);
        publisher.GetSpan(1)[0] = 0x91;
        Assert.Equal(StoreStatus.Success, publisher.Advance(1, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, publisher.Commit(StoreWaitOptions.Infinite));

        // NoWait orders logical removal but intentionally leaves its physical
        // generation for a later helper.
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key, StoreWaitOptions.NoWait));

        StoreStatus noWait = store.TryReserve(
            key,
            payloadLength: 1,
            descriptor: default,
            StoreWaitOptions.NoWait,
            out ValueReservation rejected);

        Assert.Equal(StoreStatus.StoreBusy, noWait);
        Assert.False(rejected.IsValid);

        // The create call did successfully reclaim the old generation. Its
        // StoreBusy result is specifically the operation-wide retry gate before
        // a fresh structural lookup, not an incomplete cleanup result.
        LockFreeSlotTable slots = ReadSlots(store);
        long reclaimed = AtomicControlWord.LoadAcquire(
            ref slots.Slot(removedBinding.SlotIndex).Control);
        Assert.Equal(LockFreeSlotTable.FreeState, SlotState(reclaimed));
        Assert.Equal(removedBinding.Generation + 1, SlotGeneration(reclaimed));

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation replacement));
        Assert.True(replacement.IsValid);
        Assert.Equal(StoreStatus.Success, replacement.Abort(StoreWaitOptions.Infinite));
    }

    [Theory]
    [InlineData(LockFreeSlotTable.AbortingState)]
    [InlineData(LockFreeSlotTable.ReclaimingState)]
    public void NoWaitStopsBeforeFreshLookupAfterCleaningHelpableGeneration(
        int helpableState)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"helpable-{helpableState}");
        byte[] key = [0x43];
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation owner));

        ReservationHandle oldHandle = owner.HandleForEngine;
        IndexBinding oldBinding = IndexBinding.Decode(oldHandle.SlotBinding);
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(oldBinding.SlotIndex);

        if (helpableState == LockFreeSlotTable.AbortingState)
        {
            long reserved = SlotControl(
                LockFreeSlotTable.ReservedState,
                oldBinding.Generation,
                checked((int)oldHandle.ParticipantToken));
            long aborting = SlotControl(
                helpableState,
                oldBinding.Generation,
                participantToken: 0);
            Assert.Equal(
                reserved,
                AtomicControlWord.CompareExchange(ref slot.Control, aborting, reserved));
        }
        else
        {
            owner.GetSpan(1)[0] = 0x93;
            Assert.Equal(StoreStatus.Success, owner.Advance(1, StoreWaitOptions.Infinite));
            Assert.Equal(StoreStatus.Success, owner.Commit(StoreWaitOptions.Infinite));
            Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key, StoreWaitOptions.NoWait));

            long removeRequested = SlotControl(
                LockFreeSlotTable.RemoveRequestedState,
                oldBinding.Generation,
                participantToken: 0);
            long reclaiming = SlotControl(
                LockFreeSlotTable.ReclaimingState,
                oldBinding.Generation,
                participantToken: 0);
            Assert.Equal(
                removeRequested,
                AtomicControlWord.CompareExchange(
                    ref slot.Control,
                    reclaiming,
                    removeRequested));
        }

        Assert.Equal(
            StoreStatus.StoreBusy,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.NoWait,
                out ValueReservation rejected));
        Assert.False(rejected.IsValid);

        long reclaimed = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(LockFreeSlotTable.FreeState, SlotState(reclaimed));
        Assert.Equal(oldBinding.Generation + 1, SlotGeneration(reclaimed));

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort(StoreWaitOptions.Infinite));
    }

    [Theory]
    [InlineData(AtomicAbortScenario.OwnInitializing)]
    [InlineData(AtomicAbortScenario.OwnReserved)]
    [InlineData(AtomicAbortScenario.Aborting)]
    [InlineData(AtomicAbortScenario.LaterGeneration)]
    [InlineData(AtomicAbortScenario.Reclaiming)]
    [InlineData(AtomicAbortScenario.TerminalRetired)]
    [InlineData(AtomicAbortScenario.LowerGeneration)]
    [InlineData(AtomicAbortScenario.Published)]
    [InlineData(AtomicAbortScenario.RemoveRequested)]
    [InlineData(AtomicAbortScenario.Free)]
    [InlineData(AtomicAbortScenario.NonterminalRetired)]
    [InlineData(AtomicAbortScenario.WrongInitializingOwner)]
    [InlineData(AtomicAbortScenario.WrongReservedOwner)]
    [InlineData(AtomicAbortScenario.OwnedAborting)]
    [InlineData(AtomicAbortScenario.WrongDirectoryBinding)]
    [InlineData(AtomicAbortScenario.ExplicitPublicationIntent)]
    [InlineData(AtomicAbortScenario.UnknownPublicationIntent)]
    public void AtomicCandidateAbortStrictlyClassifiesStableSlotState(
        AtomicAbortScenario scenario)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"atomic-abort-{scenario}");
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                [0x42],
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));

        ReservationHandle originalHandle = reservation.HandleForEngine;
        IndexBinding originalBinding = IndexBinding.Decode(originalHandle.SlotBinding);
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(originalBinding.SlotIndex);
        long originalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(LockFreeSlotTable.ReservedState, SlotState(originalControl));

        ReservationHandle targetHandle = originalHandle;
        ulong targetBinding = originalHandle.SlotBinding;
        long targetGeneration = originalBinding.Generation;
        int targetIntent = (int)SlotPublicationIntent.AtomicPublication;
        int participant = checked((int)originalHandle.ParticipantToken);
        int wrongParticipant = participant == 1 ? 2 : 1;
        TentativeReservationAbortResult expected = scenario switch
        {
            AtomicAbortScenario.OwnInitializing
                or AtomicAbortScenario.OwnReserved
                or AtomicAbortScenario.Aborting => TentativeReservationAbortResult.Aborted,
            AtomicAbortScenario.LaterGeneration
                or AtomicAbortScenario.Reclaiming
                or AtomicAbortScenario.TerminalRetired => TentativeReservationAbortResult.Invalid,
            _ => TentativeReservationAbortResult.Corrupt,
        };
        long targetControl = scenario switch
        {
            AtomicAbortScenario.OwnInitializing =>
                SlotControl(LockFreeSlotTable.InitializingState, targetGeneration, participant),
            AtomicAbortScenario.OwnReserved =>
                SlotControl(LockFreeSlotTable.ReservedState, targetGeneration, participant),
            AtomicAbortScenario.Aborting =>
                SlotControl(LockFreeSlotTable.AbortingState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.LaterGeneration =>
                SlotControl(LockFreeSlotTable.FreeState, targetGeneration + 1, participantToken: 0),
            AtomicAbortScenario.Reclaiming =>
                SlotControl(LockFreeSlotTable.ReclaimingState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.TerminalRetired =>
                Retarget(
                    originalHandle,
                    originalBinding.SlotIndex,
                    LockFreeSlotTable.TerminalGeneration,
                    LockFreeSlotTable.TerminalGeneration,
                    ref targetHandle,
                    ref targetBinding,
                    ref targetGeneration,
                    LockFreeSlotTable.RetiredState,
                    participantToken: 0),
            AtomicAbortScenario.LowerGeneration =>
                Retarget(
                    originalHandle,
                    originalBinding.SlotIndex,
                    originalBinding.Generation + 1,
                    originalBinding.Generation,
                    ref targetHandle,
                    ref targetBinding,
                    ref targetGeneration,
                    LockFreeSlotTable.ReservedState,
                    participant),
            AtomicAbortScenario.Published =>
                SlotControl(LockFreeSlotTable.PublishedState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.RemoveRequested =>
                SlotControl(LockFreeSlotTable.RemoveRequestedState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.Free =>
                SlotControl(LockFreeSlotTable.FreeState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.NonterminalRetired =>
                SlotControl(LockFreeSlotTable.RetiredState, targetGeneration, participantToken: 0),
            AtomicAbortScenario.WrongInitializingOwner =>
                SlotControl(LockFreeSlotTable.InitializingState, targetGeneration, wrongParticipant),
            AtomicAbortScenario.WrongReservedOwner =>
                SlotControl(LockFreeSlotTable.ReservedState, targetGeneration, wrongParticipant),
            AtomicAbortScenario.OwnedAborting =>
                SlotControl(LockFreeSlotTable.AbortingState, targetGeneration, participant),
            AtomicAbortScenario.WrongDirectoryBinding =>
                SlotControl(LockFreeSlotTable.ReservedState, targetGeneration, participant),
            AtomicAbortScenario.ExplicitPublicationIntent =>
                SlotControl(LockFreeSlotTable.ReservedState, targetGeneration, participant),
            AtomicAbortScenario.UnknownPublicationIntent =>
                SlotControl(LockFreeSlotTable.ReservedState, targetGeneration, participant),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };

        if (scenario == AtomicAbortScenario.WrongDirectoryBinding)
        {
            targetBinding = IndexBinding.Encode(
                originalBinding.SlotIndex,
                originalBinding.Generation + 1);
        }

        if (scenario == AtomicAbortScenario.ExplicitPublicationIntent)
        {
            targetIntent = (int)SlotPublicationIntent.ExplicitReservation;
        }
        else if (scenario == AtomicAbortScenario.UnknownPublicationIntent)
        {
            targetIntent = 3;
        }

        Volatile.Write(ref slot.DirectoryBinding, targetBinding);
        Volatile.Write(ref slot.PublicationIntent, targetIntent);
        AtomicControlWord.StoreRelease(ref slot.Control, targetControl);
        try
        {
            Assert.Equal(expected, slots.TryBeginAtomicCandidateAbort(targetHandle));
            long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (expected == TentativeReservationAbortResult.Aborted)
            {
                Assert.Equal(LockFreeSlotTable.AbortingState, SlotState(observed));
                Assert.Equal(targetGeneration, SlotGeneration(observed));
                Assert.Equal(0UL, SlotParticipant(observed));
            }
            else
            {
                Assert.Equal(targetControl, observed);
            }
        }
        finally
        {
            Volatile.Write(ref slot.DirectoryBinding, originalHandle.SlotBinding);
            Volatile.Write(
                ref slot.PublicationIntent,
                (int)SlotPublicationIntent.ExplicitReservation);
            AtomicControlWord.StoreRelease(ref slot.Control, originalControl);
        }

        bool structurallyMalformed = scenario is
            AtomicAbortScenario.NonterminalRetired
            or AtomicAbortScenario.WrongInitializingOwner
            or AtomicAbortScenario.WrongReservedOwner
            or AtomicAbortScenario.OwnedAborting;
        Assert.Equal(
            structurallyMalformed ? StoreStatus.CorruptStore : StoreStatus.Success,
            reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Fact]
    public void ExactDirectoryBindingRejectsOwnedControlWithOutOfRangeParticipantIndex()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore("directory-participant-token");
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                [0x51],
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));

        ReservationHandle handle = reservation.HandleForEngine;
        IndexBinding binding = IndexBinding.Decode(handle.SlotBinding);
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long originalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
        const int malformedParticipant = 7; // generation 1, index-plus-one 3 for count 2
        Assert.False(ParticipantToken.IsStructurallyValid(malformedParticipant, 2));
        AtomicControlWord.StoreRelease(
            ref slot.Control,
            SlotControl(
                LockFreeSlotTable.ReservedState,
                binding.Generation,
                malformedParticipant));

        try
        {
            StoreStatus status = slots.ClassifyDirectoryBinding(
                handle.SlotBinding,
                out int state,
                out SlotPublicationIntent intent);

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.Equal(LockFreeSlotTable.FreeState, state);
            Assert.Equal(SlotPublicationIntent.None, intent);
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.Control, originalControl);
        }

        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
    }

    private static long Retarget(
        in ReservationHandle original,
        int slotIndex,
        long handleGeneration,
        long controlGeneration,
        ref ReservationHandle targetHandle,
        ref ulong targetBinding,
        ref long targetGeneration,
        int controlState,
        int participantToken)
    {
        targetBinding = IndexBinding.Encode(slotIndex, handleGeneration);
        targetGeneration = handleGeneration;
        targetHandle = new ReservationHandle(
            original.StoreId,
            original.ParticipantToken,
            targetBinding,
            original.PayloadLength);

        return SlotControl(controlState, controlGeneration, participantToken);
    }

    private static MemoryStore CreateStore(string suffix)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            SharedMemoryStoreOptions.CreateLockFree(
                $"sms-v2-conflict-abort-{suffix}-{Guid.NewGuid():N}",
                slotCount: 1,
                maxValueBytes: 1,
                maxDescriptorBytes: 0,
                maxKeyBytes: 8,
                leaseRecordCount: 2,
                participantRecordCount: 2,
                openMode: OpenMode.CreateNew,
                enableLeaseRecovery: true),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static LockFreeSlotTable ReadSlots(MemoryStore store)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        return ReadPrivate<LockFreeSlotTable>(engine, "_slots");
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Missing field {owner.GetType().FullName}.{fieldName}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(owner));
    }

    private static long SlotControl(int state, long generation, int participantToken) =>
        unchecked((long)AtomicControlWord.EncodeSlot(state, generation, participantToken));

    private static int SlotState(long control) =>
        (int)(unchecked((ulong)control) & 0x7UL);

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static ulong SlotParticipant(long control) =>
        (unchecked((ulong)control) >> 36) & 0x0fff_ffffUL;

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public enum AtomicAbortScenario
    {
        OwnInitializing,
        OwnReserved,
        Aborting,
        LaterGeneration,
        Reclaiming,
        TerminalRetired,
        LowerGeneration,
        Published,
        RemoveRequested,
        Free,
        NonterminalRetired,
        WrongInitializingOwner,
        WrongReservedOwner,
        OwnedAborting,
        WrongDirectoryBinding,
        ExplicitPublicationIntent,
        UnknownPublicationIntent,
    }
}
