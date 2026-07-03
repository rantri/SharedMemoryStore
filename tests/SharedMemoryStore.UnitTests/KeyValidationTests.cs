using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class KeyValidationTests
{
    [Fact]
    public void EmptyAndOversizedKeysReturnDistinctOutcomesAcrossOperations()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(maxKeyBytes: 2));

        Assert.Equal(StoreStatus.InvalidKey, store.TryPublish(ReadOnlySpan<byte>.Empty, [1]));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryPublish([1, 2, 3], [1]));

        Assert.Equal(StoreStatus.InvalidKey, store.TryReserve(ReadOnlySpan<byte>.Empty, 1, default, out _));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryReserve([1, 2, 3], 1, default, out _));

        Assert.Equal(StoreStatus.InvalidKey, store.TryAcquire(ReadOnlySpan<byte>.Empty, out _));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryAcquire([1, 2, 3], out _));

        Assert.Equal(StoreStatus.InvalidKey, store.TryRemove(ReadOnlySpan<byte>.Empty));
        Assert.Equal(StoreStatus.KeyTooLarge, store.TryRemove([1, 2, 3]));
    }
}
