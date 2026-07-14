using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeCheckpointSpecializationTests
{
    [Fact]
    public void OrdinaryAndInstrumentedFactoriesCloseTheSameEngineOverStaticStrategies()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using MemoryStore ordinary = OpenOrdinary();
        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore instrumented = OpenInstrumented(scheduler);

        object ordinaryEngine = ReadEngine(ordinary);
        object instrumentedEngine = ReadEngine(instrumented);
        Type ordinaryType = ordinaryEngine.GetType();
        Type instrumentedType = instrumentedEngine.GetType();

        Assert.True(ordinaryType.IsConstructedGenericType);
        Assert.True(instrumentedType.IsConstructedGenericType);
        Assert.Equal(typeof(LockFreeStoreEngine<>), ordinaryType.GetGenericTypeDefinition());
        Assert.Equal(typeof(LockFreeStoreEngine<>), instrumentedType.GetGenericTypeDefinition());
        Assert.Equal(typeof(NoOpLockFreeCheckpoint), Assert.Single(ordinaryType.GetGenericArguments()));
        Assert.Equal(typeof(InstrumentedLockFreeCheckpoint), Assert.Single(instrumentedType.GetGenericArguments()));

        Assert.Equal(
            typeof(NoOpLockFreeCheckpoint),
            RequiredField(ordinaryType, "_checkpoint").FieldType);
        Assert.Equal(
            typeof(InstrumentedLockFreeCheckpoint),
            RequiredField(instrumentedType, "_checkpoint").FieldType);

        Assert.True(typeof(LockFreeStoreEngine).IsAbstract && typeof(LockFreeStoreEngine).IsSealed);
        Assert.Empty(typeof(LockFreeStoreEngine).GetFields(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
    }

    [Fact]
    public void ProtocolComponentsCarryNoRuntimeCheckpointCallbacksOrEmitterFields()
    {
        Type[] components =
        [
            typeof(LockFreeStoreEngine<>),
            typeof(LockFreeParticipantRegistry),
            typeof(LockFreeSlotTable),
            typeof(LockFreeKeyDirectory),
            typeof(LockFreeLeaseRegistry),
            typeof(LockFreeReclaimer),
            typeof(LockFreeRecovery),
            typeof(LockFreeDiagnostics)
        ];

        foreach (Type component in components)
        {
            FieldInfo[] fields = component.GetFields(
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic);
            Assert.DoesNotContain(fields, field => IsAction(field.FieldType));
            Assert.DoesNotContain(fields, field =>
                typeof(ILockFreeCheckpointEmitter).IsAssignableFrom(field.FieldType));
            Assert.DoesNotContain(fields, field =>
                Nullable.GetUnderlyingType(field.FieldType) == typeof(InstrumentedLockFreeCheckpoint));
        }

        FieldInfo[] engineFields = typeof(LockFreeStoreEngine<>).GetFields(
            BindingFlags.Instance | BindingFlags.NonPublic);
        FieldInfo checkpoint = Assert.Single(engineFields, field => field.Name == "_checkpoint");
        Assert.True(checkpoint.FieldType.IsGenericParameter);
    }

    [Fact]
    public void InstrumentedOperationsObserveOnlyCanonicalCatalogEntries()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = OpenInstrumented(scheduler);
        scheduler.ClearObservations();

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out ValueLease lease));
        Assert.Equal(7, lease.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));

        ControlledLockFreeScheduler.Observation[] observations = scheduler.Snapshot().ToArray();
        Assert.NotEmpty(observations);
        foreach (ControlledLockFreeScheduler.Observation observation in observations)
        {
            Assert.Equal(
                LockFreeCheckpointCatalog.Get(observation.Entry.Id),
                observation.Entry);
        }
    }

    [Fact]
    public void ResourceObserverPairsExactClaimAndReleaseWithoutChangingCheckpointCallbacks()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        var checkpoints = new ConcurrentQueue<LockFreeCheckpointEntry>();
        var resources = new ConcurrentQueue<LockFreeSlotResourceEvent>();
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(
            checkpoints.Enqueue,
            resources.Enqueue);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-resource-observer-{Guid.NewGuid():N}"),
            checkpoint,
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, status);
        using MemoryStore store = Assert.IsType<MemoryStore>(candidate);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryRemove([1], StoreWaitOptions.Infinite));

        Assert.NotEmpty(checkpoints);
        LockFreeSlotResourceEvent[] observed = resources.ToArray();
        LockFreeSlotResourceEvent claim = Assert.Single(
            observed,
            static item => item.Kind == LockFreeSlotResourceEventKind.Claim);
        LockFreeSlotResourceEvent released = Assert.Single(
            observed,
            static item => item.Kind is LockFreeSlotResourceEventKind.Free
                or LockFreeSlotResourceEventKind.Retire);
        Assert.Equal(claim.SlotIndex, released.SlotIndex);
        Assert.Equal(claim.Generation, released.Generation);
    }

    private static bool IsAction(Type type) =>
        type == typeof(Action)
        || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Action<>));

    private static FieldInfo RequiredField(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"Missing required field {type.FullName}.{name}.");

    private static object ReadEngine(MemoryStore store) =>
        RequiredField(typeof(MemoryStore), "_engine").GetValue(store)
        ?? throw new InvalidOperationException("The lock-free facade has no engine.");

    private static MemoryStore OpenOrdinary()
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options($"sms-v2-checkpoint-noop-{Guid.NewGuid():N}"),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore OpenInstrumented(ControlledLockFreeScheduler scheduler)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-checkpoint-instrumented-{Guid.NewGuid():N}"),
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
