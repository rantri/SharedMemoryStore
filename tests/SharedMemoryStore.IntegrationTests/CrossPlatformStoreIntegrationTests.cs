using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformStoreIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void SeparateProcessCanPublishValueVisibleToCurrentProcess()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        var options = IntegrationStoreFactory.Options(
            slotCount: 4,
            maxValueBytes: 32,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount: 8);

        using var store = IntegrationStoreFactory.Create(options);
        using var owner = LeaseOwnerProcessHarness.StartLiveOwner(options, keyValue: 7);

        var key = BitConverter.GetBytes(7);
        var acquire = store.TryAcquire(key, out var lease);
        Assert.Equal(StoreStatus.Success, acquire);
        using (lease)
        {
            Assert.Equal(1, lease.ValueLength);
            Assert.Equal(7, lease.ValueSpan[0]);
        }

        Assert.True(owner.CheckLeaseValid());
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key));
        Assert.True(owner.CheckLeaseValid());
        Assert.Equal(StoreStatus.Success, owner.Release());
    }
}
