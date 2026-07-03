namespace SharedMemoryStore.ContractTests;

public sealed class ErrorTaxonomyContractTests
{
    [Fact]
    public void ReservationStatusesAndDiagnosticsAreExposed()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, default, out var reservation));
        Assert.Equal(StoreStatus.ReservationIncomplete, reservation.Commit());
        Assert.Equal(StoreStatus.ReservationWriteOutOfRange, reservation.Advance(5));
        Assert.Equal(StoreStatus.Success, reservation.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, reservation.Abort());

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.ReservationIncomplete));
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.ReservationWriteOutOfRange));
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.InvalidReservation));
        Assert.Equal(1, diagnostics.AbortedReservationCount);
        Assert.Equal(0, diagnostics.ActiveReservationRecoveryCount);
        Assert.Equal(0, diagnostics.UnsupportedReservationRecoveryCount);
        Assert.Equal(0, diagnostics.FailedReservationRecoveryCount);
        Assert.Equal(StoreStatus.InvalidReservation, diagnostics.LastFailureStatus);
        Assert.Equal(1, diagnostics.GetFailureCount(StoreStatus.ReservationIncomplete));
    }
}
