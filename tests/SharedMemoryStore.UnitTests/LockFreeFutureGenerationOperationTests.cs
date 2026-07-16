using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeFutureGenerationOperationTests
{
    private const int IntentInsert = 1;
    private const int IntentUnlink = 2;
    private const int PhasePrepared = 1;

    [Fact]
    public void OlderInsertPreparationDoesNotClearOrReplaceFutureGenerationOperation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            out LockFreeKeyDirectory directory,
            out LockFreeSlotTable slots);
        byte[] key = [0x41];
        ulong keyHash = StoreKey.Hash(key);
        Assert.Equal(
            StoreStatus.Success,
            slots.TryClaimReservation(
                keyHash,
                keyLength: key.Length,
                descriptorLength: 0,
                payloadLength: 1,
                out var reservation));
        key.CopyTo(slots.GetInitializingKeySpan(reservation));

        long operationGeneration = IndexBinding.Decode(reservation.SlotBinding).Generation;
        ulong futureOperation = DirectoryOperation.Encode(
            IntentInsert,
            PhasePrepared,
            targetKind: 0,
            targetIndex: 0,
            generation: operationGeneration + 1);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        AtomicControlWord.StoreRelease(
            ref slot.DirectoryOperation,
            unchecked((long)futureOperation));

        StoreStatus status = directory.TryInsert(
            key,
            keyHash,
            reservation.SlotBinding,
            out _);

        Assert.Equal(StoreStatus.CorruptStore, status);
        AssertOperation(ref slot, futureOperation);
    }

    [Fact]
    public void OlderUnlinkPreparationDoesNotClearOrReplaceFutureGenerationOperation()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore(
            out LockFreeKeyDirectory directory,
            out LockFreeSlotTable slots);
        byte[] key = [0x42];
        ulong keyHash = StoreKey.Hash(key);
        Assert.Equal(
            StoreStatus.Success,
            slots.TryClaimReservation(
                keyHash,
                keyLength: key.Length,
                descriptorLength: 0,
                payloadLength: 1,
                out var reservation));
        key.CopyTo(slots.GetInitializingKeySpan(reservation));
        Assert.Equal(StoreStatus.Success, slots.TryBeginAbort(reservation));

        long operationGeneration = IndexBinding.Decode(reservation.SlotBinding).Generation;
        ulong futureOperation = DirectoryOperation.Encode(
            IntentUnlink,
            PhasePrepared,
            targetKind: 0,
            targetIndex: 0,
            generation: operationGeneration + 1);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        AtomicControlWord.StoreRelease(
            ref slot.DirectoryOperation,
            unchecked((long)futureOperation));

        StoreStatus status = directory.TryUnlink(reservation.SlotBinding);

        Assert.Equal(StoreStatus.CorruptStore, status);
        AssertOperation(ref slot, futureOperation);
    }

    private static void AssertOperation(ref ValueSlotMetadataV2 slot, ulong expected)
    {
        Assert.Equal(
            expected,
            unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation)));
    }

    private static MemoryStore CreateStore(
        out LockFreeKeyDirectory directory,
        out LockFreeSlotTable slots)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-future-operation-{Guid.NewGuid():N}",
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
