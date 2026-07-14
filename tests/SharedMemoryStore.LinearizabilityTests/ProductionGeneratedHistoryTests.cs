using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;
using Store = SharedMemoryStore.MemoryStore;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SharedMemoryStore.LinearizabilityTests;

/// <summary>
/// Captures bounded histories from public MemoryStore calls. These histories
/// complement the high-count outcome stress: they retain real invocation and
/// response envelopes and are checked, and minimized on failure, by the
/// reference-model checker.
/// </summary>
public sealed class ProductionGeneratedHistoryTests
{
    private const int DefaultSeed = 0x5eed_0200;
    private static readonly byte[] Key = [0x51];
    private static readonly byte[] Left = [0x61];
    private static readonly byte[] Right = [0x62];
    private static readonly byte[] Next = [0x63];
    private const string KeyHex = "51";
    private const string LeftHex = "61";
    private const string RightHex = "62";
    private const string NextHex = "63";
    private readonly ITestOutputHelper _output;

    public ProductionGeneratedHistoryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> RequiredFamilies => new()
    {
        "publish-publish",
        "publish-reserve",
        "reserve-reserve",
        "commit-acquire",
        "acquire-remove",
        "release-reclaim",
        "recovery-live-lease",
        "disposal-operation"
    };

    [Theory]
    [MemberData(nameof(RequiredFamilies))]
    [Trait("Category", "ProductionGeneratedHistory")]
    public void ConfiguredProductionHistoriesAreLinearizableAndFailureMinimizable(string family)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        int historyCount = ConfiguredHistoryCount();
        int seed = FamilySeed(ConfiguredSeed(), FamilyOrdinal(family));
        var random = new DeterministicRandom(seed);
        for (var historyIndex = 0; historyIndex < historyCount; historyIndex++)
        {
            CapturedHistory captured;
            var overlapAttempt = 0;
            do
            {
                overlapAttempt++;
                captured = family switch
                {
                    "publish-publish" => CapturePublishPublish(random.NextDelay(), random.NextDelay()),
                    "publish-reserve" => CapturePublishReserve(random.NextDelay(), random.NextDelay()),
                    "reserve-reserve" => CaptureReserveReserve(random.NextDelay(), random.NextDelay()),
                    "commit-acquire" => CaptureCommitAcquire(random.NextDelay(), random.NextDelay()),
                    "acquire-remove" => CaptureAcquireRemove(random.NextDelay(), random.NextDelay()),
                    "release-reclaim" => CaptureReleaseReclaim(random.NextDelay(), random.NextDelay()),
                    "recovery-live-lease" => CaptureRecoveryLiveLease(random.NextDelay(), random.NextDelay()),
                    "disposal-operation" => CaptureDisposalOperation(random.NextDelay(), random.NextDelay()),
                    _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
                };
            }
            while (!captured.RaceOperations[0].ImplementationOverlaps(captured.RaceOperations[1])
                   && overlapAttempt < 128);

            IReadOnlyList<RecordedOperation> history = captured.History;
            int actorCount = history.Select(static operation => operation.ActorId).Distinct().Count();
            if (history.Count is < 6 or > 12 || actorCount is < 2 or > 4)
            {
                throw new XunitException(
                    $"family={family}, history={historyIndex}, seed={seed}: " +
                    $"calls={history.Count}, actors={actorCount}; expected 6-12 calls and 2-4 actors.");
            }

            AssertProductionRaceOverlap(
                captured.RaceOperations[0],
                captured.RaceOperations[1],
                $"family={family}, history={historyIndex}, seed={seed}, attempts={overlapAttempt}");

            LinearizabilityCheckResult result = captured.Checker.Check(
                history,
                captured.ResourceWitnesses);
            if (!result.IsLinearizable)
            {
                IReadOnlyList<RecordedOperation> minimized = captured.Checker.MinimizeFailingHistory(
                    history,
                    captured.ResourceWitnesses);
                throw new XunitException(
                    $"family={family}, history={historyIndex}, seed={seed}: {result.Failure}; " +
                    $"history={Format(history)}; minimized={Format(minimized)}");
            }
        }

