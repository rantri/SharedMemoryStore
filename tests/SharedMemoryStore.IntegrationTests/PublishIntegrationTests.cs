using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class PublishIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void PublishesLargeValueAndDescriptorIntoNamedStore()
    {
        var value = Enumerable.Range(0, 1_300_000).Select(i => (byte)(i % 251)).ToArray();
        var descriptor = new byte[] { 1, 2, 3, 4, 5, 6 };
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(
            slotCount: 2,
            maxValueBytes: value.Length,
            maxDescriptorBytes: descriptor.Length));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1, 2, 3], value, descriptor));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1, 2, 3], out var lease));
        Assert.Equal(value.Length, lease.ValueLength);
        Assert.Equal(descriptor.Length, lease.DescriptorLength);
        Assert.True(value.AsSpan().SequenceEqual(lease.ValueSpan));
        Assert.True(descriptor.AsSpan().SequenceEqual(lease.DescriptorSpan));
        lease.Dispose();
    }
}
