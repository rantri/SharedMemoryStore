using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class PublishValidationTests
{
    [Fact]
    public void TryPublishRejectsKeyDescriptorAndValueBoundaries()
    {
        var options = StoreTestNames.Options(maxKeyBytes: 4, maxDescriptorBytes: 2, maxValueBytes: 3);
        using var store = StoreTestNames.CreateStore(options);

        Assert.Equal(StoreStatus.InvalidKey, store.TryPublish(ReadOnlySpan<byte>.Empty, [1]));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryPublish([1, 2, 3, 4, 5], [1]));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryPublish([1], [1, 2, 3, 4]));
        Assert.Equal(StoreStatus.DescriptorTooLarge, store.TryPublish([1], [1], [1, 2, 3]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1, 2, 3, 4], [1, 2, 3], [1, 2]));
    }

    [Fact]
    public void TryPublishDoesNotAllocateAfterWarmup()
    {
        var options = StoreTestNames.Options(slotCount: 2, maxValueBytes: 16);
        using var store = StoreTestNames.CreateStore(options);
        var key = new byte[] { 1 };
        var value = new byte[] { 2, 3, 4 };

        AllocationAssert.NoAllocAfterWarmup(() =>
        {
            _ = store.TryRemove(key);
            return store.TryPublish(key, value);
        });
    }
}
