using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeDirectoryReferenceRevalidationTests
{
    private const int TargetPrimary = 1;
    private const int TargetOverflow = 2;
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CachedExactLookupRemovedBeforeSlotClassificationRetriesWithoutCorruption(
        bool useOverflow)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        int slotCount = useOverflow ? 20 : 2;
        string name = $"sms-v2-lookup-witness-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions createOptions = Options(
            name,
            slotCount,
            OpenMode.CreateNew);
        SharedMemoryStoreOptions openOptions = Options(
            name,
            slotCount,
            OpenMode.OpenExisting);
        StoreLayoutV2 layout = StoreLayoutV2.FromOptions(createOptions);
        byte[][] keys = useOverflow
            ? GenerateBucketPairCollisions(count: 17, layout)
            : [[0x51]];
        byte[] targetKey = keys[^1];

        using var lookupScheduler = new ControlledLockFreeScheduler();
        using var unlinkScheduler = new ControlledLockFreeScheduler();
        using MemoryStore lookupStore = OpenInstrumented(createOptions, lookupScheduler);
        using MemoryStore unlinkStore = OpenInstrumented(openOptions, unlinkScheduler);
        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                lookupStore.TryPublish(keys[index], [unchecked((byte)(0x20 + index))]));
        }

        StoreInternals internals = ReadInternals(lookupStore);
        DirectoryEntry oldEntry = FindEntry(internals.Directory, targetKey);
        Assert.Equal(useOverflow ? TargetOverflow : TargetPrimary, oldEntry.Location.Kind);
        IndexBinding oldBinding = IndexBinding.Decode(oldEntry.Binding);

        lookupScheduler.PauseAt(LockFreeCheckpointId.ReserveAfterExistingLookup);
        Task<OperationResult> republish = PublishAsync(lookupStore, targetKey, 0xE1);
        Assert.True(
            lookupScheduler.WaitUntilPaused(TestTimeout),
            "The publisher did not pause after returning an exact lookup witness.");

        unlinkScheduler.PauseAt(
            LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance);
        Task<OperationResult> remove = RemoveAsync(unlinkStore, targetKey);
        Assert.True(
            unlinkScheduler.WaitUntilPaused(TestTimeout),
            "The unlink helper did not pause after winning the descriptor clear.");

        try
        {
            ref ValueSlotMetadataV2 oldSlot = ref internals.Slots.Slot(oldBinding.SlotIndex);
            Assert.Equal(
                LockFreeSlotTable.ReclaimingState,
                (int)(unchecked((ulong)AtomicControlWord.LoadAcquire(ref oldSlot.Control)) & 0x7UL));
            Assert.Equal(0, AtomicControlWord.LoadAcquire(ref oldSlot.DirectoryOperation));
            Assert.Equal(0, AtomicControlWord.LoadAcquire(ref oldSlot.DirectoryLocation));
            Assert.Equal(0UL, ReadCell(internals, oldEntry.Location));

            lookupScheduler.Continue();
            OperationResult publishResult = await republish.WaitAsync(TestTimeout);
            Assert.Equal(StoreStatus.Success, publishResult.Status);
            Assert.Null(publishResult.CorruptionOrigin);
        }
        finally
        {
            lookupScheduler.Continue();
            unlinkScheduler.Continue();
        }

        OperationResult removeResult = await remove.WaitAsync(TestTimeout);
        Assert.NotEqual(StoreStatus.CorruptStore, removeResult.Status);
        Assert.Null(removeResult.CorruptionOrigin);
        DirectoryEntry currentEntry = FindEntry(internals.Directory, targetKey);
        Assert.NotEqual(oldEntry.Binding, currentEntry.Binding);
        Assert.NotEqual(
            oldBinding.SlotIndex,
            IndexBinding.Decode(currentEntry.Binding).SlotIndex);
        Assert.Equal(StoreStatus.Success, lookupStore.TryAcquire(targetKey, out ValueLease current));
        Assert.Equal(0xE1, current.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, current.Release());
    }

    [Fact]
    public void StableInvalidSlotMetadataWithLiveExactSourceStillFailsClosed()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = OpenInstrumented(
            Options(
                $"sms-v2-live-invalid-witness-{Guid.NewGuid():N}",
                slotCount: 2,
                OpenMode.CreateNew),
            scheduler);
        byte[] key = [0x61];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x71]));
        StoreInternals internals = ReadInternals(store);
        DirectoryEntry entry = FindEntry(internals.Directory, key);
        IndexBinding binding = IndexBinding.Decode(entry.Binding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        long validOperation = AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation);
        Assert.NotEqual(0, validOperation);

        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, 0);
        StoreStatus status;
        try
        {
            status = store.TryPublish(
                key,
                [0x72],
                descriptor: default,
                StoreWaitOptions.Infinite);
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, validOperation);
        }

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.Equal(entry.Binding, ReadCell(internals, entry.Location));
        AssertMappingLatched(internals);
        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));
    }

    [Fact]
    public async Task ChangedInvalidPrimaryReferenceIsRetriedWithoutCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = new InvalidReferenceController();
        using MemoryStore store = CreateInstrumentedStore(slotCount: 4, controller);
        StoreInternals internals = ReadInternals(store);
        byte[][] keys = GenerateBucketPairCollisions(count: 2, internals.Layout);

        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[0], [0x11]));
        DirectoryEntry anchor = FindEntry(internals.Directory, keys[0]);
        Assert.Equal(TargetPrimary, anchor.Location.Kind);
        ulong invalid = NextGeneration(anchor.Binding);
        controller.Arm(
            LockFreeCheckpointId.ReserveBeforeSlotClaim,
            () => WriteCell(internals, anchor.Location, invalid));

        Task<OperationResult> publish = PublishAsync(store, keys[1], 0x12);
        Assert.True(controller.WaitUntilPaused(TestTimeout));
        try
        {
            Assert.Equal(invalid, ReadCell(internals, anchor.Location));
            WriteCell(internals, anchor.Location, anchor.Binding);
        }
        finally
        {
            controller.Continue();
        }

        OperationResult result = await publish.WaitAsync(TestTimeout);
        Assert.Equal(StoreStatus.Success, result.Status);
        Assert.Null(result.CorruptionOrigin);
        Assert.Equal(anchor.Binding, ReadCell(internals, anchor.Location));
        AssertAcquirable(store, keys[0]);
        AssertAcquirable(store, keys[1]);
    }

    [Fact]
    public async Task ChangedInvalidOverflowReferenceIsRetriedWithoutCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int slotCount = 20;
        using var controller = new InvalidReferenceController();
        using MemoryStore store = CreateInstrumentedStore(slotCount, controller);
        StoreInternals internals = ReadInternals(store);
        byte[][] keys = GenerateBucketPairCollisions(count: 18, internals.Layout);
        for (var index = 0; index < 17; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(keys[index], [unchecked((byte)(0x20 + index))]));
        }

        DirectoryEntry anchor = FindEntry(internals.Directory, keys[16]);
        Assert.Equal(TargetOverflow, anchor.Location.Kind);
        ulong invalid = NextGeneration(anchor.Binding);
        controller.Arm(
            LockFreeCheckpointId.ReserveBeforeSlotClaim,
            () => WriteCell(internals, anchor.Location, invalid));

        Task<OperationResult> publish = PublishAsync(store, keys[17], 0x41);
        Assert.True(controller.WaitUntilPaused(TestTimeout));
        try
        {
            Assert.Equal(invalid, ReadCell(internals, anchor.Location));
            WriteCell(internals, anchor.Location, anchor.Binding);
        }
        finally
        {
            controller.Continue();
        }

        OperationResult result = await publish.WaitAsync(TestTimeout);
        Assert.Equal(StoreStatus.Success, result.Status);
        Assert.Null(result.CorruptionOrigin);
        Assert.Equal(anchor.Binding, ReadCell(internals, anchor.Location));
        AssertAcquirable(store, keys[16]);
        AssertAcquirable(store, keys[17]);
    }

    [Fact]
    public async Task ChangedInvalidSpillSummaryReferenceIsRetriedWithoutCorruption()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        const int slotCount = 20;
        using var controller = new InvalidReferenceController();
        using MemoryStore store = CreateInstrumentedStore(slotCount, controller);
        StoreInternals internals = ReadInternals(store);
        byte[][] keys = GenerateBucketPairCollisions(count: 18, internals.Layout);
        for (var index = 0; index < keys.Length; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(keys[index], [unchecked((byte)(0x50 + index))]));
        }

        DirectoryEntry retained = FindEntry(internals.Directory, keys[16]);
        DirectoryEntry removed = FindEntry(internals.Directory, keys[17]);
        Assert.Equal(TargetOverflow, retained.Location.Kind);
        Assert.Equal(TargetOverflow, removed.Location.Kind);
        int canonicalBucket = CanonicalBucket(StoreKey.Hash(keys[16]), internals.Layout.PrimaryBucketCount);
        ulong invalidSummary = SpillSummary.EncodePresent(NextGeneration(retained.Binding));
        ulong replacementSummary = SpillSummary.EncodePresent(retained.Binding);
        controller.Arm(
            LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas,
            () => WriteSpillSummary(internals, canonicalBucket, invalidSummary));

        Task<OperationResult> remove = RemoveAsync(store, keys[17]);
        Assert.True(controller.WaitUntilPaused(TestTimeout));
        try
        {
            Assert.Equal(invalidSummary, ReadSpillSummary(internals, canonicalBucket));
            WriteSpillSummary(internals, canonicalBucket, replacementSummary);
        }
        finally
        {
            controller.Continue();
        }

        OperationResult result = await remove.WaitAsync(TestTimeout);
        Assert.Equal(StoreStatus.Success, result.Status);
        Assert.Null(result.CorruptionOrigin);
        Assert.Equal(replacementSummary, ReadSpillSummary(internals, canonicalBucket));
        AssertAcquirable(store, keys[16]);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(keys[17], out _));
    }

    [Fact]
    public async Task UnchangedInvalidReferenceStillFailsClosed()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = new InvalidReferenceController();
        using MemoryStore store = CreateInstrumentedStore(slotCount: 4, controller);
        StoreInternals internals = ReadInternals(store);
        byte[][] keys = GenerateBucketPairCollisions(count: 2, internals.Layout);

        Assert.Equal(StoreStatus.Success, store.TryPublish(keys[0], [0x71]));
        DirectoryEntry anchor = FindEntry(internals.Directory, keys[0]);
        ulong invalid = NextGeneration(anchor.Binding);
        controller.Arm(
            LockFreeCheckpointId.ReserveBeforeSlotClaim,
            () => WriteCell(internals, anchor.Location, invalid));

        Task<OperationResult> publish = PublishAsync(store, keys[1], 0x72);
        Assert.True(controller.WaitUntilPaused(TestTimeout));
        try
        {
            Assert.Equal(invalid, ReadCell(internals, anchor.Location));
        }
        finally
        {
            controller.Continue();
        }

        OperationResult result = await publish.WaitAsync(TestTimeout);
        Assert.Equal(StoreStatus.CorruptStore, result.Status);
        Assert.NotNull(result.CorruptionOrigin);
        Assert.Equal(invalid, ReadCell(internals, anchor.Location));
    }

    [Theory]
    [InlineData(MalformedOwnerShape.OutOfRangeInitializing)]
    [InlineData(MalformedOwnerShape.OutOfRangeReserved)]
    [InlineData(MalformedOwnerShape.OwnedPublished)]
    [InlineData(MalformedOwnerShape.OwnedRemoveRequested)]
    [InlineData(MalformedOwnerShape.OwnedAborting)]
    [InlineData(MalformedOwnerShape.OwnedReclaiming)]
    [InlineData(MalformedOwnerShape.OwnedRetired)]
    [InlineData(MalformedOwnerShape.NonterminalRetired)]
    public void StableMalformedSlotOwnerShapeFailsClosedOnEveryLookupPath(
        MalformedOwnerShape malformedShape)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var controller = new InvalidReferenceController();
        using MemoryStore store = CreateInstrumentedStore(slotCount: 2, controller);
        byte[] key = [0x73];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x83]));

        StoreInternals internals = ReadInternals(store);
        DirectoryEntry entry = FindEntry(internals.Directory, key);
        IndexBinding binding = IndexBinding.Decode(entry.Binding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        long originalControl = AtomicControlWord.LoadAcquire(ref slot.Control);
        long malformedControl = MalformedControl(malformedShape, binding.Generation);

        AtomicControlWord.StoreRelease(ref slot.Control, malformedControl);
        try
        {
            Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));

            // Each operation receives the same exact live directory witness.
            // Reinstalling it here also makes this assertion distinguish a
            // fail-closed result from a classifier that silently erased the
            // malformed reference as ordinary stale state.
            WriteCell(internals, entry.Location, entry.Binding);
            Assert.Equal(StoreStatus.CorruptStore, store.TryRemove(key));

            WriteCell(internals, entry.Location, entry.Binding);
            Assert.Equal(
                StoreStatus.CorruptStore,
                internals.Directory.TryLookup(
                    key,
                    StoreKey.Hash(key),
                    out _,
                    out _));
            Assert.Equal(entry.Binding, ReadCell(internals, entry.Location));
        }
        finally
        {
            WriteCell(internals, entry.Location, entry.Binding);
            AtomicControlWord.StoreRelease(ref slot.Control, originalControl);
        }

        AssertMappingLatched(internals);
        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));
    }

    private static Task<OperationResult> PublishAsync(MemoryStore store, byte[] key, byte value) =>
        Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            StoreStatus status = store.TryPublish(
                key,
                [value],
                descriptor: default,
                StoreWaitOptions.Infinite);
            return new OperationResult(status, LockFreeCorruptionTrace.Consume());
        });

    private static Task<OperationResult> RemoveAsync(MemoryStore store, byte[] key) =>
        Task.Run(() =>
        {
            _ = LockFreeCorruptionTrace.Consume();
            StoreStatus status = store.TryRemove(key, StoreWaitOptions.Infinite);
            return new OperationResult(status, LockFreeCorruptionTrace.Consume());
        });

    private static void AssertAcquirable(MemoryStore store, byte[] key)
    {
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    private static unsafe void AssertMappingLatched(StoreInternals internals) =>
        Assert.Equal(
            LayoutV2Constants.StoreCorrupt,
            AtomicControlWord.LoadAcquire(
                ref ((StoreHeaderV2*)internals.Region.Pointer)->Control));

    private static DirectoryEntry FindEntry(LockFreeKeyDirectory directory, byte[] key)
    {
        StoreStatus status = directory.TryLookup(
            key,
            StoreKey.Hash(key),
            out ulong binding,
            out DirectoryLocation location);
        Assert.Equal(StoreStatus.Success, status);
        return new DirectoryEntry(binding, location);
    }

    private static ulong NextGeneration(ulong binding)
    {
        IndexBinding decoded = IndexBinding.Decode(binding);
        return IndexBinding.Encode(decoded.SlotIndex, checked(decoded.Generation + 1));
    }

    private static long MalformedControl(MalformedOwnerShape shape, long generation)
    {
        const int malformedParticipant = 13;
        Assert.False(ParticipantToken.IsStructurallyValid(malformedParticipant, 4));
        int validParticipant = checked((int)ParticipantToken.Encode(
            recordIndex: 0,
            generation: 1,
            participantCount: 4));
        Assert.True(ParticipantToken.IsStructurallyValid(
            unchecked((ulong)validParticipant),
            4));

        (int state, long controlGeneration, int participant) = shape switch
        {
            MalformedOwnerShape.OutOfRangeInitializing =>
                (LockFreeSlotTable.InitializingState, generation, malformedParticipant),
            MalformedOwnerShape.OutOfRangeReserved =>
                (LockFreeSlotTable.ReservedState, generation, malformedParticipant),
            MalformedOwnerShape.OwnedPublished =>
                (LockFreeSlotTable.PublishedState, generation, validParticipant),
            MalformedOwnerShape.OwnedRemoveRequested =>
                (LockFreeSlotTable.RemoveRequestedState, generation, validParticipant),
            MalformedOwnerShape.OwnedAborting =>
                (LockFreeSlotTable.AbortingState, generation, validParticipant),
            MalformedOwnerShape.OwnedReclaiming =>
                (LockFreeSlotTable.ReclaimingState, generation, validParticipant),
            MalformedOwnerShape.OwnedRetired =>
                (
                    LockFreeSlotTable.RetiredState,
                    LockFreeSlotTable.TerminalGeneration,
                    validParticipant),
            MalformedOwnerShape.NonterminalRetired =>
                (LockFreeSlotTable.RetiredState, generation, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        return unchecked((long)AtomicControlWord.EncodeSlot(
            state,
            controlGeneration,
            participant));
    }

    private static ulong ReadCell(StoreInternals internals, DirectoryLocation location) =>
        unchecked((ulong)AtomicControlWord.LoadAcquire(ref Cell(internals, location)));

    private static void WriteCell(StoreInternals internals, DirectoryLocation location, ulong binding) =>
        AtomicControlWord.StoreRelease(ref Cell(internals, location), unchecked((long)binding));

    private static unsafe ref long Cell(StoreInternals internals, DirectoryLocation location)
    {
        long offset = location.Kind switch
        {
            TargetPrimary => PrimaryCellOffset(internals.Layout, checked((int)location.Index)),
            TargetOverflow => internals.Layout.OverflowDirectoryOffset
                + (location.Index * internals.Layout.OverflowStride),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static long PrimaryCellOffset(StoreLayoutV2 layout, int absoluteCellIndex)
    {
        int bucket = absoluteCellIndex / LayoutV2Constants.PrimaryLanesPerBucket;
        int lane = absoluteCellIndex % LayoutV2Constants.PrimaryLanesPerBucket;
        return layout.PrimaryDirectoryOffset
            + ((long)bucket * layout.PrimaryBucketStride)
            + 16
            + (lane * sizeof(long));
    }

    private static ulong ReadSpillSummary(StoreInternals internals, int canonicalBucket) =>
        unchecked((ulong)AtomicControlWord.LoadAcquire(ref SpillSummaryWord(internals, canonicalBucket)));

    private static void WriteSpillSummary(
        StoreInternals internals,
        int canonicalBucket,
        ulong raw) =>
        AtomicControlWord.StoreRelease(
            ref SpillSummaryWord(internals, canonicalBucket),
            unchecked((long)raw));

    private static unsafe ref long SpillSummaryWord(StoreInternals internals, int canonicalBucket)
    {
        long offset = internals.Layout.PrimaryDirectoryOffset
            + ((long)canonicalBucket * internals.Layout.PrimaryBucketStride);
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static MemoryStore CreateInstrumentedStore(
        int slotCount,
        InvalidReferenceController controller)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-v2-invalid-reference-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: slotCount,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            controller.CreateCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(
        string name,
        int slotCount,
        OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: slotCount,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static MemoryStore OpenInstrumented(
        SharedMemoryStoreOptions options,
        ControlledLockFreeScheduler scheduler)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static StoreInternals ReadInternals(MemoryStore store)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        return new StoreInternals(
            ReadPrivate<LockFreeKeyDirectory>(engine, "_directory"),
            ReadPrivate<LockFreeSlotTable>(engine, "_slots"),
            ReadPrivate<MemoryMappedStoreRegion>(engine, "_region"),
            ReadPrivate<StoreLayoutV2>(engine, "_layout"));
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {owner.GetType().FullName}.{fieldName}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(owner));
    }

    private static byte[][] GenerateBucketPairCollisions(int count, StoreLayoutV2 layout)
    {
        var keys = new List<byte[]>(count);
        uint bucketMask = checked((uint)(layout.PrimaryBucketCount - 1));
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

            if (first == 0 && second == 1)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static int CanonicalBucket(ulong hash, int bucketCount) =>
        (int)(Mix(hash) & checked((uint)(bucketCount - 1)));

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class InvalidReferenceController : IDisposable
    {
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: false);
        private LockFreeCheckpointId _mutationCheckpoint;
        private Action? _mutation;
        private int _mutationApplied;
        private int _revalidationReached;
        private bool _disposed;

        internal InstrumentedLockFreeCheckpoint CreateCheckpoint() =>
            LockFreeCheckpointFactory.CreateInstrumented(Observe);

        internal void Arm(LockFreeCheckpointId mutationCheckpoint, Action mutation)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _mutationCheckpoint = mutationCheckpoint;
            _mutation = mutation;
        }

        internal bool WaitUntilPaused(TimeSpan timeout) => _paused.Wait(timeout);

        internal void Continue() => _resume.Set();

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _resume.Set();
            _paused.Set();
            _resume.Dispose();
            _paused.Dispose();
        }

        private void Observe(LockFreeCheckpointEntry entry)
        {
            if (entry.Id == _mutationCheckpoint
                && Interlocked.CompareExchange(ref _mutationApplied, 1, 0) == 0)
            {
                (_mutation ?? throw new InvalidOperationException("The mutation is not armed."))();
            }

            if (entry.Id
                    != LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation
                || Interlocked.CompareExchange(ref _revalidationReached, 1, 0) != 0)
            {
                return;
            }

            _paused.Set();
            if (!_resume.Wait(TestTimeout))
            {
                throw new TimeoutException("Invalid-reference revalidation was not resumed.");
            }
        }
    }

    private readonly record struct StoreInternals(
        LockFreeKeyDirectory Directory,
        LockFreeSlotTable Slots,
        MemoryMappedStoreRegion Region,
        StoreLayoutV2 Layout);

    private readonly record struct DirectoryEntry(ulong Binding, DirectoryLocation Location);

    private readonly record struct OperationResult(StoreStatus Status, string? CorruptionOrigin);

    public enum MalformedOwnerShape
    {
        OutOfRangeInitializing,
        OutOfRangeReserved,
        OwnedPublished,
        OwnedRemoveRequested,
        OwnedAborting,
        OwnedReclaiming,
        OwnedRetired,
        NonterminalRetired,
    }
}
