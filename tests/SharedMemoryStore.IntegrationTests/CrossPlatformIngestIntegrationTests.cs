using System.Buffers;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformIngestIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void DirectReservationCommitIsVisibleOnSupportedHosts()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, [9], out var reservation));
        new byte[] { 1, 2, 3, 4 }.CopyTo(reservation.GetSpan(4));
        Assert.Equal(StoreStatus.Success, reservation.Advance(4));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        using (lease)
        {
            Assert.True(lease.ValueSpan.SequenceEqual(new byte[] { 1, 2, 3, 4 }));
            Assert.True(lease.DescriptorSpan.SequenceEqual(new byte[] { 9 }));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void SegmentedPublishIsVisibleOnSupportedHosts()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options());
        var payload = new ReadOnlySequence<byte>([5, 6, 7]);

        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([2], payload, [8], out var copied));
        Assert.Equal(3, copied);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var lease));
        using (lease)
        {
            Assert.True(lease.ValueSpan.SequenceEqual(new byte[] { 5, 6, 7 }));
            Assert.True(lease.DescriptorSpan.SequenceEqual(new byte[] { 8 }));
        }
    }
}
