using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class FrameValueIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void FrameShapedDescriptorAndPayloadUseGeneralLifecycle()
    {
        var payload = Enumerable.Range(0, 1_300_000).Select(i => (byte)(i % 239)).ToArray();
        var descriptor = BitConverter.GetBytes(payload.Length);
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(
            slotCount: 2,
            maxValueBytes: payload.Length,
            maxDescriptorBytes: descriptor.Length,
            leaseRecordCount: 4));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], payload, descriptor));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var second));
        Assert.True(payload.AsSpan().SequenceEqual(first.ValueSpan));
        Assert.True(first.ValueSpan.SequenceEqual(second.ValueSpan));

        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        first.Dispose();
        second.Dispose();
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [9, 9, 9]));
    }
}
