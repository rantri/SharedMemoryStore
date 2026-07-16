using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeStructuralControlValidationTests
{
    [Theory]
    [InlineData(LeaseMutationPath.Activate)]
    [InlineData(LeaseMutationPath.CancelClaim)]
    [InlineData(LeaseMutationPath.Release)]
    public void ExactLeaseMutationRejectsMalformedControlWithoutRecycling(
        LeaseMutationPath path)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"sms-v2-lease-structure-{path}-{Guid.NewGuid():N}");
        LockFreeLeaseRegistry leases = ReadEngineField<LockFreeLeaseRegistry>(store, "_leases");
        ulong slotBinding = IndexBinding.Encode(slotIndex: 0, generation: 1);
        Assert.Equal(StoreStatus.Success, leases.TryClaim(slotBinding, acquireSequence: 1, out LeaseHandle lease));
        if (path == LeaseMutationPath.Release)
        {
            Assert.Equal(StoreStatus.Success, leases.TryActivate(lease));
        }

        ref LeaseRecordV2 record = ref leases.Record(0);
        long original = AtomicControlWord.LoadAcquire(ref record.Control);
        long incarnation = LeaseIncarnation(original);
        long malformed = path switch
        {
            LeaseMutationPath.Activate => LeaseControl(
                LockFreeLeaseRegistry.ActiveState,
                incarnation,
                participantToken: 0),
            LeaseMutationPath.CancelClaim => LeaseControl(
                LockFreeLeaseRegistry.RecoveringState,
                incarnation,
                checked((int)lease.ParticipantToken)),
            LeaseMutationPath.Release => LeaseControl(
                LockFreeLeaseRegistry.ReleasingState,
                incarnation,
                checked((int)lease.ParticipantToken)),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
        };

        try
        {
            AtomicControlWord.StoreRelease(ref record.Control, malformed);

            StoreStatus status = path switch
            {
                LeaseMutationPath.Activate => leases.TryActivate(lease),
                LeaseMutationPath.CancelClaim => leases.TryCancelClaim(lease),
                LeaseMutationPath.Release => leases.TryRelease(lease),
                _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
            };

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref record.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref record.Control, original);
            Assert.Equal(
                StoreStatus.Success,
                path == LeaseMutationPath.Release
                    ? leases.TryRelease(lease)
                    : leases.TryCancelClaim(lease));
        }
    }

    [Fact]
    public void ActiveLeaseScanRejectsMalformedNonActiveControlInsteadOfSkippingIt()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"sms-v2-lease-scan-structure-{Guid.NewGuid():N}");
        LockFreeLeaseRegistry leases = ReadEngineField<LockFreeLeaseRegistry>(store, "_leases");
        ulong slotBinding = IndexBinding.Encode(slotIndex: 0, generation: 1);
        Assert.Equal(StoreStatus.Success, leases.TryClaim(slotBinding, acquireSequence: 1, out LeaseHandle lease));
        Assert.Equal(StoreStatus.Success, leases.TryActivate(lease));
        ref LeaseRecordV2 record = ref leases.Record(0);
        long original = AtomicControlWord.LoadAcquire(ref record.Control);
        long malformed = LeaseControl(
            LockFreeLeaseRegistry.FreeState,
            LeaseIncarnation(original),
            checked((int)lease.ParticipantToken));

        try
        {
            AtomicControlWord.StoreRelease(ref record.Control, malformed);

            StoreStatus status = leases.ScanHasActiveLease(
                slotBinding,
                LockFreeOperationBudget.StructuralAttempt,
                out bool hasActiveLease);

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.False(hasActiveLease);
            Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref record.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref record.Control, original);
            Assert.Equal(StoreStatus.Success, leases.TryRelease(lease));
        }
    }

    [Fact]
    public void RemovePropagatesMalformedLeaseScanAsCorruptStore()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"sms-v2-remove-lease-structure-{Guid.NewGuid():N}");
        byte[] key = [0x31];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x41]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        LockFreeLeaseRegistry leases = ReadEngineField<LockFreeLeaseRegistry>(store, "_leases");
        ref LeaseRecordV2 record = ref leases.Record(0);
        long original = AtomicControlWord.LoadAcquire(ref record.Control);
        long malformed = LeaseControl(
            LockFreeLeaseRegistry.FreeState,
            LeaseIncarnation(original),
            checked((int)LeaseParticipant(original)));

        try
        {
            AtomicControlWord.StoreRelease(ref record.Control, malformed);

            StoreStatus status = store.TryRemove(key, StoreWaitOptions.Infinite);

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref record.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref record.Control, original);
            Assert.Equal(StoreStatus.CorruptStore, lease.Release());
        }
    }

    [Theory]
    [InlineData(ParticipantCleanupCorruption.ZeroIncarnation)]
    [InlineData(ParticipantCleanupCorruption.InvalidActiveBinding)]
    public void ParticipantDisposalCleanupRejectsMalformedLeaseWithoutMutationOrThrow(
        ParticipantCleanupCorruption corruption)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"sms-v2-dispose-lease-structure-{corruption}-{Guid.NewGuid():N}");
        LockFreeLeaseRegistry leases = ReadEngineField<LockFreeLeaseRegistry>(store, "_leases");
        LockFreeReclaimer reclaimer = ReadEngineField<LockFreeReclaimer>(store, "_reclaimer");
        ulong slotBinding = IndexBinding.Encode(slotIndex: 0, generation: 1);
        Assert.Equal(StoreStatus.Success, leases.TryClaim(slotBinding, acquireSequence: 1, out LeaseHandle lease));
        Assert.Equal(StoreStatus.Success, leases.TryActivate(lease));
        ref LeaseRecordV2 record = ref leases.Record(0);
        long originalControl = AtomicControlWord.LoadAcquire(ref record.Control);
        ulong originalBinding = record.SlotBinding;
        long malformedControl = corruption == ParticipantCleanupCorruption.ZeroIncarnation
            ? LockFreeLeaseRegistry.ActiveState
                | (checked((long)lease.ParticipantToken) << 36)
            : originalControl;

        try
        {
            AtomicControlWord.StoreRelease(ref record.Control, malformedControl);
            if (corruption == ParticipantCleanupCorruption.InvalidActiveBinding)
            {
                record.SlotBinding = 0;
            }

            NoOpLockFreeCheckpoint checkpoint = default;
            StoreStatus status = leases.ReleaseParticipantLeases(
                lease.ParticipantToken,
                reclaimer,
                LockFreeOperationBudget.StructuralAttempt,
                ref checkpoint,
                out int released);

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.Equal(0, released);
            Assert.Equal(malformedControl, AtomicControlWord.LoadAcquire(ref record.Control));
            Assert.Equal(
                corruption == ParticipantCleanupCorruption.InvalidActiveBinding ? 0UL : originalBinding,
                record.SlotBinding);
        }
        finally
        {
            record.SlotBinding = originalBinding;
            AtomicControlWord.StoreRelease(ref record.Control, originalControl);
            Assert.Equal(StoreStatus.Success, leases.TryRelease(lease));
        }
    }

    [Theory]
    [InlineData(ReservationMutationPath.MarkReserved)]
    [InlineData(ReservationMutationPath.Advance)]
    [InlineData(ReservationMutationPath.Commit)]
    [InlineData(ReservationMutationPath.Abort)]
    public void ExactReservationMutationRejectsMalformedControlInsteadOfCompletedStatus(
        ReservationMutationPath path)
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore store = CreateStore($"sms-v2-reservation-structure-{path}-{Guid.NewGuid():N}");
        LockFreeSlotTable slots = ReadEngineField<LockFreeSlotTable>(store, "_slots");
        Assert.Equal(
            StoreStatus.Success,
            slots.TryClaimReservation(
                keyHash: 1,
                keyLength: 1,
                descriptorLength: 0,
                payloadLength: 1,
                out ReservationHandle reservation));
        if (path != ReservationMutationPath.MarkReserved)
        {
            Assert.Equal(StoreStatus.Success, slots.TryMarkReserved(reservation));
        }

        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        long original = AtomicControlWord.LoadAcquire(ref slot.Control);
        long generation = SlotGeneration(original);
        long malformed = SlotControl(
            LockFreeSlotTable.PublishedState,
            generation,
            checked((int)reservation.ParticipantToken));

        try
        {
            AtomicControlWord.StoreRelease(ref slot.Control, malformed);

            StoreStatus status = path switch
            {
                ReservationMutationPath.MarkReserved => slots.TryMarkReserved(reservation),
                ReservationMutationPath.Advance => slots.AdvanceReservation(reservation, byteCount: 1),
                ReservationMutationPath.Commit => slots.CommitReservation(reservation, commitSequence: 1),
                ReservationMutationPath.Abort => slots.TryBeginAbort(reservation),
                _ => throw new ArgumentOutOfRangeException(nameof(path), path, null)
            };

            Assert.Equal(StoreStatus.CorruptStore, status);
            Assert.Equal(malformed, AtomicControlWord.LoadAcquire(ref slot.Control));
        }
        finally
        {
            AtomicControlWord.StoreRelease(ref slot.Control, original);
            Assert.Equal(StoreStatus.Success, slots.AbortUnboundReservation(reservation));
        }
    }

    private static MemoryStore CreateStore(string name)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 4,
            maxDescriptorBytes: 0,
            maxKeyBytes: 4,
            leaseRecordCount: 1,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static T ReadEngineField<T>(MemoryStore store, string name)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return Assert.IsType<T>(engine.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
    }

    private static long LeaseControl(int state, long incarnation, int participantToken) =>
        unchecked((long)AtomicControlWord.EncodeLease(state, incarnation, participantToken));

    private static long SlotControl(int state, long generation, int participantToken) =>
        unchecked((long)AtomicControlWord.EncodeSlot(state, generation, participantToken));

    private static long LeaseIncarnation(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static ulong LeaseParticipant(long control) =>
        (unchecked((ulong)control) >> 36) & 0x0fff_ffffUL;

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public enum LeaseMutationPath
    {
        Activate,
        CancelClaim,
        Release
    }

    public enum ReservationMutationPath
    {
        MarkReserved,
        Advance,
        Commit,
        Abort
    }

    public enum ParticipantCleanupCorruption
    {
        ZeroIncarnation,
        InvalidActiveBinding
    }
}
