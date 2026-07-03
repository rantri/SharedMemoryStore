using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class ReservationStateTests
{
    [Fact]
    public void PendingReservationIsInvisibleAndBlocksDuplicateKey()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 3, [9], out var reservation));
        Assert.True(reservation.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryReserve([1], 3, default, out _));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [1, 2, 3]));

        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    [Fact]
    public void CommitRequiresExactAdvancedBytes()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, [7], out var reservation));
        new byte[] { 1, 2 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(2, reservation.BytesWritten);
        Assert.Equal(2, reservation.RemainingBytes);
        Assert.Equal(StoreStatus.ReservationIncomplete, reservation.Commit());

        new byte[] { 3, 4 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(StoreStatus.ReservationWriteOutOfRange, reservation.Advance(1));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 7 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
    }

    [Fact]
    public void AbortDisposeAndRepeatedCompletionAreDeterministic()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var abortReservation));
        Assert.Equal(StoreStatus.Success, abortReservation.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, abortReservation.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, abortReservation.Commit());
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([1], out _));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var disposeReservation));
        disposeReservation.Dispose();
        Assert.False(disposeReservation.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var commitReservation));
        commitReservation.GetSpan()[0] = 42;
        Assert.Equal(StoreStatus.Success, commitReservation.Advance(1));
        Assert.Equal(StoreStatus.Success, commitReservation.Commit());
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, commitReservation.Abort());
        Assert.Equal(StoreStatus.ReservationAlreadyCompleted, commitReservation.Commit());
    }

    [Fact]
    public void SpanViewCanFillReservationPayload()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options());

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 3, default, out var reservation));
        new byte[] { 8, 9, 10 }.CopyTo(reservation.GetSpan(3));
        Assert.Equal(StoreStatus.Success, reservation.Advance(3));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(new byte[] { 8, 9, 10 }, lease.ValueSpan.ToArray());
        lease.Dispose();
    }
}
