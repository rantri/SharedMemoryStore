using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LeaseRecoveryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void ExplicitRecoveryCanRecoverCurrentProcessLeaseWhenRequested()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(enableRecovery: true));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        var status = store.TryRecoverLeases(new LeaseRecoveryOptions(RecoverCurrentProcessLeases: true), out var report);

        Assert.Equal(StoreStatus.Success, status);
        Assert.Equal(1, report.RecoveredLeaseCount);
        Assert.False(lease.IsValid);
        Assert.Equal(StoreStatus.LeaseAlreadyReleased, lease.Release());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RecoveryReturnsUnsupportedWhenDisabled()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(enableRecovery: false));

        Assert.Equal(StoreStatus.UnsupportedPlatform, store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report));
        Assert.Equal(0, report.RecoveredLeaseCount);
        Assert.True(report.UnsupportedLeaseCount > 0);
    }
}
