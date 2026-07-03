using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreDisposalRaceTests
{
    [Fact]
    public void StoreOperationsRacingDisposeReturnDocumentedOutcomes()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 4));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));

        ConcurrentOperationRunner.RunDisposalRace(
            10_000,
            i =>
            {
                var key = new[] { (byte)(i % 3 + 1) };
                return (i % 5) switch
                {
                    0 => store.TryPublish(key, [1]),
                    1 => store.TryAcquire(key, out var lease) == StoreStatus.Success ? lease.Release() : StoreStatus.NotFound,
                    2 => store.TryRemove(key),
                    3 => store.TryRecoverLeases(new LeaseRecoveryOptions(true), out _),
                    _ => store.TryRecoverReservations(new ReservationRecoveryOptions(true), out _)
                };
            },
            store.Dispose);
    }

    [Fact]
    public void TokensAfterDisposalReturnEmptyOrDisposedOutcomes()
    {
        var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 2, default, out var reservation));

        store.Dispose();

        Assert.False(lease.IsValid);
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, lease.Release());
        Assert.False(reservation.IsValid);
        Assert.True(reservation.GetMemory().IsEmpty);
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Advance(1));
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Commit());
        Assert.Equal(StoreStatus.StoreDisposed, reservation.Abort());
    }
}
