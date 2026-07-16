using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeSpillSummaryTests
{
    private const int SlotCount = 20;
    private const int CanonicalBucket = 0;
    private const int SecondaryBucket = 1;

    [Fact]
    public async Task PresentCandidateIsPublishedBeforeOverflowCellCas()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(17);
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler);
        PublishPrimaryPair(store, keys);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication);

        StoreStatus publishStatus = default;
        Task publish = Task.Run(() => publishStatus = store.TryPublish(
            keys[16],
            [0xA1],
            default,
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            LockFreeKeyDirectory directory = ReadDirectory(store);
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));

            Assert.True(summary.IsPresent);
            Assert.NotEqual(0UL, summary.Binding);
            Assert.Equal(summary.Binding, directory.ReadCanonicalMutation(CanonicalBucket));
            Assert.Equal(0, directory.OverflowOccupancy);
        }
        finally
        {
            scheduler.Continue();
        }

        await publish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, publishStatus);
        Assert.Equal(1, ReadDirectory(store).OverflowOccupancy);
    }

    [Fact]
    public async Task EmptyFullScanAndVersionedClearBothPrecedeMutationRelease()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(18);
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler);
        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xB1], default, StoreWaitOptions.Infinite));

        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan);
        StoreStatus firstRemoveStatus = default;
        Task firstRemove = Task.Run(() => firstRemoveStatus = store.TryRemove(
            keys[16],
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            LockFreeKeyDirectory directory = ReadDirectory(store);
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));

            Assert.True(summary.IsPresent);
            Assert.Equal(0, directory.OverflowOccupancy);
            Assert.Equal(summary.Binding, directory.ReadCanonicalMutation(CanonicalBucket));
        }
        finally
        {
            scheduler.Continue();
        }

        await firstRemove.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, firstRemoveStatus);
        SpillSummary firstEmpty = SpillSummary.Decode(
            ReadDirectory(store).ReadSpillSummary(CanonicalBucket));
        Assert.False(firstEmpty.IsPresent);
        Assert.False(firstEmpty.IsInitial);

        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[17], [0xB2], default, StoreWaitOptions.Infinite));
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterSpillSummaryClear);
        StoreStatus secondRemoveStatus = default;
        Task secondRemove = Task.Run(() => secondRemoveStatus = store.TryRemove(
            keys[17],
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            LockFreeKeyDirectory directory = ReadDirectory(store);
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));

            Assert.False(summary.IsPresent);
            Assert.False(summary.IsInitial);
            Assert.Equal(summary.Binding, directory.ReadCanonicalMutation(CanonicalBucket));
            Assert.Equal(0, directory.OverflowOccupancy);
        }
        finally
        {
            scheduler.Continue();
        }

        await secondRemove.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(StoreStatus.Success, secondRemoveStatus);
        Assert.Equal(0UL, ReadDirectory(store).ReadCanonicalMutation(CanonicalBucket));
    }

    [Fact]
    public async Task PausedOldClearerCannotClearACompletedLaterSpill()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(18);
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler);
        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xC1], default, StoreWaitOptions.Infinite));
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan);

        StoreStatus delayedRemoveStatus = default;
        Task delayedRemove = Task.Run(() => delayedRemoveStatus = store.TryRemove(
            keys[16],
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        LockFreeKeyDirectory directory = ReadDirectory(store);
        SpillSummary oldPresent = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
        try
        {
            LockFreeOperationBudget maintenance = LockFreeOperationBudget.UnboundedScan;
            Assert.Equal(
                StoreStatus.Success,
                directory.HelpMutation(CanonicalBucket, maintenance, maxSteps: 128));
            SpillSummary oldEmpty = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Assert.False(oldEmpty.IsPresent);
            Assert.Equal(oldPresent.Binding, oldEmpty.Binding);

            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(keys[17], [0xC2], default, StoreWaitOptions.Infinite));
            SpillSummary laterPresent = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Assert.True(laterPresent.IsPresent);
            Assert.NotEqual(oldPresent.Binding, laterPresent.Binding);
        }
        finally
        {
            scheduler.Continue();
        }

        await delayedRemove.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(delayedRemoveStatus, new[] { StoreStatus.Success, StoreStatus.NotFound });
        SpillSummary afterResume = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
        Assert.True(afterResume.IsPresent);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(keys[17], out ValueLease current));
        Assert.Equal(0xC2, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public async Task PausedOldSetterCannotAbaThroughVersionedEmpty()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(17);
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler);
        PublishPrimaryPair(store, keys);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas);

        Task<StoreStatus> delayedPublish = Task.Run(() => store.TryPublish(
            keys[16],
            [0xD1],
            default,
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        LockFreeKeyDirectory directory = ReadDirectory(store);
        try
        {
            Assert.Equal(0UL, directory.ReadSpillSummary(CanonicalBucket));
            LockFreeOperationBudget maintenance = LockFreeOperationBudget.UnboundedScan;
            Assert.Equal(
                StoreStatus.Success,
                directory.HelpMutation(CanonicalBucket, maintenance, maxSteps: 128));
            Assert.True(SpillSummary.Decode(
                directory.ReadSpillSummary(CanonicalBucket)).IsPresent);

            // Adversarial fencing injection: this deliberately violates the
            // administrative override's process-wide quiescence precondition
            // so a delayed writer can resume after reuse. Only mapped-state
            // generation safety is asserted; the paused call's public result
            // is outside the supported recovery contract.
            Assert.Equal(
                StoreStatus.Success,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: true),
                    StoreWaitOptions.Infinite,
                    out ReservationRecoveryReport recovery));
            Assert.True(recovery.RecoveredReservationCount > 0);

            SpillSummary empty = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Assert.False(empty.IsPresent);
            Assert.False(empty.IsInitial);
            Assert.Equal(0, directory.OverflowOccupancy);
        }
        finally
        {
            scheduler.Continue();
        }

        _ = await delayedPublish.WaitAsync(TimeSpan.FromSeconds(5));
        SpillSummary afterResume = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
        Assert.False(afterResume.IsPresent);
        Assert.False(afterResume.IsInitial);
        Assert.Equal(0, directory.OverflowOccupancy);
    }

    [Fact]
    public async Task SetterPostCasValidationFailureStillConvergesAndReleasesMutation()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(18);
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler);
        PublishPrimaryPair(store, keys);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication);

        Task<StoreStatus> delayedPublish = Task.Run(() => store.TryPublish(
            keys[16],
            [0xD2],
            default,
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        LockFreeKeyDirectory directory = ReadDirectory(store);
        try
        {
            Assert.True(SpillSummary.Decode(
                directory.ReadSpillSummary(CanonicalBucket)).IsPresent);
            LockFreeOperationBudget maintenance = LockFreeOperationBudget.UnboundedScan;
            Assert.Equal(
                StoreStatus.Success,
                directory.HelpMutation(CanonicalBucket, maintenance, maxSteps: 128));
            // Adversarial fencing injection; see the quiescence note in
            // PausedOldSetterCannotAbaThroughVersionedEmpty. The supported
            // contract does not define the paused live call's return value.
            Assert.Equal(
                StoreStatus.Success,
                store.TryRecoverReservations(
                    new ReservationRecoveryOptions(RecoverCurrentProcessReservations: true),
                    StoreWaitOptions.Infinite,
                    out ReservationRecoveryReport recovery));
            Assert.True(recovery.RecoveredReservationCount > 0);
            Assert.Equal(0UL, directory.ReadCanonicalMutation(CanonicalBucket));
            Assert.False(SpillSummary.Decode(
                directory.ReadSpillSummary(CanonicalBucket)).IsPresent);
        }
        finally
        {
            scheduler.Continue();
        }

        _ = await delayedPublish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0UL, directory.ReadCanonicalMutation(CanonicalBucket));
        Assert.Equal(0, directory.OverflowOccupancy);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[17], [0xD3], default, StoreWaitOptions.Infinite));
        Assert.True(SpillSummary.Decode(
            directory.ReadSpillSummary(CanonicalBucket)).IsPresent);
    }

    [Fact]
    public void InfiniteChurnClearsSummaryAndLaterMissingLookupsSkipOverflow()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(18);
        using MemoryStore store = CreateStore();
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot before));

        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xE1], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot during));
        Assert.True(during.SpilledBucketCount > 0);

        for (var index = 0; index < 17; index++)
        {
            Assert.Equal(StoreStatus.Success, store.TryRemove(keys[index], StoreWaitOptions.Infinite));
        }

        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterCleanup));
        Assert.Equal(0, afterCleanup.SpilledBucketCount);
        Assert.Equal(0, afterCleanup.OverflowDirectoryOccupancy);
        Assert.True(afterCleanup.OverflowScanCount > before.OverflowScanCount);
        Assert.True(afterCleanup.MaxObservedOverflowScanLength >= SlotCount);

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(keys[17], out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterFirstMiss));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(keys[17], out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterSecondMiss));
        Assert.Equal(afterCleanup.OverflowScanCount, afterFirstMiss.OverflowScanCount);
        Assert.Equal(afterFirstMiss.OverflowScanCount, afterSecondMiss.OverflowScanCount);
    }

    [Fact]
    public void CurrentExactSummaryWitnessAvoidsRepeatedMutationReleaseScans()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(17);
        using MemoryStore store = CreateStore();
        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xE2], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterSpill));

        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryRemove(keys[index], StoreWaitOptions.Infinite));
        }

        Assert.Equal(
            StoreStatus.Success,
            store.TryGetDiagnostics(out DiagnosticsSnapshot afterPrimaryRemovals));
        Assert.Equal(afterSpill.OverflowScanCount, afterPrimaryRemovals.OverflowScanCount);
        Assert.Equal(1, afterPrimaryRemovals.SpilledBucketCount);
        Assert.Equal(1, afterPrimaryRemovals.OverflowDirectoryOccupancy);

        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[16], StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterLastRemoval));
        Assert.True(afterLastRemoval.OverflowScanCount > afterPrimaryRemovals.OverflowScanCount);
        Assert.Equal(0, afterLastRemoval.SpilledBucketCount);
        Assert.Equal(0, afterLastRemoval.OverflowDirectoryOccupancy);
    }

    [Fact]
    public void RemovingNewestOfTwoSpillsRepointsSummaryToOlderExactWitness()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(18);
        using MemoryStore store = CreateStore();
        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xE3], default, StoreWaitOptions.Infinite));
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[17], [0xE4], default, StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterTwoSpills));

        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[17], StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterNewestRemoval));
        Assert.True(afterNewestRemoval.OverflowScanCount > afterTwoSpills.OverflowScanCount);
        Assert.Equal(1, afterNewestRemoval.SpilledBucketCount);
        Assert.Equal(1, afterNewestRemoval.OverflowDirectoryOccupancy);

        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryRemove(keys[index], StoreWaitOptions.Infinite));
        }

        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterPrimaryRemovals));
        Assert.Equal(afterNewestRemoval.OverflowScanCount, afterPrimaryRemovals.OverflowScanCount);
        Assert.Equal(1, afterPrimaryRemovals.SpilledBucketCount);
        Assert.Equal(1, afterPrimaryRemovals.OverflowDirectoryOccupancy);

        Assert.Equal(StoreStatus.Success, store.TryRemove(keys[16], StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot afterFinalRemoval));
        Assert.True(afterFinalRemoval.OverflowScanCount > afterPrimaryRemovals.OverflowScanCount);
        Assert.Equal(0, afterFinalRemoval.SpilledBucketCount);
        Assert.Equal(0, afterFinalRemoval.OverflowDirectoryOccupancy);
    }

    [Fact]
    public void OverflowReservationAbortConvergesToVersionedEmpty()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(17);
        using MemoryStore store = CreateStore();
        PublishPrimaryPair(store, keys);

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(
                keys[16],
                1,
                default,
                StoreWaitOptions.Infinite,
                out ValueReservation reservation));
        Assert.Equal(StoreStatus.Success, reservation.Abort(StoreWaitOptions.Infinite));

        LockFreeKeyDirectory directory = ReadDirectory(store);
        SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
        Assert.False(summary.IsPresent);
        Assert.False(summary.IsInitial);
        Assert.Equal(0, directory.OverflowOccupancy);
        Assert.Equal(0UL, directory.ReadCanonicalMutation(CanonicalBucket));
    }

    [Fact]
    public void MappingOutOfRangeEmptySummaryFailsClosedInsteadOfSuppressingLiveOverflow()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        byte[][] keys = GenerateBucketPairCollisions(17);
        using MemoryStore store = CreateStore();
        PublishPrimaryPair(store, keys);
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublish(keys[16], [0xF1], default, StoreWaitOptions.Infinite));
        Assert.Equal(1, ReadDirectory(store).OverflowOccupancy);

        ulong invalidForMapping = SpillSummary.EncodeEmpty(
            IndexBinding.Encode(slotIndex: SlotCount, generation: 1));
        WriteSpillSummary(store, CanonicalBucket, invalidForMapping);

        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(keys[16], out _));
        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryGetDiagnostics(StoreWaitOptions.Infinite, out _));
    }

    private static void PublishPrimaryPair(MemoryStore store, byte[][] keys)
    {
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(
                    keys[index],
                    [unchecked((byte)index)],
                    default,
                    StoreWaitOptions.Infinite));
        }

        Assert.Equal(0, ReadDirectory(store).OverflowOccupancy);
    }

    private static byte[][] GenerateBucketPairCollisions(int count)
    {
        var keys = new List<byte[]>(count);
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(SlotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        for (long candidate = 1; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            ulong hash = StoreKey.Hash(key);
            int first = (int)(Mix(hash) & bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
            if (second == first)
            {
                second = (first + 1) & (int)bucketMask;
            }

            if (first == CanonicalBucket && second == SecondaryBucket)
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

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is null.");
        FieldInfo directoryField = engine.GetType().GetField(
            "_directory",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("Lock-free engine._directory is absent.");
        return Assert.IsType<LockFreeKeyDirectory>(directoryField.GetValue(engine));
    }

    private static unsafe void WriteSpillSummary(
        MemoryStore store,
        int canonicalBucketIndex,
        ulong raw)
    {
        FieldInfo engineField = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is absent.");
        object engine = engineField.GetValue(store)
            ?? throw new Xunit.Sdk.XunitException("MemoryStore._engine is null.");
        var region = Assert.IsType<SharedMemoryStore.Interop.MemoryMappedStoreRegion>(
            engine.GetType().GetField(
                "_region",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine));
        var layout = Assert.IsType<StoreLayoutV2>(
            engine.GetType().GetField(
                "_layout",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(engine));
        long offset = layout.PrimaryDirectoryOffset
            + ((long)canonicalBucketIndex * layout.PrimaryBucketStride);
        ref long summary = ref *(long*)(region.Pointer + offset);
        AtomicControlWord.StoreRelease(ref summary, unchecked((long)raw));
    }

    private static MemoryStore CreateInstrumentedStore(ControlledLockFreeScheduler scheduler)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-spill-summary-instrumented-{Guid.NewGuid():N}"),
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateStore()
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options($"sms-v2-spill-summary-{Guid.NewGuid():N}"),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: SlotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: SlotCount,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
