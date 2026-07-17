using System.Reflection;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeReclamationTests
{
    [Theory]
    [InlineData(StoreStatus.Success, StoreStatus.Success)]
    [InlineData(StoreStatus.RemovePending, StoreStatus.RemovePending)]
    [InlineData(StoreStatus.StoreBusy, StoreStatus.RemovePending)]
    [InlineData(StoreStatus.NotFound, StoreStatus.RemovePending)]
    [InlineData(StoreStatus.InvalidReservation, StoreStatus.RemovePending)]
    [InlineData(StoreStatus.CorruptStore, StoreStatus.CorruptStore)]
    public void PostLogicalRemovePreservesDurableOutcome(
        StoreStatus reclaimStatus,
        StoreStatus expected)
    {
        Assert.Equal(
            expected,
            LockFreeStoreEngine.NormalizePostLogicalRemoveOutcome(reclaimStatus));
    }

    [Theory]
    [InlineData(StoreStatus.Success, StoreStatus.Success)]
    [InlineData(StoreStatus.RemovePending, StoreStatus.DuplicateKey)]
    [InlineData(StoreStatus.NotFound, StoreStatus.DuplicateKey)]
    [InlineData(StoreStatus.StoreBusy, StoreStatus.StoreBusy)]
    [InlineData(StoreStatus.OperationCanceled, StoreStatus.OperationCanceled)]
    [InlineData(StoreStatus.CorruptStore, StoreStatus.CorruptStore)]
    public void ExistingGenerationNormalizationNeverMasksOperationalOrStructuralFailure(
        StoreStatus reclaimStatus,
        StoreStatus expected)
    {
        Assert.Equal(
            expected,
            LockFreeStoreEngine.NormalizeExistingGenerationReclaimOutcome(reclaimStatus));
    }

    [Theory]
    [InlineData(StoreStatus.Success, StoreStatus.Success)]
    [InlineData(StoreStatus.NotFound, StoreStatus.Success)]
    [InlineData(StoreStatus.StoreBusy, StoreStatus.StoreBusy)]
    [InlineData(StoreStatus.OperationCanceled, StoreStatus.OperationCanceled)]
    [InlineData(StoreStatus.CorruptStore, StoreStatus.CorruptStore)]
    public void AbortingUnlinkScanNormalizesOnlyExactAbsence(
        StoreStatus unlinkStatus,
        StoreStatus expected)
    {
        Assert.Equal(
            expected,
            LockFreeReclaimer.NormalizeAbortingUnlinkOutcome(unlinkStatus));
    }

    [Theory]
    [InlineData(StoreStatus.Success, StoreStatus.Success)]
    [InlineData(StoreStatus.NotFound, StoreStatus.Success)]
    [InlineData(StoreStatus.RemovePending, StoreStatus.Success)]
    [InlineData(StoreStatus.StoreBusy, StoreStatus.StoreBusy)]
    [InlineData(StoreStatus.OperationCanceled, StoreStatus.OperationCanceled)]
    [InlineData(StoreStatus.CorruptStore, StoreStatus.CorruptStore)]
    public void ReclaimScanNormalizesOnlyConservativeLifecycleRaces(
        StoreStatus reclaimStatus,
        StoreStatus expected)
    {
        Assert.Equal(
            expected,
            LockFreeReclaimer.NormalizeObservedReclaimOutcome(reclaimStatus));
    }

    [Fact]
    public void FinalReleaseReclaimsExactRemovedGenerationAndAllowsRepublish()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2, 3]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [4]));

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [4]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement));
        Assert.Equal(4, replacement.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task ExistingLookupThatDisappearsDuringFinalReleaseContinuesRepublishInsteadOfReturningNotFound()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        byte[] key = [0x71];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key));
        scheduler.PauseAt(LockFreeCheckpointId.ReserveAfterExistingLookup);

        StoreStatus republishStatus = default;
        var republish = Task.Run(() => republishStatus = store.TryPublish(
            key,
            [2],
            default,
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        // Final release removes the exact old directory generation while the
        // publisher is between its existing-key lookup and revalidation.
        Assert.Equal(StoreStatus.Success, lease.Release(StoreWaitOptions.Infinite));
        scheduler.Continue();
        await republish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, republishStatus);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var current));
        Assert.Equal(2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public async Task SecondHandlePublishesAtCapacityOneWhileAbortOwnerIsPausedAfterOwnershipRelease()
    {
        string name = $"sms-v2-reclamation-abort-help-{Guid.NewGuid():N}";
        var createOptions = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 1,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var openOptions = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 1,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.OpenExisting,
            enableLeaseRecovery: true);
        using var scheduler = new ControlledLockFreeScheduler();
        Assert.Equal(
            StoreOpenStatus.Success,
            LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                createOptions,
                scheduler.CreateInstrumentedCheckpoint(),
                out var firstHandle));
        using var first = Assert.IsType<MemoryStore>(firstHandle);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(openOptions, out var secondHandle));
        using var second = Assert.IsType<MemoryStore>(secondHandle);

        Assert.Equal(StoreStatus.Success, first.TryReserve([0x81], 1, default, out var reservation));
        reservation.GetSpan(1)[0] = 1;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        scheduler.PauseAt(LockFreeCheckpointId.AbortAfterOwnershipReleaseCas);

        StoreStatus abortStatus = default;
        var abort = Task.Run(() => abortStatus = reservation.Abort(StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        // The other process/handle sees an unowned Aborting slot. Allocation
        // pressure must derive the exact binding from control, help it to the
        // next generation, and retry its claim rather than return StoreFull.
        Assert.Equal(
            StoreStatus.Success,
            second.TryPublish([0x82], [2], default, StoreWaitOptions.Infinite));

        scheduler.Continue();
        await abort.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, abortStatus);
        Assert.Equal(StoreStatus.Success, second.TryAcquire([0x82], out var current));
        Assert.Equal(2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public void StaleCopiedLeaseCannotReleaseReusedLeaseRecordOrSlotGeneration()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        var stale = first;
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, first.Release());

        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [2]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var current));
        Assert.Contains(stale.Release(), new[] { StoreStatus.InvalidLease, StoreStatus.LeaseAlreadyReleased });
        Assert.True(current.IsValid);
        Assert.Equal(2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public void ExactUnlinkOfRemovedKeyPreservesUnrelatedPublishedBinding()
    {
        using var store = CreateStore(slotCount: 2, leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [22]));

        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var unrelated));
        Assert.Equal(22, unrelated.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, unrelated.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [33]));
    }

    [Fact]
    public void RetryingRemovalBeforeRepublishCannotAffectLaterGeneration()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.NotFound, store.TryRemove([1]));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [2]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var current));
        Assert.Equal(2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public async Task ConcurrentFinalReleaseAndRetryingRemoveReclaimExactlyOnce()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));

        StoreStatus releaseStatus = default;
        StoreStatus retryStatus = default;
        using var start = new Barrier(3);
        var release = Task.Run(() =>
        {
            start.SignalAndWait();
            releaseStatus = lease.Release();
        });
        var retry = Task.Run(() =>
        {
            start.SignalAndWait();
            retryStatus = store.TryRemove([1]);
        });
        start.SignalAndWait();
        await Task.WhenAll(release, retry).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, releaseStatus);
        Assert.Contains(retryStatus, new[]
        {
            StoreStatus.Success,
            StoreStatus.RemovePending,
            StoreStatus.NotFound
        });
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [2]));
    }

    [Fact]
    public async Task CancellationAfterLogicalRemovalHandsPendingReclaimToFinalRelease()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RemoveAfterLeaseClassification);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(
            [1],
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token)));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        scheduler.Continue();
        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Remove never published a helpable pending-removal state.");
        Assert.Equal(StoreStatus.RemovePending, removeStatus);
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [2]));
    }

    [Fact]
    public async Task PausedStaleRemoveCannotRemoveRepublishedSlotGeneration()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        scheduler.PauseAt(LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas);

        StoreStatus staleStatus = default;
        var staleRemove = Task.Run(() => staleStatus = store.TryRemove([1]));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [2]));
        scheduler.Continue();
        await staleRemove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.NotFound, staleStatus);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var current));
        Assert.Equal(2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public async Task DelayedTargetSelectedInsertHelperCannotOrphanProgressedBinding()
    {
        byte[][] keys = GenerateCanonicalBucketCollisions(count: 5, slotCount: 2);
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 2, leaseRecordCount: 2);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterOperationValidation, occurrence: 2);

        StoreStatus firstPublishStatus = default;
        var firstPublish = Task.Run(() => firstPublishStatus = store.TryPublish(keys[0], [0xA1]));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        StoreStatus oneStepStatus = default;
        SlotSnapshot progressed = default;
        try
        {
            LockFreeKeyDirectory directory = ReadDirectory(store);
            oneStepStatus = directory.HelpMutation(canonicalBucketIndex: 0, maxSteps: 1);
            progressed = RequireOperationSnapshot(store, slotCount: 2, intent: 1, phase: 3);
        }
        finally
        {
            scheduler.Continue();
        }

        await firstPublish.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Insert helper never paused after observing TargetSelected.");
        Assert.Equal(StoreStatus.StoreBusy, oneStepStatus);
        Assert.Equal(LockFreeSlotTable.InitializingState, SlotState(progressed.Control));
        AssertGenerationConsistent(progressed);
        Assert.Equal(StoreStatus.Success, firstPublishStatus);
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[1], [0xB2]));
        AssertPublishedValue(store, keys[0], 0xA1);
        AssertPublishedValue(store, keys[1], 0xB2);

        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[0]));
        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[2], [0xC3]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[3], [0xD4]));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish(keys[4], [0xE5]));
        AssertPublishedValue(store, keys[2], 0xC3);
        AssertPublishedValue(store, keys[3], 0xD4);
    }

    [Fact]
    public async Task DelayedTargetSelectedUnlinkHelperCannotOrphanProgressedBinding()
    {
        byte[][] keys = GenerateCanonicalBucketCollisions(count: 5, slotCount: 2);
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 2, leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[0], [0x11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[1], [0x22]));
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterOperationValidation, occurrence: 2);

        StoreStatus removeStatus = default;
        var remove = Task.Run(() => removeStatus = store.TryRemove(keys[0]));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        StoreStatus oneStepStatus = default;
        SlotSnapshot progressed = default;
        try
        {
            LockFreeKeyDirectory directory = ReadDirectory(store);
            oneStepStatus = directory.HelpMutation(canonicalBucketIndex: 0, maxSteps: 1);
            progressed = RequireOperationSnapshot(store, slotCount: 2, intent: 2, phase: 3);
        }
        finally
        {
            scheduler.Continue();
        }

        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "Unlink helper never paused after observing TargetSelected.");
        Assert.Equal(StoreStatus.StoreBusy, oneStepStatus);
        Assert.Equal(LockFreeSlotTable.ReclaimingState, SlotState(progressed.Control));
        Assert.Equal(StoreStatus.Success, removeStatus);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(keys[0], out _));
        AssertPublishedValue(store, keys[1], 0x22);

        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[2], [0x33]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[3], [0x44]));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish(keys[4], [0x55]));
        AssertPublishedValue(store, keys[2], 0x33);
        AssertPublishedValue(store, keys[3], 0x44);
    }

    [Theory]
    [InlineData("DirectoryAfterOperationValidation")]
    [InlineData("DirectoryAfterLocationValidation")]
    public async Task DelayedValidatedUnlinkHelperCannotDamageReusedSlotGeneration(string checkpointName)
    {
        LockFreeCheckpointId checkpoint = RequireCheckpoint(checkpointName);
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0xA1], [1, 2], [3]));
        SlotSnapshot oldGeneration = ReadSlotSnapshot(store, slotIndex: 0);
        scheduler.PauseAt(checkpoint);

        StoreStatus delayedStatus = default;
        var delayed = Task.Run(() => delayedStatus = store.TryRemove([0xA1]));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        StoreStatus helperStatus = default;
        StoreStatus republishStatus = default;
        SlotSnapshot beforeDelayedResume = default;
        DirectoryOccupancy occupancyBeforeDelayedResume = default;
        try
        {
            helperStatus = store.TryRemove([0xA1]);
            republishStatus = store.TryPublish([0xB2], [0x21, 0x22, 0x23], [0x31, 0x32]);
            beforeDelayedResume = ReadSlotSnapshot(store, slotIndex: 0);
            occupancyBeforeDelayedResume = ReadDirectoryOccupancy(store);
        }
        finally
        {
            scheduler.Continue();
        }

        await delayed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, $"The helper never reached {checkpointName}.");
        Assert.DoesNotContain(StoreStatus.CorruptStore, new[] { helperStatus, republishStatus, delayedStatus });
        Assert.Contains(helperStatus, new[] { StoreStatus.Success, StoreStatus.NotFound });
        Assert.Contains(delayedStatus, new[] { StoreStatus.Success, StoreStatus.NotFound });
        Assert.Equal(StoreStatus.Success, republishStatus);
        Assert.Equal(SlotGeneration(oldGeneration.Control) + 1, SlotGeneration(beforeDelayedResume.Control));
        Assert.NotEqual(oldGeneration.DirectoryBinding, beforeDelayedResume.DirectoryBinding);
        AssertGenerationConsistent(beforeDelayedResume);
        Assert.Equal(beforeDelayedResume, ReadSlotSnapshot(store, slotIndex: 0));
        Assert.Equal(new DirectoryOccupancy(Primary: 1, Overflow: 0), occupancyBeforeDelayedResume);
        Assert.Equal(occupancyBeforeDelayedResume, ReadDirectoryOccupancy(store));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([0xA1], out _));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0xB2], out var current));
        Assert.Equal(new byte[] { 0x21, 0x22, 0x23 }, current.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 0x31, 0x32 }, current.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, current.Release());
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([0xB2], [9]));

        Assert.Equal(StoreStatus.Success, store.TryRemove([0xB2]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([0xC3], [0x41], [0x42]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0xC3], out var final));
        Assert.Equal(0x41, final.ValueSpan[0]);
        Assert.Equal(0x42, final.DescriptorSpan[0]);
        Assert.Equal(StoreStatus.Success, final.Release());
    }

    [Fact]
    public async Task DelayedReclaimLoserCannotZeroOrdinaryMetadataAfterFreeToInitializingReuse()
    {
        LockFreeCheckpointId checkpoint = RequireCheckpoint("ReclaimAfterMetadataValidation");
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([0xD1], [1, 2, 3], [4]));
        SlotSnapshot oldGeneration = ReadSlotSnapshot(store, slotIndex: 0);
        scheduler.PauseAt(checkpoint);

        StoreStatus delayedStatus = default;
        var delayed = Task.Run(() => delayedStatus = store.TryRemove([0xD1]));
        bool paused = scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5));
        StoreStatus republishStatus = default;
        SlotSnapshot beforeDelayedResume = default;
        DirectoryOccupancy occupancyBeforeDelayedResume = default;
        try
        {
            // SlotCount=1 forces allocation pressure to run a second reclaim
            // helper, advance the generation, and claim Initializing for D2.
            republishStatus = store.TryPublish([0xD2], [0x51, 0x52, 0x53, 0x54], [0x61, 0x62]);
            beforeDelayedResume = ReadSlotSnapshot(store, slotIndex: 0);
            occupancyBeforeDelayedResume = ReadDirectoryOccupancy(store);
        }
        finally
        {
            scheduler.Continue();
        }

        await delayed.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(paused, "The losing reclaimer never reached its post-validation window.");
        Assert.DoesNotContain(StoreStatus.CorruptStore, new[] { republishStatus, delayedStatus });
        Assert.Equal(StoreStatus.Success, republishStatus);
        Assert.Contains(delayedStatus, new[] { StoreStatus.Success, StoreStatus.NotFound });
        Assert.Equal(SlotGeneration(oldGeneration.Control) + 1, SlotGeneration(beforeDelayedResume.Control));
        Assert.NotEqual(oldGeneration.DirectoryBinding, beforeDelayedResume.DirectoryBinding);
        AssertGenerationConsistent(beforeDelayedResume);
        Assert.Equal(beforeDelayedResume, ReadSlotSnapshot(store, slotIndex: 0));
        Assert.Equal(new DirectoryOccupancy(Primary: 1, Overflow: 0), occupancyBeforeDelayedResume);
        Assert.Equal(occupancyBeforeDelayedResume, ReadDirectoryOccupancy(store));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([0xD1], out _));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([0xD2], out var current));
        Assert.Equal(new byte[] { 0x51, 0x52, 0x53, 0x54 }, current.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 0x61, 0x62 }, current.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, current.Release());

        Assert.Equal(StoreStatus.Success, store.TryRemove([0xD2]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([0xD3], [0x71], [0x72]));
    }

    [Fact]
    public void ReclaimCleanupWritesAreFencedByExactOldReclaimingControl()
    {
        var delayed = new ReclaimValidation(
            Generation: 70,
            ExpectedReclaimingControl: TaggedControl(generation: 70, state: 6));
        var reused = new OrdinarySlotState(
            Generation: 71,
            Control: TaggedControl(generation: 71, state: 1),
            DirectoryBinding: ((ulong)71 << 31) | 1,
            DirectoryOperation: ((ulong)71 << 16) | 1,
            DirectoryLocation: ((ulong)71 << 16) | 2,
            KeyHash: 0xABCD,
            KeyLength: 3,
            DescriptorLength: 2,
            ValueLength: 8,
            BytesAdvanced: 0,
            CommitSequence: 99);

        OrdinarySlotState afterDelayedResume = ResumeReclaimCleanup(reused, delayed);

        Assert.Equal(reused, afterDelayedResume);
    }

    [Fact]
    public void ProductionHasRecordLocalReclaimerWithLogicalRemoveAndHelpingEntryPoints()
    {
        var type = typeof(MemoryStore).Assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeReclaimer",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(type is not null, "LockFreeReclaimer is required for remove/release cooperation.");

        MethodInfo[] methods = type!.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains(methods, method => method.Name.Contains("Logical", StringComparison.OrdinalIgnoreCase)
            && method.Name.Contains("Remove", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Reclaim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Help", StringComparison.OrdinalIgnoreCase));
    }

    private static LockFreeCheckpointId RequireCheckpoint(string name)
    {
        bool found = Enum.TryParse(name, ignoreCase: false, out LockFreeCheckpointId checkpoint)
            && Enum.IsDefined(checkpoint);
        Assert.True(found, $"The canonical checkpoint catalog is missing {name}.");
        return checkpoint;
    }

    private static OrdinarySlotState ResumeReclaimCleanup(
        OrdinarySlotState current,
        ReclaimValidation delayed)
    {
        if (current.Generation != delayed.Generation
            || current.Control != delayed.ExpectedReclaimingControl)
        {
            return current;
        }

        return current with
        {
            DirectoryBinding = 0,
            DirectoryOperation = 0,
            DirectoryLocation = 0,
            KeyHash = 0,
            KeyLength = 0,
            DescriptorLength = 0,
            ValueLength = 0,
            BytesAdvanced = 0,
            CommitSequence = 0
        };
    }

    private static long TaggedControl(long generation, int state) =>
        unchecked((long)(((ulong)generation << 3) | (uint)state));

    private readonly record struct ReclaimValidation(long Generation, long ExpectedReclaimingControl);

    private readonly record struct OrdinarySlotState(
        long Generation,
        long Control,
        ulong DirectoryBinding,
        ulong DirectoryOperation,
        ulong DirectoryLocation,
        ulong KeyHash,
        int KeyLength,
        int DescriptorLength,
        int ValueLength,
        long BytesAdvanced,
        long CommitSequence);

    private readonly record struct SlotSnapshot(
        long Control,
        ulong DirectoryBinding,
        long DirectoryLocation,
        long DirectoryOperation,
        ulong KeyHash,
        int KeyLength,
        int DescriptorLength,
        int ValueLength,
        long BytesAdvanced,
        long CommitSequence,
        long KeyOffset,
        long DescriptorOffset,
        long PayloadOffset);

    private readonly record struct DirectoryOccupancy(int Primary, int Overflow);

    private static SlotSnapshot ReadSlotSnapshot(MemoryStore store, int slotIndex)
    {
        object engine = ReadPrivateField<object>(store, "_engine");
        LockFreeSlotTable slots = ReadPrivateField<LockFreeSlotTable>(engine, "_slots");
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        long control = AtomicControlWord.LoadAcquire(ref slot.Control);
        var snapshot = new SlotSnapshot(
            control,
            slot.DirectoryBinding,
            AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation),
            AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation),
            slot.KeyHash,
            slot.KeyLength,
            slot.DescriptorLength,
            slot.ValueLength,
            slot.BytesAdvanced,
            slot.CommitSequence,
            slot.KeyOffset,
            slot.DescriptorOffset,
            slot.PayloadOffset);
        Assert.Equal(control, AtomicControlWord.LoadAcquire(ref slot.Control));
        return snapshot;
    }

    private static DirectoryOccupancy ReadDirectoryOccupancy(MemoryStore store)
    {
        LockFreeKeyDirectory directory = ReadDirectory(store);
        return new DirectoryOccupancy(directory.PrimaryOccupancy, directory.OverflowOccupancy);
    }

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store)
    {
        object engine = ReadPrivateField<object>(store, "_engine");
        return ReadPrivateField<LockFreeKeyDirectory>(engine, "_directory");
    }

    private static SlotSnapshot RequireOperationSnapshot(
        MemoryStore store,
        int slotCount,
        int intent,
        int phase)
    {
        for (int slotIndex = 0; slotIndex < slotCount; slotIndex++)
        {
            SlotSnapshot snapshot = ReadSlotSnapshot(store, slotIndex);
            if (snapshot.DirectoryOperation == 0)
            {
                continue;
            }

            DirectoryOperation operation = DirectoryOperation.Decode(
                unchecked((ulong)snapshot.DirectoryOperation));
            if (operation.Intent == intent && operation.Phase == phase)
            {
                return snapshot;
            }
        }

        Assert.Fail($"No intent={intent}, phase={phase} directory operation was present.");
        return default;
    }

    private static void AssertPublishedValue(MemoryStore store, byte[] key, byte expected)
    {
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
        Assert.Equal(expected, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    private static byte[][] GenerateCanonicalBucketCollisions(int count, int slotCount)
    {
        var keys = new List<byte[]>(count);
        int primaryLanes = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLanes / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        for (long candidate = 0; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            if ((Mix(StoreKey.Hash(key)) & bucketMask) == 0)
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

    private static void AssertGenerationConsistent(SlotSnapshot snapshot)
    {
        long generation = SlotGeneration(snapshot.Control);
        Assert.Equal(generation, IndexBinding.Decode(snapshot.DirectoryBinding).Generation);
        if (snapshot.DirectoryLocation != 0)
        {
            Assert.Equal(
                generation,
                DirectoryLocation.Decode(unchecked((ulong)snapshot.DirectoryLocation)).Generation);
        }

        if (snapshot.DirectoryOperation != 0)
        {
            Assert.Equal(
                generation,
                DirectoryOperation.Decode(unchecked((ulong)snapshot.DirectoryOperation)).Generation);
        }
    }

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static int SlotState(long control) =>
        (int)(unchecked((ulong)control) & 0x7UL);

    private static T ReadPrivateField<T>(object owner, string name)
        where T : class
    {
        FieldInfo? field = owner.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field!.GetValue(owner));
    }

    private static MemoryStore CreateStore(int slotCount, int leaseRecordCount)
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-reclamation-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateInstrumentedStore(
        ControlledLockFreeScheduler scheduler,
        int slotCount,
        int leaseRecordCount)
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-reclamation-instrumented-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }
}
