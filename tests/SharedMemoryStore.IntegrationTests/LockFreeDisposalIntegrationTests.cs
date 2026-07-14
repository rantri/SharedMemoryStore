using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeDisposalIntegrationTests
{
    private const int SlotCount = 8;
    private const int SimultaneousRaceRepetitions = 4;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public static TheoryData<DisposalOperation, int> CheckpointSchedules => new()
    {
        { DisposalOperation.Publish, (int)LockFreeCheckpointId.PublishAfterCommitPublication },
        { DisposalOperation.Reserve, (int)LockFreeCheckpointId.ReserveAfterReservationPublication },
        { DisposalOperation.ReservationProjection, (int)LockFreeCheckpointId.ProjectAfterSpanProjection },
        { DisposalOperation.Commit, (int)LockFreeCheckpointId.CommitAfterPublicationCas },
        { DisposalOperation.Abort, (int)LockFreeCheckpointId.AbortAfterUnlinkCompletion },
        { DisposalOperation.Acquire, (int)LockFreeCheckpointId.AcquireAfterPublishedRevalidation },
        { DisposalOperation.ValueProjection, (int)LockFreeCheckpointId.ProjectAfterSpanProjection },
        { DisposalOperation.Release, (int)LockFreeCheckpointId.ReleaseAfterRecordRecycle },
        { DisposalOperation.Remove, (int)LockFreeCheckpointId.RemoveAfterLeaseClassification }
    };

    public static TheoryData<DisposalOperation> EveryOperation => new()
    {
        DisposalOperation.Publish,
        DisposalOperation.Reserve,
        DisposalOperation.ReservationProjection,
        DisposalOperation.Advance,
        DisposalOperation.Commit,
        DisposalOperation.Abort,
        DisposalOperation.Acquire,
        DisposalOperation.ValueProjection,
        DisposalOperation.DescriptorProjection,
        DisposalOperation.Release,
        DisposalOperation.Remove,
        DisposalOperation.RecoverLeases,
        DisposalOperation.RecoverReservations,
        DisposalOperation.Diagnostics,
        DisposalOperation.RepeatedDispose
    };

    [Theory]
    [MemberData(nameof(CheckpointSchedules))]
    [Trait("Category", "Integration")]
    public async Task InFlightCallKeepsItsLocalMappingAliveWhileSecondHandleProgresses(
        DisposalOperation operation,
        int checkpointValue)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var checkpoint = (LockFreeCheckpointId)checkpointValue;
        using var gate = new CheckpointGate();
        using var context = CreateContext(operation, gate);
        gate.Arm(checkpoint);

        Task<OperationObservation> invocation = Task.Run(() => Invoke(context, operation));
        Assert.True(gate.WaitUntilPaused(TestTimeout), $"{operation} did not reach {checkpoint}.");

        using var disposeStarted = new ManualResetEventSlim(initialState: false);
        Task disposal = Task.Run(() =>
        {
            disposeStarted.Set();
            context.First.Dispose();
        });

        Assert.True(disposeStarted.Wait(TestTimeout));
        AssertSecondHandleProgress(context.Second, context.ProgressKey);
        bool disposedBeforeInvocationReturned = await Task.WhenAny(
            disposal,
            Task.Delay(TimeSpan.FromMilliseconds(250))) == disposal;

        gate.Continue();
        OperationObservation observation = await invocation.WaitAsync(TestTimeout);
        await disposal.WaitAsync(TestTimeout);

        Assert.False(
            disposedBeforeInvocationReturned,
            $"Dispose completed while the {operation} facade call was paused inside its engine.");
        AssertDocumentedOutcome(operation, observation);
        AssertDisposedTokenSurface(context);
        AssertSecondHandleCanReuseCapacity(context);
    }

    [Theory]
    [MemberData(nameof(EveryOperation))]
    [Trait("Category", "Integration")]
    public async Task EveryOperationAndTokenCallbackHasOnlyDocumentedDisposalRaceOutcomes(
        DisposalOperation operation)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        for (var repetition = 0; repetition < SimultaneousRaceRepetitions; repetition++)
        {
            using var context = CreateContext(operation);
            using var start = new Barrier(participantCount: 3);
            var exceptions = new ConcurrentQueue<Exception>();
            OperationObservation observation = default;

            Task invocation = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    observation = Invoke(context, operation);
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
            });
            Task disposal = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    context.First.Dispose();
                    context.First.Dispose();
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }
            });

            start.SignalAndWait();
            AssertSecondHandleProgress(context.Second, context.ProgressKey);
            await Task.WhenAll(invocation, disposal).WaitAsync(TestTimeout);

            Assert.Empty(exceptions);
            AssertDocumentedOutcome(operation, observation);
            AssertDisposedTokenSurface(context);
            AssertSecondHandleCanReuseCapacity(context);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ConcurrentRepeatedDisposeReturnsEveryOwnedResourceToTheOtherHandle()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var context = CreateContext(DisposalOperation.ReservationProjection);
        Task[] disposals = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                for (var call = 0; call < 32; call++)
                {
                    context.First.Dispose();
                }
            }))
            .ToArray();

        await Task.WhenAll(disposals).WaitAsync(TestTimeout);

        AssertDisposedTokenSurface(context);
        AssertSecondHandleCanReuseCapacity(context);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PausedLiveDisposerPublishesRecoverableHandoffWithoutBlockingOtherHandles()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var gate = new CheckpointGate();
        using var context = CreateContext(DisposalOperation.ReservationProjection, gate);
        gate.Arm(LockFreeCheckpointId.DisposalAfterParticipantClosingPublication);

        Task disposal = Task.Run(context.First.Dispose);
        Assert.True(
            gate.WaitUntilPaused(TestTimeout),
            "Dispose did not pause after publishing exact participant Closing.");
        try
        {
            Assert.Equal(StoreStatus.Success, context.Second.TryGetDiagnostics(out DiagnosticsSnapshot paused));
            Assert.Equal(1, paused.ClosingParticipantCount);

            Assert.Equal(
                StoreStatus.Success,
                context.Second.TryRecoverLeases(
                    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
                    StoreWaitOptions.Infinite,
                    out LeaseRecoveryReport leases));
            Assert.Equal(1, leases.RecoveredLeaseCount);

            Assert.Equal(
                StoreStatus.Success,
                context.Second.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
                    StoreWaitOptions.Infinite,
                    out ReservationRecoveryReport reservations));
            Assert.Equal(1, reservations.RecoveredReservationCount);

            AssertSecondHandleProgress(context.Second, context.ProgressKey);
            Assert.Equal(StoreStatus.Success, context.Second.TryGetDiagnostics(out DiagnosticsSnapshot helped));
            Assert.Equal(0, helped.ClosingParticipantCount);
            Assert.Equal(0, helped.RecoveringParticipantCount);
        }
        finally
        {
            gate.Continue();
        }

        await disposal.WaitAsync(TestTimeout);
        LockFreeCheckpointId[] observed = gate.ObservedIds();
        Assert.True(
            Array.IndexOf(observed, LockFreeCheckpointId.DisposalAfterParticipantClosingPublication)
            < Array.IndexOf(observed, LockFreeCheckpointId.DisposalAfterParticipantRelease));
        AssertSecondHandleCanReuseCapacity(context);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DisposeReleasesOwnedLeaseAndReclaimsItsPendingRemovalWithoutAnotherRecoveryCall()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-disposal-pending-remove-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        SharedMemoryStoreOptions open = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.OpenExisting,
            enableLeaseRecovery: true);

        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(create, out MemoryStore? owner));
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(open, out MemoryStore? survivor));
        Assert.NotNull(owner);
        Assert.NotNull(survivor);
        try
        {
            Assert.Equal(StoreStatus.Success, survivor!.TryPublish(Key(1), [1]));
            Assert.Equal(StoreStatus.Success, owner!.TryAcquire(Key(1), out ValueLease lease));
            Assert.Equal(StoreStatus.RemovePending, survivor.TryRemove(Key(1)));

            owner.Dispose();

            Assert.Equal(StoreStatus.Success, survivor.TryPublish(Key(2), [2]));
            Assert.Equal(StoreStatus.Success, survivor.TryRemove(Key(2)));
            Assert.Equal(StoreStatus.StoreDisposed, lease.Release());
        }
        finally
        {
            owner?.Dispose();
            survivor?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HeldColdOpenLockCannotDelayDisposeOrOtherHandleDataProgress()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-disposal-held-cold-lock-{Guid.NewGuid():N}";
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(Options(name, OpenMode.CreateNew), out MemoryStore? owner));
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(Options(name, OpenMode.OpenExisting), out MemoryStore? survivor));
        Assert.NotNull(owner);
        Assert.NotNull(survivor);

        try
        {
            using var held = new DedicatedColdSynchronizationHolder(name);
            var stopwatch = Stopwatch.StartNew();
            Task dispose = Task.Run(owner!.Dispose);
            Task progress = Task.Run(() =>
            {
                Assert.Equal(StoreStatus.Success, survivor!.TryPublish(Key(701), [7]));
                Assert.Equal(StoreStatus.Success, survivor.TryAcquire(Key(701), out ValueLease lease));
                Assert.Equal(7, lease.ValueSpan[0]);
                Assert.Equal(StoreStatus.Success, lease.Release());
                Assert.Equal(StoreStatus.Success, survivor.TryRemove(Key(701)));
            });

            await Task.WhenAll(dispose, progress).WaitAsync(TimeSpan.FromSeconds(1));
            stopwatch.Stop();
            Assert.True(
                stopwatch.Elapsed <= TimeSpan.FromMilliseconds(500),
                $"Dispose/data progress waited {stopwatch.Elapsed} on the cold open lock.");
        }
        finally
        {
            owner?.Dispose();
            survivor?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void HeldColdOpenLockIsScopedToItsNamedStore()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string blockedName = $"sms-v2-held-cold-store-a-{Guid.NewGuid():N}";
        string independentName = $"sms-v2-held-cold-store-b-{Guid.NewGuid():N}";
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(
                Options(blockedName, OpenMode.CreateNew),
                out MemoryStore? blockedOwner));
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(
                Options(independentName, OpenMode.CreateNew),
                out MemoryStore? independentOwner));
        Assert.NotNull(blockedOwner);
        Assert.NotNull(independentOwner);

        try
        {
            using var held = new DedicatedColdSynchronizationHolder(blockedName);
            Assert.Equal(
                StoreOpenStatus.StoreBusy,
                MemoryStore.TryCreateOrOpen(
                    Options(blockedName, OpenMode.OpenExisting),
                    StoreWaitOptions.NoWait,
                    out MemoryStore? blockedOpen));
            Assert.Null(blockedOpen);

            Assert.Equal(
                StoreOpenStatus.Success,
                MemoryStore.TryCreateOrOpen(
                    Options(independentName, OpenMode.OpenExisting),
                    out MemoryStore? independentOpen));
            using (independentOpen)
            {
                Assert.Equal(StoreStatus.Success, independentOpen!.TryPublish(Key(801), [8]));
                Assert.Equal(StoreStatus.Success, independentOpen.TryAcquire(Key(801), out ValueLease lease));
                Assert.Equal(8, lease.ValueSpan[0]);
                Assert.Equal(StoreStatus.Success, lease.Release());
                Assert.Equal(StoreStatus.Success, independentOpen.TryRemove(Key(801)));
            }
        }
        finally
        {
            blockedOwner?.Dispose();
            independentOwner?.Dispose();
        }
    }

    private static DisposalContext CreateContext(
        DisposalOperation operation,
        CheckpointGate? checkpointGate = null)
    {
        string name = $"sms-v2-disposal-{operation}-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = Options(name, OpenMode.CreateNew);
        MemoryStore first;
        if (checkpointGate is null)
        {
            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(create, out var opened));
            first = Assert.IsType<MemoryStore>(opened);
        }
        else
        {
            Assert.Equal(
                StoreOpenStatus.Success,
                LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                    create,
                    LockFreeCheckpointFactory.CreateInstrumented(checkpointGate.Observe),
                    out var opened));
            first = Assert.IsType<MemoryStore>(opened);
        }

        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(Options(name, OpenMode.OpenExisting), out var secondStore));
        MemoryStore second = Assert.IsType<MemoryStore>(secondStore);

        byte[] publishedKey = Key(1);
        byte[] reservationKey = Key(2);
        byte[] operationKey = Key(3);
        byte[] progressKey = Key(4);
        Assert.Equal(StoreStatus.Success, second.TryPublish(publishedKey, [7, 8], [9, 10]));
        Assert.Equal(StoreStatus.Success, first.TryAcquire(publishedKey, out ValueLease lease));
        Assert.Equal(
            StoreStatus.Success,
            first.TryReserve(reservationKey, payloadLength: 2, descriptor: [11], out ValueReservation reservation));

        if (operation == DisposalOperation.Commit)
        {
            reservation.GetSpan().Fill(12);
            Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        }

        return new DisposalContext(
            first,
            second,
            reservation,
            lease,
            publishedKey,
            reservationKey,
            operationKey,
            progressKey);
    }

    private static OperationObservation Invoke(DisposalContext context, DisposalOperation operation)
    {
        switch (operation)
        {
            case DisposalOperation.Publish:
                return Status(context.First.TryPublish(context.OperationKey, [21], [22]));

            case DisposalOperation.Reserve:
                return Status(context.First.TryReserve(
                    context.OperationKey,
                    payloadLength: 1,
                    descriptor: [22],
                    out _));

            case DisposalOperation.ReservationProjection:
            {
                Span<byte> span = context.Reservation.GetSpan();
                return Projection(span.Length);
            }

            case DisposalOperation.Advance:
                return Status(context.Reservation.Advance(1));

            case DisposalOperation.Commit:
                return Status(context.Reservation.Commit());

            case DisposalOperation.Abort:
                return Status(context.Reservation.Abort());

            case DisposalOperation.Acquire:
                return Status(context.First.TryAcquire(context.PublishedKey, out _));

            case DisposalOperation.ValueProjection:
            {
                ReadOnlySpan<byte> span = context.Lease.ValueSpan;
                return Projection(span.Length);
            }

            case DisposalOperation.DescriptorProjection:
            {
                ReadOnlySpan<byte> span = context.Lease.DescriptorSpan;
                return Projection(span.Length);
            }

            case DisposalOperation.Release:
                return Status(context.Lease.Release());

            case DisposalOperation.Remove:
                return Status(context.First.TryRemove(context.PublishedKey));

            case DisposalOperation.RecoverLeases:
                // This theory deliberately races another handle's ordinary work.
                // The current-process override requires process-wide quiescence,
                // so exercise the concurrency-safe recovery mode here.
                return Status(context.First.TryRecoverLeases(
                    new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
                    out _));

            case DisposalOperation.RecoverReservations:
                return Status(context.First.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
                    out _));

            case DisposalOperation.Diagnostics:
                return Status(context.First.TryGetDiagnostics(out _));

            case DisposalOperation.RepeatedDispose:
                context.First.Dispose();
                return Status(StoreStatus.Success);

            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    private static void AssertDocumentedOutcome(
        DisposalOperation operation,
        OperationObservation observation)
    {
        if (operation is DisposalOperation.ReservationProjection
            or DisposalOperation.ValueProjection
            or DisposalOperation.DescriptorProjection)
        {
            // Disposal may invalidate borrowed storage after projection returns,
            // so this race may inspect the projection shape but not its bytes.
            Assert.Contains(observation.ProjectedLength, new[] { 0, 2 });
            return;
        }

        StoreStatus status = Assert.IsType<StoreStatus>(observation.Status);
        Assert.NotEqual(StoreStatus.UnknownFailure, status);
        Assert.NotEqual(StoreStatus.CorruptStore, status);
        Assert.Contains(status, AllowedStatuses(operation));
    }

    private static IReadOnlyCollection<StoreStatus> AllowedStatuses(DisposalOperation operation) =>
        operation switch
        {
            DisposalOperation.Publish => [StoreStatus.Success, StoreStatus.StoreDisposed],
            DisposalOperation.Reserve => [StoreStatus.Success, StoreStatus.StoreDisposed],
            DisposalOperation.Advance =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.InvalidReservation],
            DisposalOperation.Commit =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.InvalidReservation,
                    StoreStatus.ReservationAlreadyCompleted],
            DisposalOperation.Abort =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.InvalidReservation,
                    StoreStatus.ReservationAlreadyCompleted],
            DisposalOperation.Acquire =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.NotFound],
            DisposalOperation.Release =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.InvalidLease,
                    StoreStatus.LeaseAlreadyReleased],
            DisposalOperation.Remove =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.NotFound,
                    StoreStatus.RemovePending],
            DisposalOperation.RecoverLeases or DisposalOperation.RecoverReservations =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.StoreBusy,
                    StoreStatus.UnsupportedPlatform],
            DisposalOperation.Diagnostics =>
                [StoreStatus.Success, StoreStatus.StoreDisposed, StoreStatus.UnsupportedPlatform],
            DisposalOperation.RepeatedDispose => [StoreStatus.Success],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };

    private static void AssertSecondHandleProgress(MemoryStore second, byte[] key)
    {
        Assert.Equal(StoreStatus.Success, second.TryPublish(key, [31, 32], [33]));
        Assert.Equal(StoreStatus.Success, second.TryAcquire(key, out ValueLease lease));
        Assert.Equal(new byte[] { 31, 32 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 33 }, lease.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, second.TryRemove(key));

        byte[] reservationKey = Key(5);
        Assert.Equal(StoreStatus.Success, second.TryReserve(reservationKey, 1, default, out var reservation));
        reservation.GetSpan()[0] = 34;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    private static void AssertDisposedTokenSurface(DisposalContext context)
    {
        Assert.Equal(StoreStatus.StoreDisposed, context.First.TryPublish(Key(90), [1]));
        Assert.False(context.Reservation.IsValid);
        Assert.True(context.Reservation.GetSpan().IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, context.Reservation.Advance(0));
        Assert.Equal(StoreStatus.StoreDisposed, context.Reservation.Commit());
        Assert.Equal(StoreStatus.StoreDisposed, context.Reservation.Abort());
        Assert.False(context.Lease.IsValid);
        Assert.True(context.Lease.ValueSpan.IsEmpty);
        Assert.True(context.Lease.DescriptorSpan.IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, context.Lease.Release());
    }

    private static void AssertSecondHandleCanReuseCapacity(DisposalContext context)
    {
        _ = context.Second.TryRecoverLeases(new LeaseRecoveryOptions(true), out _);
        _ = context.Second.TryRecoverReservations(new ReservationRecoveryOptions(true), out _);
        RemoveIfPresent(context.Second, context.PublishedKey);
        RemoveIfPresent(context.Second, context.ReservationKey);
        RemoveIfPresent(context.Second, context.OperationKey);
        RemoveIfPresent(context.Second, context.ProgressKey);
        RemoveIfPresent(context.Second, Key(5));

        for (var index = 0; index < SlotCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                context.Second.TryPublish(Key(100 + index), [(byte)index]));
        }

        Assert.Equal(StoreStatus.StoreFull, context.Second.TryPublish(Key(200), [1]));
        for (var index = 0; index < SlotCount; index++)
        {
            Assert.Equal(StoreStatus.Success, context.Second.TryRemove(Key(100 + index)));
        }
    }

    private static void RemoveIfPresent(MemoryStore store, byte[] key)
    {
        StoreStatus status = store.TryRemove(key);
        Assert.Contains(status, new[] { StoreStatus.Success, StoreStatus.NotFound });
    }

    private static OperationObservation Status(StoreStatus status) => new(status, 0);

    private static OperationObservation Projection(int length) => new(null, length);

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: SlotCount,
            maxValueBytes: 32,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 16,
            participantRecordCount: 4,
            openMode: openMode,
            enableLeaseRecovery: true);

    private static byte[] Key(int value) => BitConverter.GetBytes(value);

    private static bool IsSupportedLockFreeHost() =>
        LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture);

    public enum DisposalOperation
    {
        Publish,
        Reserve,
        ReservationProjection,
        Advance,
        Commit,
        Abort,
        Acquire,
        ValueProjection,
        DescriptorProjection,
        Release,
        Remove,
        RecoverLeases,
        RecoverReservations,
        Diagnostics,
        RepeatedDispose
    }

    private readonly record struct OperationObservation(
        StoreStatus? Status,
        int ProjectedLength);

    /// <summary>
    /// Owns the platform synchronization primitive on one dedicated thread.
    /// Windows mutexes must be released by their acquiring thread even when an
    /// async xUnit continuation moves the test itself to another thread.
    /// </summary>
    private sealed class DedicatedColdSynchronizationHolder : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;
        private readonly string _name;
        private Exception? _failure;
        private int _disposed;

        internal DedicatedColdSynchronizationHolder(string name)
        {
            _name = name;
            _thread = new Thread(Hold)
            {
                IsBackground = true,
                Name = "SharedMemoryStore cold-lock test holder"
            };
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out acquiring the cold store synchronization primitive.");
            }

            if (_failure is not null)
            {
                throw new InvalidOperationException("Unable to hold cold store synchronization.", _failure);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _release.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Cold synchronization holder did not exit.");
            }

            _ready.Dispose();
            _release.Dispose();
        }

        private void Hold()
        {
            try
            {
                using IDisposable held = PlatformCapabilityProbe.HoldStoreSynchronization(_name);
                _ready.Set();
                _release.Wait();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
        }
    }

    private sealed class DisposalContext : IDisposable
    {
        internal DisposalContext(
            MemoryStore first,
            MemoryStore second,
            ValueReservation reservation,
            ValueLease lease,
            byte[] publishedKey,
            byte[] reservationKey,
            byte[] operationKey,
            byte[] progressKey)
        {
            First = first;
            Second = second;
            Reservation = reservation;
            Lease = lease;
            PublishedKey = publishedKey;
            ReservationKey = reservationKey;
            OperationKey = operationKey;
            ProgressKey = progressKey;
        }

        internal MemoryStore First { get; }
        internal MemoryStore Second { get; }
        internal ValueReservation Reservation { get; }
        internal ValueLease Lease { get; }
        internal byte[] PublishedKey { get; }
        internal byte[] ReservationKey { get; }
        internal byte[] OperationKey { get; }
        internal byte[] ProgressKey { get; }

        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
        }
    }

    private sealed class CheckpointGate : IDisposable
    {
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private readonly List<LockFreeCheckpointId> _observed = [];
        private LockFreeCheckpointId? _target;
        private int _claimed;

        internal void Arm(LockFreeCheckpointId target)
        {
            _target = target;
            Volatile.Write(ref _claimed, 0);
            lock (_observed)
            {
                _observed.Clear();
            }
            _paused.Reset();
            _resume.Reset();
        }

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            lock (_observed)
            {
                _observed.Add(entry.Id);
            }

            if (_target != entry.Id || Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            {
                return;
            }

            _paused.Set();
            if (!_resume.Wait(TestTimeout))
            {
                throw new TimeoutException($"Checkpoint {entry.Id} was not resumed.");
            }
        }

        internal bool WaitUntilPaused(TimeSpan timeout) => _paused.Wait(timeout);

        internal LockFreeCheckpointId[] ObservedIds()
        {
            // This gate is used by one instrumented engine. Observation order
            // is append-only and queried only after its disposal task exits.
            lock (_observed)
            {
                return _observed.ToArray();
            }
        }

        internal void Continue() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _paused.Dispose();
            _resume.Dispose();
        }
    }
}
