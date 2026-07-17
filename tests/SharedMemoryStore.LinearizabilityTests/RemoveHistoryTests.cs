using System.Runtime.InteropServices;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.LinearizabilityTests;

public sealed class RemoveHistoryTests
{
    [Fact]
    [Trait("Category", "Linearizability")]
    public void OverlappingAcquireSuccessAndRemovePendingLinearizeAcquireFirst()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, 1, 3, 5, 8),
            Operation(2, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.RemovePending, 2, 4, 6, 7)
        };

        var result = CreateChecker().Check(WithPublishedSetup(history));

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([63, 1, 2], result.Linearization);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void OverlappingRemoveAndNotFoundAcquireLinearizeRemoveFirst()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.NotFound, 1, 3, 7, 8),
            Operation(2, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.Success, 2, 4, 5, 6)
        };

        var result = CreateChecker().Check(WithPublishedSetup(history));

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([63, 2, 1], result.Linearization);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void AcquireCannotSucceedWhenInvokedAfterCompletedRemoval()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, 5, 6, 7, 8)
        };

        var result = CreateChecker().Check(WithPublishedSetup(history));

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void RemovePendingWithoutALeaseRepresentsCompletedLogicalRemovalWithBoundedWorkRemaining()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "key", "first"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.RemovePending, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.Publish(1, "key", "second"), ReferenceResultCode.Success, 9, 10, 11, 12)
        };

        var result = CreateChecker().Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2, 3], result.Linearization);
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public void SequentialInfiniteRemoveRetriesRemainPendingUntilProtectingLeaseIsReleased()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var store = CreateStore();
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish([1], [9], default, StoreWaitOptions.Infinite));
        var recorder = new MonotonicHistoryRecorder();
        ValueLease lease = default;

        try
        {
            PendingInvocation acquire = recorder.Invoke(
                1,
                actorId: 1,
                ReferenceCommand.AcquireLease(1, 20, "key"));
            acquire.Enter();
            StoreStatus acquireStatus = store.TryAcquire(
                [1],
                StoreWaitOptions.Infinite,
                out lease);
            Assert.Equal(StoreStatus.Success, acquireStatus);
            acquire.Complete(ReferenceResultCode.Success);

            PendingInvocation firstRemove = recorder.Invoke(
                2,
                actorId: 2,
                ReferenceCommand.Remove(1, "key"));
            firstRemove.Enter();
            StoreStatus firstRemoveStatus = store.TryRemove([1], StoreWaitOptions.Infinite);
            Assert.Equal(StoreStatus.RemovePending, firstRemoveStatus);
            firstRemove.Complete(ReferenceResultCode.RemovePending);

            PendingInvocation secondRemove = recorder.Invoke(
                3,
                actorId: 2,
                ReferenceCommand.Remove(1, "key"));
            secondRemove.Enter();
            StoreStatus secondRemoveStatus = store.TryRemove([1], StoreWaitOptions.Infinite);
            Assert.Equal(StoreStatus.RemovePending, secondRemoveStatus);
            secondRemove.Complete(ReferenceResultCode.RemovePending);

            PendingInvocation release = recorder.Invoke(
                4,
                actorId: 1,
                ReferenceCommand.ReleaseLease(1, 20));
            release.Enter();
            StoreStatus releaseStatus = lease.Release(StoreWaitOptions.Infinite);
            Assert.Equal(StoreStatus.Success, releaseStatus);
            release.Complete(ReferenceResultCode.Success);

            Assert.Equal(
                StoreStatus.Success,
                store.TryGetDiagnostics(StoreWaitOptions.Infinite, out var diagnostics));
            Assert.Equal(0, diagnostics.PendingRemovalCount);
            Assert.Equal(2, diagnostics.FreeSlotCount);

            PendingInvocation finalRemove = recorder.Invoke(
                5,
                actorId: 2,
                ReferenceCommand.Remove(1, "key"));
            finalRemove.Enter();
            StoreStatus finalRemoveStatus = store.TryRemove([1], StoreWaitOptions.Infinite);
            Assert.Equal(StoreStatus.NotFound, finalRemoveStatus);
            finalRemove.Complete(ReferenceResultCode.NotFound);

            LinearizabilityCheckResult result = CreateChecker().Check(
                WithPublishedSetup(recorder.Snapshot()));

            Assert.True(result.IsLinearizable, result.Failure);
            Assert.Equal([63, 1, 2, 3, 4, 5], result.Linearization);
        }
        finally
        {
            if (lease.IsValid)
            {
                _ = lease.Release(StoreWaitOptions.Infinite);
            }
        }
    }

    [Fact]
    [Trait("Category", "Linearizability")]
    public async Task PublicOverlappingAcquireRemoveHistoryMatchesReferenceOrdering()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var store = CreateStore();
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
        var recorder = new MonotonicHistoryRecorder();
        var acquireInvocation = recorder.Invoke(1, 1, ReferenceCommand.AcquireLease(1, 20, "key"));
        var removeInvocation = recorder.Invoke(2, 2, ReferenceCommand.Remove(1, "key"));
        ValueLease lease = default;

        await RunConcurrent(
            () =>
            {
                acquireInvocation.Enter();
                var status = store.TryAcquire([1], out lease);
                acquireInvocation.Complete(status switch
                {
                    StoreStatus.Success => ReferenceResultCode.Success,
                    StoreStatus.NotFound => ReferenceResultCode.NotFound,
                    _ => ReferenceResultCode.Unexpected
                });
            },
            () =>
            {
                removeInvocation.Enter();
                var status = store.TryRemove([1]);
                removeInvocation.Complete(status switch
                {
                    StoreStatus.Success => ReferenceResultCode.Success,
                    StoreStatus.RemovePending => ReferenceResultCode.RemovePending,
                    StoreStatus.NotFound => ReferenceResultCode.NotFound,
                    _ => ReferenceResultCode.Unexpected
                });
            });

        var result = CreateChecker().Check(WithPublishedSetup(recorder.Snapshot()));
        try
        {
            Assert.True(result.IsLinearizable, result.Failure);
        }
        finally
        {
            if (lease.IsValid)
            {
                Assert.Equal(StoreStatus.Success, lease.Release());
            }
        }
    }

    private static LinearizabilityChecker CreateChecker() =>
        new(2, 2, initialParticipants: [1]);

    private static IReadOnlyList<RecordedOperation> WithPublishedSetup(
        IReadOnlyList<RecordedOperation> operations)
    {
        var history = new RecordedOperation[operations.Count + 1];
        history[0] = new RecordedOperation(
            63,
            1,
            ReferenceCommand.Publish(1, "key", "value"),
            ReferenceResultCode.Success,
            -4,
            -3,
            -2,
            -1);
        for (var index = 0; index < operations.Count; index++)
        {
            history[index + 1] = operations[index];
        }

        return history;
    }

    private static RecordedOperation Operation(
        int id,
        ReferenceCommand command,
        ReferenceResultCode result,
        long invocation,
        long entry,
        long returned,
        long response) =>
        new(id, id, command, result, invocation, entry, returned, response);

    private static Store CreateStore()
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-remove-history-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, Store.TryCreateOrOpen(options, out var store));
        return Assert.IsType<Store>(store);
    }

    private static async Task RunConcurrent(Action first, Action second)
    {
        using var barrier = new Barrier(3);
        var firstTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            first();
        });
        var secondTask = Task.Run(() =>
        {
            barrier.SignalAndWait();
            second();
        });
        barrier.SignalAndWait();
        await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
