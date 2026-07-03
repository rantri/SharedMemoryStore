using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class IndexHealthTests
{
    [Fact]
    public void DiagnosticsCountOccupiedTombstoneEmptyAndReusableCapacity()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 4, maxKeyBytes: 8));
        Assert.Equal(StoreStatus.Success, store.TryPublish(ChurnKeyFactory.Key(1), [1]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(ChurnKeyFactory.Key(2), [2]));
        Assert.Equal(StoreStatus.Success, store.TryRemove(ChurnKeyFactory.Key(1)));

        var diagnostics = store.GetDiagnostics();

        Assert.Equal(1, diagnostics.OccupiedIndexEntryCount);
        Assert.Equal(1, diagnostics.TombstoneIndexEntryCount);
        Assert.Equal(diagnostics.IndexEntryCount - 2, diagnostics.EmptyIndexEntryCount);
        Assert.Equal(diagnostics.EmptyIndexEntryCount + diagnostics.TombstoneIndexEntryCount, diagnostics.UsableIndexCapacity);
    }

    [Fact]
    public void PressureCompactionClearsTombstonesAndPreservesValues()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 4, maxKeyBytes: 8));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(StoreStatus.Success, store.TryPublish(ChurnKeyFactory.Key(i), [(byte)i]));
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(StoreStatus.Success, store.TryRemove(ChurnKeyFactory.Key(i)));
        }

        var diagnostics = store.GetDiagnostics();
        Assert.True(diagnostics.IndexCompactionCount > 0);
        Assert.Equal(0, diagnostics.TombstoneIndexEntryCount);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(ChurnKeyFactory.Key(3), out var lease));
        Assert.Equal(3, lease.ValueSpan[0]);
        lease.Dispose();
    }
}