        _output.WriteLine(
            $"family={family} seed={seed} completedHistories={historyCount} " +
            "source=production-MemoryStore callsPerHistory=6 actors=2-3 checker=reference minimizer=on-failure");
    }

    private static CapturedHistory CapturePublishPublish(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-publish", slotCount: 2, leaseCount: 2, recorder);
        PendingInvocation first = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, KeyHex, LeftHex));
        PendingInvocation second = recorder.Invoke(2, 2, ReferenceCommand.Publish(1, KeyHex, RightHex));
        RunConcurrent(
            first,
            () => CompletePublish(first, store, Left),
            second,
            () => CompletePublish(second, store, Right),
            firstDelay,
            secondDelay);

        PendingInvocation acquire = recorder.Invoke(3, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        acquire.Enter();
        StoreStatus acquireStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
        CompleteAcquire(acquire, acquireStatus, lease);

        PendingInvocation remove = recorder.Invoke(4, 3, ReferenceCommand.Remove(1, KeyHex));
        remove.Enter();
        remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

        PendingInvocation release = recorder.Invoke(5, 3, ReferenceCommand.ReleaseLease(1, 20));
        release.Enter();
        release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

        PendingInvocation republish = recorder.Invoke(6, 3, ReferenceCommand.Publish(1, KeyHex, NextHex));
        republish.Enter();
        republish.Complete(MapPublish(store.TryPublish(Key, Next, default, StoreWaitOptions.Infinite)));

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 2, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 1,
            raceSecondId: 2);
    }

    private static CapturedHistory CapturePublishReserve(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-publish-reserve", slotCount: 2, leaseCount: 2, recorder);
        ValueReservation reservation = default;
        StoreStatus publishStatus = default;
        StoreStatus reserveStatus = default;
        PendingInvocation publish = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, KeyHex, LeftHex));
        PendingInvocation reserve = recorder.Invoke(2, 2, ReferenceCommand.Reserve(1, 10, KeyHex, RightHex));
        RunConcurrent(
            publish,
            () =>
            {
                publishStatus = store.TryPublish(Key, Left, default, StoreWaitOptions.Infinite);
                publish.Complete(MapPublish(publishStatus));
            },
            reserve,
            () =>
            {
                reserveStatus = store.TryReserve(
                    Key,
                    1,
                    default,
                    StoreWaitOptions.Infinite,
                    out reservation);
                reserve.Complete(MapReserve(reserveStatus));
            },
            firstDelay,
            secondDelay);

        if ((publishStatus == StoreStatus.Success ? 1 : 0)
            + (reserveStatus == StoreStatus.Success ? 1 : 0) != 1)
        {
            throw new XunitException(
                $"publish/reserve history did not produce one winner: publish={publishStatus}, reserve={reserveStatus}.");
        }

        if (reservation.IsValid)
        {
            PendingInvocation abort = recorder.Invoke(3, 3, ReferenceCommand.AbortReservation(1, 10));
            abort.Enter();
            abort.Complete(MapAbort(reservation.Abort(StoreWaitOptions.Infinite)));

            CompleteSequentialPublish(recorder, 4, actorId: 3, store, Next);
            PendingInvocation acquire = recorder.Invoke(5, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
            acquire.Enter();
            StoreStatus status = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
            CompleteAcquire(acquire, status, lease);

            PendingInvocation release = recorder.Invoke(6, 3, ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));
        }
        else
        {
            PendingInvocation acquire = recorder.Invoke(3, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
            acquire.Enter();
            StoreStatus status = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
            CompleteAcquire(acquire, status, lease);

            PendingInvocation release = recorder.Invoke(4, 3, ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

            PendingInvocation remove = recorder.Invoke(5, 3, ReferenceCommand.Remove(1, KeyHex));
            remove.Enter();
            remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

            CompleteSequentialPublish(recorder, 6, actorId: 3, store, Next);
        }

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 2, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 1,
            raceSecondId: 2);
    }

    private static CapturedHistory CaptureReserveReserve(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-reserve-reserve", slotCount: 2, leaseCount: 2, recorder);
        ValueReservation firstReservation = default;
        ValueReservation secondReservation = default;
        StoreStatus firstStatus = default;
        StoreStatus secondStatus = default;
        PendingInvocation first = recorder.Invoke(1, 1, ReferenceCommand.Reserve(1, 10, KeyHex, LeftHex));
        PendingInvocation second = recorder.Invoke(2, 2, ReferenceCommand.Reserve(1, 11, KeyHex, RightHex));
        RunConcurrent(
            first,
            () =>
            {
                firstStatus = store.TryReserve(
                    Key,
                    1,
                    default,
                    StoreWaitOptions.Infinite,
                    out firstReservation);
                first.Complete(MapReserve(firstStatus));
            },
            second,
            () =>
            {
                secondStatus = store.TryReserve(
                    Key,
                    1,
                    default,
                    StoreWaitOptions.Infinite,
                    out secondReservation);
                second.Complete(MapReserve(secondStatus));
            },
            firstDelay,
            secondDelay);

        if ((firstStatus == StoreStatus.Success ? 1 : 0)
            + (secondStatus == StoreStatus.Success ? 1 : 0) != 1)
        {
            throw new XunitException(
                $"reserve/reserve history did not produce one winner: first={firstStatus}, second={secondStatus}.");
        }

        ValueReservation winner = firstReservation.IsValid ? firstReservation : secondReservation;
        int winnerId = firstReservation.IsValid ? 10 : 11;
        PendingInvocation abort = recorder.Invoke(3, 3, ReferenceCommand.AbortReservation(1, winnerId));
        abort.Enter();
        abort.Complete(MapAbort(winner.Abort(StoreWaitOptions.Infinite)));

        CompleteSequentialPublish(recorder, 4, actorId: 3, store, Next);
        PendingInvocation acquire = recorder.Invoke(5, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        acquire.Enter();
        StoreStatus acquireStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
        CompleteAcquire(acquire, acquireStatus, lease);

        PendingInvocation release = recorder.Invoke(6, 3, ReferenceCommand.ReleaseLease(1, 20));
        release.Enter();
        release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 2, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 1,
            raceSecondId: 2);
    }

    private static CapturedHistory CaptureCommitAcquire(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-commit", slotCount: 1, leaseCount: 2, recorder);
        PendingInvocation reserve = recorder.Invoke(1, 3, ReferenceCommand.Reserve(1, 10, KeyHex, NextHex));
        reserve.Enter();
        StoreStatus reserveStatus = store.TryReserve(
            Key,
            payloadLength: 1,
            descriptor: default,
            StoreWaitOptions.Infinite,
            out ValueReservation reservation);
        if (reserveStatus == StoreStatus.Success)
        {
            reservation.GetSpan(1)[0] = Next[0];
            reserveStatus = reservation.Advance(1, StoreWaitOptions.Infinite);
        }
        reserve.Complete(MapReserve(reserveStatus));

        ValueLease lease = default;
        PendingInvocation commit = recorder.Invoke(2, 1, ReferenceCommand.CommitReservation(1, 10));
        PendingInvocation acquire = recorder.Invoke(3, 2, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        RunConcurrent(
            commit,
            () =>
            {
                commit.Complete(MapCommit(reservation.Commit(StoreWaitOptions.Infinite)));
            },
            acquire,
            () =>
            {
                StoreStatus status = store.TryAcquire(Key, StoreWaitOptions.Infinite, out lease);
                CompleteAcquire(acquire, status, lease);
            },
            firstDelay,
            secondDelay);

        if (!lease.IsValid)
        {
            PendingInvocation acquireAfter = recorder.Invoke(4, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
            acquireAfter.Enter();
            StoreStatus acquireAfterStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out lease);
            CompleteAcquire(acquireAfter, acquireAfterStatus, lease);

            PendingInvocation remove = recorder.Invoke(5, 3, ReferenceCommand.Remove(1, KeyHex));
            remove.Enter();
            remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

            PendingInvocation release = recorder.Invoke(6, 3, ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));
        }
        else
        {
            PendingInvocation remove = recorder.Invoke(4, 3, ReferenceCommand.Remove(1, KeyHex));
            remove.Enter();
            remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

            PendingInvocation release = recorder.Invoke(5, 3, ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

            PendingInvocation republish = recorder.Invoke(6, 3, ReferenceCommand.Publish(1, KeyHex, NextHex));
            republish.Enter();
            republish.Complete(MapPublish(store.TryPublish(Key, Next, default, StoreWaitOptions.Infinite)));
        }

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 1, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 2,
            raceSecondId: 3);
    }

    private static CapturedHistory CaptureAcquireRemove(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-acquire-remove", slotCount: 1, leaseCount: 2, recorder);
        CompleteSequentialPublish(recorder, 1, actorId: 3, store, Left);

        ValueLease lease = default;
        StoreStatus acquireStatus = default;
        StoreStatus removeStatus = default;
        PendingInvocation acquire = recorder.Invoke(2, 1, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        PendingInvocation remove = recorder.Invoke(3, 2, ReferenceCommand.Remove(1, KeyHex));
        RunConcurrent(
            acquire,
            () =>
            {
                acquireStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out lease);
                CompleteAcquire(acquire, acquireStatus, lease);
            },
            remove,
            () =>
            {
                removeStatus = store.TryRemove(Key, StoreWaitOptions.Infinite);
                remove.Complete(MapRemove(removeStatus));
            },
            firstDelay,
            secondDelay);

        if (acquireStatus == StoreStatus.Success)
        {
            PendingInvocation release = recorder.Invoke(4, 3, ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

            CompleteSequentialPublish(recorder, 5, actorId: 3, store, Next);
            PendingInvocation finalRemove = recorder.Invoke(6, 3, ReferenceCommand.Remove(1, KeyHex));
            finalRemove.Enter();
            finalRemove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));
        }
        else
        {
            CompleteSequentialPublish(recorder, 4, actorId: 3, store, Next);
            PendingInvocation acquireAfter = recorder.Invoke(5, 3, ReferenceCommand.AcquireLease(1, 21, KeyHex));
            acquireAfter.Enter();
            StoreStatus acquireAfterStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease current);
            CompleteAcquire(acquireAfter, acquireAfterStatus, current);

            PendingInvocation release = recorder.Invoke(6, 3, ReferenceCommand.ReleaseLease(1, 21));
            release.Enter();
            release.Complete(MapRelease(current.Release(StoreWaitOptions.Infinite)));
        }

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 1, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 2,
            raceSecondId: 3);
    }

    private static CapturedHistory CaptureReleaseReclaim(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore("history-release", slotCount: 1, leaseCount: 2, recorder);
        CompleteSequentialPublish(recorder, 1, actorId: 3, store, Left);

        PendingInvocation acquire = recorder.Invoke(2, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        acquire.Enter();
        StoreStatus acquireStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
        CompleteAcquire(acquire, acquireStatus, lease);

        PendingInvocation remove = recorder.Invoke(3, 3, ReferenceCommand.Remove(1, KeyHex));
        remove.Enter();
        remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

        StoreStatus republishStatus = default;
        PendingInvocation release = recorder.Invoke(4, 1, ReferenceCommand.ReleaseLease(1, 20));
        PendingInvocation republish = recorder.Invoke(5, 2, ReferenceCommand.Publish(1, KeyHex, NextHex));
        RunConcurrent(
            release,
            () =>
            {
                release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));
            },
            republish,
            () =>
            {
                republishStatus = store.TryPublish(Key, Next, default, StoreWaitOptions.Infinite);
                republish.Complete(MapPublish(republishStatus));
            },
            firstDelay,
            secondDelay);

        if (republishStatus == StoreStatus.Success)
        {
            PendingInvocation verify = recorder.Invoke(6, 3, ReferenceCommand.AcquireLease(1, 21, KeyHex));
            verify.Enter();
            StoreStatus verifyStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease verifyLease);
            CompleteAcquire(verify, verifyStatus, verifyLease);
        }
        else
        {
            CompleteSequentialPublish(recorder, 6, actorId: 3, store, Next);
        }

        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 1, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 4,
            raceSecondId: 5);
    }

    private static CapturedHistory CaptureRecoveryLiveLease(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        using Store store = CreateStore(
            "history-recovery",
            slotCount: 1,
            leaseCount: 2,
            recorder,
            enableLeaseRecovery: true);
        CompleteSequentialPublish(recorder, 1, actorId: 3, store, Left);

        PendingInvocation acquire = recorder.Invoke(2, 3, ReferenceCommand.AcquireLease(1, 20, KeyHex));
        acquire.Enter();
        StoreStatus acquireStatus = store.TryAcquire(Key, StoreWaitOptions.Infinite, out ValueLease lease);
        CompleteAcquire(acquire, acquireStatus, lease);

        PendingInvocation remove = recorder.Invoke(3, 3, ReferenceCommand.Remove(1, KeyHex));
        remove.Enter();
        remove.Complete(MapRemove(store.TryRemove(Key, StoreWaitOptions.Infinite)));

        PendingInvocation recovery = recorder.Invoke(4, 1, ReferenceCommand.RecoverLease(1, 20));
        PendingInvocation release = recorder.Invoke(5, 2, ReferenceCommand.ReleaseLease(1, 20));
        RunConcurrent(
            recovery,
            () =>
            {
                StoreStatus status = store.TryRecoverLeases(
                    new LeaseRecoveryOptions(false),
                    StoreWaitOptions.Infinite,
                    out LeaseRecoveryReport report);
                recovery.Complete(MapSemanticRecovery(status, report));
            },
            release,
            () =>
            {
                release.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));
            },
            firstDelay,
            secondDelay);

        CompleteSequentialPublish(recorder, 6, actorId: 3, store, Next);
        IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
        return Captured(
            new LinearizabilityChecker(2, 1, initialParticipants: [1], leaseCapacity: 2),
            history,
            recorder.ResourceSnapshot(),
            raceFirstId: 4,
            raceSecondId: 5);
    }

    private static CapturedHistory CaptureDisposalOperation(int firstDelay, int secondDelay)
    {
        var recorder = new MonotonicHistoryRecorder(strictProductionHistory: true);
        SharedMemoryStoreOptions options = CreateOptions(
            "history-disposal",
            slotCount: 1,
            leaseCount: 2,
            OpenMode.CreateNew);
        StoreOpenStatus created = TryCreateInstrumented(options, recorder, out Store? participantOne);
        if (created != StoreOpenStatus.Success || participantOne is null)
        {
            throw new XunitException($"Could not create disposal history store: {created}.");
        }

        using Store participantTwo = OpenStore(options, OpenMode.OpenExisting, recorder);
        try
        {
            CompleteSequentialPublish(recorder, 1, actorId: 3, participantOne, Left);

            PendingInvocation operation = recorder.Invoke(
                2,
                1,
                ReferenceCommand.Publish(1, KeyHex, RightHex));
            PendingInvocation dispose = recorder.Invoke(3, 2, ReferenceCommand.DisposeParticipant(1));
            RunConcurrent(
                operation,
                () =>
                {
                    StoreStatus status = participantOne.TryPublish(
                        Key,
                        Right,
                        default,
                        StoreWaitOptions.Infinite);
                    ReferenceResultCode mapped = status switch
                    {
                        StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
                        StoreStatus.StoreDisposed => ReferenceResultCode.ParticipantNotActive,
                        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
                        _ => ReferenceResultCode.Unexpected
                    };
                    operation.Complete(mapped);
                },
                dispose,
                () =>
                {
                    participantOne.Dispose();
                    dispose.Complete(ReferenceResultCode.Success);
                },
                firstDelay,
                secondDelay);

            PendingInvocation keeperAcquire = recorder.Invoke(4, 3, ReferenceCommand.AcquireLease(2, 21, KeyHex));
            keeperAcquire.Enter();
            StoreStatus keeperAcquireStatus = participantTwo.TryAcquire(
                Key,
                StoreWaitOptions.Infinite,
                out ValueLease lease);
            CompleteAcquire(keeperAcquire, keeperAcquireStatus, lease);

            PendingInvocation keeperRelease = recorder.Invoke(5, 3, ReferenceCommand.ReleaseLease(2, 21));
            keeperRelease.Enter();
            keeperRelease.Complete(MapRelease(lease.Release(StoreWaitOptions.Infinite)));

            PendingInvocation remove = recorder.Invoke(6, 3, ReferenceCommand.Remove(2, KeyHex));
            remove.Enter();
            remove.Complete(MapRemove(participantTwo.TryRemove(Key, StoreWaitOptions.Infinite)));

            IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
            return Captured(
                new LinearizabilityChecker(2, 1, initialParticipants: [1, 2], leaseCapacity: 2),
                history,
                recorder.ResourceSnapshot(),
                raceFirstId: 2,
                raceSecondId: 3);
        }
        finally
        {
            participantOne.Dispose();
        }
    }

    private static void CompleteSequentialPublish(
        MonotonicHistoryRecorder recorder,
        int id,
        int actorId,
        Store store,
        byte[] value)
    {
        PendingInvocation publish = recorder.Invoke(
            id,
            actorId,
            ReferenceCommand.Publish(1, KeyHex, Convert.ToHexString(value)));
        publish.Enter();
        publish.Complete(MapPublish(store.TryPublish(Key, value, default, StoreWaitOptions.Infinite)));
    }

    private static void CompletePublish(PendingInvocation invocation, Store store, byte[] value)
    {
        invocation.Complete(MapPublish(store.TryPublish(Key, value, default, StoreWaitOptions.Infinite)));
    }

    private static void CompleteAcquire(
        PendingInvocation invocation,
        StoreStatus status,
        in ValueLease lease) =>
        CompleteAcquire(invocation, MapAcquire(status), lease);

    private static void CompleteAcquire(
        PendingInvocation invocation,
        ReferenceResultCode result,
        in ValueLease lease)
    {
        if (result != ReferenceResultCode.Success)
        {
            invocation.Complete(result);
            return;
        }

        ReadOnlySpan<byte> returnedBytes = lease.ValueSpan;
        IndexBinding binding = IndexBinding.Decode(lease.HandleForEngine.SlotBinding);
        invocation.Complete(
            result,
            Convert.ToHexString(returnedBytes),
            binding.Generation);
    }

    private static ReferenceResultCode MapPublish(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
        StoreStatus.NotFound => ReferenceResultCode.NotFound,
        StoreStatus.StoreFull => ReferenceResultCode.StoreFull,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapReserve(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
        StoreStatus.StoreFull => ReferenceResultCode.StoreFull,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        StoreStatus.InvalidReservation => ReferenceResultCode.InvalidReservation,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapCommit(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.InvalidReservation => ReferenceResultCode.InvalidReservation,
        StoreStatus.ReservationAlreadyCompleted => ReferenceResultCode.ReservationAlreadyCompleted,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapAbort(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.InvalidReservation => ReferenceResultCode.InvalidReservation,
        StoreStatus.ReservationAlreadyCompleted => ReferenceResultCode.ReservationAlreadyCompleted,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapAcquire(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.NotFound => ReferenceResultCode.NotFound,
        StoreStatus.LeaseTableFull => ReferenceResultCode.LeaseTableFull,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapRemove(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.NotFound => ReferenceResultCode.NotFound,
        StoreStatus.RemovePending => ReferenceResultCode.RemovePending,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapRelease(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.InvalidLease => ReferenceResultCode.InvalidLease,
        StoreStatus.LeaseAlreadyReleased => ReferenceResultCode.LeaseAlreadyReleased,
        StoreStatus.StoreBusy => ReferenceResultCode.Unexpected,
        _ => ReferenceResultCode.Unexpected
    };

    private static ReferenceResultCode MapSemanticRecovery(
        StoreStatus status,
        LeaseRecoveryReport report)
    {
        if (status == StoreStatus.StoreBusy)
        {
            return ReferenceResultCode.Unexpected;
        }

        if (status != StoreStatus.Success
            || report.UnsupportedLeaseCount != 0
            || report.FailedRecoveryCount != 0)
        {
            return ReferenceResultCode.Unexpected;
        }

        return report.RecoveredLeaseCount switch
        {
            1 => ReferenceResultCode.Success,
            0 => ReferenceResultCode.InvalidLease,
            _ => ReferenceResultCode.Unexpected
        };
    }

    private static CapturedHistory Captured(
        LinearizabilityChecker checker,
        IReadOnlyList<RecordedOperation> history,
        IReadOnlyList<RecordedSlotResourceWitness> resourceWitnesses,
        int raceFirstId,
        int raceSecondId)
    {
        RecordedOperation first = history.Single(operation => operation.Id == raceFirstId);
        RecordedOperation second = history.Single(operation => operation.Id == raceSecondId);
        return new CapturedHistory(checker, history, resourceWitnesses, [first, second]);
    }

    internal static void AssertProductionRaceOverlap(
        RecordedOperation first,
        RecordedOperation second,
        string context = "production history")
    {
        if (!first.ImplementationOverlaps(second))
        {
            throw new XunitException(
                $"{context}: mapped implementation intervals did not overlap "
                + $"(first entry/return={first.EntrySequence}/{first.ReturnSequence}, "
                + $"second entry/return={second.EntrySequence}/{second.ReturnSequence}).");
        }
    }

    private static void RunConcurrent(
        PendingInvocation firstInvocation,
        Action firstBody,
        PendingInvocation secondInvocation,
        Action secondBody,
        int firstDelay,
        int secondDelay)
    {
        using var barrier = new Barrier(3);
        Exception? firstFailure = null;
        Exception? secondFailure = null;
        var firstThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                Thread.SpinWait(firstDelay);
                firstInvocation.Enter();
                firstBody();
            }
            catch (Exception error)
            {
                firstFailure = error;
            }
        })
        {
            IsBackground = true,
            Name = "sms-production-history-first"
        };
        var secondThread = new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                Thread.SpinWait(secondDelay);
                secondInvocation.Enter();
                secondBody();
            }
            catch (Exception error)
            {
                secondFailure = error;
            }
        })
        {
            IsBackground = true,
            Name = "sms-production-history-second"
        };
        firstThread.Start();
        secondThread.Start();
        barrier.SignalAndWait();
        if (!firstThread.Join(TimeSpan.FromSeconds(10))
            || !secondThread.Join(TimeSpan.FromSeconds(10)))
        {
            throw new XunitException("Production history race timed out.");
        }

        if (firstFailure is not null || secondFailure is not null)
        {
            Exception failure = firstFailure ?? secondFailure!;
            throw new XunitException(
                $"Production history actor failed: {failure.GetType().Name}: {failure.Message}");
        }
    }

    private static Store CreateStore(
        string prefix,
        int slotCount,
        int leaseCount,
        MonotonicHistoryRecorder recorder,
        bool enableLeaseRecovery = false)
    {
        SharedMemoryStoreOptions options = CreateOptions(
            prefix,
            slotCount,
            leaseCount,
            OpenMode.CreateNew,
            enableLeaseRecovery);
        StoreOpenStatus status = TryCreateInstrumented(options, recorder, out Store? store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            throw new XunitException($"Could not create production history store: {status}.");
        }

        return store;
    }

    private static Store OpenStore(
        SharedMemoryStoreOptions source,
        OpenMode openMode,
        MonotonicHistoryRecorder recorder)
    {
        var options = new SharedMemoryStoreOptions
        {
            Profile = source.Profile,
            Name = source.Name,
            OpenMode = openMode,
            TotalBytes = source.TotalBytes,
            SlotCount = source.SlotCount,
            MaxValueBytes = source.MaxValueBytes,
            MaxDescriptorBytes = source.MaxDescriptorBytes,
            MaxKeyBytes = source.MaxKeyBytes,
            LeaseRecordCount = source.LeaseRecordCount,
            ParticipantRecordCount = source.ParticipantRecordCount,
            EnableLeaseRecovery = source.EnableLeaseRecovery
        };
        StoreOpenStatus status = TryCreateInstrumented(options, recorder, out Store? store);
        if (status != StoreOpenStatus.Success || store is null)
        {
            throw new XunitException($"Could not open production history store: {status}.");
        }

        return store;
    }

    private static StoreOpenStatus TryCreateInstrumented(
        SharedMemoryStoreOptions options,
        MonotonicHistoryRecorder recorder,
        out Store? store)
    {
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            static _ => { },
            recorder.ObserveSlotResource,
            recorder,
            recorder);
        return LockFreeInstrumentedStoreFactory.TryCreateOrOpen(options, checkpoint, out store);
    }

    private static SharedMemoryStoreOptions CreateOptions(
        string prefix,
        int slotCount,
        int leaseCount,
        OpenMode openMode,
        bool enableLeaseRecovery = false) =>
        SharedMemoryStoreOptions.CreateLockFree(
            $"sms-generated-{prefix}-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: leaseCount,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery);

    private static string Format(IReadOnlyList<RecordedOperation> history) =>
        string.Join(
            "; ",
            history.Select(static operation =>
                $"#{operation.Id}/a{operation.ActorId}:{operation.Command.Kind}={operation.Result}"));

    private static int ConfiguredHistoryCount()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_PRODUCTION_HISTORY_COUNT");
        return int.TryParse(configured, out int value) && value > 0 ? value : 1;
    }

    private static int ConfiguredSeed()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_PRODUCTION_HISTORY_SEED");
        return int.TryParse(configured, out int value) ? value : DefaultSeed;
    }

    private static int FamilyOrdinal(string family) => family switch
    {
        "publish-publish" => 1,
        "publish-reserve" => 2,
        "reserve-reserve" => 3,
        "commit-acquire" => 4,
        "acquire-remove" => 5,
        "release-reclaim" => 6,
        "recovery-live-lease" => 7,
        "disposal-operation" => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
    };

    private static int FamilySeed(int rootSeed, int family) =>
        unchecked(rootSeed + family * (int)0x9e37_79b9u);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed record CapturedHistory(
        LinearizabilityChecker Checker,
        IReadOnlyList<RecordedOperation> History,
        IReadOnlyList<RecordedSlotResourceWitness> ResourceWitnesses,
        IReadOnlyList<RecordedOperation> RaceOperations);

    private struct DeterministicRandom
    {
        private uint _state;

        internal DeterministicRandom(int seed)
        {
            _state = unchecked((uint)seed);
            if (_state == 0)
            {
                _state = 0x7f4a_7c15u;
            }
        }

        internal int NextDelay()
        {
            uint value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (int)(value & 127u);
        }
    }
}
