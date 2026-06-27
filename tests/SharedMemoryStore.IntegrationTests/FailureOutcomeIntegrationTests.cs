using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class FailureOutcomeIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void FailureOutcomesAreDeterministic()
    {
        var options = IntegrationStoreFactory.Options(slotCount: 1, maxValueBytes: 2, maxDescriptorBytes: 1, maxKeyBytes: 1);
        var store = IntegrationStoreFactory.Create(options);

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.ValueTooLarge, store.TryPublish([1], [1, 2, 3]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([2], [1]));
        Assert.Equal(StoreStatus.InvalidLease, default(ValueLease).Release());

        store.Dispose();
        Assert.Equal(StoreStatus.StoreDisposed, store.TryPublish([1], [1]));
    }
}
