using System.Reflection;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeReservationRecoveryTests
{
    [Fact]
    public void RecoverySurfaceSeparatesClassificationExactCasHelpingAndParticipantRetirement()
    {
        Type? recovery = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeRecovery",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(recovery is not null, "LockFreeRecovery is required for owner-classified record-local recovery.");

        MethodInfo[] methods = recovery!.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains(methods, method => method.Name.Contains("Reservation", StringComparison.OrdinalIgnoreCase)
            && method.Name.Contains("Recover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Classif", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Help", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Participant", StringComparison.OrdinalIgnoreCase)
            && (method.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase)
                || method.Name.Contains("Reclaim", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task CommitWinningBeforeRecoveryCasPreservesPublishedValueWithoutFailedCount()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        reservation.GetSpan()[0] = 7;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        StoreStatus recoveryStatus = default;
        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => recoveryStatus = store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, reservation.Commit());
        scheduler.Continue();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, recoveryStatus);
        Assert.Equal(1, report.ScannedReservationCount);
        Assert.Equal(0, report.RecoveredReservationCount);
        Assert.Equal(0, report.ActiveReservationCount);
        Assert.Equal(0, report.UnsupportedReservationCount);
        Assert.Equal(0, report.FailedRecoveryCount);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(7, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task AbortWinningBeforeRecoveryCasIsACompletedRaceNotAFailedRecovery()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, reservation.Abort());
        scheduler.Continue();
        Assert.Equal(StoreStatus.Success, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, report.ScannedReservationCount);
        Assert.Equal(0, report.RecoveredReservationCount);
        Assert.Equal(0, report.ActiveReservationCount);
        Assert.Equal(0, report.UnsupportedReservationCount);
        Assert.Equal(0, report.FailedRecoveryCount);
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task RecoveryWinningExactCasFencesConcurrentCommit()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        reservation.GetSpan()[0] = 9;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        scheduler.PauseAt(LockFreeCheckpointId.CommitBeforePublicationCas);

        StoreStatus commitStatus = default;
        var commit = Task.Run(() => commitStatus = reservation.Commit());
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
        Assert.Equal(1, report.RecoveredReservationCount);
        scheduler.Continue();
        await commit.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.InvalidReservation, commitStatus);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task RecoveryWinningExactCasFencesConcurrentAbortAndRemainsHelpable()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        scheduler.PauseAt(LockFreeCheckpointId.AbortBeforeAbortCas);

        StoreStatus abortStatus = default;
        var abort = Task.Run(() => abortStatus = reservation.Abort());
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
        Assert.Equal(1, report.RecoveredReservationCount);
        scheduler.Continue();
        await abort.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.InvalidReservation, abortStatus);
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task CurrentProcessInitializingWriterPausedBeforeMetadataCannotBeRecoveredOrDamageReuse()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        scheduler.PauseAt(LockFreeCheckpointId.SlotClaimAfterParticipantRecheck);

        StoreStatus reserveStatus = default;
        ValueReservation reservation = default;
        var reserve = Task.Run(() => reserveStatus = store.TryReserve([1], 1, default, out reservation));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
        Assert.Equal(new ReservationRecoveryReport(1, 0, 1, 0, 0), report);
        scheduler.Continue();
        await reserve.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, reserveStatus);
        Assert.True(reservation.IsValid);
        reservation.GetSpan()[0] = 0x5a;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Commit());
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(0x5a, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Theory]
    [InlineData((int)SlotPublicationIntent.None)]
    [InlineData(3)]
    public async Task DiscoverableInitializingUnknownPublicationIntentFailsClosed(int invalidIntent)
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        LockFreeSlotTable slots = ReadSlots(store);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);

        Task<(StoreStatus Status, ValueReservation Reservation)> reserve = Task.Run(() =>
        {
            StoreStatus status = store.TryReserve([1], 1, default, out ValueReservation reservation);
            return (status, reservation);
        });
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        ulong operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref slot.DirectoryOperation));
        DirectoryOperation operation = DirectoryOperation.Decode(operationRaw);
        Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(slot.Control));
        Assert.Equal(1, operation.Intent);
        Assert.Equal(SlotGeneration(slot.Control), operation.Generation);
        int validIntent = Volatile.Read(ref slot.PublicationIntent);
        Assert.Equal((int)SlotPublicationIntent.ExplicitReservation, validIntent);

        Volatile.Write(ref slot.PublicationIntent, invalidIntent);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(false),
                    out ReservationRecoveryReport report));
            Assert.Equal(new ReservationRecoveryReport(1, 0, 0, 0, 1), report);
            Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(slot.Control));
        }
        finally
        {
            Volatile.Write(ref slot.PublicationIntent, validIntent);
            scheduler.Continue();
        }

        (StoreStatus status, ValueReservation reservation) =
            await reserve.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort());
    }

    [Theory]
    [InlineData((int)SlotPublicationIntent.None)]
    [InlineData(3)]
    public void ReservedUnknownPublicationIntentFailsClosed(int invalidIntent)
    {
        using var store = CreateStore(slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        Assert.Equal(LockFreeSlotTable.ReservedState, SlotState(slot.Control));

        int validIntent = Volatile.Read(ref slot.PublicationIntent);
        Assert.Equal((int)SlotPublicationIntent.ExplicitReservation, validIntent);
        Volatile.Write(ref slot.PublicationIntent, invalidIntent);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(false),
                    out ReservationRecoveryReport report));
            Assert.Equal(new ReservationRecoveryReport(1, 0, 0, 0, 1), report);
            Assert.False(reservation.IsValid);
        }
        finally
        {
            Volatile.Write(ref slot.PublicationIntent, validIntent);
        }

        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort());
    }

    [Fact]
    public async Task PreMetadataInitializingIgnoresStaleUnknownPublicationIntent()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        LockFreeSlotTable slots = ReadSlots(store);
        scheduler.PauseAt(LockFreeCheckpointId.SlotClaimAfterParticipantRecheck);

        Task<(StoreStatus Status, ValueReservation Reservation)> reserve = Task.Run(() =>
        {
            StoreStatus status = store.TryReserve([1], 1, default, out ValueReservation reservation);
            return (status, reservation);
        });
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(slot.Control));
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
        Volatile.Write(ref slot.PublicationIntent, 3);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                out ReservationRecoveryReport report));
        Assert.Equal(new ReservationRecoveryReport(1, 0, 1, 0, 0), report);
        Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(slot.Control));

        scheduler.Continue();
        (StoreStatus status, ValueReservation reservation) =
            await reserve.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    [Theory]
    [InlineData(LockFreeSlotTable.AbortingState, (int)SlotPublicationIntent.None)]
    [InlineData(LockFreeSlotTable.AbortingState, 3)]
    [InlineData(LockFreeSlotTable.ReclaimingState, (int)SlotPublicationIntent.None)]
    [InlineData(LockFreeSlotTable.ReclaimingState, 3)]
    public void DiscoverableUnownedUnknownPublicationIntentFailsClosed(
        int unownedState,
        int invalidIntent)
    {
        using var store = CreateStore(slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(reservation.HandleForEngine));

        if (unownedState == LockFreeSlotTable.ReclaimingState)
        {
            long aborting = AtomicControlWord.LoadAcquire(ref slot.Control);
            long reclaiming = unchecked((long)AtomicControlWord.EncodeSlot(
                LockFreeSlotTable.ReclaimingState,
                SlotGeneration(aborting),
                participantToken: 0));
            Assert.Equal(
                aborting,
                AtomicControlWord.CompareExchange(ref slot.Control, reclaiming, aborting));
        }

        Assert.Equal(unownedState, SlotState(slot.Control));
        int validIntent = Volatile.Read(ref slot.PublicationIntent);
        Volatile.Write(ref slot.PublicationIntent, invalidIntent);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(false),
                    out ReservationRecoveryReport report));
            Assert.Equal(new ReservationRecoveryReport(0, 0, 0, 0, 1), report);
            Assert.Equal(unownedState, SlotState(slot.Control));
        }
        finally
        {
            Volatile.Write(ref slot.PublicationIntent, validIntent);
        }

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
        Assert.False(reservation.IsValid);
    }

    [Fact]
    public void UnreferencedOperationZeroAbortIgnoresStaleIntentAndReclaimsDirectly()
    {
        using var store = CreateStore(slotCount: 1);
        LockFreeSlotTable slots = ReadSlots(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        long free = AtomicControlWord.LoadAcquire(ref slot.Control);
        long generation = SlotGeneration(free);
        Assert.Equal(LayoutV2Constants.SlotFree, SlotState(free));
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));

        long aborting = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.AbortingState,
            generation,
            participantToken: 0));
        Assert.Equal(free, AtomicControlWord.CompareExchange(ref slot.Control, aborting, free));
        Volatile.Write(ref slot.PublicationIntent, 3);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                out ReservationRecoveryReport report));
        Assert.Equal(default, report);
        Assert.Equal(LayoutV2Constants.SlotFree, SlotState(slot.Control));
        Assert.Equal(generation + 1, SlotGeneration(slot.Control));
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
    }

    [Fact]
    public async Task CurrentDirectoryReferenceWithoutMetadataReadyMarkerFailsClosed()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        LockFreeSlotTable slots = ReadSlots(store);
        LockFreeKeyDirectory directory = ReadDirectory(store);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);

        Task<(StoreStatus Status, ValueReservation Reservation)> reserve = Task.Run(() =>
        {
            StoreStatus status = store.TryReserve([1], 1, default, out ValueReservation reservation);
            return (status, reservation);
        });
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        ulong binding = slot.DirectoryBinding;
        ulong operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref slot.DirectoryOperation));
        Assert.NotEqual(0UL, operationRaw);
        Assert.True(HasCanonicalMutation(directory, binding));

        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, 0);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(false),
                    out ReservationRecoveryReport report));
            Assert.Equal(new ReservationRecoveryReport(1, 0, 0, 0, 1), report);
            Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(slot.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(
                ref slot.DirectoryOperation,
                unchecked((long)operationRaw));
            scheduler.Continue();
        }

        (StoreStatus status, ValueReservation reservation) =
            await reserve.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort());
    }

    [Fact]
    public void CurrentDirectoryCellWithoutMetadataReadyMarkerFailsClosed()
    {
        using var store = CreateStore(slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        LockFreeSlotTable slots = ReadSlots(store);
        LockFreeKeyDirectory directory = ReadDirectory(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        ulong binding = slot.DirectoryBinding;
        Assert.False(HasCanonicalMutation(directory, binding));
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(reservation.HandleForEngine));

        long operation = AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation);
        long location = AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation);
        Assert.NotEqual(0, operation);
        Assert.NotEqual(0, location);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, 0);
        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, 0);
        try
        {
            Assert.Equal(
                StoreStatus.CorruptStore,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(false),
                    out ReservationRecoveryReport report));
            Assert.Equal(new ReservationRecoveryReport(0, 0, 0, 0, 1), report);
            Assert.Equal(LockFreeSlotTable.AbortingState, SlotState(slot.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, location);
            AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, operation);
        }

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
        Assert.False(reservation.IsValid);
    }

    [Fact]
    public void InitializingRecoveryRequiresStaleOwnerOrPublishedQuiescentHandoff()
    {
        ParticipantClassification currentActive = Classification(
            ParticipantClassificationKind.CurrentProcess,
            LayoutV2Constants.ParticipantActive);
        ParticipantClassification currentClosing = Classification(
            ParticipantClassificationKind.CurrentProcess,
            LayoutV2Constants.ParticipantClosing);
        ParticipantClassification liveRecovering = Classification(
            ParticipantClassificationKind.Live,
            LayoutV2Constants.ParticipantRecovering);
        ParticipantClassification stale = Classification(
            ParticipantClassificationKind.Stale,
            LayoutV2Constants.ParticipantActive);
        ParticipantClassification inconsistentClosing = Classification(
            ParticipantClassificationKind.Inconsistent,
            LayoutV2Constants.ParticipantClosing);

        Assert.False(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.InitializingState,
            currentActive,
            recoverCurrentProcessReservations: true));
        Assert.True(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.ReservedState,
            currentActive,
            recoverCurrentProcessReservations: true));
        Assert.True(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.InitializingState,
            currentClosing,
            recoverCurrentProcessReservations: false));
        Assert.True(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.InitializingState,
            liveRecovering,
            recoverCurrentProcessReservations: false));
        Assert.True(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.InitializingState,
            stale,
            recoverCurrentProcessReservations: false));
        Assert.False(LockFreeRecovery.CanRecoverReservation(
            LockFreeSlotTable.InitializingState,
            inconsistentClosing,
            recoverCurrentProcessReservations: true));
    }

    [Fact]
    public async Task CancellationBeforeRecoveryCasPreservesReservationAndReturnsPartialCounts()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        StoreStatus recoveryStatus = default;
        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => recoveryStatus = store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.OperationCanceled, recoveryStatus);
        Assert.Equal(1, report.ScannedReservationCount);
        Assert.Equal(0, report.RecoveredReservationCount);
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    [Fact]
    public async Task DeadlineBeforeRecoveryCasPreservesReservationAndReturnsPartialCounts()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        StoreStatus recoveryStatus = default;
        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => recoveryStatus = store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50)),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.StoreBusy, recoveryStatus);
        Assert.Equal(1, report.ScannedReservationCount);
        Assert.Equal(0, report.RecoveredReservationCount);
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    [Fact]
    public async Task CancellationAfterRecoveryCasDoesNotUndoRecoveredOutcome()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);

        StoreStatus recoveryStatus = default;
        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => recoveryStatus = store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, recoveryStatus);
        Assert.Equal(1, report.RecoveredReservationCount);
        Assert.False(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task CancellationAfterAbortOwnershipReleaseReturnsSuccessAndLeavesHelpableCleanup()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.AbortAfterOwnershipReleaseCas);

        StoreStatus abortStatus = default;
        var abort = Task.Run(() => abortStatus = reservation.Abort(
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token)));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();
        await abort.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, abortStatus);
        Assert.False(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task DeadlineAfterRecoveryCasDoesNotUndoRecoveredOutcome()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);

        StoreStatus recoveryStatus = default;
        ReservationRecoveryReport report = default;
        var recovery = Task.Run(() => recoveryStatus = store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50)),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, recoveryStatus);
        Assert.Equal(1, report.RecoveredReservationCount);
        Assert.False(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public async Task DeadlineDuringLaterPublishedHelperPreservesEarlierRecoveredOutcome()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var first));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var second));

        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        var slots = Assert.IsType<LockFreeSlotTable>(engine.GetType()
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(second.HandleForEngine));

        // Slot zero is recovered and ordered first. Slot one is already
        // Aborting; pause its second unlink validation until the public budget
        // expires to verify the earlier durable recovery is not rewritten.
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterLocationValidation, occurrence: 2);
        ReservationRecoveryReport report = default;
        Task<StoreStatus> recovery = Task.Run(() => store.TryRecoverReservations(
            new ReservationRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50)),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();

        Assert.Equal(StoreStatus.Success, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, report.RecoveredReservationCount);
        Assert.False(first.IsValid);
        Assert.False(second.IsValid);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                StoreWaitOptions.Infinite,
                out _));
        Assert.Equal(StoreStatus.Success, store.TryReserve([3], 1, default, out var replacement1));
        Assert.Equal(StoreStatus.Success, store.TryReserve([4], 1, default, out var replacement2));
        Assert.Equal(StoreStatus.Success, replacement1.Abort());
        Assert.Equal(StoreStatus.Success, replacement2.Abort());
    }

    [Fact]
    public void ReportCountsDistinguishLiveCurrentRecoveryAndRestoreAllCapacity()
    {
        using var store = CreateStore(slotCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var first));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var second));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out var active));
        Assert.Equal(new ReservationRecoveryReport(2, 0, 2, 0, 0), active);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var recovered));
        Assert.Equal(new ReservationRecoveryReport(2, 2, 0, 0, 0), recovered);
        Assert.False(first.IsValid);
        Assert.False(second.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryReserve([3], 1, default, out var replacement1));
        Assert.Equal(StoreStatus.Success, store.TryReserve([4], 1, default, out var replacement2));
        Assert.Equal(StoreStatus.Success, replacement1.Abort());
        Assert.Equal(StoreStatus.Success, replacement2.Abort());
    }

    private static MemoryStore CreateInstrumentedStore(ControlledLockFreeScheduler scheduler, int slotCount)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-reservation-recovery-{Guid.NewGuid():N}", slotCount),
            scheduler.CreateInstrumentedCheckpoint(),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateStore(int slotCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options($"sms-v2-reservation-recovery-{Guid.NewGuid():N}", slotCount),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static LockFreeSlotTable ReadSlots(MemoryStore store)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return Assert.IsType<LockFreeSlotTable>(engine.GetType()
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
    }

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return Assert.IsType<LockFreeKeyDirectory>(engine.GetType()
            .GetField("_directory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
    }

    private static bool HasCanonicalMutation(LockFreeKeyDirectory directory, ulong binding)
    {
        StoreLayoutV2 layout = (StoreLayoutV2)typeof(LockFreeKeyDirectory)
            .GetField("_layout", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(directory)!;
        for (var bucketIndex = 0; bucketIndex < layout.PrimaryBucketCount; bucketIndex++)
        {
            if (directory.ReadCanonicalMutation(bucketIndex) == binding)
            {
                return true;
            }
        }

        return false;
    }

    private static int SlotState(long control) => (int)(unchecked((ulong)control) & 0x7UL);

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static SharedMemoryStoreOptions Options(string name, int slotCount) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

    private static ParticipantClassification Classification(
        ParticipantClassificationKind kind,
        int participantState) =>
        new(
            kind,
            new ParticipantIncarnation(
                RecordIndex: 0,
                Generation: 1,
                Token: 1,
                State: participantState,
                ProcessId: Environment.ProcessId,
                IdentityKind: LayoutV2Constants.IdentityUnknown,
                ProcessStartValue: 0,
                OpenSequence: 1,
                PidNamespaceId: 0,
                ReservedValue: 0,
                Control: 0));
}
