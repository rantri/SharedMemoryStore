namespace SharedMemoryStore.ContractTests;

public sealed class ValueLeaseContractTests
{
    [Fact]
    public void ValueLeaseExposesSpansAndExactlyOnceRelease()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [10, 11, 12], [5, 6]));

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.True(lease.IsValid);
        Assert.Equal(3, lease.ValueLength);
        Assert.Equal(2, lease.DescriptorLength);
        Assert.Equal(new byte[] { 10, 11, 12 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 5, 6 }, lease.DescriptorSpan.ToArray());

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.False(lease.IsValid);
        Assert.Equal(0, lease.ValueLength);
        Assert.Equal(0, lease.DescriptorLength);
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.True(lease.DescriptorSpan.IsEmpty);
        Assert.Equal(StoreStatus.LeaseAlreadyReleased, lease.Release());
    }

    [Fact]
    public void DefaultLeaseIsInvalid()
    {
        var lease = default(ValueLease);
        Assert.False(lease.IsValid);
        Assert.Equal(StoreStatus.InvalidLease, lease.Release());
    }

    [Fact]
    public void CommittedPayloadRemainsVisibleAfterReservationCompletes()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 3, default, out var reservation));
        new byte[] { 4, 5, 6 }.CopyTo(reservation.GetSpan(3));
        Assert.Equal(StoreStatus.Success, reservation.Advance(3));
        Assert.Equal(StoreStatus.Success, reservation.Commit());
        Assert.True(reservation.GetSpan().IsEmpty);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new byte[] { 4, 5, 6 }, lease.ValueSpan.ToArray());
        lease.Dispose();
    }
}
