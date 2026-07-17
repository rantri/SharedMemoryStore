using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed unsafe class LockFreeRecoveryCorruptionPropagationTests
{
    [Fact]
    public void ReservationRecoveryReturnsCorruptAfterAnEarlierSuccessfulRecovery()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-reservation-owner-corruption-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(Options(name, OpenMode.CreateNew));
        using MemoryStore malformedOwner = Open(Options(name, OpenMode.OpenExisting));
        Assert.Equal(
            StoreStatus.Success,
            controller.TryReserve([0x31], 1, default, StoreWaitOptions.Infinite, out ValueReservation first));
        Assert.Equal(
            StoreStatus.Success,
            malformedOwner.TryReserve([0x32], 1, default, StoreWaitOptions.Infinite, out ValueReservation target));
        Assert.True(
            IndexBinding.Decode(first.HandleForEngine.SlotBinding).SlotIndex
            < IndexBinding.Decode(target.HandleForEngine.SlotBinding).SlotIndex);

        EngineInternals targetInternals = ReadInternals(malformedOwner);
        ref ParticipantRecordV2 participant = ref ParticipantRecord(targetInternals);
        Volatile.Write(ref participant.Reserved, 1);

        StoreStatus status = controller.TryRecoverReservations(
            new ReservationRecoveryOptions(RecoverCurrentProcessReservations: true),
            StoreWaitOptions.Infinite,
            out ReservationRecoveryReport report);

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.True(report.RecoveredReservationCount >= 1);
        Assert.Equal(1, Volatile.Read(ref participant.Reserved));
        AssertCorrupt(targetInternals.Region);
    }

    [Fact]
    public void LeaseRecoveryReturnsCorruptAfterAnEarlierSuccessfulRecovery()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        string name = $"sms-v2-lease-owner-corruption-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(Options(name, OpenMode.CreateNew));
        using MemoryStore malformedOwner = Open(Options(name, OpenMode.OpenExisting));
        byte[] key = [0x41];
        Assert.Equal(StoreStatus.Success, controller.TryPublish(key, [0x51]));
        Assert.Equal(StoreStatus.Success, controller.TryAcquire(key, out ValueLease first));
        Assert.Equal(StoreStatus.Success, malformedOwner.TryAcquire(key, out ValueLease target));
        Assert.True(
            IndexBinding.Decode(first.HandleForEngine.LeaseToken).SlotIndex
            < IndexBinding.Decode(target.HandleForEngine.LeaseToken).SlotIndex);

        EngineInternals targetInternals = ReadInternals(malformedOwner);
        ref ParticipantRecordV2 participant = ref ParticipantRecord(targetInternals);
        Volatile.Write(ref participant.Reserved, 1);

        StoreStatus status = controller.TryRecoverLeases(
            new LeaseRecoveryOptions(RecoverCurrentProcessLeases: true),
            StoreWaitOptions.Infinite,
            out LeaseRecoveryReport report);

        Assert.Equal(StoreStatus.CorruptStore, status);
        Assert.True(report.RecoveredLeaseCount >= 1);
        Assert.Equal(1, Volatile.Read(ref participant.Reserved));
        AssertCorrupt(targetInternals.Region);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode mode) =>
        SharedMemoryStoreOptions.Create(
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
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        return new EngineInternals(
            ReadField<LockFreeParticipantRegistry.Registration>(engine, "_registration"),
            ReadField<MemoryMappedStoreRegion>(engine, "_region"),
            ReadField<StoreLayoutV2>(engine, "_layout"));
    }

    private static T ReadField<T>(object owner, string name) =>
        Assert.IsType<T>(owner.GetType()
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(owner));

    private static ref ParticipantRecordV2 ParticipantRecord(in EngineInternals internals) =>
        ref *(ParticipantRecordV2*)(
            internals.Region.Pointer
            + internals.Layout.ParticipantOffset
            + ((long)internals.Registration.RecordIndex * internals.Layout.ParticipantStride));

    private static void AssertCorrupt(MemoryMappedStoreRegion region) =>
        Assert.Equal(
            LayoutV2Constants.StoreCorrupt,
            AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)region.Pointer)->Control));

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct EngineInternals(
        LockFreeParticipantRegistry.Registration Registration,
        MemoryMappedStoreRegion Region,
        StoreLayoutV2 Layout);
}
