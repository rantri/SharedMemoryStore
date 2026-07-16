using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.LinearizabilityTests;

public sealed class LeaseCapacityHistoryTests
{
    private static readonly TimeSpan TestBound = TimeSpan.FromSeconds(10);

    [Fact]
    [Trait("Category", "Linearizability")]
    public void StableLeaseCapacityEmitsOneExactProofInsideReturningAcquire()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using Store store = CreateStore(recorder, recorder);
        CompletePublish(recorder, 1, store, [0x11], "first");
        CompletePublish(recorder, 2, store, [0x12], "second");

        ValueLease held = default;
        try
        {
            PendingInvocation firstAcquire = recorder.Invoke(
                3,
                1,
                ReferenceCommand.AcquireLease(1, 20, "first"));
            firstAcquire.Enter();
            StoreStatus firstStatus = store.TryAcquire(
                [0x11],
                StoreWaitOptions.Infinite,
                out held);
            Assert.Equal(StoreStatus.Success, firstStatus);
            firstAcquire.Complete(ReferenceResultCode.Success);

            PendingInvocation contender = recorder.Invoke(
                4,
                2,
                ReferenceCommand.AcquireLease(1, 21, "second"));
            contender.Enter();
            StoreStatus contenderStatus = store.TryAcquire(
                [0x12],
                StoreWaitOptions.Infinite,
                out _);
            RecordedOperation contenderOperation = contender.Complete(
                MapAcquire(contenderStatus));

            IReadOnlyList<RecordedSlotResourceWitness> resources =
                recorder.ResourceSnapshot();
            RecordedSlotResourceWitness proof = Assert.Single(
                resources,
                static witness =>
                    witness.Kind == RecordedSlotResourceKind.LeaseTableFullProof);
            Assert.Equal(StoreStatus.LeaseTableFull, contenderStatus);
            Assert.Equal(1, proof.Generation);
            Assert.InRange(
                proof.Sequence,
                contenderOperation.EntrySequence + 1,
                contenderOperation.ReturnSequence - 1);
            Assert.InRange(
                proof.ConfirmationSequence,
                proof.Sequence + 1,
                contenderOperation.ReturnSequence - 1);

            LinearizabilityCheckResult result = new LinearizabilityChecker(
                participantCapacity: 4,
                valueCapacity: 2,
                initialParticipants: [1],
                leaseCapacity: 1).Check(recorder.Snapshot(), resources);
            Assert.True(result.IsLinearizable, result.Failure);
        }
        finally
        {
            if (held.IsValid)
            {
                Assert.Equal(StoreStatus.Success, held.Release());
            }
        }
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public async Task LeaseMovementRejectsCandidateAndInfiniteAcquireRetriesToSuccess()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using var pause = new LeaseTableFullProofPause(recorder);
        using Store store = CreateStore(recorder, pause);
        CompletePublish(recorder, 1, store, [0x21], "key");

        ValueLease held = default;
        ValueLease replacement = default;
        PendingInvocation firstAcquire = recorder.Invoke(
            2,
            1,
            ReferenceCommand.AcquireLease(1, 20, "key"));
        firstAcquire.Enter();
        StoreStatus firstStatus = store.TryAcquire(
            [0x21],
            StoreWaitOptions.Infinite,
            out held);
        Assert.Equal(StoreStatus.Success, firstStatus);
        firstAcquire.Complete(ReferenceResultCode.Success);

        PendingInvocation contender = recorder.Invoke(
            3,
            2,
            ReferenceCommand.AcquireLease(1, 21, "key"));
        StoreStatus contenderStatus = default;
        Task contenderTask = Task.Run(() =>
        {
            contender.Enter();
            contenderStatus = store.TryAcquire(
                [0x21],
                StoreWaitOptions.Infinite,
                out replacement);
            contender.Complete(MapAcquire(contenderStatus));
        });

