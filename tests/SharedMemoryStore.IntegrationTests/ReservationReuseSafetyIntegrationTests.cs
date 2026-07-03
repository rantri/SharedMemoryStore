using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class ReservationReuseSafetyIntegrationTests
{
    [Fact]
    [Trait("Category", "ReservationReuseSafety")]
    public void StaleReservationTokenCannotCompleteAfterSlotReuseCycles()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1, maxValueBytes: 4));

        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, default, out var staleReservation));
        Assert.Equal(StoreStatus.Success, staleReservation.Abort());

        for (var i = 0; i < 10_000; i++)
        {
            var key = new[] { (byte)(i % 251 + 1) };
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)i]));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var lease));
            Assert.Equal((byte)i, lease.ValueSpan[0]);
            Assert.Equal(StoreStatus.Success, lease.Release());
            Assert.Equal(StoreStatus.Success, store.TryRemove(key));
        }

        Assert.Equal(StoreStatus.InvalidReservation, staleReservation.Advance(1));
        Assert.Equal(StoreStatus.InvalidReservation, staleReservation.Commit());
        Assert.Equal(StoreStatus.InvalidReservation, staleReservation.Abort());
    }
}
