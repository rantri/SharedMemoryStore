using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed unsafe class LockFreeCorruptionScannerTests
{
    [Fact]
    public void PublishAllocationScanLatchesMalformedSlotControl()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("publish-slot-scan");
        EngineInternals internals = ReadInternals(store);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(0);
        long malformed = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.FreeState,
            generation: 1,
            participantToken: 1));
        AtomicControlWord.StoreRelease(ref slot.Control, malformed);

        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([0x11], [0x21]));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void AcquireAllocationScanLatchesMalformedLeaseControl()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("acquire-lease-scan");
        byte[] key = [0x12];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x22]));
        EngineInternals internals = ReadInternals(store);
        ref LeaseRecordV2 lease = ref internals.Leases.Record(0);
        long malformed = unchecked((long)AtomicControlWord.EncodeLease(
            LockFreeLeaseRegistry.RetiredState,
            generation: 1,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref lease.Control, malformed);

        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref lease.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void LeaseRecoveryScanLatchesMalformedControlInsteadOfCountingFailure()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("lease-recovery-scan");
        EngineInternals internals = ReadInternals(store);
        ref LeaseRecordV2 lease = ref internals.Leases.Record(0);
        long malformed = unchecked((long)AtomicControlWord.EncodeLease(
            LockFreeLeaseRegistry.RetiredState,
            generation: 1,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref lease.Control, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverLeases(new LeaseRecoveryOptions(false), out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref lease.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void ReservationRecoveryScanLatchesMalformedControlInsteadOfSkippingIt()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("reservation-recovery-scan");
        EngineInternals internals = ReadInternals(store);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(0);
        long malformed = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.RetiredState,
            generation: 1,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref slot.Control, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void ParticipantReferenceProofLatchesMalformedResourceControl()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("participant-reference-scan");
        EngineInternals internals = ReadInternals(store);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(0);
        long malformed = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.RetiredState,
            generation: 1,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref slot.Control, malformed);

        StoreStatus status = internals.Participants.HasParticipantReferences(
            internals.Registration.Token,
            LockFreeOperationBudget.StructuralAttempt,
            out bool hasReferences);

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.True(hasReferences);
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void HotOperationLatchesMalformedLocalParticipantControl()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("local-participant-control");
        EngineInternals internals = ReadInternals(store);
        ref ParticipantRecordV2 participant = ref *(ParticipantRecordV2*)(
            internals.Region.Pointer
            + internals.Layout.ParticipantOffset
            + ((long)internals.Registration.RecordIndex * internals.Layout.ParticipantStride));
        long malformed = unchecked((long)AtomicControlWord.EncodeParticipant(
            LayoutV2Constants.ParticipantFree,
            internals.Registration.Generation,
            pid: Environment.ProcessId));
        AtomicControlWord.StoreRelease(ref participant.Control, malformed);

        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([0x13], [0x23]));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref participant.Control));
        AssertCorrupt(internals.Region);
    }

    [Fact]
    public void ReservationRecoveryExactReferenceScanLatchesMalformedUnrelatedDirectoryWord()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open("reservation-directory-reference-scan");
        Assert.Equal(StoreStatus.Success, store.TryReserve([0x14], 1, default, out var reservation));
        EngineInternals internals = ReadInternals(store);
        int slotIndex = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding).SlotIndex;
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(slotIndex);
        Assert.Equal(StoreStatus.Success, internals.Slots.TryBeginAbort(reservation.HandleForEngine));

        ulong locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref slot.DirectoryLocation));
        DirectoryLocation location = DirectoryLocation.Decode(locationRaw);
        ref long exactCell = ref DirectoryCell(internals, location);
        Assert.Equal(
            unchecked((long)slot.DirectoryBinding),
            AtomicControlWord.LoadAcquire(ref exactCell));
        AtomicControlWord.StoreRelease(ref exactCell, 0);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, 0);
        AtomicControlWord.StoreRelease(ref slot.DirectoryOperation, 0);

        long malformed = unchecked((long)IndexBinding.Encode(
            internals.Layout.SlotCount,
            generation: 1));
        ref long mutation = ref DirectoryWord(internals, DirectoryWordKind.BucketMutation);
        AtomicControlWord.StoreRelease(ref mutation, malformed);

        Assert.Equal(
            StoreStatus.CorruptStore,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref mutation));
        AssertCorrupt(internals.Region);
    }

    [Theory]
    [InlineData(DirectoryWordKind.BucketMutation)]
    [InlineData(DirectoryWordKind.PrimaryCell)]
    [InlineData(DirectoryWordKind.OverflowCell)]
    public void DiagnosticsLatchesMalformedDirectoryWordWithoutRewritingIt(
        DirectoryWordKind wordKind)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open($"diagnostics-{wordKind}");
        EngineInternals internals = ReadInternals(store);
        long malformed = unchecked((long)IndexBinding.Encode(
            internals.Layout.SlotCount,
            generation: 1));
        ref long word = ref DirectoryWord(internals, wordKind);
        Assert.Equal(0, AtomicControlWord.LoadAcquire(ref word));
        AtomicControlWord.StoreRelease(ref word, malformed);

        Assert.Equal(StoreStatus.CorruptStore, store.TryGetDiagnostics(out _));
        Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref word));
        AssertCorrupt(internals.Region);
    }

    private static MemoryStore Open(string purpose)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-v2-corruption-{purpose}-{Guid.NewGuid():N}",
            slotCount: 4,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 4,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static EngineInternals ReadInternals(MemoryStore store)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return new EngineInternals(
            ReadField<LockFreeSlotTable>(engine, "_slots"),
            ReadField<LockFreeLeaseRegistry>(engine, "_leases"),
            ReadField<LockFreeParticipantRegistry>(engine, "_participants"),
            ReadField<LockFreeParticipantRegistry.Registration>(engine, "_registration"),
            ReadField<MemoryMappedStoreRegion>(engine, "_region"),
            ReadField<StoreLayoutV2>(engine, "_layout"));
    }

    private static T ReadField<T>(object owner, string name) =>
        Assert.IsType<T>(owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));

    private static void AssertCorrupt(MemoryMappedStoreRegion region) =>
        Assert.Equal(
            LayoutV2Constants.StoreCorrupt,
            AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)region.Pointer)->Control));

    private static ref long DirectoryWord(
        in EngineInternals internals,
        DirectoryWordKind wordKind)
    {
        long offset = wordKind switch
        {
            DirectoryWordKind.BucketMutation => internals.Layout.PrimaryDirectoryOffset + 8,
            DirectoryWordKind.PrimaryCell => internals.Layout.PrimaryDirectoryOffset + 16,
            DirectoryWordKind.OverflowCell => internals.Layout.OverflowDirectoryOffset,
            _ => throw new ArgumentOutOfRangeException(nameof(wordKind)),
        };
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static ref long DirectoryCell(
        in EngineInternals internals,
        DirectoryLocation location)
    {
        long offset = location.Kind switch
        {
            1 => internals.Layout.PrimaryDirectoryOffset
                + ((location.Index / LayoutV2Constants.PrimaryLanesPerBucket)
                    * internals.Layout.PrimaryBucketStride)
                + 16
                + ((location.Index % LayoutV2Constants.PrimaryLanesPerBucket) * sizeof(long)),
            2 => internals.Layout.OverflowDirectoryOffset
                + (location.Index * internals.Layout.OverflowStride),
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };
        return ref *(long*)(internals.Region.Pointer + offset);
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct EngineInternals(
        LockFreeSlotTable Slots,
        LockFreeLeaseRegistry Leases,
        LockFreeParticipantRegistry Participants,
        LockFreeParticipantRegistry.Registration Registration,
        MemoryMappedStoreRegion Region,
        StoreLayoutV2 Layout);

    public enum DirectoryWordKind
    {
        BucketMutation,
        PrimaryCell,
        OverflowCell,
    }
}
