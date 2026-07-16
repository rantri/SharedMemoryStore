using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeLifecycleSuccessorValidationTests
{
    [Fact]
    public async Task OrderedLeaseReleaseLatchesSameIncarnationReactivationButRemainsSuccessful()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(
            scheduler,
            $"sms-v2-lease-successor-{Guid.NewGuid():N}");
        byte[] key = [0x11];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x21]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));

        LockFreeLeaseRegistry leases = ReadEngineField<LockFreeLeaseRegistry>(store, "_leases");
        long active = ReadLeaseControl(leases, recordIndex: 0);
        long incarnation = LeaseIncarnation(active);
        scheduler.PauseAt(LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas);

        StoreStatus releaseStatus = default;
        Task release = Task.Run(() => releaseStatus = lease.Release(StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Equal(
                LockFreeLeaseRegistry.ReleasingState,
                LeaseState(ReadLeaseControl(leases, recordIndex: 0)));

            // This word is structurally canonical but cannot follow
            // Releasing(g): Active(g) would resurrect already-ended protection.
            WriteLeaseControl(leases, recordIndex: 0, active);
        }
        finally
        {
            scheduler.Continue();
        }

        await release.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, releaseStatus);
        Assert.Equal(active, ReadLeaseControl(leases, recordIndex: 0));
        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));
        Assert.Equal(incarnation, LeaseIncarnation(active));
    }

    [Fact]
    public async Task ParticipantGenerationAdvanceLatchesStableSameGenerationRegression()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(
            scheduler,
            $"sms-v2-participant-successor-{Guid.NewGuid():N}");
        LockFreeParticipantRegistry participants =
            ReadEngineField<LockFreeParticipantRegistry>(store, "_participants");
        LockFreeParticipantRegistry.Registration registration =
            ReadEngineField<LockFreeParticipantRegistry.Registration>(store, "_registration");
        long reclaiming = ParticipantControl(
            LayoutV2Constants.ParticipantReclaiming,
            registration.Generation,
            pid: 0);
        WriteParticipantControl(participants, registration.RecordIndex, reclaiming);

        scheduler.PauseAt(LockFreeCheckpointId.ParticipantBeforeReclaimGenerationAdvanceCas);
        InstrumentedLockFreeCheckpoint instrumented = scheduler.CreateInstrumentedCheckpoint();
        ParticipantTransitionResult transition = default;
        Task helper = Task.Run(() =>
        {
            InstrumentedLockFreeCheckpoint checkpoint = instrumented;
            transition = participants.HelpReclaiming(
                registration.RecordIndex,
                registration.Generation,
                ref checkpoint);
        });
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Equal(
                reclaiming,
                ReadParticipantControl(participants, registration.RecordIndex));

            // Active(g) is canonical in isolation, but Reclaiming(g) has no
            // same-generation successor except Retired(g) at terminal rollover.
            WriteParticipantControl(
                participants,
                registration.RecordIndex,
                registration.ActiveControl);
        }
        finally
        {
            scheduler.Continue();
        }

        await helper.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(ParticipantTransitionResult.Inconsistent, transition);
        Assert.Equal(
            registration.ActiveControl,
            ReadParticipantControl(participants, registration.RecordIndex));
        Assert.Equal(StoreStatus.CorruptStore, store.TryPublish([0x31], [0x41]));
    }

    [Fact]
    public async Task RemoveOwnershipCasLatchesStablePublishedRegression()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateInstrumentedStore(
            scheduler,
            $"sms-v2-reclaimer-successor-{Guid.NewGuid():N}");
        byte[] key = [0x51];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0x61]));
        LockFreeSlotTable slots = ReadEngineField<LockFreeSlotTable>(store, "_slots");
        long published = ReadSlotControl(slots, slotIndex: 0);
        long generation = SlotGeneration(published);
        scheduler.PauseAt(LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas);

        StoreStatus removeStatus = default;
        Task remove = Task.Run(() => removeStatus = store.TryRemove(
            key,
            StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        try
        {
            Assert.Equal(
                LockFreeSlotTable.RemoveRequestedState,
                SlotState(ReadSlotControl(slots, slotIndex: 0)));

            // Published(g) is structurally valid but cannot follow the already
            // ordered RemoveRequested(g) lifecycle.
            WriteSlotControl(slots, slotIndex: 0, published);
        }
        finally
        {
            scheduler.Continue();
        }

        await remove.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.CorruptStore, removeStatus);
        Assert.Equal(published, ReadSlotControl(slots, slotIndex: 0));
        Assert.Equal(generation, SlotGeneration(published));
        Assert.Equal(StoreStatus.CorruptStore, store.TryAcquire(key, out _));
    }

    private static MemoryStore CreateInstrumentedStore(
        ControlledLockFreeScheduler scheduler,
        string name)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 4,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
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

    private static long ParticipantControl(int state, int generation, int pid) =>
        unchecked((long)AtomicControlWord.EncodeParticipant(state, generation, pid));

    private static long ReadLeaseControl(LockFreeLeaseRegistry leases, int recordIndex)
    {
        ref LeaseRecordV2 record = ref leases.Record(recordIndex);
        return AtomicControlWord.LoadAcquire(ref record.Control);
    }

    private static void WriteLeaseControl(
        LockFreeLeaseRegistry leases,
        int recordIndex,
        long control)
    {
        ref LeaseRecordV2 record = ref leases.Record(recordIndex);
        AtomicControlWord.StoreRelease(ref record.Control, control);
    }

    private static long ReadParticipantControl(
        LockFreeParticipantRegistry participants,
        int recordIndex)
    {
        ref ParticipantRecordV2 record = ref participants.Record(recordIndex);
        return AtomicControlWord.LoadAcquire(ref record.Control);
    }

    private static void WriteParticipantControl(
        LockFreeParticipantRegistry participants,
        int recordIndex,
        long control)
    {
        ref ParticipantRecordV2 record = ref participants.Record(recordIndex);
        AtomicControlWord.StoreRelease(ref record.Control, control);
    }

    private static long ReadSlotControl(LockFreeSlotTable slots, int slotIndex)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        return AtomicControlWord.LoadAcquire(ref slot.Control);
    }

    private static void WriteSlotControl(
        LockFreeSlotTable slots,
        int slotIndex,
        long control)
    {
        ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
        AtomicControlWord.StoreRelease(ref slot.Control, control);
    }

    private static long LeaseIncarnation(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static int LeaseState(long control) =>
        (int)(unchecked((ulong)control) & 0x7UL);

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static int SlotState(long control) =>
        (int)(unchecked((ulong)control) & 0x7UL);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
