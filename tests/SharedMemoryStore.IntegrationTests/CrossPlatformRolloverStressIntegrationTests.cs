using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformRolloverStressIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Stress")]
    public void SupportedHostsCompleteReuseChurnWithoutStaleLeaseAcceptance()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        using var store = IntegrationStoreFactory.Create(
            IntegrationStoreFactory.Options(
                slotCount: 2,
                maxValueBytes: 8,
                maxKeyBytes: 4,
                leaseRecordCount: 4));

        for (var i = 0; i < 10_000; i++)
        {
            var key = BitConverter.GetBytes(i);
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)i]));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
            var stale = lease;
            Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key));
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.False(stale.IsValid);
        }

        Assert.Equal(2, store.GetDiagnostics().FreeSlotCount);
    }
}
