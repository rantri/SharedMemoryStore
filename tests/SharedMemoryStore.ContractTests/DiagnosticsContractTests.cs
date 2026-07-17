namespace SharedMemoryStore.ContractTests;

public sealed class DiagnosticsContractTests
{
    [Fact]
    public void DisposedDiagnosticsUseCachedMetricsAndLiveLocalCounters()
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
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
            Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), disposed.ProtocolInfo);
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
