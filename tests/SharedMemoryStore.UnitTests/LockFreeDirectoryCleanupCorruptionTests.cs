using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeDirectoryCleanupCorruptionTests
{
    private const int IntentInsert = 1;
    private const int IntentUnlink = 2;
    private const int PhaseTargetSelected = 2;
    private const int TargetPrimary = 1;
    private const long Generation = 17;
    private const ulong KeyHash = 0xd6e8_feb8_6659_fd93UL;

    [Fact]
    public void UnlinkCellCleanupCasLossLatchesStableMalformedWinner()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(out EngineInternals internals);
        SeedTargetSelectedOperation(internals, IntentUnlink, LockFreeSlotTable.ReclaimingState);
        ref long cell = ref PrimaryCell(internals);
        long malformed = MalformedBinding(internals.Layout);
        AtomicControlWord.StoreRelease(ref cell, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            internals.Directory.HelpMutation(CanonicalBucket(internals.Layout), maxSteps: 1));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref cell));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void CanceledInsertLocationCleanupCasLossLatchesStableMalformedWinner()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(out EngineInternals internals);
        SeedTargetSelectedOperation(internals, IntentInsert, LockFreeSlotTable.AbortingState);
        AtomicControlWord.StoreRelease(
            ref PrimaryCell(internals),
            unchecked((long)IndexBinding.Encode(slotIndex: 0, Generation)));
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(0);
        long malformed = unchecked((long)ulong.MaxValue);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            internals.Directory.HelpMutation(CanonicalBucket(internals.Layout), maxSteps: 1));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void MutationCleanupCasLossLatchesStableMalformedWinner()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(out EngineInternals internals);
        int bucket = CanonicalBucket(internals.Layout);
        ulong expected = IndexBinding.Encode(slotIndex: 0, Generation);
        ref long mutation = ref BucketMutation(internals, bucket);
        long malformed = MalformedBinding(internals.Layout);
        AtomicControlWord.StoreRelease(ref mutation, malformed);

        MethodInfo cleanup = typeof(LockFreeKeyDirectory).GetMethod(
            "TryClearMutationWord",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing mutation cleanup helper.");
        StoreStatus status = Assert.IsType<StoreStatus>(cleanup.Invoke(
            internals.Directory,
            [bucket, expected]));

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref mutation));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void LiveReservationRecoveryLatchesStableMalformedCompletedLocation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(out EngineInternals internals);
        Assert.Equal(StoreStatus.Success, store.TryReserve([0x31], 1, default, out var reservation));
        int slotIndex = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding).SlotIndex;
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(slotIndex);
        long malformed = unchecked((long)ulong.MaxValue);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public async Task CanceledCompletedInsertWithStableMalformedLocationReturnsCorrupt()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(scheduler, out EngineInternals internals);
        scheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
        Task<(StoreStatus Status, ValueReservation Reservation)> reserve = Task.Run(() =>
        {
            StoreStatus status = store.TryReserve([0x32], 1, default, out var reservation);
            return (status, reservation);
        });

        bool resumed = false;
        try
        {
            Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            (int bucket, ulong binding) = FindMutation(internals.Directory, internals.Layout);
            Assert.Equal(
                StoreStatus.StoreBusy,
                internals.Directory.HelpMutation(bucket, maxSteps: 3));
            IndexBinding decoded = IndexBinding.Decode(binding);
            DirectoryOperation operation = ReadOperation(internals.Slots, decoded.SlotIndex);
            Assert.Equal(IntentInsert, operation.Intent);
            Assert.Equal(5, operation.Phase);
            long control = ReadSlotControl(internals.Slots, decoded.SlotIndex);
            Assert.Equal(LockFreeSlotTable.ReservedState, (int)((ulong)control & 0x7UL));
            var handle = new ReservationHandle(
                ReadField<ulong>(internals.Slots, "_storeId"),
                unchecked((ulong)control) >> 36,
                binding,
                ReadValueLength(internals.Slots, decoded.SlotIndex));
            Assert.Equal(StoreStatus.Success, internals.Slots.TryBeginAbort(handle));

            long malformed = unchecked((long)ulong.MaxValue);
            WriteLocation(internals.Slots, decoded.SlotIndex, malformed);
            scheduler.Continue();
            resumed = true;

            (StoreStatus status, ValueReservation reservation) =
                await reserve.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.False(reservation.IsValid);
            Assert.Equal(malformed, ReadLocation(internals.Slots, decoded.SlotIndex));
            AssertCorrupt(internals.Region);
        }
        finally
        {
            if (!resumed)
            {
                scheduler.Continue();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledInsertZeroLocationWindowRemainsLegalForOutcomeAndRecovery(
        bool recoverBeforeOwnerResumes)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var ownerScheduler = new ControlledLockFreeScheduler();
        using var cancelScheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(ownerScheduler, out EngineInternals internals);
        ownerScheduler.PauseAt(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
        Task<(StoreStatus Status, ValueReservation Reservation)> reserve = Task.Run(() =>
        {
            StoreStatus status = store.TryReserve([0x33], 1, default, out var reservation);
            return (status, reservation);
        });

        bool ownerResumed = false;
        bool cancelResumed = false;
        Task<StoreStatus>? cancel = null;
        try
        {
            Assert.True(ownerScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            (int bucket, ulong binding) = FindMutation(internals.Directory, internals.Layout);
            Assert.Equal(
                StoreStatus.StoreBusy,
                internals.Directory.HelpMutation(bucket, maxSteps: 3));
            IndexBinding decoded = IndexBinding.Decode(binding);
            DirectoryOperation completed = ReadOperation(internals.Slots, decoded.SlotIndex);
            Assert.Equal(IntentInsert, completed.Intent);
            Assert.Equal(5, completed.Phase);
            long control = ReadSlotControl(internals.Slots, decoded.SlotIndex);
            var handle = new ReservationHandle(
                ReadField<ulong>(internals.Slots, "_storeId"),
                unchecked((ulong)control) >> 36,
                binding,
                ReadValueLength(internals.Slots, decoded.SlotIndex));
            Assert.Equal(StoreStatus.Success, internals.Slots.TryBeginAbort(handle));

            cancelScheduler.PauseAt(
                LockFreeCheckpointId.DirectoryAfterCancelLocationClearBeforeDescriptorRejection);
            cancel = Task.Run(() =>
            {
                InstrumentedLockFreeCheckpoint checkpoint =
                    cancelScheduler.CreateInstrumentedCheckpoint();
                return internals.Directory.HelpMutation(
                    bucket,
                    LockFreeOperationBudget.UnboundedScan,
                    ref checkpoint,
                    maxSteps: 128);
            });
            Assert.True(cancelScheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, ReadLocation(internals.Slots, decoded.SlotIndex));
            DirectoryOperation pausedOperation = ReadOperation(internals.Slots, decoded.SlotIndex);
            Assert.Equal(IntentInsert, pausedOperation.Intent);
            Assert.Equal(5, pausedOperation.Phase);
            Assert.Equal(
                LayoutV2Constants.StoreReady,
                ReadStoreControl(internals.Region));

            if (recoverBeforeOwnerResumes)
            {
                Assert.Equal(
                    StoreStatus.Success,
                    store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
                Assert.Equal(
                    LayoutV2Constants.StoreReady,
                    ReadStoreControl(internals.Region));
            }

            ownerScheduler.Continue();
            ownerResumed = true;
            (StoreStatus ownerStatus, ValueReservation reservation) =
                await reserve.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(StoreStatus.InvalidReservation, ownerStatus);
            Assert.False(reservation.IsValid);

            cancelScheduler.Continue();
            cancelResumed = true;
            Assert.NotEqual(
                StoreStatus.CorruptStore,
                await cancel.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            if (!ownerResumed)
            {
                ownerScheduler.Continue();
            }

            if (cancel is not null && !cancelResumed)
            {
                cancelScheduler.Continue();
            }
        }
    }

    [Fact]
    public void SpillSummaryCasLossLatchesOnlyStableMalformedObservation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore transientStore = CreateStore(out EngineInternals transient);
        ulong binding = IndexBinding.Encode(slotIndex: 0, generation: 1);
        ulong validReplacement = SpillSummary.EncodeEmpty(binding);
        ulong desired = SpillSummary.EncodePresent(binding);
        ulong malformed = ulong.MaxValue;
        Assert.Equal(
            StoreStatus.Success,
            InvokeSummaryObservationValidation(
                transient.Directory,
                validReplacement,
                expected: 0,
                desired,
                malformed));
        Assert.Equal(
            LayoutV2Constants.StoreReady,
            ReadStoreControl(transient.Region));

        using MemoryStore stableStore = CreateStore(out EngineInternals stable);
        Assert.Equal(
            StoreStatus.CorruptStore,
            InvokeSummaryObservationValidation(
                stable.Directory,
                malformed,
                expected: 0,
                desired,
                malformed));
        AssertCorrupt(stable.Region);
    }

    private static void SeedTargetSelectedOperation(
        in EngineInternals internals,
        int intent,
        int slotState)
    {
        int bucket = CanonicalBucket(internals.Layout);
        int targetIndex = bucket * LayoutV2Constants.PrimaryLanesPerBucket;
        ulong binding = IndexBinding.Encode(slotIndex: 0, Generation);
        ulong operation = DirectoryOperation.Encode(
            intent,
            PhaseTargetSelected,
            TargetPrimary,
            targetIndex,
            Generation);
        ulong location = DirectoryLocation.Encode(TargetPrimary, targetIndex, Generation);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(0);
        slot.DirectoryBinding = binding;
        slot.KeyHash = KeyHash;
        Volatile.Write(
            ref slot.PublicationIntent,
            (int)SlotPublicationIntent.ExplicitReservation);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, unchecked((long)location));
        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, unchecked((long)operation));
        AtomicControlWord.StoreRelease(
            ref slot.Control,
            unchecked((long)AtomicControlWord.EncodeSlot(
                slotState,
                Generation,
                participantToken: 0)));
        AtomicControlWord.StoreRelease(ref BucketMutation(internals, bucket), unchecked((long)binding));
    }

    private static unsafe ref long PrimaryCell(in EngineInternals internals)
    {
        int bucket = CanonicalBucket(internals.Layout);
        long offset = internals.Layout.PrimaryDirectoryOffset
            + ((long)bucket * internals.Layout.PrimaryBucketStride)
            + 16;
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static unsafe ref long BucketMutation(in EngineInternals internals, int bucket)
    {
        long offset = internals.Layout.PrimaryDirectoryOffset
            + ((long)bucket * internals.Layout.PrimaryBucketStride)
            + 8;
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static DirectoryOperation ReadOperation(LockFreeSlotTable slots, int slotIndex)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        return DirectoryOperation.Decode(
            unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation)));
    }

    private static long ReadSlotControl(LockFreeSlotTable slots, int slotIndex)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        return AtomicControlWord.LoadAcquire(ref slot.Control);
    }

    private static int ReadValueLength(LockFreeSlotTable slots, int slotIndex) =>
        slots.Slot(slotIndex).ValueLength;

    private static long ReadLocation(LockFreeSlotTable slots, int slotIndex)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        return AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation);
    }

    private static void WriteLocation(LockFreeSlotTable slots, int slotIndex, long value)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, value);
    }

    private static long MalformedBinding(StoreLayoutV2 layout) =>
        unchecked((long)IndexBinding.Encode(layout.SlotCount, generation: 1));

    private static int CanonicalBucket(StoreLayoutV2 layout) =>
        (int)(Mix(KeyHash) & checked((uint)(layout.PrimaryBucketCount - 1)));

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static MemoryStore CreateStore(out EngineInternals internals)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-v2-cleanup-corruption-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        MemoryStore result = Assert.IsType<MemoryStore>(store);
        object engine = ReadField<object>(result, "_engine");
        internals = new EngineInternals(
            ReadField<LockFreeKeyDirectory>(engine, "_directory"),
            ReadField<LockFreeSlotTable>(engine, "_slots"),
            ReadField<MemoryMappedStoreRegion>(engine, "_region"),
            ReadField<StoreLayoutV2>(engine, "_layout"));
        return result;
    }

    private static MemoryStore CreateInstrumentedStore(
        ControlledLockFreeScheduler scheduler,
        out EngineInternals internals)
    {
        SharedMemoryStoreOptions options = Options(
            $"sms-v2-cleanup-corruption-instrumented-{Guid.NewGuid():N}");
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        MemoryStore result = Assert.IsType<MemoryStore>(store);
        object engine = ReadField<object>(result, "_engine");
        internals = new EngineInternals(
            ReadField<LockFreeKeyDirectory>(engine, "_directory"),
            ReadField<LockFreeSlotTable>(engine, "_slots"),
            ReadField<MemoryMappedStoreRegion>(engine, "_region"),
            ReadField<StoreLayoutV2>(engine, "_layout"));
        return result;
    }

    private static SharedMemoryStoreOptions Options(string name) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

    private static (int Bucket, ulong Binding) FindMutation(
        LockFreeKeyDirectory directory,
        StoreLayoutV2 layout)
    {
        for (var bucket = 0; bucket < layout.PrimaryBucketCount; bucket++)
        {
            ulong binding = directory.ReadCanonicalMutation(bucket);
            if (binding != 0)
            {
                return (bucket, binding);
            }
        }

        throw new InvalidOperationException("No active canonical mutation was found.");
    }

    private static StoreStatus InvokeSummaryObservationValidation(
        LockFreeKeyDirectory directory,
        ulong current,
        ulong expected,
        ulong desired,
        ulong observed)
    {
        MethodInfo validation = typeof(LockFreeKeyDirectory).GetMethod(
            "ValidateSpillSummaryCasObservation",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing spill-summary CAS validator.");
        object?[] arguments =
        [
            unchecked((long)current),
            expected,
            desired,
            observed,
        ];
        return Assert.IsType<StoreStatus>(validation.Invoke(directory, arguments));
    }

    private static T ReadField<T>(object owner, string name) =>
        Assert.IsAssignableFrom<T>(owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));

    private static void AssertCorrupt(MemoryMappedStoreRegion region) =>
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadStoreControl(region));

    private static unsafe long ReadStoreControl(MemoryMappedStoreRegion region) =>
        AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)region.Pointer)->Control);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct EngineInternals(
        LockFreeKeyDirectory Directory,
        LockFreeSlotTable Slots,
        MemoryMappedStoreRegion Region,
        StoreLayoutV2 Layout);
}
