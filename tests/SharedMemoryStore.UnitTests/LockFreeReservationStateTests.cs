using System.Reflection;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeReservationStateTests
{
    [Fact]
    public void PublicationIntentFeatureAndWireAssignmentsAreStable()
    {
        Assert.Equal(1UL, LayoutV2Constants.SpillSummaryVersionedEmptyRequiredFeature);
        Assert.Equal(2UL, LayoutV2Constants.PublicationIntentRequiredFeature);
        Assert.Equal(4UL, LayoutV2Constants.PidNamespaceIdentityRequiredFeature);
        Assert.Equal(7UL, LayoutV2Constants.RequiredFeatures);
        Assert.Equal(0, (int)SlotPublicationIntent.None);
        Assert.Equal(1, (int)SlotPublicationIntent.ExplicitReservation);
        Assert.Equal(2, (int)SlotPublicationIntent.AtomicPublication);

        using MemoryStore store = CreateStore();
        Assert.Equal(7UL, store.ProtocolInfo.RequiredFeatures);
    }

    [Fact]
    public void FirstClaimCarriesParticipantAndStoreIdentityAndReuseAdvancesGeneration()
    {
        using var store = CreateStore(slotCount: 1);

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var first));
        var firstHandle = first.HandleForEngine;
        Assert.NotEqual(0UL, firstHandle.StoreId);
        Assert.NotEqual(0UL, firstHandle.ParticipantToken);
        Assert.NotEqual(0UL, firstHandle.SlotBinding);
        Assert.Equal(0, ParticipantToken.Decode(firstHandle.ParticipantToken, participantCount: 1).RecordIndex);
        Assert.Equal(0, IndexBinding.Decode(firstHandle.SlotBinding).SlotIndex);

        Assert.Equal(StoreStatus.Success, first.Abort());
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var second));
        var secondHandle = second.HandleForEngine;

        Assert.Equal(firstHandle.StoreId, secondHandle.StoreId);
        Assert.Equal(firstHandle.ParticipantToken, secondHandle.ParticipantToken);
        Assert.NotEqual(firstHandle.SlotBinding, secondHandle.SlotBinding);
        Assert.Equal(
            IndexBinding.Decode(firstHandle.SlotBinding).Generation + 1,
            IndexBinding.Decode(secondHandle.SlotBinding).Generation);

        Assert.Equal(StoreStatus.InvalidReservation, first.Advance(1));
        Assert.Equal(StoreStatus.InvalidReservation, first.Commit());
        Assert.Equal(StoreStatus.InvalidReservation, first.Abort());
        Assert.Equal(StoreStatus.Success, second.Abort());
    }

    [Fact]
    public void CopiedReservationHasOneExclusiveCursorAndRequiresExactAdvance()
    {
        using var store = CreateStore();
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, [9], out var reservation));
        var copy = reservation;

        new byte[] { 1, 2 }.CopyTo(reservation.GetSpan(2));
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(2, copy.BytesWritten);
        Assert.Equal(2, copy.RemainingBytes);
        Assert.Equal(StoreStatus.ReservationIncomplete, copy.Commit());

        new byte[] { 3, 4 }.CopyTo(copy.GetSpan(2));
        Assert.Equal(StoreStatus.Success, copy.Advance(2));
        Assert.Equal(4, reservation.BytesWritten);
        Assert.Equal(StoreStatus.ReservationWriteOutOfRange, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Commit());
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, copy.Commit());
    }

    [Fact]
    public void CommitAbortAndRecoveryEachFenceTheExactReservationGeneration()
    {
        using var store = CreateStore(slotCount: 1, enableRecovery: true);

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var committed));
        committed.GetSpan(1)[0] = 7;
        Assert.Equal(StoreStatus.Success, committed.Advance(1));
        Assert.Equal(StoreStatus.Success, committed.Commit());
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, committed.Commit());
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, committed.Abort());

        // A committed value still owns the only slot, so use independent stores for
        // abort and recovery terminal paths.
        using var abortStore = CreateStore(slotCount: 1, enableRecovery: true);
        Assert.Equal(StoreStatus.Success, abortStore.TryReserve([2], 1, default, out var aborted));
        Assert.Equal(StoreStatus.Success, aborted.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, aborted.Commit());

        Assert.Equal(StoreStatus.Success, abortStore.TryReserve([3], 1, default, out var recovered));
        Assert.Equal(
            StoreStatus.Success,
            abortStore.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
        Assert.Equal(1, report.RecoveredReservationCount);
        Assert.False(recovered.IsValid);
        Assert.Equal(StoreStatus.InvalidReservation, recovered.Advance(1));
        Assert.Equal(StoreStatus.InvalidReservation, recovered.Commit());
        Assert.Equal(StoreStatus.InvalidReservation, recovered.Abort());

        Assert.Equal(StoreStatus.Success, abortStore.TryReserve([4], 1, default, out var reused));
        Assert.NotEqual(recovered.HandleForEngine.SlotBinding, reused.HandleForEngine.SlotBinding);
        Assert.Equal(StoreStatus.Success, reused.Abort());
    }

    [Fact]
    public void TerminalGenerationPublishesRetiredInsteadOfWrappingToFree()
    {
        const long terminalGeneration = 0x1_ffff_ffffL;
        var slotTable = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeSlotTable",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(slotTable is not null, "The layout-v2 slot state machine is missing.");

        var advanceOrRetire = slotTable!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(method =>
                method.Name.Contains("Advance", StringComparison.OrdinalIgnoreCase)
                && method.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase)
                && method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(long)
                && (method.ReturnType == typeof(long) || method.ReturnType == typeof(ulong)));
        Assert.True(
            advanceOrRetire is not null,
            "LockFreeSlotTable needs a pure advance-or-retire transition seam for terminal rollover tests.");

        var nextFree = Convert.ToUInt64(advanceOrRetire!.Invoke(null, [terminalGeneration - 1]));
        var retired = Convert.ToUInt64(advanceOrRetire.Invoke(null, [terminalGeneration]));

        Assert.Equal(AtomicControlWord.EncodeSlot(state: 0, terminalGeneration, participantToken: 0), nextFree);
        Assert.Equal(AtomicControlWord.EncodeSlot(state: 7, terminalGeneration, participantToken: 0), retired);
    }

    [Fact]
    public void WritableProjectionEndsAfterCommitAbortRecoveryAndStoreDispose()
    {
        using var commitStore = CreateStore();
        Assert.Equal(StoreStatus.Success, commitStore.TryReserve([1], 1, default, out var committed));
        committed.GetSpan()[0] = 1;
        Assert.Equal(StoreStatus.Success, committed.Advance(1));
        Assert.Equal(StoreStatus.Success, committed.Commit());
        Assert.True(committed.GetSpan().IsEmpty);
        Assert.True(committed.DangerousGetMemory().IsEmpty);

        using var abortStore = CreateStore();
        Assert.Equal(StoreStatus.Success, abortStore.TryReserve([2], 1, default, out var aborted));
        Assert.Equal(StoreStatus.Success, aborted.Abort());
        Assert.True(aborted.GetSpan().IsEmpty);
        Assert.True(aborted.DangerousGetMemory().IsEmpty);

        using var recoveryStore = CreateStore(enableRecovery: true);
        Assert.Equal(StoreStatus.Success, recoveryStore.TryReserve([3], 1, default, out var recovered));
        Assert.Equal(
            StoreStatus.Success,
            recoveryStore.TryRecoverReservations(new ReservationRecoveryOptions(true), out _));
        Assert.True(recovered.GetSpan().IsEmpty);
        Assert.True(recovered.DangerousGetMemory().IsEmpty);

        var disposedStore = CreateStore();
        Assert.Equal(StoreStatus.Success, disposedStore.TryReserve([4], 1, default, out var disposed));
        disposedStore.Dispose();
        Assert.True(disposed.GetSpan().IsEmpty);
        Assert.True(disposed.DangerousGetMemory().IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, disposed.Advance(1));
    }

    [Fact]
    public void CancellationBeforeBindingLeavesNoKeyOrSlotOwnership()
    {
        using var store = CreateStore(slotCount: 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledWait = new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token);

        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryReserve([1], 1, default, canceledWait, out var canceled));
        Assert.False(canceled.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Fact]
    public void CancellationAfterBindingAndBeforeCommitPreservesThePendingLifecycle()
    {
        using var store = CreateStore(slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        reservation.GetSpan()[0] = 9;

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledWait = new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token);

        Assert.Equal(StoreStatus.OperationCanceled, reservation.Advance(1, canceledWait));
        Assert.True(reservation.IsValid);
        Assert.Equal(0, reservation.BytesWritten);
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.OperationCanceled, reservation.Commit(canceledWait));
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        cancellation.Cancel();
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(9, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void NoWaitMayOrderBindingAndCommitWhenThereIsNoContention()
    {
        using var store = CreateStore();

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([1], 1, default, StoreWaitOptions.NoWait, out var reservation));
        reservation.GetSpan()[0] = 3;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1, StoreWaitOptions.NoWait));
        Assert.Equal(StoreStatus.Success, reservation.Commit(StoreWaitOptions.NoWait));
    }

    private static MemoryStore CreateStore(
        int slotCount = 4,
        bool enableRecovery = true)
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-reservation-state-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 8,
            participantRecordCount: 1,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: enableRecovery);
        var status = MemoryStore.TryCreateOrOpen(options, out var store);

        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<MemoryStore>(store);
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), result.ProtocolInfo);
        Assert.Equal(2, result.ProtocolInfo.LayoutMajorVersion);
        return result;
    }
}
