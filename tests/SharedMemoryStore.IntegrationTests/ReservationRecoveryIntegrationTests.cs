using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class ReservationRecoveryIntegrationTests
{
    private const int FailureInjectionCycleCount = 100_000;
    private static readonly byte[] FailureInjectionKey = [0x33];

    [Fact]
    [Trait("Category", "Integration")]
    public void ExplicitRecoveryReclaimsControlledStaleReservation()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 3, default, out _));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));

        Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
        Assert.Equal(1, report.ScannedReservationCount);
        Assert.Equal(1, report.RecoveredReservationCount);
        Assert.Equal(0, report.FailedRecoveryCount);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [9]));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void AbortReturnsSlotToFreePoolWithoutVisibility()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 3, default, out var reservation));
        reservation.GetSpan()[0] = 8;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Abort());

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1, 2, 3]));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Stress")]
    public void OneHundredThousandFailureInjectionCyclesReclaimCapacity()
    {
        using var store = IntegrationStoreFactory.Create(
            IntegrationStoreFactory.Options(
                slotCount: 1,
                maxValueBytes: 8,
                leaseRecordCount: 4));

        for (var i = 0; i < FailureInjectionCycleCount; i++)
        {
            switch (i % 4)
            {
                case 0:
                    Assert.Equal(StoreStatus.Success, store.TryReserve(FailureInjectionKey, 4, default, out var abortReservation));
                    abortReservation.GetSpan()[0] = 1;
                    Assert.Equal(StoreStatus.Success, abortReservation.Advance(1));
                    Assert.Equal(StoreStatus.Success, abortReservation.Abort());
                    break;

                case 1:
                    Assert.Equal(StoreStatus.Success, store.TryReserve(FailureInjectionKey, 4, default, out var disposedReservation));
                    disposedReservation.GetSpan()[0] = 2;
                    Assert.Equal(StoreStatus.Success, disposedReservation.Advance(1));
                    disposedReservation.Dispose();
                    break;

                case 2:
                    Assert.Equal(StoreStatus.Success, store.TryReserve(FailureInjectionKey, 4, default, out var incompleteReservation));
                    incompleteReservation.GetSpan()[0] = 3;
                    Assert.Equal(StoreStatus.Success, incompleteReservation.Advance(1));
                    Assert.Equal(StoreStatus.ReservationIncomplete, incompleteReservation.Commit());
                    Assert.Equal(StoreStatus.NotFound, store.TryAcquire(FailureInjectionKey, out _));
                    Assert.Equal(StoreStatus.Success, incompleteReservation.Abort());
                    break;

                default:
                    Assert.Equal(StoreStatus.Success, store.TryReserve(FailureInjectionKey, 4, default, out var recoveredReservation));
                    recoveredReservation.GetSpan()[0] = 4;
                    Assert.Equal(StoreStatus.Success, recoveredReservation.Advance(1));
                    Assert.Equal(StoreStatus.Success, store.TryRecoverReservations(new ReservationRecoveryOptions(true), out var report));
                    Assert.Equal(1, report.RecoveredReservationCount);
                    Assert.Equal(StoreStatus.InvalidReservation, recoveredReservation.Abort());
                    break;
            }

            Assert.Equal(StoreStatus.NotFound, store.TryAcquire(FailureInjectionKey, out _));
            Assert.Equal(StoreStatus.Success, store.TryPublish(FailureInjectionKey, [9]));
            Assert.Equal(StoreStatus.Success, store.TryRemove(FailureInjectionKey));
        }

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(FailureInjectionCycleCount / 4, diagnostics.GetFailureCount(StoreStatus.ReservationIncomplete));
        Assert.Equal(FailureInjectionCycleCount / 4, diagnostics.RecoveredReservationCount);
        Assert.Equal(FailureInjectionCycleCount / 4, diagnostics.GetFailureCount(StoreStatus.InvalidReservation));
        Assert.Equal((FailureInjectionCycleCount / 4) * 3, diagnostics.AbortedReservationCount);
        Assert.Equal(1, diagnostics.FreeSlotCount);
    }
}
