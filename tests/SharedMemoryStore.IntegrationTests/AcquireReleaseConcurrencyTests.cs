using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class AcquireReleaseConcurrencyTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void ConcurrentAcquireReleaseAndDuplicatePublishRemainDeterministic()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 4, leaseRecordCount: 32));
        var key = new byte[] { 1 };
        var value = new byte[] { 42, 43, 44 };
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, value));

        Parallel.For(0, 100, _ =>
        {
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
            Assert.True(value.AsSpan().SequenceEqual(lease.ValueSpan));
            Assert.Equal(StoreStatus.Success, lease.Release());
        });

        var statuses = new StoreStatus[8];
        Parallel.For(0, statuses.Length, i => statuses[i] = store.TryPublish(key, [1]));
        Assert.All(statuses, status => Assert.Equal(StoreStatus.DuplicateKey, status));

        Parallel.For(0, 3, i =>
        {
            var localKey = new[] { (byte)(10 + i) };
            Assert.Equal(StoreStatus.Success, store.TryPublish(localKey, [2]));
            Assert.True(store.TryRemove(localKey) is StoreStatus.Success or StoreStatus.RemovePending);
        });
    }
}
