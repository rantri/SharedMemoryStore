namespace SharedMemoryStore.ContractTests;

public sealed class DiagnosticsContractTests
{
    [Fact]
    public void DiagnosticSnapshotDistinguishesIndexStatesAndProbeCounters()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options(slotCount: 4, maxKeyBytes: 8));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [2]));
        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([9], out _));

        var diagnostics = store.GetDiagnostics();

        Assert.True(diagnostics.IndexEntryCount > 0);
        Assert.Equal(1, diagnostics.OccupiedIndexEntryCount);
        Assert.Equal(1, diagnostics.TombstoneIndexEntryCount);
        Assert.True(diagnostics.EmptyIndexEntryCount > 0);
        Assert.True(diagnostics.TombstonePressureRatio > 0);
        Assert.True(diagnostics.UsableIndexCapacity > 0);
        Assert.True(diagnostics.LastObservedProbeLength > 0);
        Assert.True(diagnostics.MaxObservedProbeLength >= diagnostics.LastObservedProbeLength);
        ReliabilityAssertions.AssertIndexHealthAddsUp(diagnostics);
    }

    [Fact]
    public void DisposedLegacyDiagnosticsRetainTheHistoricalSnapshotContract()
    {
        MemoryStore store = ContractStoreFactory.Create(
            ContractStoreFactory.Options(slotCount: 4, maxKeyBytes: 8, leaseRecordCount: 4));
        try
        {
            for (byte value = 1; value <= 4; value++)
            {
                Assert.Equal(StoreStatus.Success, store.TryPublish([value], [value]));
            }

            for (byte value = 1; value <= 3; value++)
            {
                Assert.Equal(StoreStatus.Success, store.TryRemove([value]));
            }

            Assert.Equal(StoreStatus.NotFound, store.TryAcquire([0x7f], out _));
            DiagnosticsSnapshot live = store.GetDiagnostics();
            Assert.True(live.IndexCompactionCount > 0);

            store.Dispose();

            StoreStatus status = store.TryGetDiagnostics(out DiagnosticsSnapshot disposed);

            Assert.Equal(StoreStatus.StoreDisposed, status);
            Assert.Equal(StoreProfile.Legacy, disposed.Profile);
            Assert.Equal(live.ProtocolInfo, disposed.ProtocolInfo);
            Assert.Equal(live.TotalBytes, disposed.TotalBytes);
            Assert.Equal(live.SlotCount, disposed.SlotCount);
            Assert.Equal(live.IndexEntryCount, disposed.IndexEntryCount);
            Assert.Equal(live.IndexCompactionCount, disposed.IndexCompactionCount);
            Assert.Equal(live.GetFailureCount(StoreStatus.NotFound), disposed.GetFailureCount(StoreStatus.NotFound));
            Assert.Equal(live.GetFailureCount(StoreStatus.StoreDisposed), disposed.GetFailureCount(StoreStatus.StoreDisposed));
            Assert.Equal(live.LastFailureStatus, disposed.LastFailureStatus);
            Assert.Equal(0, disposed.FreeSlotCount);
            Assert.Equal(0, disposed.PublishedSlotCount);
            Assert.Equal(0, disposed.OccupiedIndexEntryCount);
            Assert.Equal(0, disposed.UsableIndexCapacity);

            DiagnosticsSnapshot formatted = store.GetDiagnostics();

            Assert.Equal(disposed.TotalBytes, formatted.TotalBytes);
            Assert.Equal(disposed.SlotCount, formatted.SlotCount);
            Assert.Equal(disposed.IndexEntryCount, formatted.IndexEntryCount);
            Assert.Equal(disposed.IndexCompactionCount, formatted.IndexCompactionCount);
            Assert.Equal(
                disposed.GetFailureCount(StoreStatus.StoreDisposed) + 1,
                formatted.GetFailureCount(StoreStatus.StoreDisposed));
            Assert.Equal(StoreStatus.StoreDisposed, formatted.LastFailureStatus);
        }
        finally
        {
            store.Dispose();
        }
    }

    [Fact]
    public void DisposedLockFreeDiagnosticsUseCachedMetricsAndLiveLocalCounters()
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-disposed-diagnostics-{Guid.NewGuid():N}",
            slotCount: 3,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        MemoryStore store = ContractStoreFactory.Create(options);
        try
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
            Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [2]));
            Assert.Equal(StoreStatus.NotFound, store.TryAcquire([2], out _));
            DiagnosticsSnapshot live = store.GetDiagnostics();

            store.Dispose();

            StoreStatus status = store.TryGetDiagnostics(out DiagnosticsSnapshot disposed);

            Assert.Equal(StoreStatus.StoreDisposed, status);
            Assert.Equal(StoreProfile.LockFree, disposed.Profile);
            Assert.Equal(live.ProtocolInfo, disposed.ProtocolInfo);
            Assert.Equal(live.TotalBytes, disposed.TotalBytes);
            Assert.Equal(live.SlotCount, disposed.SlotCount);
            Assert.Equal(live.ParticipantRecordCount, disposed.ParticipantRecordCount);
            Assert.Equal(live.IndexEntryCount, disposed.IndexEntryCount);
            Assert.Equal(live.PublishedSlotCount, disposed.PublishedSlotCount);
            Assert.Equal(live.PrimaryDirectoryOccupancy, disposed.PrimaryDirectoryOccupancy);
            Assert.Equal(live.GetFailureCount(StoreStatus.DuplicateKey), disposed.GetFailureCount(StoreStatus.DuplicateKey));
            Assert.Equal(live.GetFailureCount(StoreStatus.NotFound), disposed.GetFailureCount(StoreStatus.NotFound));
            Assert.Equal(live.GetFailureCount(StoreStatus.StoreDisposed), disposed.GetFailureCount(StoreStatus.StoreDisposed));
            Assert.Equal(live.LastFailureStatus, disposed.LastFailureStatus);

            DiagnosticsSnapshot formatted = store.GetDiagnostics();

            Assert.Equal(disposed.ProtocolInfo, formatted.ProtocolInfo);
            Assert.Equal(disposed.TotalBytes, formatted.TotalBytes);
            Assert.Equal(disposed.SlotCount, formatted.SlotCount);
            Assert.Equal(disposed.IndexEntryCount, formatted.IndexEntryCount);
            Assert.Equal(
                disposed.GetFailureCount(StoreStatus.StoreDisposed) + 1,
                formatted.GetFailureCount(StoreStatus.StoreDisposed));
            Assert.Equal(StoreStatus.StoreDisposed, formatted.LastFailureStatus);
        }
        finally
        {
            store.Dispose();
        }
    }
}
