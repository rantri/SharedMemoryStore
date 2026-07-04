using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformRecoveryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void LeaseAndReservationRecoveryUseSupportedHostOwnerClassification()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(enableRecovery: true));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var leaseReport));
        Assert.Equal(1, leaseReport.RecoveredLeaseCount);
        Assert.False(lease.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 2, default, out var reservation));
        reservation.GetSpan(1)[0] = 2;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var reservationReport));
        Assert.Equal(1, reservationReport.RecoveredReservationCount);
    }
}
