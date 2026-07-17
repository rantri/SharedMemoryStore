using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.LinearizabilityTests;

public sealed class PublicationHistoryTests
{
    [Fact]
    [Trait("Category", "Linearizability")]
    public void ConcurrentSameKeyPublishHistoryHasExactlyOneWinner()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using var store = CreateStore(slotCount: 2, recorder);
        var first = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, "same", "left"));
        var second = recorder.Invoke(2, 2, ReferenceCommand.Publish(1, "same", "right"));

        RunConcurrent(
            () => CompletePublish(first, store, [0x51], [0x11]),
            () => CompletePublish(second, store, [0x51], [0x22]));

        var history = recorder.Snapshot();
        var result = new LinearizabilityChecker(
            participantCapacity: 4,
            valueCapacity: 2,
            initialParticipants: [1]).Check(history, recorder.ResourceSnapshot());

        Assert.All(history, static operation => Assert.True(operation.HasValidCallEnvelope));
        Assert.True(history[0].Overlaps(history[1]));
        Assert.Single(history, static operation => operation.Result == ReferenceResultCode.Success);
        Assert.Single(history, static operation => operation.Result == ReferenceResultCode.DuplicateKey);
        Assert.True(result.IsLinearizable, result.Failure);
        AssertLockFree(store);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void ConcurrentDifferentKeyPublishHistoryRespectsOneSlotGlobalCapacity()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using var store = CreateStore(slotCount: 1, recorder);
        var first = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, "left-key", "left"));
        var second = recorder.Invoke(2, 2, ReferenceCommand.Publish(1, "right-key", "right"));

        RunConcurrent(
            () => CompletePublish(first, store, [0x61], [0x11]),
            () => CompletePublish(second, store, [0x62], [0x22]));

        var history = recorder.Snapshot();
        var result = new LinearizabilityChecker(
            participantCapacity: 4,
            valueCapacity: 1,
            initialParticipants: [1]).Check(history, recorder.ResourceSnapshot());

        Assert.True(history[0].Overlaps(history[1]));
        Assert.Single(history, static operation => operation.Result == ReferenceResultCode.Success);
        Assert.Single(history, static operation => operation.Result == ReferenceResultCode.StoreFull);
        Assert.True(result.IsLinearizable, result.Failure);
        AssertLockFree(store);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public async Task StoreFullWhileOneSlotClaimIsPausedHasAnExactPhysicalWitness()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using var pause = new ClaimPause();
        using var cancellation = new CancellationTokenSource();
        using Store store = CreateStore(slotCount: 1, recorder, pause.Observe);
        PendingInvocation tentative = recorder.Invoke(
            1,
            1,
            ReferenceCommand.Publish(1, "tentative", "value"));
        StoreStatus tentativeStatus = default;
        Task tentativeTask = Task.Run(() =>
        {
            tentative.Enter();
            tentativeStatus = store.TryPublish(
                [0x71],
                [0x11],
                default,
                new StoreWaitOptions(Timeout.InfiniteTimeSpan, cancellation.Token));
            tentative.Complete(MapPublish(tentativeStatus));
        });

        try
        {
            pause.WaitUntilReached();
            PendingInvocation contender = recorder.Invoke(
                2,
                2,
                ReferenceCommand.Publish(1, "contender", "value"));
            contender.Enter();
            StoreStatus contenderStatus = store.TryPublish(
                [0x72],
                [0x22],
                default,
                StoreWaitOptions.Infinite);
            RecordedOperation contenderOperation = contender.Complete(MapPublish(contenderStatus));

            cancellation.Cancel();
            pause.Resume();
            await tentativeTask.WaitAsync(TimeSpan.FromSeconds(10));

            IReadOnlyList<RecordedOperation> history = recorder.Snapshot();
            IReadOnlyList<RecordedSlotResourceWitness> resources = recorder.ResourceSnapshot();
            LinearizabilityCheckResult result = new LinearizabilityChecker(
                participantCapacity: 4,
                valueCapacity: 1,
                initialParticipants: [1]).Check(history, resources);

            Assert.Equal(StoreStatus.StoreFull, contenderStatus);
            Assert.Equal(StoreStatus.OperationCanceled, tentativeStatus);
            RecordedSlotResourceWitness claim = Assert.Single(
                resources,
                static witness => witness.Kind == RecordedSlotResourceKind.Claim);
            RecordedSlotResourceWitness freed = Assert.Single(
                resources,
                static witness => witness.Kind == RecordedSlotResourceKind.Free);
            Assert.True(claim.Sequence < contenderOperation.ReturnSequence);
            Assert.True(freed.Sequence > contenderOperation.ReturnSequence);
            Assert.True(result.IsLinearizable, result.Failure);
        }
        finally
        {
            cancellation.Cancel();
            pause.Resume();
            await tentativeTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void StableTwoSlotCapacityEmitsOneExactStoreFullProof()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using Store store = CreateStore(slotCount: 2, recorder);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x41], [0x11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x42], [0x22]));

        PendingInvocation contender = recorder.Invoke(
            1,
            1,
            ReferenceCommand.Publish(1, "contender", "value"));
        contender.Enter();
        StoreStatus status = store.TryPublish([0x43], [0x33], default, StoreWaitOptions.Infinite);
        RecordedOperation operation = contender.Complete(MapPublish(status));
        IReadOnlyList<RecordedSlotResourceWitness> resources = recorder.ResourceSnapshot();

        Assert.Equal(StoreStatus.StoreFull, status);
        RecordedSlotResourceWitness proof = Assert.Single(
            resources,
            static witness => witness.Kind == RecordedSlotResourceKind.StoreFullProof);
        Assert.InRange(proof.Sequence, operation.EntrySequence + 1, operation.ReturnSequence - 1);
        LinearizabilityCheckResult result = new LinearizabilityChecker(
            participantCapacity: 4,
            valueCapacity: 2,
            initialParticipants: [1]).Check([operation], resources);
        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public async Task SlotMovementBetweenCollectsMakesInfiniteCallerRetryAndClaimWithoutAFullProof()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var recorder = new MonotonicHistoryRecorder();
        using var pause = new StoreFullProofPause();
        using Store store = CreateStore(slotCount: 2, recorder, pause.Observe);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x51], [0x11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([0x52], [0x22]));

        PendingInvocation contender = recorder.Invoke(
            1,
            1,
            ReferenceCommand.Publish(1, "contender", "value"));
        StoreStatus contenderStatus = default;
        Task contenderTask = Task.Run(() =>
        {
            contender.Enter();
            contenderStatus = store.TryPublish(
                [0x53],
                [0x33],
                default,
                StoreWaitOptions.Infinite);
            contender.Complete(MapPublish(contenderStatus));
        });

        try
        {
            pause.WaitUntilReached();
            Assert.Equal(StoreStatus.Success, store.TryRemove([0x51], StoreWaitOptions.Infinite));
            pause.Resume();
            await contenderTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(StoreStatus.Success, contenderStatus);
            Assert.DoesNotContain(
                recorder.ResourceSnapshot(),
                static witness => witness.Kind == RecordedSlotResourceKind.StoreFullProof);
            Assert.Equal(StoreStatus.StoreFull, store.TryPublish([0x54], [0x44]));
        }
        finally
        {
            pause.Resume();
            await contenderTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static Store CreateStore(
        int slotCount,
        MonotonicHistoryRecorder recorder,
        Action<LockFreeCheckpointEntry>? checkpointObserver = null)
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-linearizable-publish-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew);
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            entry => checkpointObserver?.Invoke(entry),
            recorder.ObserveSlotResource,
            recorder,
            recorder);
        var status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(options, checkpoint, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<Store>(store);
    }

    private static void CompletePublish(
        PendingInvocation invocation,
        Store store,
        byte[] key,
        byte[] value)
    {
        invocation.Enter();
        var status = store.TryPublish(key, value);
        invocation.Complete(status switch
        {
            StoreStatus.Success => ReferenceResultCode.Success,
            StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
            StoreStatus.StoreFull => ReferenceResultCode.StoreFull,
            StoreStatus.OperationCanceled => ReferenceResultCode.OperationCanceled,
            StoreStatus.StoreBusy => ReferenceResultCode.StoreBusy,
            _ => ReferenceResultCode.Unexpected
        });
    }

    private static ReferenceResultCode MapPublish(StoreStatus status) => status switch
    {
        StoreStatus.Success => ReferenceResultCode.Success,
        StoreStatus.DuplicateKey => ReferenceResultCode.DuplicateKey,
        StoreStatus.StoreFull => ReferenceResultCode.StoreFull,
        StoreStatus.OperationCanceled => ReferenceResultCode.OperationCanceled,
        StoreStatus.StoreBusy => ReferenceResultCode.StoreBusy,
        _ => ReferenceResultCode.Unexpected
    };

    private static void RunConcurrent(Action first, Action second)
    {
        using var start = new Barrier(participantCount: 3);
        var firstTask = Task.Run(() =>
        {
            start.SignalAndWait();
            first();
        });
        var secondTask = Task.Run(() =>
        {
            start.SignalAndWait();
            second();
        });
        start.SignalAndWait();
        Assert.True(Task.WaitAll([firstTask, secondTask], TimeSpan.FromSeconds(10)));
    }

    private static void AssertLockFree(Store store)
    {
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), store.ProtocolInfo);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class ClaimPause : IDisposable
    {
        private readonly ManualResetEventSlim _reached = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _paused;

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id != LockFreeCheckpointId.SlotClaimAfterParticipantRecheck
                || Interlocked.Exchange(ref _paused, 1) != 0)
            {
                return;
            }

            _reached.Set();
            if (!_resume.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new Xunit.Sdk.XunitException("Paused claim was not resumed within the test bound.");
            }
        }

        internal void WaitUntilReached()
        {
            Assert.True(_reached.Wait(TimeSpan.FromSeconds(10)), "The slot claim checkpoint was not reached.");
        }

        internal void Resume() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _reached.Dispose();
            _resume.Dispose();
        }
    }

    private sealed class StoreFullProofPause : IDisposable
    {
        private readonly ManualResetEventSlim _reached = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private int _paused;

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id != LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
                || Interlocked.Exchange(ref _paused, 1) != 0)
            {
                return;
            }

            _reached.Set();
            if (!_resume.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new Xunit.Sdk.XunitException("Paused StoreFull proof was not resumed within the test bound.");
            }
        }

        internal void WaitUntilReached() =>
            Assert.True(_reached.Wait(TimeSpan.FromSeconds(10)), "The StoreFull proof checkpoint was not reached.");

        internal void Resume() => _resume.Set();

        public void Dispose()
        {
            _resume.Set();
            _reached.Dispose();
            _resume.Dispose();
        }
    }
}
