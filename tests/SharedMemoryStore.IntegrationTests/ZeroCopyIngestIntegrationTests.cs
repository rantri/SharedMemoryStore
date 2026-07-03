using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class ZeroCopyIngestIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void DirectSpanFillCommitsCompleteImmutableValue()
    {
        var payload = Enumerable.Range(0, 1024).Select(i => (byte)(i % 251)).ToArray();
        var descriptor = new byte[] { 1, 2, 3, 4 };
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(
            slotCount: 2,
            maxValueBytes: payload.Length,
            maxDescriptorBytes: descriptor.Length));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], payload.Length, descriptor, out var reservation));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        payload.CopyTo(reservation.GetSpan(payload.Length));
        Assert.Equal(StoreStatus.Success, reservation.Advance(payload.Length));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.True(payload.AsSpan().SequenceEqual(lease.ValueSpan));
        Assert.True(descriptor.AsSpan().SequenceEqual(lease.DescriptorSpan));
        lease.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DirectSpanFillCommitsSmallImmutableValue()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 4, [9], out var reservation));
        new byte[] { 4, 5, 6, 7 }.CopyTo(reservation.GetSpan(4));
        Assert.Equal(StoreStatus.Success, reservation.Advance(4));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var lease));
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 9 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void MixedPublishAndIngestAcquireRemoveReuseWorkflowsRemainCompatible()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 2));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2], [3]));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 3, [4], out var reservation));
        new byte[] { 5, 6, 7 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(3));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var publishLease));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var ingestLease));
        Assert.Equal(new byte[] { 1, 2 }, publishLease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 5, 6, 7 }, ingestLease.ValueSpan.ToArray());
        publishLease.Dispose();
        ingestLease.Dispose();

        Assert.Equal(StoreStatus.Success, store.TryRemove([1]));
        Assert.Equal(StoreStatus.Success, store.TryRemove([2]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [8]));
    }
}
