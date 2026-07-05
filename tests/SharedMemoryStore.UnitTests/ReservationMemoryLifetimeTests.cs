using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class ReservationMemoryLifetimeTests
{
    [Fact]
    public void CompletedReservationDoesNotExposeWritableSpan()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var commitReservation));
        new byte[] { 1, 2 }.CopyTo(commitReservation.GetSpan(2));
        Assert.Equal(StoreStatus.Success, commitReservation.Advance(2));
        Assert.Equal(StoreStatus.Success, commitReservation.Commit());
        Assert.True(commitReservation.GetSpan().IsEmpty);
        Assert.True(commitReservation.DangerousGetMemory().IsEmpty);

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, default, out var abortReservation));
        Assert.Equal(StoreStatus.Success, abortReservation.Abort());
        Assert.True(abortReservation.GetSpan().IsEmpty);
        Assert.True(abortReservation.DangerousGetMemory().IsEmpty);

        Assert.Equal(StoreStatus.Success, store.TryReserve([3], 1, default, out var disposeReservation));
        disposeReservation.Dispose();
        Assert.True(disposeReservation.GetSpan().IsEmpty);
        Assert.True(disposeReservation.DangerousGetMemory().IsEmpty);
    }

    [Fact]
    public void ReservationAfterStoreDisposalReturnsDisposedOrEmptyOutcomes()
    {
        var store = StoreTestNames.CreateStore(StoreTestNames.Options());
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var reservation));

        store.Dispose();

        Assert.True(reservation.GetSpan().IsEmpty);
        Assert.True(reservation.DangerousGetMemory().IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Advance(1));
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Commit());
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Abort());
    }

    [Fact]
    public void StaleReservationTokenCannotAffectReusedSlot()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var stale));
        Assert.Equal(StoreStatus.Success, stale.Abort());

        for (var i = 0; i < 100; i++)
        {
            var key = new[] { (byte)(i + 2) };
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)i]));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
            Assert.Equal((byte)i, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.Equal(StoreStatus.Success, store.TryRemove(key));
        }

        Assert.True(stale.GetSpan().IsEmpty);
        Assert.True(stale.DangerousGetMemory().IsEmpty);
        Assert.Equal(StoreStatus.InvalidReservation, stale.Advance(1));
    }
}
