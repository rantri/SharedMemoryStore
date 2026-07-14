using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeRemoveStateTests
{
    [Fact]
    public async Task AcquireOrderedBeforeLogicalRemoveReturnsLeaseAndRemovePending()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7, 8]));
        scheduler.PauseAt(LockFreeCheckpointId.AcquireAfterPublishedRevalidation);

        ValueLease lease = default;
        StoreStatus acquireStatus = default;
        var acquire = Task.Run(() => acquireStatus = store.TryAcquire([1], out lease));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        StoreStatus removeStatus = store.TryRemove([1], StoreWaitOptions.NoWait);
        scheduler.Continue();
        await acquire.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, acquireStatus);
        Assert.Equal(StoreStatus.RemovePending, removeStatus);
        Assert.Equal(new byte[] { 7, 8 }, lease.ValueSpan.ToArray());
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task LogicalRemoveOrderedBeforeLeaseClaimMakesAcquireReturnNotFound()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        scheduler.PauseAt(LockFreeCheckpointId.AcquireBeforeLeaseClaimCas);

        ValueLease lease = default;
        StoreStatus acquireStatus = default;
        var acquire = Task.Run(() => acquireStatus = store.TryAcquire([1], out lease));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        StoreStatus removeStatus = store.TryRemove([1]);
        scheduler.Continue();
        await acquire.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(removeStatus, new[] { StoreStatus.Success, StoreStatus.RemovePending });
        Assert.Equal(StoreStatus.NotFound, acquireStatus);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public async Task AcquireAfterNoLeaseClassificationCannotEscapeBeforeReclaimOwnership()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7, 8]));
        scheduler.PauseAt(LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove([1]));
        Assert.True(
            scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)),
            "Remove never reached its sole lease scan before reclaim ownership.");

        StoreStatus acquireStatus = store.TryAcquire([1], out ValueLease lease);

        Assert.Equal(StoreStatus.NotFound, acquireStatus);
        Assert.False(lease.IsValid);
        Assert.Equal(0, lease.ValueSpan.Length);

        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(removeStatus, new[] { StoreStatus.Success, StoreStatus.RemovePending });
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out ValueLease replacement));
        Assert.Equal(9, replacement.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public void CancellationBeforeLogicalRemovalPreservesPublishedGeneration()
    {
        using var store = CreateStore();
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var status = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token));

        Assert.Equal(StoreStatus.OperationCanceled, status);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(5, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task CancellationAtPreRemovalCheckpointDoesNotCrossLogicalRemovalPoint()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token)));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Remove never reached its pre-ordering checkpoint.");
        Assert.Equal(StoreStatus.OperationCanceled, removeStatus);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var preserved));
        Assert.Equal(5, preserved.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, preserved.Release());
    }

    [Fact]
    public async Task DeadlineAtPreRemovalCheckpointDoesNotCrossLogicalRemovalPoint()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));
        scheduler.PauseAt(LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50))));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Remove never reached its pre-ordering checkpoint.");
        Assert.Equal(StoreStatus.StoreBusy, removeStatus);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var preserved));
        Assert.Equal(StoreStatus.Success, preserved.Release());
    }

    [Fact]
    public async Task CancellationAfterLogicalRemovalAndClassificationDoesNotUndoOutcome()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RemoveAfterLeaseClassification);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token)));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromMilliseconds(500));
        cancellation.Cancel();
        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Remove never reached its post-ordering classification checkpoint.");
        Assert.Contains(removeStatus, new[] { StoreStatus.Success, StoreStatus.RemovePending });
        Assert.NotEqual(StoreStatus.OperationCanceled, removeStatus);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
    }

    [Fact]
    public async Task DeadlineAfterLogicalRemovalAndClassificationDoesNotUndoOutcome()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        scheduler.PauseAt(LockFreeCheckpointId.RemoveAfterLeaseClassification);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50))));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Remove never reached its post-ordering classification checkpoint.");
        Assert.Equal(StoreStatus.RemovePending, removeStatus);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void PostRemovalNoWaitScanExpiryReturnsConservativePending()
    {
        using var store = CreateStore(leaseRecordCount: 8_192);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [5]));

        StoreStatus removeStatus = store.TryRemove([1], StoreWaitOptions.NoWait);

        Assert.Equal(StoreStatus.RemovePending, removeStatus);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [6]));
    }

    [Fact]
    public void NoWaitRemovalWithActiveLeaseReturnsConservativePendingAndLogicalAbsence()
    {
        using var store = CreateStore();
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [3]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        var status = store.TryRemove([1], StoreWaitOptions.NoWait);

        Assert.Equal(StoreStatus.RemovePending, status);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.True(lease.IsValid);
        Assert.Equal(3, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task LeaseProjectionRetriesAcrossLogicalRemoveWithoutReturningTransientEmptyData()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        byte[] key = [0x31];
        byte[] value = [0x41, 0x42, 0x43];
        byte[] descriptor = [0x51, 0x52];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, value, descriptor));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        scheduler.PauseAt(LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation);

        byte[]? projectedValue = null;
        byte[]? projectedDescriptor = null;
        var projection = Task.Run(() =>
        {
            projectedValue = lease.ValueSpan.ToArray();
            projectedDescriptor = lease.DescriptorSpan.ToArray();
        });
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key, StoreWaitOptions.NoWait));
        scheduler.Continue();
        await projection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(value, projectedValue);
        Assert.Equal(descriptor, projectedDescriptor);
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x61]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease replacement));
        Assert.Equal(0x61, replacement.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task CopiedLeaseProjectionRacingReleaseReclaimAndReuseExpiresWithoutPoisoningStore()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler);
        byte[] key = [0x32];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x71, 0x72], [0x73]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        ValueLease copiedLease = lease;
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key, StoreWaitOptions.NoWait));
        scheduler.PauseAt(LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation);

        byte[]? projectedValue = null;
        var projection = Task.Run(() => projectedValue = copiedLease.ValueSpan.ToArray());
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x81, 0x82], [0x83]));
        scheduler.Continue();
        await projection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(Assert.IsType<byte[]>(projectedValue));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease replacement));
        Assert.Equal(new byte[] { 0x81, 0x82 }, replacement.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 0x83 }, replacement.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    private static MemoryStore CreateInstrumentedStore(ControlledLockFreeScheduler scheduler)
    {
        var options = Options();
        var status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateStore(int leaseRecordCount = 4)
    {
        var status = MemoryStore.TryCreateOrOpen(Options(leaseRecordCount), out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(int leaseRecordCount = 4) => SharedMemoryStoreOptions.CreateLockFree(
        $"sms-v2-remove-state-{Guid.NewGuid():N}",
        slotCount: 2,
        maxValueBytes: 16,
        maxDescriptorBytes: 4,
        maxKeyBytes: 8,
        leaseRecordCount,
        participantRecordCount: 2,
        openMode: OpenMode.CreateNew,
        enableLeaseRecovery: true);
}
