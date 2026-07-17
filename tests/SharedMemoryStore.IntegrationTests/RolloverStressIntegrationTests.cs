using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class RolloverStressIntegrationTests
{
    private const int PostBoundaryOperationCount = 1_000_000;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task StoreCompletesOneMillionMixedOperationsUnderConcurrency()
    {
        using var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 16, maxValueBytes: 4, maxKeyBytes: 4, leaseRecordCount: 16));
        Assert.Equal(StoreStatus.Success, store.TryPublish(Key(1), [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(Key(1), out var boundaryLease));
        var staleLease = boundaryLease;
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove(Key(1)));
        Assert.Equal(StoreStatus.Success, boundaryLease.Release());
        Assert.False(staleLease.IsValid);

        var exceptions = new ConcurrentQueue<Exception>();
        var completed = 0;
        using var start = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, 4).Select(worker => Task.Run(() =>
        {
            start.Wait();
            while (true)
            {
                var operation = Interlocked.Increment(ref completed);
                if (operation > PostBoundaryOperationCount)
                {
                    return;
                }

                try
                {
                    RecordDocumentedOutcome(InvokeMixedOperation(store, worker, operation));
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.Equal(PostBoundaryOperationCount + workers.Length, Volatile.Read(ref completed));
        Assert.Empty(exceptions);
        Assert.False(staleLease.IsValid);
    }

    private static StoreStatus InvokeMixedOperation(MemoryStore store, int worker, int operation)
    {
        var key = Key(100_000 + operation);
        return ((operation + worker) % 8) switch
        {
            0 => PublishAcquireRemoveRelease(store, key, operation),
            1 => ReserveCommitRemove(store, key, operation),
            2 => ReserveAbort(store, key),
            3 => store.TryRecoverLeases(new LeaseRecoveryOptions(true), out _),
            4 => store.TryRecoverReservations(new ReservationRecoveryOptions(true), out _),
            5 => store.TryAcquire(key, out _),
            6 => PublishSegmentsRemove(store, key, operation),
            _ => ReadDiagnostics(store)
        };
    }

    private static StoreStatus PublishAcquireRemoveRelease(MemoryStore store, byte[] key, int operation)
    {
        var status = store.TryPublish(key, [(byte)operation]);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        status = store.TryAcquire(key, out var lease);
        if (status != StoreStatus.Success)
        {
            _ = store.TryRemove(key);
            return status;
        }

        status = store.TryRemove(key);
        if (status != StoreStatus.RemovePending)
        {
            _ = lease.Release();
            return status;
        }

        return lease.Release();
    }

    private static StoreStatus ReserveCommitRemove(MemoryStore store, byte[] key, int operation)
    {
        var status = store.TryReserve(key, 1, default, out var reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        var span = reservation.GetSpan(1);
        if (span.IsEmpty)
        {
            _ = reservation.Abort();
            return StoreStatus.InvalidReservation;
        }

        span[0] = (byte)operation;
        status = reservation.Advance(1);
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return status;
        }

        status = reservation.Commit();
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return status;
        }

        return store.TryRemove(key);
    }

    private static StoreStatus ReserveAbort(MemoryStore store, byte[] key)
    {
        var status = store.TryReserve(key, 1, default, out var reservation);
        return status == StoreStatus.Success ? reservation.Abort() : status;
    }

    private static StoreStatus PublishSegmentsRemove(MemoryStore store, byte[] key, int operation)
    {
        var status = store.TryPublishSegments(key, new ReadOnlySequence<byte>([(byte)operation]), default, out _);
        return status == StoreStatus.Success ? store.TryRemove(key) : status;
    }

    private static StoreStatus ReadDiagnostics(MemoryStore store)
    {
        _ = store.GetDiagnostics();
        return StoreStatus.Success;
    }

    private static void RecordDocumentedOutcome(StoreStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new InvalidOperationException("Operation returned an undefined status: " + status);
        }

        if (status == StoreStatus.UnknownFailure)
        {
            throw new InvalidOperationException("Operation returned UnknownFailure during concurrency stress.");
        }
    }

    private static byte[] Key(int value) => BitConverter.GetBytes(value);
}
