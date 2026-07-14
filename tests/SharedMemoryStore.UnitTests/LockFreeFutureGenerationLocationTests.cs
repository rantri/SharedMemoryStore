using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed unsafe class LockFreeFutureGenerationLocationTests
{
    private const int IntentInsert = 1;
    private const int IntentUnlink = 2;
    private const int PhasePrepared = 1;
    private const int PhaseTargetSelected = 2;
    private const int TargetPrimary = 1;
    private const long OperationGeneration = 17;
    private const long FutureGeneration = OperationGeneration + 1;
    private const ulong KeyHash = 0xd6e8_feb8_6659_fd93UL;

    [Fact]
    public void PreparedUnlinkHelperRejectsStableFutureGenerationLocation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            out LockFreeKeyDirectory directory,
            out LockFreeSlotTable slots,
            out MemoryMappedStoreRegion region,
            out StoreLayoutV2 layout);
        int canonicalBucket = CanonicalBucket(KeyHash, layout.PrimaryBucketCount);
        int targetIndex = canonicalBucket * LayoutV2Constants.PrimaryLanesPerBucket;
        ulong binding = IndexBinding.Encode(slotIndex: 0, OperationGeneration);
        ulong operation = DirectoryOperation.Encode(
            IntentUnlink,
            PhasePrepared,
            targetKind: 0,
            targetIndex: 0,
            OperationGeneration);
        ulong futureLocation = DirectoryLocation.Encode(
            TargetPrimary,
            targetIndex,
            FutureGeneration);

        SeedOperation(
            slots,
            region,
            layout,
            canonicalBucket,
            binding,
            operation,
            futureLocation,
            LockFreeSlotTable.ReclaimingState);

        _ = LockFreeCorruptionTrace.Consume();
        Assert.Equal(
            StoreStatus.CorruptStore,
            directory.HelpMutation(canonicalBucket, maxSteps: 1));
        Assert.NotNull(LockFreeCorruptionTrace.Consume());

        AssertLocation(slots, futureLocation);
    }

    [Fact]
    public void TargetSelectedUnlinkHelperRejectsStableFutureGenerationLocation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            out LockFreeKeyDirectory directory,
            out LockFreeSlotTable slots,
            out MemoryMappedStoreRegion region,
            out StoreLayoutV2 layout);
        int canonicalBucket = CanonicalBucket(KeyHash, layout.PrimaryBucketCount);
        int targetIndex = canonicalBucket * LayoutV2Constants.PrimaryLanesPerBucket;
        ulong binding = IndexBinding.Encode(slotIndex: 0, OperationGeneration);
        ulong operation = DirectoryOperation.Encode(
            IntentUnlink,
            PhaseTargetSelected,
            TargetPrimary,
            targetIndex,
            OperationGeneration);
        ulong futureLocation = DirectoryLocation.Encode(
            TargetPrimary,
            targetIndex,
            FutureGeneration);

        SeedOperation(
            slots,
            region,
            layout,
            canonicalBucket,
            binding,
            operation,
            futureLocation,
            LockFreeSlotTable.ReclaimingState);
        SetPrimaryCell(region, layout, targetIndex, binding);

        _ = LockFreeCorruptionTrace.Consume();
        Assert.Equal(
            StoreStatus.CorruptStore,
            directory.HelpMutation(canonicalBucket, maxSteps: 1));
        Assert.NotNull(LockFreeCorruptionTrace.Consume());

        AssertLocation(slots, futureLocation);
    }

    [Fact]
    public void TargetSelectedInsertHelperRejectsStableFutureGenerationLocation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            out LockFreeKeyDirectory directory,
            out LockFreeSlotTable slots,
            out MemoryMappedStoreRegion region,
            out StoreLayoutV2 layout);
        int canonicalBucket = CanonicalBucket(KeyHash, layout.PrimaryBucketCount);
        int targetIndex = canonicalBucket * LayoutV2Constants.PrimaryLanesPerBucket;
        ulong binding = IndexBinding.Encode(slotIndex: 0, OperationGeneration);
        ulong operation = DirectoryOperation.Encode(
            IntentInsert,
            PhaseTargetSelected,
            TargetPrimary,
            targetIndex,
            OperationGeneration);
        ulong futureLocation = DirectoryLocation.Encode(
            TargetPrimary,
            targetIndex,
            FutureGeneration);

        SeedOperation(
            slots,
            region,
            layout,
            canonicalBucket,
            binding,
            operation,
            futureLocation,
            LockFreeSlotTable.InitializingState);
        SetPrimaryCell(region, layout, targetIndex, binding);

        _ = LockFreeCorruptionTrace.Consume();
        Assert.Equal(
            StoreStatus.CorruptStore,
            directory.HelpMutation(canonicalBucket, maxSteps: 1));
        Assert.NotNull(LockFreeCorruptionTrace.Consume());

        AssertLocation(slots, futureLocation);
    }

    private static void SeedOperation(
        LockFreeSlotTable slots,
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        int canonicalBucket,
        ulong binding,
        ulong operation,
        ulong location,
        int slotState)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        slot.DirectoryBinding = binding;
        slot.KeyHash = KeyHash;
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, unchecked((long)location));
        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, unchecked((long)operation));
        AtomicControlWord.StoreRelease(
            ref slot.Control,
            unchecked((long)AtomicControlWord.EncodeSlot(
                slotState,
                OperationGeneration,
                participantToken: 0)));

        SetBucketMutation(region, layout, canonicalBucket, binding);
    }

    private static void AssertLocation(LockFreeSlotTable slots, ulong expected)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        Assert.Equal(
            expected,
            unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation)));
    }

    private static void SetBucketMutation(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        int canonicalBucket,
        ulong binding)
    {
        long offset = layout.PrimaryDirectoryOffset
            + ((long)canonicalBucket * layout.PrimaryBucketStride)
            + 8;
        ref long mutation = ref *(long*)(region.Pointer + offset);
        AtomicControlWord.StoreRelease(ref mutation, unchecked((long)binding));
    }

    private static void SetPrimaryCell(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        int targetIndex,
        ulong binding)
    {
        int bucket = targetIndex / LayoutV2Constants.PrimaryLanesPerBucket;
        int lane = targetIndex % LayoutV2Constants.PrimaryLanesPerBucket;
        long offset = layout.PrimaryDirectoryOffset
            + ((long)bucket * layout.PrimaryBucketStride)
            + 16
            + (lane * sizeof(long));
        ref long cell = ref *(long*)(region.Pointer + offset);
        AtomicControlWord.StoreRelease(ref cell, unchecked((long)binding));
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

    private static MemoryStore CreateStore(
        out LockFreeKeyDirectory directory,
        out LockFreeSlotTable slots,
        out MemoryMappedStoreRegion region,
        out StoreLayoutV2 layout)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-future-location-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        MemoryStore result = Assert.IsType<MemoryStore>(store);
        object engine = ReadPrivate<object>(result, "_engine");
        directory = ReadPrivate<LockFreeKeyDirectory>(engine, "_directory");
        slots = ReadPrivate<LockFreeSlotTable>(engine, "_slots");
        region = ReadPrivate<MemoryMappedStoreRegion>(engine, "_region");
        layout = ReadPrivate<StoreLayoutV2>(engine, "_layout");
        return result;
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {owner.GetType().FullName}.{fieldName}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(owner));
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
