using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeInsertCancellationRaceTests
{
    private const int CanonicalBucket = 0;
    private const int OverflowSlotCount = 17;
    private const int OverflowAnchorCount = 16;
    private const int RaceSlotCount = 4;
    // The finite budget is real wall-clock time. Leave enough pre-checkpoint
    // setup margin for parallel full-solution runs, then wait past that same
    // deadline so the controlled schedule still proves the expired-budget path.
    private static readonly StoreWaitOptions FiniteRaceWait =
        new(TimeSpan.FromSeconds(2));
    private static readonly TimeSpan ExpiredRaceDelay = TimeSpan.FromMilliseconds(2_250);

    [Fact]
    public Task PreparedInsertCanceledAfterCurrentOperationRevalidationDoesNotReportCorruption() =>
        RunSingleSlotCancellationRace(
            LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch,
            expectedPhase: 1,
            expectedSlotState: LockFreeSlotTable.InitializingState,
            expectedStatus: StoreStatus.InvalidReservation,
            key: [0x31]);

    [Fact]
    public Task BindingChangedInsertCanceledAfterStateValidationDoesNotPublishReservedOrReportCorruption() =>
        RunSingleSlotCancellationRace(
            LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication,
            expectedPhase: 3,
            expectedSlotState: LockFreeSlotTable.InitializingState,
            expectedStatus: StoreStatus.InvalidReservation,
            key: [0x32]);

    [Fact]
    public Task CompletedExplicitReserveCanceledBeforeLocationReadStillReturnsOrderedSuccess() =>
        RunSingleSlotCancellationRace(
            LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead,
            expectedPhase: 5,
            expectedSlotState: LockFreeSlotTable.ReservedState,
            expectedStatus: StoreStatus.Success,
            key: [0x33]);

    [Fact]
    public Task ExplicitReserveCanceledBeforePendingClassificationStillReturnsOrderedSuccess() =>
        RunSingleSlotCancellationRace(
            LockFreeCheckpointId.ReserveAfterDirectoryInsertBeforePendingClassification,
            expectedPhase: 5,
            expectedSlotState: LockFreeSlotTable.ReservedState,
            expectedStatus: StoreStatus.Success,
            key: [0x34]);

    [Theory]
    [InlineData(TargetAfterCancellation.Empty)]
    [InlineData(TargetAfterCancellation.ValidReplacement)]
    [InlineData(TargetAfterCancellation.Malformed)]
    [InlineData(TargetAfterCancellation.OutOfRange)]
    public async Task DelayedInsertCancellationTargetHandoffIsClassifiedExactly(
        TargetAfterCancellation targetAfterCancellation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        string name = $"sms-v2-insert-unlink-handoff-{Guid.NewGuid():N}";
        using var insertScheduler = new ControlledLockFreeScheduler();
        using var unlinkScheduler = new ControlledLockFreeScheduler();
        using MemoryStore owner = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            insertScheduler);
        using MemoryStore unlinker = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            unlinkScheduler);
        LockFreeKeyDirectory ownerDirectory = ReadDirectory(owner);
        LockFreeKeyDirectory unlinkDirectory = ReadDirectory(unlinker);
        LockFreeSlotTable slots = ReadSlots(owner);

        Assert.Equal(
            StoreStatus.Success,
            owner.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        ReservationHandle handle = reservation.HandleForEngine;
        Assert.Equal(
            StoreStatus.Success,
            ownerDirectory.TryLookup(
                key,
                StoreKey.Hash(key),
                LockFreeOperationBudget.UnboundedScan,
                out ulong binding,
                out DirectoryLocation location));
        Assert.Equal(handle.SlotBinding, binding);
        Assert.Equal(1, location.Kind);
        Assert.Equal(binding, ReadDirectoryCell(ownerDirectory, location));

        // Recreate the reachable window after an insert helper has claimed its
        // exact target cell but before it publishes DirectoryLocation.  The
        // successful reservation supplies fully initialized immutable slot
        // metadata; only the three atomic protocol words are rewound.
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(handle));
        IndexBinding decoded = IndexBinding.Decode(binding);
        int slotIndex = decoded.SlotIndex;
        ulong targetSelected = DirectoryOperation.Encode(
            intent: 1,
            phase: 2,
            location.Kind,
            location.Index,
            decoded.Generation);
        AtomicControlWord.StoreRelease(ref slots.Slot(slotIndex).DirectoryLocation, 0);
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryOperation,
            unchecked((long)targetSelected));
        WriteCanonicalMutation(ownerDirectory, CanonicalBucket, binding);

        insertScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch);
        Task<(StoreStatus Status, string? CorruptionOrigin)> delayedInsert = Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            InstrumentedLockFreeCheckpoint checkpoint =
                insertScheduler.CreateInstrumentedCheckpoint();
            StoreStatus status = ownerDirectory.HelpMutation(
                CanonicalBucket,
                LockFreeOperationBudget.UnboundedScan,
                ref checkpoint,
                maxSteps: 1);
            return (status, LockFreeCorruptionTrace.Consume());
        });

        var insertResumed = false;
        var unlinkResumed = false;
        Task<(StoreStatus Status, string? CorruptionOrigin)>? delayedUnlink = null;
        try
        {
            Assert.True(insertScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(targetSelected, ReadDirectoryOperation(slots, slotIndex));

            unlinkScheduler.PauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
            delayedUnlink = Task.Run(() =>
            {
                _ = LockFreeCorruptionTrace.Consume();
                InstrumentedLockFreeCheckpoint checkpoint =
                    unlinkScheduler.CreateInstrumentedCheckpoint();
                StoreStatus status = unlinkDirectory.TryUnlink(
                    binding,
                    LockFreeOperationBudget.UnboundedScan,
                    ref checkpoint);
                return (status, LockFreeCorruptionTrace.Consume());
            });
            Assert.True(unlinkScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

            DirectoryOperation preparedUnlink = DirectoryOperation.Decode(
                ReadDirectoryOperation(slots, slotIndex));
            Assert.Equal(2, preparedUnlink.Intent);
            Assert.Equal(1, preparedUnlink.Phase);
            Assert.Equal(
                0,
                AtomicControlWord.LoadAcquire(ref slots.Slot(slotIndex).DirectoryLocation));
            Assert.Equal(binding, ReadDirectoryCell(ownerDirectory, location));

            // The delayed helper still dispatches its validated Insert snapshot.
            // CancelInsert exact-clears the target, but cannot replace the newer
            // Unlink/Prepared descriptor that now owns the canonical mutation.
            insertScheduler.Continue();
            insertResumed = true;
            (StoreStatus insertStatus, string? insertCorruption) =
                await delayedInsert.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.StoreBusy, insertStatus);
            Assert.Null(insertCorruption);
            Assert.Equal(0UL, ReadDirectoryCell(ownerDirectory, location));
            Assert.Equal(preparedUnlink.Value, ReadDirectoryOperation(slots, slotIndex));
            Assert.Equal(binding, ownerDirectory.ReadCanonicalMutation(CanonicalBucket));

            ulong replacement = targetAfterCancellation switch
            {
                TargetAfterCancellation.Empty => 0,
                TargetAfterCancellation.ValidReplacement => IndexBinding.Encode(
                    (decoded.SlotIndex + 1) % RaceSlotCount,
                    decoded.Generation),
                TargetAfterCancellation.Malformed => ulong.MaxValue,
                TargetAfterCancellation.OutOfRange => IndexBinding.Encode(
                    RaceSlotCount,
                    decoded.Generation),
                _ => throw new ArgumentOutOfRangeException(nameof(targetAfterCancellation)),
            };
            if (replacement != 0)
            {
                WriteDirectoryCell(ownerDirectory, location, replacement);
            }

            unlinkScheduler.Continue();
            unlinkResumed = true;
            (StoreStatus unlinkStatus, string? unlinkCorruption) =
                await delayedUnlink.WaitAsync(TimeSpan.FromSeconds(5));

            if (targetAfterCancellation is
                TargetAfterCancellation.Malformed or TargetAfterCancellation.OutOfRange)
            {
                Assert.Equal(StoreStatus.CorruptStore, unlinkStatus);
                Assert.NotNull(unlinkCorruption);
                Assert.Equal(replacement, ReadDirectoryCell(ownerDirectory, location));
                return;
            }

            Assert.Equal(StoreStatus.Success, unlinkStatus);
            Assert.Null(unlinkCorruption);
            Assert.Equal(replacement, ReadDirectoryCell(ownerDirectory, location));
            if (replacement != 0)
            {
                // The delayed unlink must preserve a valid winner it does not
                // own.  The test supplied that stand-in binding directly, so
                // it also withdraws it before asserting old-slot reclamation.
                WriteDirectoryCell(ownerDirectory, location, binding: 0);
            }
        }
        finally
        {
            if (!insertResumed)
            {
                insertScheduler.Continue();
            }

            if (!unlinkResumed)
            {
                unlinkScheduler.Continue();
            }
        }

        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(key, out _));
        AssertDirectoryDrained(ownerDirectory);

        byte[] laterKey = keys[1];
        Assert.Equal(
            StoreStatus.Success,
            owner.TryPublish(laterKey, [0xA5], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, owner.TryAcquire(laterKey, out ValueLease laterLease));
        Assert.Equal(0xA5, laterLease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, laterLease.Release());
        Assert.Equal(StoreStatus.Success, owner.TryRemove(laterKey, StoreWaitOptions.Infinite));
        AssertDirectoryDrained(ownerDirectory);
        AssertAllSlotCapacityReusable(owner, keys, RaceSlotCount);
    }

    [Fact]
    public async Task PreparedUnlinkFirstLocationPublisherWinsAndWithdrawsCompetingBinding()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        string name = $"sms-v2-unlink-location-arbitration-{Guid.NewGuid():N}";
        using var firstScheduler = new ControlledLockFreeScheduler();
        using var competingScheduler = new ControlledLockFreeScheduler();
        using MemoryStore firstStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            firstScheduler);
        using MemoryStore competingStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            competingScheduler);
        LockFreeKeyDirectory firstDirectory = ReadDirectory(firstStore);
        LockFreeKeyDirectory competingDirectory = ReadDirectory(competingStore);
        LockFreeSlotTable slots = ReadSlots(firstStore);

        Assert.Equal(
            StoreStatus.Success,
            firstStore.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        ReservationHandle handle = reservation.HandleForEngine;
        Assert.Equal(
            StoreStatus.Success,
            firstDirectory.TryLookup(
                key,
                StoreKey.Hash(key),
                LockFreeOperationBudget.UnboundedScan,
                out ulong binding,
                out DirectoryLocation earlierLocation));
        Assert.Equal(handle.SlotBinding, binding);
        Assert.Equal(1, earlierLocation.Kind);
        Assert.Equal(0, earlierLocation.Index % LayoutV2Constants.PrimaryLanesPerBucket);

        IndexBinding decoded = IndexBinding.Decode(binding);
        int slotIndex = decoded.SlotIndex;
        DirectoryLocation laterLocation = DirectoryLocation.Decode(DirectoryLocation.Encode(
            earlierLocation.Kind,
            earlierLocation.Index + 1,
            decoded.Generation));
        Assert.Equal(
            earlierLocation.Index / LayoutV2Constants.PrimaryLanesPerBucket,
            laterLocation.Index / LayoutV2Constants.PrimaryLanesPerBucket);

        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(handle));
        WriteDirectoryCell(firstDirectory, earlierLocation, binding: 0);
        WriteDirectoryCell(firstDirectory, laterLocation, binding);
        ulong prepared = DirectoryOperation.Encode(
            intent: 2,
            phase: 1,
            targetKind: 0,
            targetIndex: 0,
            decoded.Generation);
        AtomicControlWord.StoreRelease(ref slots.Slot(slotIndex).DirectoryLocation, 0);
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryOperation,
            unchecked((long)prepared));
        WriteCanonicalMutation(firstDirectory, CanonicalBucket, binding);

        firstScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
        Task<(StoreStatus Status, string? CorruptionOrigin)> firstTask =
            UnlinkAsync(firstDirectory, binding, firstScheduler);
        Assert.True(firstScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(binding, ReadDirectoryCell(firstDirectory, laterLocation));

        // The second helper starts later but finds the earlier scan lane. Both
        // helpers have now recovered a different exact target while the shared
        // location remains empty.
        WriteDirectoryCell(firstDirectory, earlierLocation, binding);
        competingScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
        Task<(StoreStatus Status, string? CorruptionOrigin)> competingTask =
            UnlinkAsync(competingDirectory, binding, competingScheduler);
        Assert.True(competingScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));

        var firstResumed = false;
        var competingResumed = false;
        try
        {
            // Arm the descriptor-selection pause before releasing the first
            // publisher, leaving no scheduler gap after its Location CAS.
            firstScheduler.ContinueAndPauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationValidation);
            firstResumed = true;
            Assert.True(firstScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(laterLocation.Value, ReadDirectoryLocation(slots, slotIndex));
            Assert.Equal(prepared, ReadDirectoryOperation(slots, slotIndex));

            competingScheduler.Continue();
            competingResumed = true;
            (StoreStatus competingStatus, string? competingCorruption) =
                await competingTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.Success, competingStatus);
            Assert.Null(competingCorruption);
            Assert.Equal(0UL, ReadDirectoryCell(firstDirectory, earlierLocation));

            firstScheduler.Continue();
            (StoreStatus firstStatus, string? firstCorruption) =
                await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.Success, firstStatus);
            Assert.Null(firstCorruption);
        }
        finally
        {
            if (!firstResumed || !firstTask.IsCompleted)
            {
                firstScheduler.Continue();
            }

            if (!competingResumed)
            {
                competingScheduler.Continue();
            }
        }

        Assert.Equal(0UL, ReadDirectoryCell(firstDirectory, earlierLocation));
        Assert.Equal(0UL, ReadDirectoryCell(firstDirectory, laterLocation));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(0UL, ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(0UL, firstDirectory.ReadCanonicalMutation(CanonicalBucket));
        AssertDirectoryDrained(firstDirectory);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        AssertAllSlotCapacityReusable(firstStore, keys, RaceSlotCount);
    }

    [Fact]
    public async Task DelayedInsertCleanupAndLatePublisherConvergeAfterTargetSelection()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        string name = $"sms-v2-unlink-target-handoff-{Guid.NewGuid():N}";
        using var insertScheduler = new ControlledLockFreeScheduler();
        using var firstScheduler = new ControlledLockFreeScheduler();
        using var lateScheduler = new ControlledLockFreeScheduler();
        using MemoryStore insertStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            insertScheduler);
        using MemoryStore firstStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            firstScheduler);
        using MemoryStore lateStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            lateScheduler);
        LockFreeKeyDirectory insertDirectory = ReadDirectory(insertStore);
        LockFreeKeyDirectory firstDirectory = ReadDirectory(firstStore);
        LockFreeKeyDirectory lateDirectory = ReadDirectory(lateStore);
        LockFreeSlotTable slots = ReadSlots(insertStore);

        Assert.Equal(
            StoreStatus.Success,
            insertStore.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        ReservationHandle handle = reservation.HandleForEngine;
        Assert.Equal(
            StoreStatus.Success,
            insertDirectory.TryLookup(
                key,
                StoreKey.Hash(key),
                LockFreeOperationBudget.UnboundedScan,
                out ulong binding,
                out DirectoryLocation selectedLocation));
        Assert.Equal(handle.SlotBinding, binding);
        Assert.Equal(1, selectedLocation.Kind);
        Assert.Equal(0, selectedLocation.Index % LayoutV2Constants.PrimaryLanesPerBucket);

        IndexBinding decoded = IndexBinding.Decode(binding);
        int slotIndex = decoded.SlotIndex;
        DirectoryLocation lateLocation = DirectoryLocation.Decode(DirectoryLocation.Encode(
            selectedLocation.Kind,
            selectedLocation.Index + 1,
            decoded.Generation));
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(handle));
        ulong insertTargetSelected = DirectoryOperation.Encode(
            intent: 1,
            phase: 2,
            selectedLocation.Kind,
            selectedLocation.Index,
            decoded.Generation);
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryOperation,
            unchecked((long)insertTargetSelected));
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryLocation,
            unchecked((long)selectedLocation.Value));
        WriteDirectoryCell(insertDirectory, selectedLocation, binding);
        WriteDirectoryCell(insertDirectory, lateLocation, binding: 0);
        WriteCanonicalMutation(insertDirectory, CanonicalBucket, binding);

        insertScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch);
        Task<(StoreStatus Status, string? CorruptionOrigin)> delayedInsert = Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            InstrumentedLockFreeCheckpoint checkpoint =
                insertScheduler.CreateInstrumentedCheckpoint();
            StoreStatus status = insertDirectory.HelpMutation(
                CanonicalBucket,
                LockFreeOperationBudget.UnboundedScan,
                ref checkpoint,
                maxSteps: 1);
            return (status, LockFreeCorruptionTrace.Consume());
        });
        Assert.True(insertScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        firstScheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterLocationValidation);
        Task<(StoreStatus Status, string? CorruptionOrigin)> firstTask =
            UnlinkAsync(firstDirectory, binding, firstScheduler);
        Assert.True(firstScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        DirectoryOperation prepared = DirectoryOperation.Decode(
            ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(2, prepared.Intent);
        Assert.Equal(1, prepared.Phase);
        Assert.Equal(selectedLocation.Value, ReadDirectoryLocation(slots, slotIndex));

        var insertResumed = false;
        var firstResumed = false;
        var lateResumed = false;
        Task<(StoreStatus Status, string? CorruptionOrigin)>? lateTask = null;
        try
        {
            // The old Insert snapshot dispatches cancellation after U/Prepared
            // took ownership. It exact-clears A and Location A but cannot
            // replace the newer descriptor.
            insertScheduler.Continue();
            insertResumed = true;
            (StoreStatus insertStatus, string? insertCorruption) =
                await delayedInsert.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.StoreBusy, insertStatus);
            Assert.Null(insertCorruption);
            Assert.Equal(0UL, ReadDirectoryCell(insertDirectory, selectedLocation));
            Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
            Assert.Equal(prepared.Value, ReadDirectoryOperation(slots, slotIndex));

            // A second Prepared helper recovers B and publishes it, then stays
            // paused before post-CAS source validation.
            WriteDirectoryCell(insertDirectory, lateLocation, binding);
            lateScheduler.PauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
            lateTask = UnlinkAsync(lateDirectory, binding, lateScheduler);
            Assert.True(lateScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            lateScheduler.ContinueAndPauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation);
            lateResumed = true;
            Assert.True(lateScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(lateLocation.Value, ReadDirectoryLocation(slots, slotIndex));
            Assert.Equal(prepared.Value, ReadDirectoryOperation(slots, slotIndex));

            // The first helper now selects A while B is the published location,
            // then pauses as TargetSelected before reading that conflicting
            // witness. This is the exact terminal-tolerance window.
            firstScheduler.ContinueAndPauseAt(
                LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead);
            firstResumed = true;
            Assert.True(firstScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            DirectoryOperation selected = DirectoryOperation.Decode(
                ReadDirectoryOperation(slots, slotIndex));
            Assert.Equal(2, selected.Intent);
            Assert.Equal(2, selected.Phase);
            Assert.Equal(selectedLocation.Kind, selected.Kind);
            Assert.Equal(selectedLocation.Index, selected.Index);
            Assert.Equal(lateLocation.Value, ReadDirectoryLocation(slots, slotIndex));

            firstScheduler.Continue();
            (StoreStatus firstStatus, string? firstCorruption) =
                await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.Success, firstStatus);
            Assert.Null(firstCorruption);

            lateScheduler.Continue();
            (StoreStatus lateStatus, string? lateCorruption) =
                await lateTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.Success, lateStatus);
            Assert.Null(lateCorruption);
        }
        finally
        {
            if (!insertResumed)
            {
                insertScheduler.Continue();
            }

            if (!firstResumed || !firstTask.IsCompleted)
            {
                firstScheduler.Continue();
            }

            if (lateTask is not null && (!lateResumed || !lateTask.IsCompleted))
            {
                lateScheduler.Continue();
            }
        }

        Assert.Equal(0UL, ReadDirectoryCell(insertDirectory, selectedLocation));
        Assert.Equal(0UL, ReadDirectoryCell(insertDirectory, lateLocation));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(0UL, ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(0UL, insertDirectory.ReadCanonicalMutation(CanonicalBucket));
        AssertDirectoryDrained(insertDirectory);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        AssertAllSlotCapacityReusable(insertStore, keys, RaceSlotCount);
    }

    [Fact]
    public async Task PreparedUnlinkPublisherDelayedBeforeLocationCasWithdrawsLatePublication()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        string name = $"sms-v2-unlink-late-location-cas-{Guid.NewGuid():N}";
        using var publisherScheduler = new ControlledLockFreeScheduler();
        using MemoryStore publisherStore = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            publisherScheduler);
        using MemoryStore completingStore = OpenStore(name, RaceSlotCount);
        LockFreeKeyDirectory publisherDirectory = ReadDirectory(publisherStore);
        LockFreeKeyDirectory completingDirectory = ReadDirectory(completingStore);
        LockFreeSlotTable slots = ReadSlots(publisherStore);

        Assert.Equal(
            StoreStatus.Success,
            publisherStore.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        ReservationHandle handle = reservation.HandleForEngine;
        Assert.Equal(
            StoreStatus.Success,
            publisherDirectory.TryLookup(
                key,
                StoreKey.Hash(key),
                LockFreeOperationBudget.UnboundedScan,
                out ulong binding,
                out DirectoryLocation location));
        Assert.Equal(handle.SlotBinding, binding);

        IndexBinding decoded = IndexBinding.Decode(binding);
        int slotIndex = decoded.SlotIndex;
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(handle));
        ulong prepared = DirectoryOperation.Encode(
            intent: 2,
            phase: 1,
            targetKind: 0,
            targetIndex: 0,
            decoded.Generation);
        AtomicControlWord.StoreRelease(ref slots.Slot(slotIndex).DirectoryLocation, 0);
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryOperation,
            unchecked((long)prepared));
        WriteDirectoryCell(publisherDirectory, location, binding);
        WriteCanonicalMutation(publisherDirectory, CanonicalBucket, binding);

        publisherScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas);
        Task<(StoreStatus Status, string? CorruptionOrigin)> delayedPublisher =
            UnlinkAsync(publisherDirectory, binding, publisherScheduler);
        Assert.True(publisherScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));

        _ = LockFreeCorruptionTrace.Consume();
        Assert.Equal(
            StoreStatus.Success,
            completingDirectory.TryUnlink(binding, LockFreeOperationBudget.UnboundedScan));
        Assert.Null(LockFreeCorruptionTrace.Consume());
        Assert.Equal(0UL, ReadDirectoryCell(publisherDirectory, location));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(0UL, ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(0UL, publisherDirectory.ReadCanonicalMutation(CanonicalBucket));

        var resumed = false;
        try
        {
            publisherScheduler.Continue();
            resumed = true;
            (StoreStatus publisherStatus, string? publisherCorruption) =
                await delayedPublisher.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.Success, publisherStatus);
            Assert.Null(publisherCorruption);
        }
        finally
        {
            if (!resumed)
            {
                publisherScheduler.Continue();
            }
        }

        // The delayed stale CAS briefly installed Location C after unlink was
        // terminal; its post-CAS reconciliation must remove that exact word.
        Assert.Equal(0UL, ReadDirectoryCell(publisherDirectory, location));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(0UL, ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(0UL, publisherDirectory.ReadCanonicalMutation(CanonicalBucket));
        AssertDirectoryDrained(publisherDirectory);
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        AssertAllSlotCapacityReusable(publisherStore, keys, RaceSlotCount);
    }

    [Theory]
    [InlineData(CompetingUnlinkTarget.ExactBinding)]
    [InlineData(CompetingUnlinkTarget.Empty)]
    [InlineData(CompetingUnlinkTarget.ValidReplacement)]
    [InlineData(CompetingUnlinkTarget.Malformed)]
    [InlineData(CompetingUnlinkTarget.OutOfRange)]
    public void TargetSelectedUnlinkClassifiesAnotherSameGenerationLocationExactly(
        CompetingUnlinkTarget competingTarget)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        using MemoryStore store = CreateStore(
            $"sms-v2-unlink-location-terminal-{Guid.NewGuid():N}",
            RaceSlotCount);
        LockFreeKeyDirectory directory = ReadDirectory(store);
        LockFreeSlotTable slots = ReadSlots(store);

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        ReservationHandle handle = reservation.HandleForEngine;
        Assert.Equal(
            StoreStatus.Success,
            directory.TryLookup(
                key,
                StoreKey.Hash(key),
                LockFreeOperationBudget.UnboundedScan,
                out ulong binding,
                out DirectoryLocation selectedLocation));
        Assert.Equal(handle.SlotBinding, binding);
        Assert.Equal(1, selectedLocation.Kind);

        IndexBinding decoded = IndexBinding.Decode(binding);
        int slotIndex = decoded.SlotIndex;
        DirectoryLocation lateLocation = DirectoryLocation.Decode(DirectoryLocation.Encode(
            selectedLocation.Kind,
            selectedLocation.Index + 1,
            decoded.Generation));
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(handle));
        WriteDirectoryCell(directory, selectedLocation, binding);
        ulong lateTarget = competingTarget switch
        {
            CompetingUnlinkTarget.ExactBinding => binding,
            CompetingUnlinkTarget.Empty => 0,
            CompetingUnlinkTarget.ValidReplacement => IndexBinding.Encode(
                (decoded.SlotIndex + 1) % RaceSlotCount,
                decoded.Generation),
            CompetingUnlinkTarget.Malformed => ulong.MaxValue,
            CompetingUnlinkTarget.OutOfRange => IndexBinding.Encode(
                RaceSlotCount,
                decoded.Generation),
            _ => throw new ArgumentOutOfRangeException(nameof(competingTarget)),
        };
        WriteDirectoryCell(directory, lateLocation, lateTarget);
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryLocation,
            unchecked((long)lateLocation.Value));
        AtomicControlWord.StoreRelease(
            ref slots.Slot(slotIndex).DirectoryOperation,
            unchecked((long)DirectoryOperation.Encode(
                intent: 2,
                phase: 2,
                selectedLocation.Kind,
                selectedLocation.Index,
                decoded.Generation)));
        WriteCanonicalMutation(directory, CanonicalBucket, binding);

        _ = LockFreeCorruptionTrace.Consume();
        StoreStatus unlinkStatus = directory.TryUnlink(
            binding,
            LockFreeOperationBudget.UnboundedScan);
        string? corruptionOrigin = LockFreeCorruptionTrace.Consume();
        if (competingTarget is
            CompetingUnlinkTarget.Malformed or CompetingUnlinkTarget.OutOfRange)
        {
            Assert.Equal(StoreStatus.CorruptStore, unlinkStatus);
            Assert.NotNull(corruptionOrigin);
            Assert.Equal(lateTarget, ReadDirectoryCell(directory, lateLocation));
            return;
        }

        Assert.Equal(StoreStatus.Success, unlinkStatus);
        Assert.Null(corruptionOrigin);
        Assert.Equal(0UL, ReadDirectoryCell(directory, selectedLocation));
        Assert.Equal(lateTarget == binding ? 0UL : lateTarget, ReadDirectoryCell(directory, lateLocation));
        Assert.Equal(0UL, ReadDirectoryLocation(slots, slotIndex));
        Assert.Equal(0UL, ReadDirectoryOperation(slots, slotIndex));
        Assert.Equal(0UL, directory.ReadCanonicalMutation(CanonicalBucket));
        if (lateTarget != 0 && lateTarget != binding)
        {
            // The valid replacement was injected as a stand-in only; exact
            // unlink cleanup must preserve it, and the test withdraws it before
            // checking capacity recovery.
            WriteDirectoryCell(directory, lateLocation, binding: 0);
        }

        AssertDirectoryDrained(directory);

        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        AssertAllSlotCapacityReusable(store, keys, RaceSlotCount);
    }

    [Fact]
    public async Task TentativeExplicitReserveCannotCauseDuplicateUnlessAHelperFirstOrdersReserved()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] ownerKey = keys[0];
        byte[] helperKey = keys[1];
        string name = $"sms-v2-tentative-explicit-{Guid.NewGuid():N}";
        using var ownerScheduler = new ControlledLockFreeScheduler();
        using var helperScheduler = new ControlledLockFreeScheduler();
        using MemoryStore owner = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            ownerScheduler);
        using MemoryStore helper = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            helperScheduler);
        using MemoryStore contender = OpenStore(name, RaceSlotCount);
        LockFreeKeyDirectory directory = ReadDirectory(owner);
        LockFreeSlotTable slots = ReadSlots(owner);

        ownerScheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> ownerTask =
            ReserveAsync(owner, ownerKey, FiniteRaceWait);

        var ownerResumed = false;
        var helperResumed = false;
        ReservationHandle ownerHandle = default;
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)>? helperTask = null;
        (StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin) ownerResult = default;
        (StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin) helperResult = default;
        StoreStatus contenderStatus = StoreStatus.UnknownFailure;
        ValueReservation contenderReservation = default;
        var ownerWasReservedAtContenderReturn = false;
        try
        {
            Assert.True(ownerScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            ownerHandle = CaptureCurrentReservation(
                directory,
                slots,
                expectedPhase: 1,
                expectedTargetKind: 0);
            AssertPublicationIntent(
                slots,
                ownerHandle,
                SlotPublicationIntent.ExplicitReservation);

            helperScheduler.PauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
            helperTask = ReserveAsync(helper, helperKey, StoreWaitOptions.Infinite);
            Assert.True(helperScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                ownerHandle,
                CaptureCurrentReservation(
                    directory,
                    slots,
                    expectedPhase: 2,
                    expectedTargetKind: 1));

            contenderStatus = contender.TryReserve(
                ownerKey,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.NoWait,
                out contenderReservation);
            ownerWasReservedAtContenderReturn =
                HasExactSlotState(slots, ownerHandle, LockFreeSlotTable.ReservedState);

            await Task.Delay(ExpiredRaceDelay);
            ownerScheduler.Continue();
            ownerResumed = true;
            ownerResult = await ownerTask.WaitAsync(TimeSpan.FromSeconds(5));

            helperScheduler.Continue();
            helperResumed = true;
            helperResult = await helperTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!ownerResumed)
            {
                ownerScheduler.Continue();
            }

            if (!helperResumed)
            {
                helperScheduler.Continue();
            }
        }

        AbortIfPending(contenderReservation);
        AbortIfPending(ownerResult.Reservation);
        AbortIfPending(helperResult.Reservation);

        string evidence =
            $"contender={contenderStatus}; ownerReservedBeforeContenderResponse={ownerWasReservedAtContenderReturn}; " +
            $"owner={ownerResult.Status}; ownerCorruption={ownerResult.CorruptionOrigin ?? "none"}; " +
            $"helper={helperResult.Status}; helperCorruption={helperResult.CorruptionOrigin ?? "none"}.";
        Assert.True(
            contenderStatus != StoreStatus.DuplicateKey || ownerWasReservedAtContenderReturn,
            "A same-key contender returned DuplicateKey from an Initializing-only binding. " + evidence);
        if (ownerWasReservedAtContenderReturn)
        {
            Assert.Equal(StoreStatus.Success, ownerResult.Status);
            Assert.Equal(ownerHandle, ownerResult.Reservation.HandleForEngine);
        }
        else
        {
            Assert.Equal(StoreStatus.StoreBusy, ownerResult.Status);
            Assert.NotEqual(StoreStatus.DuplicateKey, contenderStatus);
        }

        Assert.Equal(StoreStatus.Success, helperResult.Status);
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(ownerKey, out _));
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(helperKey, out _));
        AssertDirectoryDrained(directory);
        AssertAllSlotCapacityReusable(owner, keys, RaceSlotCount);
    }

    [Fact]
    public async Task PreReservedAbortWinsOverTentativeBindingAndCannotCauseDuplicate()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] ownerKey = keys[0];
        byte[] helperKey = keys[1];
        string name = $"sms-v2-tentative-abort-{Guid.NewGuid():N}";
        using var ownerScheduler = new ControlledLockFreeScheduler();
        using var helperScheduler = new ControlledLockFreeScheduler();
        using MemoryStore owner = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            ownerScheduler);
        using MemoryStore helper = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.OpenExisting,
            helperScheduler);
        using MemoryStore contender = OpenStore(name, RaceSlotCount);
        LockFreeKeyDirectory directory = ReadDirectory(owner);
        LockFreeSlotTable slots = ReadSlots(owner);

        ownerScheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> ownerTask =
            ReserveAsync(owner, ownerKey, StoreWaitOptions.Infinite);

        var ownerResumed = false;
        var helperResumed = false;
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)>? helperTask = null;
        (StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin) ownerResult = default;
        (StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin) helperResult = default;
        StoreStatus contenderStatus = StoreStatus.UnknownFailure;
        ValueReservation contenderReservation = default;
        try
        {
            Assert.True(ownerScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            ReservationHandle ownerHandle = CaptureCurrentReservation(
                directory,
                slots,
                expectedPhase: 1,
                expectedTargetKind: 0);

            helperScheduler.PauseAt(
                LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
            helperTask = ReserveAsync(helper, helperKey, StoreWaitOptions.Infinite);
            Assert.True(helperScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(
                ownerHandle,
                CaptureCurrentReservation(
                    directory,
                    slots,
                    expectedPhase: 2,
                    expectedTargetKind: 1));

            Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(ownerHandle));
            AssertSlotState(slots, ownerHandle, LockFreeSlotTable.AbortingState);
            contenderStatus = contender.TryReserve(
                ownerKey,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.NoWait,
                out contenderReservation);

            ownerScheduler.Continue();
            ownerResumed = true;
            ownerResult = await ownerTask.WaitAsync(TimeSpan.FromSeconds(5));

            helperScheduler.Continue();
            helperResumed = true;
            helperResult = await helperTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!ownerResumed)
            {
                ownerScheduler.Continue();
            }

            if (!helperResumed)
            {
                helperScheduler.Continue();
            }
        }

        AbortIfPending(contenderReservation);
        AbortIfPending(helperResult.Reservation);
        Assert.Equal(StoreStatus.InvalidReservation, ownerResult.Status);
        Assert.False(ownerResult.Reservation.IsValid);
        Assert.True(
            contenderStatus is StoreStatus.Success or StoreStatus.StoreBusy,
            $"A pre-Reserved abort left only a tentative binding, but the contender returned {contenderStatus}; " +
            $"ownerCorruption={ownerResult.CorruptionOrigin ?? "none"}; " +
            $"helper={helperResult.Status}; helperCorruption={helperResult.CorruptionOrigin ?? "none"}.");
        Assert.Equal(StoreStatus.Success, helperResult.Status);
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(ownerKey, out _));
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(helperKey, out _));
        AssertDirectoryDrained(directory);
        AssertAllSlotCapacityReusable(owner, keys, RaceSlotCount);
    }

    [Fact]
    public async Task HelperOrderedExplicitReserveReturnsSuccessAfterOwnerDeadline()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] ownerKey = keys[0];
        byte[] helperKey = keys[1];
        string name = $"sms-v2-reserved-before-budget-{Guid.NewGuid():N}";
        using var ownerScheduler = new ControlledLockFreeScheduler();
        using MemoryStore owner = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            ownerScheduler);
        using MemoryStore helper = OpenStore(name, RaceSlotCount);
        LockFreeKeyDirectory directory = ReadDirectory(owner);
        LockFreeSlotTable slots = ReadSlots(owner);

        ownerScheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> ownerTask =
            ReserveAsync(owner, ownerKey, FiniteRaceWait);

        var ownerResumed = false;
        (StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin) ownerResult = default;
        StoreStatus helperStatus = StoreStatus.UnknownFailure;
        ValueReservation helperReservation = default;
        ReservationHandle ownerHandle = default;
        try
        {
            Assert.True(ownerScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            ownerHandle = CaptureCurrentReservation(
                directory,
                slots,
                expectedPhase: 1,
                expectedTargetKind: 0);

            helperStatus = helper.TryReserve(
                helperKey,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out helperReservation);
            Assert.Equal(StoreStatus.Success, helperStatus);
            AssertSlotState(slots, ownerHandle, LockFreeSlotTable.ReservedState);

            await Task.Delay(ExpiredRaceDelay);
            ownerScheduler.Continue();
            ownerResumed = true;
            ownerResult = await ownerTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!ownerResumed)
            {
                ownerScheduler.Continue();
            }
        }

        AbortIfPending(ownerResult.Reservation);
        AbortIfPending(helperReservation);
        Assert.True(
            ownerResult.Status == StoreStatus.Success,
            $"A helper ordered Initializing->Reserved before the owner's expired budget check, " +
            $"but the owner returned {ownerResult.Status}; corruptionOrigin={ownerResult.CorruptionOrigin ?? "none"}.");
        Assert.Equal(ownerHandle, ownerResult.Reservation.HandleForEngine);
        Assert.Equal(StoreStatus.Success, helperStatus);
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(ownerKey, out _));
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(helperKey, out _));
        AssertDirectoryDrained(directory);
        AssertAllSlotCapacityReusable(owner, keys, RaceSlotCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AtomicConvenienceReservedStateRemainsTentativeUntilPublished(bool segmented)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: RaceSlotCount + 2, RaceSlotCount);
        byte[] key = keys[0];
        string name = $"sms-v2-atomic-intent-{segmented}-{Guid.NewGuid():N}";
        using var publisherScheduler = new ControlledLockFreeScheduler();
        using MemoryStore publisher = CreateInstrumentedStore(
            name,
            RaceSlotCount,
            OpenMode.CreateNew,
            publisherScheduler);
        using MemoryStore contender = OpenStore(name, RaceSlotCount);
        LockFreeKeyDirectory directory = ReadDirectory(publisher);
        LockFreeSlotTable slots = ReadSlots(publisher);

        publisherScheduler.PauseAt(
            LockFreeCheckpointId.ReserveAfterDirectoryInsertBeforePendingClassification);
        Task<(StoreStatus Status, long CopiedBytes, string? CorruptionOrigin)> publisherTask =
            PublishAsync(publisher, key, segmented, FiniteRaceWait);

        var publisherResumed = false;
        StoreStatus contenderStatus = StoreStatus.UnknownFailure;
        ValueReservation contenderReservation = default;
        (StoreStatus Status, long CopiedBytes, string? CorruptionOrigin) publisherResult = default;
        try
        {
            Assert.True(publisherScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            ReservationHandle publisherHandle = CaptureReservation(
                slots,
                LockFreeSlotTable.ReservedState,
                expectedPhase: 5,
                expectedTargetKind: 1);
            AssertPublicationIntent(
                slots,
                publisherHandle,
                SlotPublicationIntent.AtomicPublication);

            contenderStatus = contender.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.NoWait,
                out contenderReservation);

            await Task.Delay(ExpiredRaceDelay);
            publisherScheduler.Continue();
            publisherResumed = true;
            publisherResult = await publisherTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if (!publisherResumed)
            {
                publisherScheduler.Continue();
            }
        }

        AbortIfPending(contenderReservation);
        StoreStatus acquireAfterPublisher = publisher.TryAcquire(key, out ValueLease lease);
        if (acquireAfterPublisher == StoreStatus.Success)
        {
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.Equal(StoreStatus.Success, publisher.TryRemove(key, StoreWaitOptions.Infinite));
        }

        Assert.True(
            contenderStatus == StoreStatus.StoreBusy,
            $"{(segmented ? "TryPublishSegments" : "TryPublish")} exposed its internal Reserved state as " +
            $"public key ownership; the same-key contender returned {contenderStatus} before Published.");
        Assert.True(
            publisherResult.Status == StoreStatus.StoreBusy,
            $"The expired atomic convenience publisher returned {publisherResult.Status}; " +
            $"copiedBytes={publisherResult.CopiedBytes}; corruptionOrigin={publisherResult.CorruptionOrigin ?? "none"}.");
        Assert.Equal(StoreStatus.NotFound, acquireAfterPublisher);
        AssertDirectoryDrained(directory);
        AssertAllSlotCapacityReusable(publisher, keys, RaceSlotCount);
    }

    [Fact]
    public void ExplicitReservedStateBlocksSameKeyContenders()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-explicit-intent-{Guid.NewGuid():N}";
        using MemoryStore owner = CreateStore(name, slotCount: 2);
        using MemoryStore contender = OpenStore(name, slotCount: 2);
        byte[] key = [0x38];

        Assert.Equal(
            StoreStatus.Success,
            owner.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        Assert.Equal(
            StoreStatus.DuplicateKey,
            contender.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.NoWait,
                out ValueReservation duplicate));
        Assert.False(duplicate.IsValid);
        AssertPublicationIntent(
            ReadSlots(owner),
            reservation.HandleForEngine,
            SlotPublicationIntent.ExplicitReservation);

        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.NotFound, owner.TryAcquire(key, out _));
        AssertDirectoryDrained(ReadDirectory(owner));
    }

    [Fact]
    public void ExactCellWithoutMetadataReadyMarkerFailsClosedInsteadOfReportingDuplicate()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-missing-marker-{Guid.NewGuid():N}";
        using MemoryStore owner = CreateStore(name, slotCount: 2);
        using MemoryStore contender = OpenStore(name, slotCount: 2);
        byte[] key = [0x39];

        Assert.Equal(
            StoreStatus.Success,
            owner.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        LockFreeSlotTable slots = ReadSlots(owner);
        IndexBinding binding = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long validOperation = AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation);
        Assert.NotEqual(0, validOperation);

        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, 0);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                contender.TryReserve(
                    key,
                    payloadLength: 1,
                    descriptor: default,
                    StoreWaitOptions.NoWait,
                    out ValueReservation duplicate));
            Assert.False(duplicate.IsValid);
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, validOperation);
        }

        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Theory]
    [InlineData(LockFreeSlotTable.PublishedState, false)]
    [InlineData(LockFreeSlotTable.RemoveRequestedState, false)]
    [InlineData(LockFreeSlotTable.FreeState, false)]
    [InlineData(LockFreeSlotTable.InitializingState, true)]
    [InlineData(LockFreeSlotTable.ReservedState, true)]
    [InlineData(LockFreeSlotTable.RetiredState, false)]
    public void TentativeAbortFailsClosedForImpossibleSameGenerationState(
        int impossibleState,
        bool wrongOwner)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            $"sms-v2-tentative-impossible-{impossibleState}-{Guid.NewGuid():N}",
            slotCount: 1);
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                [0x3A],
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));

        ReservationHandle handle = reservation.HandleForEngine;
        LockFreeSlotTable slots = ReadSlots(store);
        IndexBinding binding = IndexBinding.Decode(handle.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long reserved = AtomicControlWord.LoadAcquire(ref slot.Control);
        int participant = 0;
        if (wrongOwner)
        {
            participant = handle.ParticipantToken == 1 ? 2 : 1;
        }

        long impossible = unchecked((long)AtomicControlWord.EncodeSlot(
            impossibleState,
            binding.Generation,
            participant));
        AtomicControlWord.StoreRelease(ref slot.Control, impossible);
        try
        {
            Assert.Equal(
                TentativeReservationAbortResult.Corrupt,
                slots.TryBeginTentativeAbort(handle));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.Control, reserved);
        }

        bool structurallyMalformed = wrongOwner
            || impossibleState == LockFreeSlotTable.RetiredState;
        Assert.Equal(
            structurallyMalformed ? StoreStatus.CorruptStore : StoreStatus.Success,
            reservation.Abort(StoreWaitOptions.Infinite));
        if (!structurallyMalformed)
        {
            AssertDirectoryDrained(ReadDirectory(store));
        }
    }

    [Fact]
    public async Task RejectedNoWaitCandidateRevalidatesWinnerButCannotClaimAgain()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-no-wait-rejected-retry-{Guid.NewGuid():N}";
        using var gate = new RejectedCandidateRetryGate();
        StoreOpenStatus opened = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, slotCount: 2, OpenMode.CreateNew),
            LockFreeCheckpointFactory.CreateInstrumented(gate.Observe),
            out MemoryStore? candidateStore);
        Assert.Equal(StoreOpenStatus.Success, opened);
        using MemoryStore candidate = Assert.IsType<MemoryStore>(candidateStore);
        using MemoryStore winner = OpenStore(name, slotCount: 2);
        byte[] key = [0x3B];

        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> candidateTask =
            ReserveAsync(candidate, key, StoreWaitOptions.NoWait);
        Assert.True(gate.WaitBeforeDirectoryInsert(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            winner.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation winningReservation));

        gate.ContinueDirectoryInsert();
        Assert.True(gate.WaitAtFreshConflictResolution(TimeSpan.FromSeconds(5)));
        Assert.Equal(StoreStatus.Success, winningReservation.Abort(StoreWaitOptions.Infinite));
        gate.ContinueConflictResolution();

        (StoreStatus status, ValueReservation reservation, string? corruptionOrigin) =
            await candidateTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.StoreBusy, status);
        Assert.False(reservation.IsValid);
        Assert.Null(corruptionOrigin);
        Assert.Equal(1, gate.SlotClaimCount);
        AssertDirectoryDrained(ReadDirectory(candidate));
    }

    [Fact]
    public void PostInsertClassifierRejectsSameGenerationPublishedControlAsCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            $"sms-v2-insert-classifier-published-{Guid.NewGuid():N}",
            slotCount: 1);
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([0x35], 1, default, StoreWaitOptions.Infinite, out ValueReservation reservation));

        ReservationHandle handle = reservation.HandleForEngine;
        LockFreeSlotTable slots = ReadSlots(store);
        IndexBinding binding = IndexBinding.Decode(handle.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long reserved = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(LockFreeSlotTable.ReservedState, SlotState(reserved));
        Assert.Equal(binding.Generation, SlotGeneration(reserved));
        long published = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.PublishedState,
            binding.Generation,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref slot.Control, published);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                slots.ClassifyReservationAfterDirectoryInsert(handle));
        }
        finally
        {
            Assert.Equal(
                published,
                AtomicControlWord.CompareExchange(ref slot.Control, reserved, published));
        }

        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        AssertDirectoryDrained(ReadDirectory(store));
    }

    [Fact]
    public void PostInsertClassifierRejectsLowerGenerationControlAsCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            $"sms-v2-insert-classifier-lower-generation-{Guid.NewGuid():N}",
            slotCount: 1);
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([0x36], 1, default, StoreWaitOptions.Infinite, out ValueReservation first));
        Assert.Equal(StoreStatus.Success, first.Abort(StoreWaitOptions.Infinite));
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([0x37], 1, default, StoreWaitOptions.Infinite, out ValueReservation reservation));

        ReservationHandle handle = reservation.HandleForEngine;
        LockFreeSlotTable slots = ReadSlots(store);
        IndexBinding binding = IndexBinding.Decode(handle.SlotBinding);
        Assert.True(binding.Generation > 1);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long reserved = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(LockFreeSlotTable.ReservedState, SlotState(reserved));
        Assert.Equal(binding.Generation, SlotGeneration(reserved));
        long staleAborting = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.AbortingState,
            binding.Generation - 1,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref slot.Control, staleAborting);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                slots.ClassifyReservationAfterDirectoryInsert(handle));
        }
        finally
        {
            Assert.Equal(
                staleAborting,
                AtomicControlWord.CompareExchange(ref slot.Control, reserved, staleAborting));
        }

        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort(StoreWaitOptions.Infinite));
    }

    [Fact]
    public async Task OverflowInsertObservingExactEmptyAfterConcurrentCancellationDoesNotReportCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(count: OverflowSlotCount + 2, OverflowSlotCount);
        string name = $"sms-v2-insert-cancel-overflow-{Guid.NewGuid():N}";
        using var insertScheduler = new ControlledLockFreeScheduler();
        using var abortScheduler = new ControlledLockFreeScheduler();
        using MemoryStore insertStore = CreateInstrumentedStore(
            name,
            OverflowSlotCount,
            OpenMode.CreateNew,
            insertScheduler);
        using MemoryStore abortStore = CreateInstrumentedStore(
            name,
            OverflowSlotCount,
            OpenMode.OpenExisting,
            abortScheduler);

        for (var index = 0; index < OverflowAnchorCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                insertStore.TryPublish(
                    keys[index],
                    [unchecked((byte)index)],
                    default,
                    StoreWaitOptions.Infinite));
        }

        LockFreeKeyDirectory insertDirectory = ReadDirectory(insertStore);
        Assert.Equal(OverflowAnchorCount, insertDirectory.PrimaryOccupancy);
        Assert.Equal(0, insertDirectory.OverflowOccupancy);

        insertScheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication);
        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> reserveTask = Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            StoreStatus status = insertStore.TryReserve(
                keys[OverflowAnchorCount],
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation);
            return (status, reservation, LockFreeCorruptionTrace.Consume());
        });

        var insertResumed = false;
        var abortResumed = false;
        Task<StoreStatus>? abortTask = null;
        try
        {
            Assert.True(insertScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            LockFreeSlotTable slots = ReadSlots(insertStore);
            ReservationHandle reservationHandle = CaptureCurrentReservation(
                insertDirectory,
                slots,
                expectedPhase: 2,
                expectedTargetKind: 2);
            Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(reservationHandle));
            AssertSlotState(slots, reservationHandle, LockFreeSlotTable.AbortingState);

            abortScheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterSpillSummaryClear);
            LockFreeKeyDirectory abortDirectory = ReadDirectory(abortStore);
            abortTask = Task.Run(() =>
            {
                InstrumentedLockFreeCheckpoint checkpoint =
                    abortScheduler.CreateInstrumentedCheckpoint();
                return abortDirectory.TryUnlink(
                    reservationHandle.SlotBinding,
                    LockFreeOperationBudget.UnboundedScan,
                    ref checkpoint);
            });
            Assert.True(abortScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

            SpillSummary empty = SpillSummary.Decode(
                abortDirectory.ReadSpillSummary(CanonicalBucket));
            Assert.False(empty.IsPresent);
            Assert.Equal(reservationHandle.SlotBinding, empty.Binding);
            Assert.Equal(
                reservationHandle.SlotBinding,
                abortDirectory.ReadCanonicalMutation(CanonicalBucket));

            // Resume the already validated insert while the cancellation helper
            // retains Empty(binding) and the exact canonical mutation. Empty is
            // a legitimate terminal observation for this canceled lifecycle.
            insertScheduler.Continue();
            insertResumed = true;
            (StoreStatus insertStatus, ValueReservation reservation, string? corruptionOrigin) =
                await reserveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(
                insertStatus != StoreStatus.CorruptStore,
                $"The resumed insert reported corruption at {corruptionOrigin ?? "an untraced origin"}.");
            Assert.False(reservation.IsValid);

            abortScheduler.Continue();
            abortResumed = true;
            StoreStatus abortStatus = await abortTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(StoreStatus.CorruptStore, abortStatus);
        }
        finally
        {
            if (!insertResumed)
            {
                insertScheduler.Continue();
            }

            if (!abortResumed)
            {
                abortScheduler.Continue();
            }
        }

        for (var index = 0; index < OverflowAnchorCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                insertStore.TryRemove(keys[index], StoreWaitOptions.Infinite));
        }

        Assert.Equal(StoreStatus.NotFound, insertStore.TryAcquire(keys[OverflowAnchorCount], out _));
        AssertAllSlotCapacityReusable(insertStore, keys);
    }

    private static async Task RunSingleSlotCancellationRace(
        LockFreeCheckpointId checkpoint,
        int expectedPhase,
        int expectedSlotState,
        StoreStatus expectedStatus,
        byte[] key)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(
            $"sms-v2-insert-cancel-{checkpoint}-{Guid.NewGuid():N}",
            slotCount: 1,
            OpenMode.CreateNew,
            scheduler);
        LockFreeKeyDirectory directory = ReadDirectory(store);
        LockFreeSlotTable slots = ReadSlots(store);
        scheduler.PauseAt(checkpoint);

        Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)> reserveTask = Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            StoreStatus status = store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation);
            return (status, reservation, LockFreeCorruptionTrace.Consume());
        });

        var resumed = false;
        try
        {
            Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            ReservationHandle reservationHandle = CaptureReservation(
                slots,
                expectedSlotState,
                expectedPhase,
                expectedTargetKind: expectedPhase == 1 ? 0 : 1);
            Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(reservationHandle));
            AssertSlotState(slots, reservationHandle, LockFreeSlotTable.AbortingState);

            scheduler.Continue();
            resumed = true;
            (StoreStatus status, ValueReservation reservation, string? corruptionOrigin) =
                await reserveTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(
                status == expectedStatus,
                $"Expected {expectedStatus} after cancellation at {checkpoint}, observed {status}; " +
                $"corruptionOrigin={corruptionOrigin ?? "none"}.");
            if (expectedStatus == StoreStatus.Success)
            {
                Assert.Equal(reservationHandle, reservation.HandleForEngine);
            }
            else
            {
                Assert.False(reservation.IsValid);
            }
        }
        finally
        {
            if (!resumed)
            {
                scheduler.Continue();
            }
        }

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(key, out _));
        AssertDirectoryDrained(directory);
        byte[] replacementKey = [unchecked((byte)(key[0] + 0x40))];
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(replacementKey, [0xA5], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([0x7F], [0x5A]));
        Assert.Equal(StoreStatus.Success, store.TryRemove(replacementKey, StoreWaitOptions.Infinite));
        AssertDirectoryDrained(directory);
    }

    private static ReservationHandle CaptureReservation(
        LockFreeSlotTable slots,
        int expectedSlotState,
        int expectedPhase,
        int expectedTargetKind)
    {
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(slots, "_layout");
        ReservationHandle found = default;
        var foundCount = 0;
        for (var slotIndex = 0; slotIndex < layout.SlotCount; slotIndex++)
        {
            ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
            long control = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (SlotState(control) != expectedSlotState)
            {
                continue;
            }

            ulong participantToken = unchecked((ulong)control) >> 36;
            if (participantToken == 0)
            {
                continue;
            }

            ulong binding = slot.DirectoryBinding;
            IndexBinding decoded = IndexBinding.Decode(binding);
            DirectoryOperation operation = DirectoryOperation.Decode(
                unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation)));
            if (decoded.SlotIndex != slotIndex
                || decoded.Generation != SlotGeneration(control)
                || operation.Intent != 1
                || operation.Phase != expectedPhase
                || operation.Kind != expectedTargetKind
                || operation.Generation != decoded.Generation)
            {
                continue;
            }

            found = new ReservationHandle(
                ReadPrivate<ulong>(slots, "_storeId"),
                participantToken,
                binding,
                slot.ValueLength);
            foundCount++;
        }

        Assert.Equal(1, foundCount);
        return found;
    }

    private static ReservationHandle CaptureCurrentReservation(
        LockFreeKeyDirectory directory,
        LockFreeSlotTable slots,
        int expectedPhase,
        int expectedTargetKind)
    {
        ulong binding = FindCurrentMutation(directory);
        IndexBinding decoded = IndexBinding.Decode(binding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(decoded.SlotIndex);
        long control = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(control));
        Assert.Equal(decoded.Generation, SlotGeneration(control));
        Assert.Equal(binding, slot.DirectoryBinding);

        DirectoryOperation operation = DirectoryOperation.Decode(
            unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation)));
        Assert.Equal(1, operation.Intent);
        Assert.Equal(expectedPhase, operation.Phase);
        Assert.Equal(expectedTargetKind, operation.Kind);
        Assert.Equal(decoded.Generation, operation.Generation);

        ulong participantToken = unchecked((ulong)control) >> 36;
        Assert.NotEqual(0UL, participantToken);
        return new ReservationHandle(
            ReadPrivate<ulong>(slots, "_storeId"),
            participantToken,
            binding,
            slot.ValueLength);
    }

    private static ulong FindCurrentMutation(LockFreeKeyDirectory directory)
    {
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(directory, "_layout");
        ulong found = 0;
        for (var bucket = 0; bucket < layout.PrimaryBucketCount; bucket++)
        {
            ulong mutation = directory.ReadCanonicalMutation(bucket);
            if (mutation == 0)
            {
                continue;
            }

            Assert.Equal(0UL, found);
            found = mutation;
        }

        Assert.NotEqual(0UL, found);
        return found;
    }

    private static void AssertDirectoryDrained(LockFreeKeyDirectory directory)
    {
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(directory, "_layout");
        Assert.Equal(0, directory.PrimaryOccupancy);
        Assert.Equal(0, directory.OverflowOccupancy);
        for (var bucket = 0; bucket < layout.PrimaryBucketCount; bucket++)
        {
            Assert.Equal(0UL, directory.ReadCanonicalMutation(bucket));
        }
    }

    private static void AssertSlotState(
        LockFreeSlotTable slots,
        in ReservationHandle reservation,
        int expectedState)
    {
        IndexBinding binding = IndexBinding.Decode(reservation.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long control = AtomicControlWord.LoadAcquire(ref slot.Control);
        Assert.Equal(expectedState, SlotState(control));
        Assert.Equal(binding.Generation, SlotGeneration(control));
    }

    private static void AssertAllSlotCapacityReusable(
        MemoryStore store,
        byte[][] keys,
        int slotCount = OverflowSlotCount)
    {
        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(
                keys[index],
                    [unchecked((byte)(0x80 + index))],
                    default,
                StoreWaitOptions.Infinite));
        }

        Assert.Equal(
            StoreStatus.StoreFull,
            store.TryPublish(
                keys[slotCount],
                [0xFF],
                default,
                StoreWaitOptions.Infinite));

        for (var index = 0; index < slotCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryRemove(keys[index], StoreWaitOptions.Infinite));
        }
    }

    private static Task<(StoreStatus Status, ValueReservation Reservation, string? CorruptionOrigin)>
        ReserveAsync(
            MemoryStore store,
            byte[] key,
            StoreWaitOptions waitOptions) =>
        Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            StoreStatus status = store.TryReserve(
                key,
                payloadLength: 1,
                descriptor: default,
                waitOptions,
                out ValueReservation reservation);
            return (status, reservation, LockFreeCorruptionTrace.Consume());
        });

    private static Task<(StoreStatus Status, string? CorruptionOrigin)> UnlinkAsync(
        LockFreeKeyDirectory directory,
        ulong binding,
        ControlledLockFreeScheduler scheduler) =>
        Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            InstrumentedLockFreeCheckpoint checkpoint = scheduler.CreateInstrumentedCheckpoint();
            StoreStatus status = directory.TryUnlink(
                binding,
                LockFreeOperationBudget.UnboundedScan,
                ref checkpoint);
            return (status, LockFreeCorruptionTrace.Consume());
        });

    private static Task<(StoreStatus Status, long CopiedBytes, string? CorruptionOrigin)>
        PublishAsync(
            MemoryStore store,
            byte[] key,
            bool segmented,
            StoreWaitOptions waitOptions) =>
        Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            long copiedBytes;
            StoreStatus status;
            if (segmented)
            {
                var payload = new ReadOnlySequence<byte>(new byte[] { 0xA5 });
                status = store.TryPublishSegments(
                    key,
                    payload,
                    descriptor: default,
                    waitOptions,
                    out copiedBytes);
            }
            else
            {
                status = store.TryPublish(
                    key,
                    [0xA5],
                    descriptor: default,
                    waitOptions);
                copiedBytes = status == StoreStatus.Success ? 1 : 0;
            }

            return (status, copiedBytes, LockFreeCorruptionTrace.Consume());
        });

    private static bool HasExactSlotState(
        LockFreeSlotTable slots,
        in ReservationHandle reservation,
        int expectedState)
    {
        IndexBinding binding = IndexBinding.Decode(reservation.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        long control = AtomicControlWord.LoadAcquire(ref slot.Control);
        return SlotState(control) == expectedState
            && SlotGeneration(control) == binding.Generation
            && slot.DirectoryBinding == reservation.SlotBinding;
    }

    private static void AssertPublicationIntent(
        LockFreeSlotTable slots,
        in ReservationHandle reservation,
        SlotPublicationIntent expected)
    {
        IndexBinding binding = IndexBinding.Decode(reservation.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(binding.SlotIndex);
        Assert.Equal((int)expected, Volatile.Read(ref slot.PublicationIntent));
    }

    private static void AbortIfPending(ValueReservation reservation)
    {
        if (reservation.IsValid)
        {
            Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));
        }
    }

    private static MemoryStore CreateInstrumentedStore(
        string name,
        int slotCount,
        OpenMode openMode,
        ControlledLockFreeScheduler scheduler)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, slotCount, openMode),
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateStore(string name, int slotCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, slotCount, OpenMode.CreateNew),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore OpenStore(string name, int slotCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, slotCount, OpenMode.OpenExisting),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name, int slotCount, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: sizeof(long),
            leaseRecordCount: Math.Max(2, slotCount),
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store) =>
        ReadPrivate<LockFreeKeyDirectory>(ReadPrivate<object>(store, "_engine"), "_directory");

    private static LockFreeSlotTable ReadSlots(MemoryStore store) =>
        ReadPrivate<LockFreeSlotTable>(ReadPrivate<object>(store, "_engine"), "_slots");

    private static ulong ReadDirectoryOperation(LockFreeSlotTable slots, int slotIndex) =>
        unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref slots.Slot(slotIndex).DirectoryOperation));

    private static ulong ReadDirectoryLocation(LockFreeSlotTable slots, int slotIndex) =>
        unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref slots.Slot(slotIndex).DirectoryLocation));

    private static ulong ReadDirectoryCell(
        LockFreeKeyDirectory directory,
        DirectoryLocation location) =>
        unchecked((ulong)AtomicControlWord.LoadAcquire(ref DirectoryCell(directory, location)));

    private static void WriteDirectoryCell(
        LockFreeKeyDirectory directory,
        DirectoryLocation location,
        ulong binding) =>
        AtomicControlWord.StoreRelease(
            ref DirectoryCell(directory, location),
            unchecked((long)binding));

    private static void WriteCanonicalMutation(
        LockFreeKeyDirectory directory,
        int canonicalBucket,
        ulong binding) =>
        AtomicControlWord.StoreRelease(
            ref CanonicalMutation(directory, canonicalBucket),
            unchecked((long)binding));

    private static unsafe ref long DirectoryCell(
        LockFreeKeyDirectory directory,
        DirectoryLocation location)
    {
        ISharedStoreRegion region = ReadPrivate<ISharedStoreRegion>(directory, "_region");
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(directory, "_layout");
        long offset = location.Kind switch
        {
            1 => PrimaryCellOffset(layout, checked((int)location.Index)),
            2 => layout.OverflowDirectoryOffset + (location.Index * layout.OverflowStride),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        return ref *(long*)(region.Pointer + offset);
    }

    private static unsafe ref long CanonicalMutation(
        LockFreeKeyDirectory directory,
        int canonicalBucket)
    {
        ISharedStoreRegion region = ReadPrivate<ISharedStoreRegion>(directory, "_region");
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(directory, "_layout");
        long offset = layout.PrimaryDirectoryOffset
            + ((long)canonicalBucket * layout.PrimaryBucketStride)
            + sizeof(long);
        return ref *(long*)(region.Pointer + offset);
    }

    private static long PrimaryCellOffset(StoreLayoutV2 layout, int absoluteCellIndex)
    {
        int bucket = absoluteCellIndex / LayoutV2Constants.PrimaryLanesPerBucket;
        int lane = absoluteCellIndex % LayoutV2Constants.PrimaryLanesPerBucket;
        return layout.PrimaryDirectoryOffset
            + ((long)bucket * layout.PrimaryBucketStride)
            + (2 * sizeof(long))
            + (lane * sizeof(long));
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {owner.GetType().FullName}.{fieldName}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(owner));
    }

    private static byte[][] GenerateBucketPairCollisions(int count, int slotCount)
    {
        var keys = new List<byte[]>(count);
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        for (long candidate = 1; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            ulong hash = StoreKey.Hash(key);
            int first = (int)(Mix(hash) & bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
            if (second == first)
            {
                second = (first + 1) & (int)bucketMask;
            }

            if (first == CanonicalBucket && second == 1)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static int SlotState(long control) => (int)(unchecked((ulong)control) & 0x7UL);

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public enum TargetAfterCancellation
    {
        Empty,
        ValidReplacement,
        Malformed,
        OutOfRange,
    }

    public enum CompetingUnlinkTarget
    {
        ExactBinding,
        Empty,
        ValidReplacement,
        Malformed,
        OutOfRange,
    }

    private sealed class RejectedCandidateRetryGate : IDisposable
    {
        private readonly ManualResetEventSlim _beforeInsert = new(false);
        private readonly ManualResetEventSlim _continueInsert = new(false);
        private readonly ManualResetEventSlim _atConflict = new(false);
        private readonly ManualResetEventSlim _continueConflict = new(false);
        private int _slotClaimCount;
        private int _beforeInsertObserved;
        private int _conflictObserved;

        internal int SlotClaimCount => Volatile.Read(ref _slotClaimCount);

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id == LockFreeCheckpointId.SlotClaimAfterParticipantRecheck)
            {
                Interlocked.Increment(ref _slotClaimCount);
            }

            if (entry.Id == LockFreeCheckpointId.DirectoryBeforeDescriptorPublication
                && Interlocked.CompareExchange(ref _beforeInsertObserved, 1, 0) == 0)
            {
                _beforeInsert.Set();
                _continueInsert.Wait();
                return;
            }

            if (entry.Id == LockFreeCheckpointId.ReserveAfterExistingLookup
                && Interlocked.CompareExchange(ref _conflictObserved, 1, 0) == 0)
            {
                _atConflict.Set();
                _continueConflict.Wait();
            }
        }

        internal bool WaitBeforeDirectoryInsert(TimeSpan timeout) =>
            _beforeInsert.Wait(timeout);

        internal void ContinueDirectoryInsert() => _continueInsert.Set();

        internal bool WaitAtFreshConflictResolution(TimeSpan timeout) =>
            _atConflict.Wait(timeout);

        internal void ContinueConflictResolution() => _continueConflict.Set();

        public void Dispose()
        {
            _continueInsert.Set();
            _continueConflict.Set();
            _beforeInsert.Dispose();
            _continueInsert.Dispose();
            _atConflict.Dispose();
            _continueConflict.Dispose();
        }
    }
}
