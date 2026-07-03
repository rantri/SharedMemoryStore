namespace SharedMemoryStore.ContractTests;

public sealed class LifecycleOutcomeContractTests
{
    [Fact]
    public void PublicOperationsReturnDocumentedDisposedOutcomes()
    {
        var store = ContractStoreFactory.Create(ContractStoreFactory.Options());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var reservation));

        store.Dispose();

        ReliabilityAssertions.AssertDisposedOutcome(store.TryPublish([3], [3]));
        ReliabilityAssertions.AssertDisposedOutcome(store.TryReserve([4], 1, default, out var disposedReservation));
        Assert.False(disposedReservation.IsValid);
        ReliabilityAssertions.AssertDisposedOutcome(store.TryAcquire([1], out var disposedLease));
        Assert.False(disposedLease.IsValid);
        ReliabilityAssertions.AssertDisposedOutcome(store.TryRemove([1]));
        ReliabilityAssertions.AssertDisposedOutcome(store.TryRecoverLeases(new LeaseRecoveryOptions(true), out _));
        ReliabilityAssertions.AssertDisposedOutcome(store.TryRecoverReservations(new ReservationRecoveryOptions(true), out _));
        ReliabilityAssertions.AssertDisposedOutcome(lease.Release());
        ReliabilityAssertions.AssertDisposedOutcome(reservation.Abort());
        Assert.False(lease.IsValid);
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.False(reservation.IsValid);
        Assert.True(reservation.GetSpan().IsEmpty);

        var diagnostics = store.GetDiagnostics();
        Assert.Equal(0, diagnostics.PublishedSlotCount);
    }

    [Fact]
    public void DisposeIsIdempotentAndDoesNotThrow()
    {
        var store = ContractStoreFactory.Create(ContractStoreFactory.Options());

        ReliabilityAssertions.AssertNoInternalLifecycleFailure(() =>
        {
            store.Dispose();
            store.Dispose();
        });
    }
}
