using SharedMemoryStore.IntegrationTests.TestSupport;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class IngestVisibilityConcurrencyTests
{
    private const int VisibilityStressCycleCount = 1_000_000;
    private static readonly byte[] VisibilityKey = [0x42];
    private static readonly byte[] VisibilityPayload = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x7F];

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ReadersDoNotObservePendingReservations()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1, maxValueBytes: 8, leaseRecordCount: 16));
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 4, default, out var reservation));

        var stop = new CancellationTokenSource();
        var observedPublished = false;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var status = store.TryAcquire([1], out var lease);
                if (status == StoreStatus.Success)
                {
                    observedPublished = true;
                    lease.Dispose();
                }
                else
                {
                    Assert.Equal(StoreStatus.NotFound, status);
                }
            }
        });

        await Task.Delay(25);
        Assert.False(observedPublished);
        new byte[] { 1, 2, 3, 4 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(4));
        Assert.Equal(StoreStatus.Success, reservation.Commit());
        await Task.Delay(25);
        stop.Cancel();
        await reader;

        Assert.True(observedPublished);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void RemoveWhileLeasedCommittedIngestValueDelaysReuseUntilRelease()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 1));
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var reservation));
        new byte[] { 1, 2 }.CopyTo(reservation.GetSpan());
        Assert.Equal(StoreStatus.Success, reservation.Advance(2));
        Assert.Equal(StoreStatus.Success, reservation.Commit());

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));
        Assert.Equal(StoreStatus.StoreFull, store.TryPublish([2], [3]));
        Assert.Equal(new byte[] { 1, 2 }, lease.ValueSpan.ToArray());

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [3]));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Stress")]
    public async Task OneMillionReserveCommitAcquireCyclesNeverExposePendingBytes()
    {
        using var store = IntegrationStoreFactory.Create(
            IntegrationStoreFactory.Options(
                slotCount: 1,
                maxValueBytes: VisibilityPayload.Length,
                leaseRecordCount: 64));

        for (var i = 0; i < VisibilityStressCycleCount; i++)
        {
            Assert.Equal(StoreStatus.Success, store.TryReserve(VisibilityKey, VisibilityPayload.Length, default, out var reservation));
            var readerProbe = (i & 0x3FF) == 0
                ? ProbePendingReservationAsync(store)
                : Task.CompletedTask;

            VisibilityPayload.CopyTo(reservation.GetSpan(VisibilityPayload.Length));
            Assert.Equal(StoreStatus.Success, reservation.Advance(VisibilityPayload.Length));
            Assert.Equal(StoreStatus.NotFound, store.TryAcquire(VisibilityKey, out _));
            await readerProbe;

            Assert.Equal(StoreStatus.Success, reservation.Commit());
            Assert.Equal(StoreStatus.Success, store.TryAcquire(VisibilityKey, out var lease));
            Assert.True(VisibilityPayload.AsSpan().SequenceEqual(lease.ValueSpan));
            Assert.Equal(StoreStatus.Success, lease.Release());

            var remove = store.TryRemove(VisibilityKey);
            if (remove == StoreStatus.RemovePending)
            {
                WaitForFreeSlot(store);
            }
            else
            {
                Assert.Equal(StoreStatus.Success, remove);
            }
        }
    }

    private static Task ProbePendingReservationAsync(Store store)
    {
        return Task.Run(() =>
        {
            for (var i = 0; i < 32; i++)
            {
                Assert.Equal(StoreStatus.NotFound, store.TryAcquire(VisibilityKey, out _));
            }
        });
    }

    private static void WaitForFreeSlot(Store store)
    {
        var timeout = TimeSpan.FromSeconds(30);
        var started = DateTime.UtcNow;
        var spin = new SpinWait();
        while (store.GetDiagnostics().FreeSlotCount == 0)
        {
            if (DateTime.UtcNow - started > timeout)
            {
                throw new TimeoutException("Timed out waiting for readers to release the committed ingest value.");
            }

            spin.SpinOnce();
        }
    }

}
