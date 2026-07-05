namespace SharedMemoryStore.ContractTests;

public sealed class ReservationMemoryContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ReservationMemoryLifetime)]
    public void ValueReservationDoesNotExposeGeneralRetainedWritableMemory()
    {
        Assert.Null(typeof(ValueReservation).GetMethod("GetMemory"));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.GetSpan)));
        Assert.NotNull(typeof(ValueReservation).GetMethod(nameof(ValueReservation.DangerousGetMemory)));
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ReservationMemoryLifetime)]
    public void CompletedReservationNoLongerProjectsWritableSpan()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));
        reservation.GetSpan(1)[0] = 9;
        Assert.Equal(StoreStatus.Success, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.False(reservation.IsValid);
        Assert.True(reservation.GetSpan().IsEmpty);
        Assert.True(reservation.DangerousGetMemory().IsEmpty);
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, reservation.Advance(0));
    }
}
