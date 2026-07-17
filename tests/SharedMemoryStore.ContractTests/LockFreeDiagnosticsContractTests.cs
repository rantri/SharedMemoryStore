using System.Reflection;
using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreeDiagnosticsContractTests
{
    [Fact]
    public void SnapshotSurfaceExposesCanonicalPressureAndRecoverySignals()
    {
        Type snapshot = typeof(DiagnosticsSnapshot);
        string[] coreProperties =
        [
            nameof(DiagnosticsSnapshot.TotalBytes),
            nameof(DiagnosticsSnapshot.SlotCount),
            nameof(DiagnosticsSnapshot.FreeSlotCount),
            nameof(DiagnosticsSnapshot.PublishedSlotCount),
            nameof(DiagnosticsSnapshot.PendingRemovalCount),
            nameof(DiagnosticsSnapshot.ActiveLeaseCount),
            nameof(DiagnosticsSnapshot.ActiveReservationCount),
            nameof(DiagnosticsSnapshot.LastFailureStatus)
        ];
        string[] v2Properties =
        [
            "ProtocolInfo",
            "InitializingSlotCount",
            "ReservedSlotCount",
            "ReclaimingSlotCount",
            "RetiredSlotCount",
            "ClaimingLeaseCount",
            "RecoveringLeaseCount",
            "FreeLeaseCount",
            "RetiredLeaseCount",
            "ParticipantRecordCount",
            "FreeParticipantCount",
            "RegisteringParticipantCount",
            "ActiveParticipantCount",
            "ClosingParticipantCount",
            "RecoveringParticipantCount",
            "ReclaimingParticipantCount",
            "RetiredParticipantCount",
            "IsParticipantTableExhausted",
            "PrimaryDirectoryOccupancy",
            "SpilledBucketCount",
            "OverflowDirectoryOccupancy",
            "OverflowScanCount",
            "MaxObservedOverflowScanLength",
            "CasRetryCount",
            "HelpedTransitionCount",
            "ContentionBudgetExhaustionCount",
            "InvalidTokenCount",
            "StaleTokenCount",
            "RecoveryAttemptCount",
            "RecoveredTransitionCount",
            "CurrentOwnerClassificationCount",
            "LiveOwnerClassificationCount",
            "StaleOwnerClassificationCount",
            "UnsupportedOwnerClassificationCount",
            "InconsistentOwnerClassificationCount",
            "ChangingOwnerClassificationCount"
        ];

        Assert.All(coreProperties, name => AssertReadableProperty(snapshot, name));
        Assert.All(v2Properties, name => AssertReadableProperty(snapshot, name));
        Assert.Null(snapshot.GetProperty("Profile"));
        Assert.Null(snapshot.GetProperty("TombstoneIndexEntryCount"));
        Assert.Null(snapshot.GetProperty("TombstonePressureRatio"));
        Assert.Null(snapshot.GetProperty("IndexCompactionCount"));
        Assert.Equal(typeof(StoreProtocolInfo), snapshot.GetProperty("ProtocolInfo")!.PropertyType);
        Assert.Equal(typeof(bool), snapshot.GetProperty("IsParticipantTableExhausted")!.PropertyType);
    }

    [Fact]
    public void BoundedScannerReportsSlotLeaseParticipantAndSpillOccupancy()
    {
        const int slotCount = 17;
        const int participantCount = 2;
        string name = $"sms-v2-diagnostics-scan-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = Options(
            name,
            OpenMode.CreateNew,
            slotCount,
            participantCount);
        using MemoryStore first = Open(create);
        StoreLayoutV2 layout = StoreLayoutV2.FromOptions(create);
        IReadOnlyList<byte[]> collidingKeys = FindCollidingKeys(
            layout.PrimaryBucketCount - 1,
            slotCount);

        foreach (byte[] key in collidingKeys)
        {
            Assert.Equal(StoreStatus.Success, first.TryPublish(key, [1]));
        }

        Assert.Equal(StoreStatus.Success, first.TryAcquire(collidingKeys[0], out ValueLease lease));
        using MemoryStore second = Open(Options(
            name,
            OpenMode.OpenExisting,
            slotCount,
            participantCount));
        LockFreeDiagnostics diagnostics = CreateDiagnostics(first, create);

        DiagnosticsSnapshot snapshot = diagnostics.CreateSnapshot();

        Assert.Equal(first.ProtocolInfo, snapshot.ProtocolInfo);
        Assert.Equal(slotCount, snapshot.SlotCount);
        Assert.Equal(slotCount, snapshot.PublishedSlotCount);
        Assert.Equal(1, snapshot.ActiveLeaseCount);
        Assert.Equal(2, snapshot.ActiveParticipantCount);
        Assert.Equal(0, snapshot.FreeParticipantCount);
        Assert.True(snapshot.IsParticipantTableExhausted);
        Assert.Equal(16, snapshot.PrimaryDirectoryOccupancy);
        Assert.True(snapshot.SpilledBucketCount >= 1);
        Assert.Equal(1, snapshot.OverflowDirectoryOccupancy);
        Assert.Equal(
            snapshot.SlotCount,
            snapshot.FreeSlotCount
                + snapshot.InitializingSlotCount
                + snapshot.ReservedSlotCount
                + snapshot.PublishedSlotCount
                + snapshot.PendingRemovalCount
                + snapshot.ReclaimingSlotCount
                + snapshot.RetiredSlotCount);
        Assert.Equal(
            snapshot.ParticipantRecordCount,
            snapshot.FreeParticipantCount
                + snapshot.RegisteringParticipantCount
                + snapshot.ActiveParticipantCount
                + snapshot.ClosingParticipantCount
                + snapshot.RecoveringParticipantCount
                + snapshot.ReclaimingParticipantCount
                + snapshot.RetiredParticipantCount);

        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void LocalCountersExposeRetryHelpTokenAndRecoveryPressureWithoutMutatingOwnership()
    {
        SharedMemoryStoreOptions options = Options(
            $"sms-v2-diagnostics-counters-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            slotCount: 3,
            participantCount: 2);
        using MemoryStore store = Open(options);
        LockFreeDiagnostics diagnostics = CreateDiagnostics(store, options);

        Assert.Equal(StoreStatus.StoreBusy, diagnostics.RecordStatus(StoreStatus.StoreBusy));
        Assert.Equal(StoreStatus.StoreFull, diagnostics.RecordStatus(StoreStatus.StoreFull));
        diagnostics.RecordOverflowScan(scannedCellCount: 7);
        diagnostics.RecordOverflowScan(scannedCellCount: 3);
        diagnostics.RecordCasRetry(4);
        diagnostics.RecordHelpedTransition(2);
        diagnostics.RecordInvalidToken(stale: false);
        diagnostics.RecordInvalidToken(stale: true);
        diagnostics.RecordRecoveryAttempt(3);
        diagnostics.RecordRecoveredTransition(2);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.CurrentProcess);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.Live);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.Stale);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.Unsupported);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.Inconsistent);
        diagnostics.RecordOwnerClassification(ParticipantClassificationKind.Changing);

        DiagnosticsSnapshot snapshot = diagnostics.CreateSnapshot();

        Assert.Equal(2, snapshot.OverflowScanCount);
        Assert.Equal(7, snapshot.MaxObservedOverflowScanLength);
        Assert.Equal(4, snapshot.CasRetryCount);
        Assert.Equal(2, snapshot.HelpedTransitionCount);
        Assert.Equal(1, snapshot.ContentionBudgetExhaustionCount);
        Assert.Equal(1, snapshot.InvalidTokenCount);
        Assert.Equal(1, snapshot.StaleTokenCount);
        Assert.Equal(3, snapshot.RecoveryAttemptCount);
        Assert.Equal(2, snapshot.RecoveredTransitionCount);
        Assert.Equal(1, snapshot.CurrentOwnerClassificationCount);
        Assert.Equal(1, snapshot.LiveOwnerClassificationCount);
        Assert.Equal(1, snapshot.StaleOwnerClassificationCount);
        Assert.Equal(1, snapshot.UnsupportedOwnerClassificationCount);
        Assert.Equal(1, snapshot.InconsistentOwnerClassificationCount);
        Assert.Equal(1, snapshot.ChangingOwnerClassificationCount);
        Assert.Equal(1, snapshot.CapacityPressureCount);
        Assert.Equal(1, snapshot.GetFailureCount(StoreStatus.StoreBusy));
        Assert.Equal(1, snapshot.GetFailureCount(StoreStatus.StoreFull));
    }

    [Fact]
    public async Task ConcurrentOverflowScanMaximumIsMonotonic()
    {
        for (var round = 0; round < 256; round++)
        {
            var telemetry = new LockFreeTelemetry();
            using var start = new Barrier(participantCount: 3);
            Task maximum = Task.Run(() =>
            {
                start.SignalAndWait();
                telemetry.RecordOverflowScan(4_096);
            });
            Task contender = Task.Run(() =>
            {
                start.SignalAndWait();
                telemetry.RecordOverflowScan(4_095);
            });

            start.SignalAndWait();
            await Task.WhenAll(maximum, contender);
            Assert.Equal(4_096, telemetry.MaxObservedOverflowScanLength);
        }
    }

    [Fact]
    public void PublicDiagnosticsExposeTheProtocolIdentityAndCurrentOccupancy()
    {
        SharedMemoryStoreOptions options = Options(
            $"sms-v2-diagnostics-public-{Guid.NewGuid():N}",
            OpenMode.CreateNew,
            slotCount: 3,
            participantCount: 2);
        using MemoryStore store = Open(options);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));

        StoreStatus status = store.TryGetDiagnostics(out DiagnosticsSnapshot snapshot);

        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), snapshot.ProtocolInfo);
        Assert.Equal(store.ProtocolInfo, snapshot.ProtocolInfo);
        Assert.Equal(1, snapshot.PublishedSlotCount);
        Assert.Equal(1, snapshot.ActiveParticipantCount);
        Assert.Equal(1, snapshot.FreeParticipantCount);
    }

    private static void AssertReadableProperty(Type type, string name)
    {
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
        Assert.True(property is not null, $"Required additive diagnostics property '{name}' is missing.");
        Assert.NotNull(property!.GetMethod);
        Assert.True(property.GetMethod!.IsPublic);
    }

    private static LockFreeDiagnostics CreateDiagnostics(
        MemoryStore store,
        SharedMemoryStoreOptions options)
    {
        object? engineValue = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store);
        object engine = Assert.IsAssignableFrom<object>(engineValue);
        var region = Assert.IsType<MemoryMappedStoreRegion>(
            engine.GetType()
                .GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(engine));
        return new LockFreeDiagnostics(
            region,
            StoreLayoutV2.FromOptions(options),
            store.ProtocolInfo);
    }

    private static MemoryStore Open(SharedMemoryStoreOptions options)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(
        string name,
        OpenMode mode,
        int slotCount,
        int participantCount) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: Math.Max(4, slotCount),
            participantRecordCount: participantCount,
            openMode: mode,
            enableLeaseRecovery: true);

    private static IReadOnlyList<byte[]> FindCollidingKeys(int bucketMask, int required)
    {
        var groups = new Dictionary<(int First, int Second), List<byte[]>>();
        Span<byte> key = stackalloc byte[4];
        for (var value = 0; value < 1_000_000; value++)
        {
            BitConverter.TryWriteBytes(key, value);
            ulong hash = StoreKey.Hash(key);
            int first = (int)(Mix(hash) & (uint)bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & (uint)bucketMask);
            if (second == first)
            {
                second = (first + 1) & bucketMask;
            }

            var pair = (first, second);
            if (!groups.TryGetValue(pair, out List<byte[]>? keys))
            {
                keys = [];
                groups.Add(pair, keys);
            }

            keys.Add(key.ToArray());
            if (keys.Count == required)
            {
                return keys;
            }
        }

        throw new InvalidOperationException("Unable to generate the required colliding key set.");
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }
}
