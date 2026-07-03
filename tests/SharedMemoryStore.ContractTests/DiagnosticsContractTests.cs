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
}