        try
        {
            pause.WaitUntilReached();
            PendingInvocation release = recorder.Invoke(
                4,
                1,
                ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            StoreStatus releaseStatus = held.Release(StoreWaitOptions.Infinite);
            Assert.Equal(StoreStatus.Success, releaseStatus);
            release.Complete(ReferenceResultCode.Success);
            pause.Resume();
            await contenderTask.WaitAsync(TestBound);

            Assert.Equal(StoreStatus.Success, contenderStatus);
            Assert.True(replacement.IsValid);
            Assert.DoesNotContain(
                recorder.ResourceSnapshot(),
                static witness =>
                    witness.Kind == RecordedSlotResourceKind.LeaseTableFullProof);
            LinearizabilityCheckResult result = new LinearizabilityChecker(
                participantCapacity: 4,
                valueCapacity: 2,
                initialParticipants: [1],
                leaseCapacity: 1).Check(
                    recorder.Snapshot(),
                    recorder.ResourceSnapshot());
            Assert.True(result.IsLinearizable, result.Failure);
        }
        finally
        {
            pause.Resume();
            await contenderTask.WaitAsync(TestBound);
            if (held.IsValid)
            {
                Assert.Equal(StoreStatus.Success, held.Release());
            }

            if (replacement.IsValid)
            {
                Assert.Equal(StoreStatus.Success, replacement.Release());
            }
        }
    }

    private static Store CreateStore(
        MonotonicHistoryRecorder recorder,
        ILockFreeLeaseTableFullProofObserver leaseProofObserver)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-linearizable-lease-full-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew);
        InstrumentedLockFreeCheckpoint checkpoint =
            LockFreeCheckpointFactory.CreateInstrumented(
                static _ => { },
                recorder.ObserveSlotResource,
                recorder,
                leaseProofObserver);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            checkpoint,
            out Store? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }

    private static void CompletePublish(
        MonotonicHistoryRecorder recorder,
        int operationId,
        Store store,
        byte[] key,
        string modelKey)
    {
        PendingInvocation publish = recorder.Invoke(
            operationId,
            1,
            ReferenceCommand.Publish(1, modelKey, "value"));
        publish.Enter();
        StoreStatus status = store.TryPublish(key, [0x31]);
        Assert.Equal(StoreStatus.Success, status);
        publish.Complete(ReferenceResultCode.Success);
    }

    private static ReferenceResultCode MapAcquire(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.NotFound => ReferenceResultCode.NotFound,
        StoreStatus.LeaseTableFull => ReferenceResultCode.LeaseTableFull,
        StoreStatus.StoreBusy => ReferenceResultCode.StoreBusy,
        StoreStatus.OperationCanceled => ReferenceResultCode.OperationCanceled,
        _ => ReferenceResultCode.Unexpected
    };

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class LeaseTableFullProofPause :
        ILockFreeLeaseTableFullProofObserver,
        IDisposable
    {
        private readonly ILockFreeLeaseTableFullProofObserver _inner;
        private readonly ManualResetEventSlim _reached = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _paused;

        internal LeaseTableFullProofPause(
            ILockFreeLeaseTableFullProofObserver inner)
        {
            _inner = inner;
        }

        public long BeginCandidate(int leaseRecordCount)
        {
            long token = _inner.BeginCandidate(leaseRecordCount);
            if (Interlocked.CompareExchange(ref _paused, 1, 0) == 0)
            {
                _reached.Set();
                if (!_resume.Wait(TestBound))
                {
                    throw new Xunit.Sdk.XunitException(
                        "Paused LeaseTableFull proof was not resumed within the test bound.");
                }
            }

            return token;
        }

        public void CompleteCandidate(long token, bool confirmed) =>
            _inner.CompleteCandidate(token, confirmed);

        internal void WaitUntilReached() => Assert.True(
            _reached.Wait(TestBound),
            "The LeaseTableFull proof candidate was not reached.");

        internal void Resume() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _reached.Dispose();
            _resume.Dispose();
        }
    }
}
