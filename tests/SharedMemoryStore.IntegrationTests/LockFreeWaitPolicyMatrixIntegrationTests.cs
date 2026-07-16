using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeWaitPolicyMatrixIntegrationTests
{
    private const int SlotCount = 12;
    private const int LeaseRecordCount = 16;
    private static readonly TimeSpan BoundaryTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan CompletionAllowance = TimeSpan.FromMilliseconds(250);

    [Theory]
    [InlineData(WaitPolicyKind.NoWait)]
    [InlineData(WaitPolicyKind.Finite)]
    [InlineData(WaitPolicyKind.Infinite)]
    [Trait("Category", "Integration")]
    public void EveryValidWaitPolicyCompletesTheFullPublicSurface(WaitPolicyKind policy)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        StoreWaitOptions wait = Wait(policy);
        SharedMemoryStoreOptions options = Options(
            $"sms-v2-valid-wait-{policy}-{Guid.NewGuid():N}",
            OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, wait, out MemoryStore? candidate));
        MemoryStore store = Assert.IsType<MemoryStore>(candidate);

        LockFreeNoOperationLockIntegrationTests.ExerciseCompleteSteadyStateSurface(store, wait);
        store.Dispose();

        Assert.Equal(StoreStatus.StoreDisposed, store.TryPublish(Key(90), [1], default, wait));
        Assert.Equal(StoreStatus.StoreDisposed, store.TryGetDiagnostics(wait, out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void AlreadyCanceledPolicyWinsAcrossEveryWaitAwareOperationAndLeaksNothing()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = new StoreWaitOptions(TimeSpan.FromSeconds(5), cancellation.Token);

        SharedMemoryStoreOptions canceledOpen = Options(
            $"sms-v2-canceled-open-{Guid.NewGuid():N}",
            OpenMode.CreateNew);
        Assert.Equal(
            StoreOpenStatus.OperationCanceled,
            MemoryStore.TryCreateOrOpen(canceledOpen, canceled, out MemoryStore? rejectedOpen));
        Assert.Null(rejectedOpen);

        using MemoryStore store = CreateStore(
            $"sms-v2-canceled-surface-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            StoreWaitOptions.Default);

        Assert.Equal(StoreStatus.OperationCanceled, store.TryPublish(Key(1), [1], default, canceled));
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryPublishSegments(
                Key(2),
                new ReadOnlySequence<byte>(new byte[] { 2, 3 }),
                default,
                canceled,
                out long copied));
        Assert.Equal(0, copied);
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryReserve(Key(3), 1, default, canceled, out ValueReservation rejectedReservation));
        Assert.False(rejectedReservation.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(4), 1, default, out ValueReservation reservation));
        reservation.GetSpan()[0] = 4;
        Assert.Equal(StoreStatus.OperationCanceled, reservation.Advance(1, canceled));
        Assert.Equal(0, reservation.BytesWritten);
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.OperationCanceled, reservation.Commit(canceled));
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.OperationCanceled, reservation.Abort(canceled));
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.OperationCanceled, store.TryAcquire(Key(4), canceled, out ValueLease rejectedLease));
        Assert.False(rejectedLease.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(Key(4), out ValueLease lease));
        Assert.Equal(StoreStatus.OperationCanceled, lease.Release(canceled));
        Assert.True(lease.IsValid);
        Assert.Equal(StoreStatus.Success, lease.Release());

        Assert.Equal(StoreStatus.OperationCanceled, store.TryRemove(Key(4), canceled));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(Key(4), out ValueLease preserved));
        Assert.Equal(StoreStatus.Success, preserved.Release());

        Assert.Equal(StoreStatus.Success, store.TryAcquire(Key(4), out ValueLease recoveryLease));
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), canceled, out LeaseRecoveryReport leaseReport));
        Assert.Equal(default, leaseReport);
        Assert.True(recoveryLease.IsValid);
        Assert.Equal(StoreStatus.Success, recoveryLease.Release());

        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(5), 1, default, out ValueReservation recoveryReservation));
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                canceled,
                out ReservationRecoveryReport reservationReport));
        Assert.Equal(default, reservationReport);
        Assert.True(recoveryReservation.IsValid);
        Assert.Equal(StoreStatus.Success, recoveryReservation.Abort());

        Assert.Equal(StoreStatus.OperationCanceled, store.TryGetDiagnostics(canceled, out _));

        AssertRemovedAndReclaimed(store, Key(4));
        AssertAllValueAndLeaseCapacityReusable(store);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LargeCapacityScansHonorNoWaitFiniteAndCancellationBounds()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int largeCount = 32_768;
        string name = $"sms-v2-large-wait-budget-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: largeCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 1,
            maxKeyBytes: 8,
            leaseRecordCount: largeCount,
            participantRecordCount: 128,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var delayCommit = 0;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id == LockFreeCheckpointId.CommitBeforePublicationCas
                && Volatile.Read(ref delayCommit) != 0)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25));
            }
        });
        Assert.Equal(
            StoreOpenStatus.Success,
            LockFreeInstrumentedStoreFactory.TryCreateOrOpen(options, checkpoint, out MemoryStore? opened));
        using MemoryStore store = Assert.IsType<MemoryStore>(opened);

        AssertBoundedStatus(
            () => store.TryGetDiagnostics(StoreWaitOptions.NoWait, out _),
            StoreStatus.StoreBusy,
            TimeSpan.Zero);
        AssertBoundedStatus(
            () => store.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                StoreWaitOptions.NoWait,
                out _),
            StoreStatus.StoreBusy,
            TimeSpan.Zero);
        AssertBoundedStatus(
            () => store.TryRecoverLeases(
                new LeaseRecoveryOptions(false),
                StoreWaitOptions.NoWait,
                out _),
            StoreStatus.StoreBusy,
            TimeSpan.Zero);

        var finite = new StoreWaitOptions(TimeSpan.FromTicks(1));
        AssertBoundedStatus(
            () => store.TryGetDiagnostics(finite, out _),
            StoreStatus.StoreBusy,
            finite.Timeout);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledInfinite = new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token);
        AssertBoundedStatus(
            () => store.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                canceledInfinite,
                out _),
            StoreStatus.OperationCanceled,
            TimeSpan.Zero);

        var publishDeadline = new StoreWaitOptions(TimeSpan.FromMilliseconds(1));
        Volatile.Write(ref delayCommit, 1);
        try
        {
            AssertBoundedStatus(
                () => store.TryPublish([0x41], [0x42], default, publishDeadline),
                StoreStatus.StoreBusy,
                publishDeadline.Timeout);
        }
        finally
        {
            Volatile.Write(ref delayCommit, 0);
        }

        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot final));
        Assert.Equal(0, final.InitializingSlotCount);
        Assert.Equal(0, final.ReservedSlotCount);
        Assert.Equal(0, final.ClaimingLeaseCount);
        Assert.Equal(0, final.ReclaimingSlotCount);
        Assert.Equal(0, final.PublishedSlotCount);
        Assert.Equal(largeCount, final.FreeSlotCount);
    }

    [Theory]
    [InlineData(BoundaryOperation.Publish)]
    [InlineData(BoundaryOperation.SegmentedPublish)]
    [InlineData(BoundaryOperation.Reserve)]
    [InlineData(BoundaryOperation.Advance)]
    [InlineData(BoundaryOperation.Commit)]
    [InlineData(BoundaryOperation.Abort)]
    [InlineData(BoundaryOperation.Acquire)]
    [InlineData(BoundaryOperation.Release)]
    [InlineData(BoundaryOperation.Remove)]
    [InlineData(BoundaryOperation.Diagnostics)]
    [InlineData(BoundaryOperation.LeaseRecovery)]
    [InlineData(BoundaryOperation.ReservationRecovery)]
    [Trait("Category", "Integration")]
    public void CancellationImmediatelyBeforeOrderingReturnsCanceledWithoutOwnerLeak(
        BoundaryOperation operation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        using var controller = BoundaryController.CancelAt(BeforeCheckpoint(operation), cancellation);
        using BoundaryContext context = Prepare(operation, controller);
        var wait = new StoreWaitOptions(TimeSpan.FromSeconds(5), cancellation.Token);

        StoreStatus status = Invoke(operation, context, wait);

        Assert.True(controller.WasReached, $"{operation} did not reach its pre-ordering checkpoint.");
        Assert.Equal(StoreStatus.OperationCanceled, status);
        AssertBeforeOrderingStateAndCleanup(operation, context);
        AssertNoOwnerControlledLeakage(context.Store);
    }

    [Theory]
    [InlineData(BoundaryOperation.Publish)]
    [InlineData(BoundaryOperation.SegmentedPublish)]
    [InlineData(BoundaryOperation.Reserve)]
    [InlineData(BoundaryOperation.Advance)]
    [InlineData(BoundaryOperation.Commit)]
    [InlineData(BoundaryOperation.Abort)]
    [InlineData(BoundaryOperation.Acquire)]
    [InlineData(BoundaryOperation.Release)]
    [InlineData(BoundaryOperation.Remove)]
    [InlineData(BoundaryOperation.Diagnostics)]
    [InlineData(BoundaryOperation.LeaseRecovery)]
    [InlineData(BoundaryOperation.ReservationRecovery)]
    [Trait("Category", "Integration")]
    public async Task DeadlineImmediatelyBeforeOrderingReturnsBusyWithinLimitAndLeaksNothing(
        BoundaryOperation operation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = BoundaryController.PauseAt(BeforeCheckpoint(operation));
        using BoundaryContext context = Prepare(operation, controller);
        var wait = new StoreWaitOptions(BoundaryTimeout);

        Task<TimedInvocation> pending = Task.Run(() => InvokeTimed(operation, context, wait));
        Assert.True(
            controller.WaitUntilPaused(TimeSpan.FromSeconds(5)),
            $"{operation} did not reach its pre-ordering checkpoint.");
        Thread.Sleep(BoundaryTimeout + TimeSpan.FromMilliseconds(30));
        controller.Continue();
        TimedInvocation result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.StoreBusy, result.Status);
        AssertWithinBound(result.Elapsed, BoundaryTimeout, operation);
        AssertBeforeOrderingStateAndCleanup(operation, context);
        AssertNoOwnerControlledLeakage(context.Store);
    }

    [Theory]
    [InlineData(BoundaryOperation.Publish)]
    [InlineData(BoundaryOperation.SegmentedPublish)]
    [InlineData(BoundaryOperation.Reserve)]
    [InlineData(BoundaryOperation.Advance)]
    [InlineData(BoundaryOperation.Commit)]
    [InlineData(BoundaryOperation.Abort)]
    [InlineData(BoundaryOperation.Acquire)]
    [InlineData(BoundaryOperation.Release)]
    [InlineData(BoundaryOperation.Remove)]
    [InlineData(BoundaryOperation.Reclaim)]
    [InlineData(BoundaryOperation.Diagnostics)]
    [InlineData(BoundaryOperation.LeaseRecovery)]
    [InlineData(BoundaryOperation.ReservationRecovery)]
    [Trait("Category", "Integration")]
    public void CancellationAfterOrderingDoesNotRewriteTheCompletedOutcome(BoundaryOperation operation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        using var controller = BoundaryController.CancelAt(AfterCheckpoint(operation), cancellation);
        using BoundaryContext context = Prepare(operation, controller);
        var wait = new StoreWaitOptions(TimeSpan.FromSeconds(5), cancellation.Token);

        StoreStatus status = Invoke(operation, context, wait);

        Assert.True(controller.WasReached, $"{operation} did not reach its post-ordering checkpoint.");
        Assert.Contains(status, CompletedOutcomes(operation));
        Assert.NotEqual(StoreStatus.OperationCanceled, status);
        AssertAfterOrderingStateAndCleanup(operation, context);
        AssertNoOwnerControlledLeakage(context.Store);
    }

    [Theory]
    [InlineData(BoundaryOperation.Publish)]
    [InlineData(BoundaryOperation.SegmentedPublish)]
    [InlineData(BoundaryOperation.Reserve)]
    [InlineData(BoundaryOperation.Advance)]
    [InlineData(BoundaryOperation.Commit)]
    [InlineData(BoundaryOperation.Abort)]
    [InlineData(BoundaryOperation.Acquire)]
    [InlineData(BoundaryOperation.Release)]
    [InlineData(BoundaryOperation.Remove)]
    [InlineData(BoundaryOperation.Reclaim)]
    [InlineData(BoundaryOperation.Diagnostics)]
    [InlineData(BoundaryOperation.LeaseRecovery)]
    [InlineData(BoundaryOperation.ReservationRecovery)]
    [Trait("Category", "Integration")]
    public async Task DeadlineAfterOrderingDoesNotRewriteOutcomeAndStillReturnsWithinAllowance(
        BoundaryOperation operation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = BoundaryController.PauseAt(AfterCheckpoint(operation));
        using BoundaryContext context = Prepare(operation, controller);
        var wait = new StoreWaitOptions(BoundaryTimeout);

        Task<TimedInvocation> pending = Task.Run(() => InvokeTimed(operation, context, wait));
        Assert.True(
            controller.WaitUntilPaused(TimeSpan.FromSeconds(5)),
            $"{operation} did not reach its post-ordering checkpoint.");
        Thread.Sleep(BoundaryTimeout + TimeSpan.FromMilliseconds(30));
        controller.Continue();
        TimedInvocation result = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(result.Status, CompletedOutcomes(operation));
        Assert.NotEqual(StoreStatus.StoreBusy, result.Status);
        AssertWithinBound(result.Elapsed, BoundaryTimeout, operation);
        AssertAfterOrderingStateAndCleanup(operation, context);
        AssertNoOwnerControlledLeakage(context.Store);
    }

    [Theory]
    [InlineData(WaitPolicyKind.NoWait)]
    [InlineData(WaitPolicyKind.Finite)]
    [Trait("Category", "Integration")]
    public void ContendedOpenReturnsBusyWithinTheSelectedBound(WaitPolicyKind policy)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-open-bound-{policy}-{Guid.NewGuid():N}";
        using MemoryStore owner = CreateStore(name, OpenMode.CreateNew, StoreWaitOptions.Default);
        using var blocker = new NamedSynchronizationBlocker(name);
        StoreWaitOptions wait = policy == WaitPolicyKind.NoWait
            ? StoreWaitOptions.NoWait
            : new StoreWaitOptions(BoundaryTimeout);
        var stopwatch = Stopwatch.StartNew();

        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting),
            wait,
            out MemoryStore? rejected);
        stopwatch.Stop();

        Assert.Equal(StoreOpenStatus.StoreBusy, status);
        Assert.Null(rejected);
        AssertWithinBound(stopwatch.Elapsed, wait.Timeout, BoundaryOperation.Open);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ContendedOpenObservesCancellationBeforeItsFiniteTimeout()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-open-cancel-{Guid.NewGuid():N}";
        using MemoryStore owner = CreateStore(name, OpenMode.CreateNew, StoreWaitOptions.Default);
        using var blocker = new NamedSynchronizationBlocker(name);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting),
            new StoreWaitOptions(TimeSpan.FromSeconds(5), cancellation.Token),
            out MemoryStore? rejected);
        stopwatch.Stop();

        Assert.Equal(StoreOpenStatus.OperationCanceled, status);
        Assert.Null(rejected);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Cancellation took {stopwatch.Elapsed}.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task InfiniteOpenWaitsUntilTheColdSynchronizationIsReleased()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-open-infinite-{Guid.NewGuid():N}";
        using MemoryStore owner = CreateStore(name, OpenMode.CreateNew, StoreWaitOptions.Default);
        var blocker = new NamedSynchronizationBlocker(name);
        MemoryStore? opened = null;
        Task<StoreOpenStatus> pending = Task.Run(() => MemoryStore.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting),
            StoreWaitOptions.Infinite,
            out opened));

        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(75));
            Assert.False(pending.IsCompleted, "Infinite open did not wait for held cold synchronization.");
        }
        finally
        {
            // Always release the owner-thread-affine Windows mutex (and the
            // equivalent Linux local/file lock) before unwinding the owner.
            blocker.Dispose();
        }

        Assert.Equal(StoreOpenStatus.Success, await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<MemoryStore>(opened).Dispose();
    }

    private static BoundaryContext Prepare(BoundaryOperation operation, BoundaryController controller)
    {
        MemoryStore store = CreateInstrumentedStore(controller);
        var context = new BoundaryContext(store, Key(1));
        switch (operation)
        {
            case BoundaryOperation.Advance:
                Assert.Equal(StoreStatus.Success, store.TryReserve(context.Key, 1, default, out context.Reservation));
                context.Reservation.GetSpan()[0] = 1;
                break;
            case BoundaryOperation.Commit:
                Assert.Equal(StoreStatus.Success, store.TryReserve(context.Key, 1, default, out context.Reservation));
                context.Reservation.GetSpan()[0] = 1;
                Assert.Equal(StoreStatus.Success, context.Reservation.Advance(1));
                break;
            case BoundaryOperation.Abort:
                Assert.Equal(StoreStatus.Success, store.TryReserve(context.Key, 1, default, out context.Reservation));
                break;
            case BoundaryOperation.Acquire:
            case BoundaryOperation.Remove:
            case BoundaryOperation.Reclaim:
                Assert.Equal(StoreStatus.Success, store.TryPublish(context.Key, [1]));
                break;
            case BoundaryOperation.Release:
            case BoundaryOperation.LeaseRecovery:
                Assert.Equal(StoreStatus.Success, store.TryPublish(context.Key, [1]));
                Assert.Equal(StoreStatus.Success, store.TryAcquire(context.Key, out context.Lease));
                break;
            case BoundaryOperation.ReservationRecovery:
                Assert.Equal(StoreStatus.Success, store.TryReserve(context.Key, 1, default, out context.Reservation));
                break;
        }

        return context;
    }

    private static StoreStatus Invoke(
        BoundaryOperation operation,
        BoundaryContext context,
        StoreWaitOptions wait)
    {
        return operation switch
        {
            BoundaryOperation.Publish => context.Store.TryPublish(context.Key, [1], default, wait),
            BoundaryOperation.SegmentedPublish => context.Store.TryPublishSegments(
                context.Key,
                new ReadOnlySequence<byte>(new byte[] { 1, 2 }),
                default,
                wait,
                out context.CopiedBytes),
            BoundaryOperation.Reserve => context.Store.TryReserve(
                context.Key,
                1,
                default,
                wait,
                out context.Reservation),
            BoundaryOperation.Advance => context.Reservation.Advance(1, wait),
            BoundaryOperation.Commit => context.Reservation.Commit(wait),
            BoundaryOperation.Abort => context.Reservation.Abort(wait),
            BoundaryOperation.Acquire => context.Store.TryAcquire(context.Key, wait, out context.Lease),
            BoundaryOperation.Release => context.Lease.Release(wait),
            BoundaryOperation.Remove or BoundaryOperation.Reclaim => context.Store.TryRemove(context.Key, wait),
            BoundaryOperation.Diagnostics => context.Store.TryGetDiagnostics(wait, out context.Diagnostics),
            BoundaryOperation.LeaseRecovery => context.Store.TryRecoverLeases(
                new LeaseRecoveryOptions(true),
                wait,
                out context.LeaseRecoveryReport),
            BoundaryOperation.ReservationRecovery => context.Store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                wait,
                out context.ReservationRecoveryReport),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    private static TimedInvocation InvokeTimed(
        BoundaryOperation operation,
        BoundaryContext context,
        StoreWaitOptions wait)
    {
        var stopwatch = Stopwatch.StartNew();
        StoreStatus status = Invoke(operation, context, wait);
        stopwatch.Stop();
        return new TimedInvocation(status, stopwatch.Elapsed);
    }

    private static LockFreeCheckpointId BeforeCheckpoint(BoundaryOperation operation) => operation switch
    {
        BoundaryOperation.Publish or BoundaryOperation.SegmentedPublish =>
            LockFreeCheckpointId.CommitBeforePublicationCas,
        BoundaryOperation.Reserve =>
            LockFreeCheckpointId.DirectoryBeforeDescriptorPublication,
        BoundaryOperation.Advance => LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas,
        BoundaryOperation.Commit => LockFreeCheckpointId.CommitBeforePublicationCas,
        BoundaryOperation.Abort => LockFreeCheckpointId.AbortBeforeAbortCas,
        BoundaryOperation.Acquire => LockFreeCheckpointId.AcquireBeforeLeaseClaimCas,
        BoundaryOperation.Release => LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas,
        BoundaryOperation.Remove => LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas,
        BoundaryOperation.Diagnostics => LockFreeCheckpointId.DiagnosticsBeforeBoundedScan,
        BoundaryOperation.LeaseRecovery or BoundaryOperation.ReservationRecovery =>
            LockFreeCheckpointId.RecoveryBeforeOwnerClassification,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static LockFreeCheckpointId AfterCheckpoint(BoundaryOperation operation) => operation switch
    {
        BoundaryOperation.Publish => LockFreeCheckpointId.PublishAfterCommitPublication,
        BoundaryOperation.SegmentedPublish => LockFreeCheckpointId.CommitAfterPublicationCas,
        BoundaryOperation.Reserve => LockFreeCheckpointId.ReserveAfterReservationPublication,
        BoundaryOperation.Advance => LockFreeCheckpointId.AdvanceAfterBytesAdvancedCas,
        BoundaryOperation.Commit => LockFreeCheckpointId.CommitAfterPublicationCas,
        BoundaryOperation.Abort => LockFreeCheckpointId.AbortAfterUnlinkCompletion,
        BoundaryOperation.Acquire => LockFreeCheckpointId.AcquireAfterPublishedRevalidation,
        BoundaryOperation.Release => LockFreeCheckpointId.ReleaseAfterRecordRecycle,
        BoundaryOperation.Remove => LockFreeCheckpointId.RemoveAfterLeaseClassification,
        BoundaryOperation.Reclaim => LockFreeCheckpointId.ReclaimAfterGenerationAdvance,
        BoundaryOperation.Diagnostics => LockFreeCheckpointId.DiagnosticsAfterSnapshotAssembly,
        BoundaryOperation.LeaseRecovery or BoundaryOperation.ReservationRecovery =>
            LockFreeCheckpointId.RecoveryAfterExactRecoveryCas,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static StoreStatus[] CompletedOutcomes(BoundaryOperation operation) => operation switch
    {
        BoundaryOperation.Remove or BoundaryOperation.Reclaim =>
            [StoreStatus.Success, StoreStatus.RemovePending],
        _ => [StoreStatus.Success]
    };

    private static void AssertBeforeOrderingStateAndCleanup(
        BoundaryOperation operation,
        BoundaryContext context)
    {
        switch (operation)
        {
            case BoundaryOperation.Publish:
            case BoundaryOperation.SegmentedPublish:
            case BoundaryOperation.Reserve:
                Assert.Equal(StoreStatus.NotFound, context.Store.TryAcquire(context.Key, out _));
                break;
            case BoundaryOperation.Advance:
                Assert.True(context.Reservation.IsValid);
                Assert.Equal(0, context.Reservation.BytesWritten);
                Assert.Equal(StoreStatus.Success, context.Reservation.Abort());
                break;
            case BoundaryOperation.Commit:
            case BoundaryOperation.Abort:
                Assert.True(context.Reservation.IsValid);
                Assert.Equal(StoreStatus.Success, context.Reservation.Abort());
                break;
            case BoundaryOperation.Acquire:
                Assert.False(context.Lease.IsValid);
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.Release:
                Assert.True(context.Lease.IsValid);
                Assert.Equal(StoreStatus.Success, context.Lease.Release());
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.Remove:
                Assert.Equal(StoreStatus.Success, context.Store.TryAcquire(context.Key, out ValueLease preserved));
                Assert.Equal(StoreStatus.Success, preserved.Release());
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.LeaseRecovery:
                Assert.True(context.Lease.IsValid);
                Assert.Equal(StoreStatus.Success, context.Lease.Release());
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.ReservationRecovery:
                Assert.True(context.Reservation.IsValid);
                Assert.Equal(StoreStatus.Success, context.Reservation.Abort());
                break;
        }
    }

    private static void AssertAfterOrderingStateAndCleanup(
        BoundaryOperation operation,
        BoundaryContext context)
    {
        switch (operation)
        {
            case BoundaryOperation.Publish:
            case BoundaryOperation.SegmentedPublish:
            case BoundaryOperation.Commit:
                Assert.Equal(StoreStatus.Success, context.Store.TryAcquire(context.Key, out ValueLease published));
                Assert.Equal(StoreStatus.Success, published.Release());
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.Reserve:
                Assert.True(context.Reservation.IsValid);
                Assert.Equal(StoreStatus.Success, context.Reservation.Abort());
                break;
            case BoundaryOperation.Advance:
                Assert.True(context.Reservation.IsValid);
                Assert.Equal(1, context.Reservation.BytesWritten);
                Assert.Equal(StoreStatus.Success, context.Reservation.Abort());
                break;
            case BoundaryOperation.Abort:
                Assert.False(context.Reservation.IsValid);
                break;
            case BoundaryOperation.Acquire:
                Assert.True(context.Lease.IsValid);
                Assert.Equal(StoreStatus.Success, context.Lease.Release());
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.Release:
                Assert.False(context.Lease.IsValid);
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.Remove:
            case BoundaryOperation.Reclaim:
                Assert.Equal(StoreStatus.NotFound, context.Store.TryAcquire(context.Key, out _));
                break;
            case BoundaryOperation.LeaseRecovery:
                Assert.False(context.Lease.IsValid);
                Assert.Equal(1, context.LeaseRecoveryReport.RecoveredLeaseCount);
                AssertRemovedAndReclaimed(context.Store, context.Key);
                break;
            case BoundaryOperation.ReservationRecovery:
                Assert.False(context.Reservation.IsValid);
                Assert.Equal(1, context.ReservationRecoveryReport.RecoveredReservationCount);
                break;
            case BoundaryOperation.Diagnostics:
                Assert.Equal(StoreProfile.LockFree, context.Diagnostics.Profile);
                break;
        }
    }

    private static void AssertNoOwnerControlledLeakage(MemoryStore store)
    {
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var snapshot));
        Assert.Equal(0, snapshot.ActiveLeaseCount);
        Assert.Equal(0, snapshot.ActiveReservationCount);
        Assert.Equal(0, snapshot.InitializingSlotCount);
        Assert.Equal(0, snapshot.ReservedSlotCount);
        Assert.Equal(0, snapshot.ReclaimingSlotCount);
        Assert.Equal(0, snapshot.PendingRemovalCount);
        Assert.Equal(0, snapshot.PublishedSlotCount);
        Assert.Equal(SlotCount, snapshot.FreeSlotCount);
    }

    private static void AssertAllValueAndLeaseCapacityReusable(MemoryStore store)
    {
        byte[] leaseKey = Key(50);
        Assert.Equal(StoreStatus.Success, store.TryPublish(leaseKey, [50]));
        var leases = new ValueLease[LeaseRecordCount];
        for (var index = 0; index < leases.Length; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryAcquire(leaseKey, out leases[index]));
        }

        Assert.Equal(StoreStatus.LeaseTableFull, store.TryAcquire(leaseKey, out _));
        foreach (ValueLease lease in leases)
        {
            Assert.Equal(StoreStatus.Success, lease.Release());
        }

        AssertRemovedAndReclaimed(store, leaseKey);

        for (var index = 0; index < SlotCount; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish(Key(100 + index), [(byte)index]));
        }

        Assert.Equal(StoreStatus.StoreFull, store.TryPublish(Key(999), [1]));
        for (var index = 0; index < SlotCount; index++)
        {
            AssertRemovedAndReclaimed(store, Key(100 + index));
        }

        AssertNoOwnerControlledLeakage(store);
    }

    private static void AssertRemovedAndReclaimed(MemoryStore store, byte[] key)
    {
        StoreStatus status = store.TryRemove(key, new StoreWaitOptions(TimeSpan.FromSeconds(1)));
        Assert.Contains(status, new[] { StoreStatus.Success, StoreStatus.NotFound, StoreStatus.RemovePending });
        if (status == StoreStatus.RemovePending)
        {
            Assert.Contains(
                store.TryRemove(key, new StoreWaitOptions(TimeSpan.FromSeconds(1))),
                new[] { StoreStatus.Success, StoreStatus.NotFound });
        }
    }

    private static void AssertWithinBound(
        TimeSpan elapsed,
        TimeSpan limit,
        BoundaryOperation operation)
    {
        TimeSpan maximum = limit + CompletionAllowance;
        Assert.True(elapsed <= maximum, $"{operation} took {elapsed} for a {limit} limit (maximum {maximum}).");
    }

    private static void AssertBoundedStatus(
        Func<StoreStatus> operation,
        StoreStatus expected,
        TimeSpan limit)
    {
        var stopwatch = Stopwatch.StartNew();
        StoreStatus status = operation();
        stopwatch.Stop();

        Assert.Equal(expected, status);
        Assert.True(
            stopwatch.Elapsed <= limit + CompletionAllowance,
            $"Large-capacity operation took {stopwatch.Elapsed} for a {limit} limit.");
    }

    private static MemoryStore CreateStore(
        string name,
        OpenMode openMode,
        StoreWaitOptions wait)
    {
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(Options(name, openMode), wait, out MemoryStore? store));
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateInstrumentedStore(BoundaryController controller)
    {
        SharedMemoryStoreOptions options = Options(
            $"sms-v2-wait-boundary-{Guid.NewGuid():N}",
            OpenMode.CreateNew);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            LockFreeCheckpointFactory.CreateInstrumented(controller.Observe),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: SlotCount,
            maxValueBytes: 64,
            maxDescriptorBytes: 16,
            maxKeyBytes: 8,
            leaseRecordCount: LeaseRecordCount,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static StoreWaitOptions Wait(WaitPolicyKind policy) => policy switch
    {
        WaitPolicyKind.NoWait => StoreWaitOptions.NoWait,
        WaitPolicyKind.Finite => new StoreWaitOptions(TimeSpan.FromMilliseconds(250)),
        WaitPolicyKind.Infinite => StoreWaitOptions.Infinite,
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private static byte[] Key(int value) => BitConverter.GetBytes(value);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture);

    public enum WaitPolicyKind
    {
        NoWait,
        Finite,
        Infinite
    }

    public enum BoundaryOperation
    {
        Open,
        Publish,
        SegmentedPublish,
        Reserve,
        Advance,
        Commit,
        Abort,
        Acquire,
        Release,
        Remove,
        Reclaim,
        Diagnostics,
        LeaseRecovery,
        ReservationRecovery
    }

    private readonly record struct TimedInvocation(StoreStatus Status, TimeSpan Elapsed);

    private sealed class BoundaryContext : IDisposable
    {
        internal BoundaryContext(MemoryStore store, byte[] key)
        {
            Store = store;
            Key = key;
        }

        internal MemoryStore Store { get; }
        internal byte[] Key { get; }
        internal ValueReservation Reservation;
        internal ValueLease Lease;
        internal long CopiedBytes;
        internal DiagnosticsSnapshot Diagnostics;
        internal LeaseRecoveryReport LeaseRecoveryReport;
        internal ReservationRecoveryReport ReservationRecoveryReport;

        public void Dispose() => Store.Dispose();
    }

    private sealed class BoundaryController : IDisposable
    {
        private readonly LockFreeCheckpointId _target;
        private readonly CancellationTokenSource? _cancellation;
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _reached;

        private BoundaryController(
            LockFreeCheckpointId target,
            CancellationTokenSource? cancellation)
        {
            _target = target;
            _cancellation = cancellation;
        }

        internal bool WasReached => Volatile.Read(ref _reached) != 0;

        internal static BoundaryController CancelAt(
            LockFreeCheckpointId target,
            CancellationTokenSource cancellation) => new(target, cancellation);

        internal static BoundaryController PauseAt(LockFreeCheckpointId target) => new(target, null);

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id != _target || Interlocked.CompareExchange(ref _reached, 1, 0) != 0)
            {
                return;
            }

            if (_cancellation is not null)
            {
                _cancellation.Cancel();
                return;
            }

            _paused.Set();
            if (!_resume.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException($"Checkpoint {_target} was not resumed.");
            }
        }

        internal bool WaitUntilPaused(TimeSpan timeout) => _paused.Wait(timeout);

        internal void Continue() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _paused.Dispose();
            _resume.Dispose();
        }
    }

    private sealed class NamedSynchronizationBlocker : IDisposable
    {
        private readonly string _storeName;
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;
        private Exception? _failure;

        internal NamedSynchronizationBlocker(string storeName)
        {
            _storeName = storeName;
            _thread = new Thread(Hold) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "The legacy synchronization was not acquired.");
            ThrowIfFailed();
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The legacy synchronization blocker did not stop.");
            _ready.Dispose();
            _release.Dispose();
            ThrowIfFailed();
        }

        private void Hold()
        {
            try
            {
                using IDisposable synchronization = PlatformCapabilityProbe.HoldStoreSynchronization(_storeName);
                _ready.Set();
                _release.Wait();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
        }

        private void ThrowIfFailed()
        {
            if (_failure is not null)
            {
                throw new Xunit.Sdk.XunitException(
                    $"The legacy synchronization blocker failed: {_failure}");
            }
        }
    }
}
