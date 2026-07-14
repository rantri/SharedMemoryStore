using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed unsafe class LockFreeStoreCorruptionLatchTests
{
    [Fact]
    public void StableMappedCorruptionPoisonsEveryHandleProjectionOperationAndFutureOpen()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-global-corruption-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions createOptions = Options(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions openOptions = Options(name, OpenMode.OpenExisting);
        using MemoryStore detector = Open(createOptions);
        using MemoryStore attached = Open(openOptions);

        byte[] publishedKey = [0x21];
        byte[] reservedKey = [0x22];
        Assert.Equal(StoreStatus.Success, detector.TryPublish(publishedKey, [0x31, 0x32], [0x41]));
        Assert.Equal(StoreStatus.Success, attached.TryAcquire(publishedKey, out ValueLease lease));
        Assert.Equal(
            StoreStatus.Success,
            attached.TryReserve(reservedKey, payloadLength: 2, descriptor: [0x42], out ValueReservation reservation));
        Assert.Equal(2, lease.ValueLength);
        Assert.Equal(1, lease.DescriptorLength);
        Assert.Equal(2, reservation.GetSpan().Length);
        Memory<byte> retainedReservationMemory = reservation.DangerousGetMemory();
        Assert.Equal(2, retainedReservationMemory.Length);

        EngineInternals detectorInternals = ReadInternals(detector);
        object attachedEngine = ReadEngine(attached);
        LockFreeParticipantRegistry.Registration attachedRegistration =
            ReadField<LockFreeParticipantRegistry.Registration>(attachedEngine, "_registration");
        LeaseHandle leaseHandle = lease.HandleForEngine;
        ReservationHandle reservationHandle = reservation.HandleForEngine;
        IndexBinding publishedBinding = IndexBinding.Decode(leaseHandle.SlotBinding);
        IndexBinding reservedBinding = IndexBinding.Decode(reservationHandle.SlotBinding);
        IndexBinding leaseBinding = IndexBinding.Decode(leaseHandle.LeaseToken);
        ref ValueSlotMetadataV2 publishedSlot =
            ref detectorInternals.Slots.Slot(publishedBinding.SlotIndex);
        long originalOperation = AtomicControlWord.LoadAcquire(ref publishedSlot.DirectoryOperation);
        Assert.NotEqual(0, originalOperation);

        int freeSlotIndex = FindFreeSlot(detectorInternals.Slots, excluded: reservedBinding.SlotIndex);
        long freeControlBefore = AtomicControlWord.LoadAcquire(
            ref detectorInternals.Slots.Slot(freeSlotIndex).Control);

        AtomicControlWord.StoreRelease(ref publishedSlot.DirectoryOperation, 0);
        Assert.Equal(StoreStatus.CorruptStore, detector.TryPublish(publishedKey, [0x55]));
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(detectorInternals.Region));

        Assert.False(lease.IsValid);
        Assert.Equal(0, lease.ValueLength);
        Assert.Equal(0, lease.DescriptorLength);
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.True(lease.DescriptorSpan.IsEmpty);
        Assert.Equal(StoreStatus.CorruptStore, lease.Release());

        Assert.False(reservation.IsValid);
        Assert.Equal(0, reservation.PayloadLength);
        Assert.Equal(0, reservation.BytesWritten);
        Assert.Equal(0, reservation.RemainingBytes);
        Assert.True(reservation.GetSpan().IsEmpty);
        Assert.True(reservation.DangerousGetMemory().IsEmpty);
        Assert.Throws<InvalidOperationException>(() => retainedReservationMemory.Pin());
        Assert.Equal(StoreStatus.CorruptStore, reservation.Advance(1));
        Assert.Equal(StoreStatus.CorruptStore, reservation.Commit());
        Assert.Equal(StoreStatus.CorruptStore, reservation.Abort());

        Assert.Equal(StoreStatus.CorruptStore, attached.TryPublish([0x23], [0x33]));
        Assert.Equal(
            freeControlBefore,
            AtomicControlWord.LoadAcquire(ref detectorInternals.Slots.Slot(freeSlotIndex).Control));

        ref ParticipantRecordV2 participantRecord = ref *(ParticipantRecordV2*)(
            detectorInternals.Region.Pointer
            + detectorInternals.Layout.ParticipantOffset
            + ((long)attachedRegistration.RecordIndex * detectorInternals.Layout.ParticipantStride));
        ref LeaseRecordV2 leaseRecord =
            ref detectorInternals.Leases.Record(leaseBinding.SlotIndex);
        ref ValueSlotMetadataV2 reservedSlot =
            ref detectorInternals.Slots.Slot(reservedBinding.SlotIndex);
        long participantBeforeDispose = AtomicControlWord.LoadAcquire(ref participantRecord.Control);
        long leaseBeforeDispose = AtomicControlWord.LoadAcquire(ref leaseRecord.Control);
        long reservationBeforeDispose = AtomicControlWord.LoadAcquire(ref reservedSlot.Control);

        attached.Dispose();

        Assert.Equal(participantBeforeDispose, AtomicControlWord.LoadAcquire(ref participantRecord.Control));
        Assert.Equal(leaseBeforeDispose, AtomicControlWord.LoadAcquire(ref leaseRecord.Control));
        Assert.Equal(reservationBeforeDispose, AtomicControlWord.LoadAcquire(ref reservedSlot.Control));

        StoreOpenStatus reopenStatus = MemoryStore.TryCreateOrOpen(openOptions, out MemoryStore? rejected);
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, reopenStatus);
        Assert.Null(rejected);
    }

    [Fact]
    public void SuppressedReleaseReclaimCorruptionStillPoisonsTheMapping()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-suppressed-corruption-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions createOptions = Options(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions openOptions = Options(name, OpenMode.OpenExisting);
        using MemoryStore remover = Open(createOptions);
        using MemoryStore reader = Open(openOptions);
        byte[] key = [0x61];
        Assert.Equal(StoreStatus.Success, remover.TryPublish(key, [0x71]));
        Assert.Equal(StoreStatus.Success, reader.TryAcquire(key, out ValueLease lease));
        Assert.Equal(StoreStatus.RemovePending, remover.TryRemove(key, StoreWaitOptions.Infinite));

        EngineInternals internals = ReadInternals(remover);
        IndexBinding binding = IndexBinding.Decode(lease.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        AtomicControlWord.StoreRelease(ref slot.DirectoryLocation, long.MaxValue);

        // Lease release is already ordered before optional physical reclaim.
        // The suppressed reclaim result still has to publish the global latch.
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(internals.Region));
        Assert.Equal(StoreStatus.CorruptStore, remover.TryAcquire(key, out _));

        StoreOpenStatus reopenStatus = MemoryStore.TryCreateOrOpen(openOptions, out MemoryStore? rejected);
        Assert.Equal(StoreOpenStatus.IncompatibleLayout, reopenStatus);
        Assert.Null(rejected);
    }

    [Fact]
    public void LeaseProjectionRejectsStableOutOfRangeMappedMetadataBeforeFormingSpan()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open(Options(
            $"sms-v2-lease-projection-corruption-{Guid.NewGuid():N}",
            OpenMode.CreateNew));
        byte[] key = [0x41];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x51]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        EngineInternals internals = ReadInternals(store);
        IndexBinding binding = IndexBinding.Decode(lease.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        slot.PayloadOffset = long.MaxValue;

        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(internals.Region));
        Assert.Equal(0, lease.ValueLength);
        Assert.Equal(StoreStatus.CorruptStore, lease.Release());
    }

    [Fact]
    public void ActiveLeaseWithStableNonprojectableSlotLifecyclePoisonsStore()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open(Options(
            $"sms-v2-lease-lifecycle-corruption-{Guid.NewGuid():N}",
            OpenMode.CreateNew));
        byte[] key = [0x44];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x54]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        EngineInternals internals = ReadInternals(store);
        IndexBinding binding = IndexBinding.Decode(lease.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        long impossible = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.ReclaimingState,
            binding.Generation,
            participantToken: 0));
        AtomicControlWord.StoreRelease(ref slot.Control, impossible);

        Assert.False(lease.IsValid);
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(internals.Region));
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.Equal(StoreStatus.CorruptStore, lease.Release());
    }

    [Fact]
    public void ReservationProjectionRejectsStableOutOfRangeMappedMetadataBeforeFormingSpan()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open(Options(
            $"sms-v2-reservation-projection-corruption-{Guid.NewGuid():N}",
            OpenMode.CreateNew));
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([0x42], payloadLength: 2, descriptor: default, out ValueReservation reservation));
        EngineInternals internals = ReadInternals(store);
        IndexBinding binding = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        slot.PayloadOffset = long.MaxValue;

        Assert.True(reservation.GetSpan().IsEmpty);
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(internals.Region));
        Assert.Equal(StoreStatus.CorruptStore, reservation.Advance(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationMutationRejectsStableMappedLengthCorruptionBeforeAdvanceOrPublish(
        bool commit)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = Open(Options(
            $"sms-v2-reservation-mutation-corruption-{commit}-{Guid.NewGuid():N}",
            OpenMode.CreateNew));
        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve([0x43], payloadLength: 1, descriptor: default, out ValueReservation reservation));
        reservation.GetSpan()[0] = 0x53;
        if (commit)
        {
            Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        }

        EngineInternals internals = ReadInternals(store);
        IndexBinding binding = IndexBinding.Decode(reservation.HandleForEngine.SlotBinding);
        ref ValueSlotMetadataV2 slot = ref internals.Slots.Slot(binding.SlotIndex);
        long controlBefore = AtomicControlWord.LoadAcquire(ref slot.Control);
        long advancedBefore = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
        slot.ValueLength = 9;

        StoreStatus status = commit ? reservation.Commit() : reservation.Advance(1);

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.Equal(LayoutV2Constants.StoreCorrupt, ReadHeaderControl(internals.Region));
        Assert.Equal(controlBefore, AtomicControlWord.LoadAcquire(ref slot.Control));
        Assert.Equal(advancedBefore, AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced));
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode mode) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 4,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 4,
            participantRecordCount: 4,
            openMode: mode,
            enableLeaseRecovery: true);

    private static MemoryStore Open(SharedMemoryStoreOptions options)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static EngineInternals ReadInternals(MemoryStore store)
    {
        object engine = ReadEngine(store);
        return new EngineInternals(
            ReadField<LockFreeSlotTable>(engine, "_slots"),
            ReadField<LockFreeLeaseRegistry>(engine, "_leases"),
            ReadField<MemoryMappedStoreRegion>(engine, "_region"),
            ReadField<StoreLayoutV2>(engine, "_layout"));
    }

    private static object ReadEngine(MemoryStore store) =>
        typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;

    private static T ReadField<T>(object owner, string name) =>
        Assert.IsType<T>(owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));

    private static int FindFreeSlot(LockFreeSlotTable slots, int excluded)
    {
        for (var index = 0; index < 4; index++)
        {
            if (index == excluded)
            {
                continue;
            }

            long control = AtomicControlWord.LoadAcquire(ref slots.Slot(index).Control);
            if ((unchecked((ulong)control) & 0x7UL) == LockFreeSlotTable.FreeState)
            {
                return index;
            }
        }

        throw new InvalidOperationException("No free slot was available for the mutation guard.");
    }

    private static long ReadHeaderControl(MemoryMappedStoreRegion region) =>
        AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)region.Pointer)->Control);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct EngineInternals(
        LockFreeSlotTable Slots,
        LockFreeLeaseRegistry Leases,
        MemoryMappedStoreRegion Region,
        StoreLayoutV2 Layout);
}
