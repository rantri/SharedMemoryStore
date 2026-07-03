using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class StoreDisposalRaceIntegrationTests
{
    private const int RaceOperationCount = 100_000;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task PublicOperationsSurviveOneHundredThousandDisposalRaceCalls()
    {
        var store = IntegrationStoreFactory.Create(IntegrationStoreFactory.Options(slotCount: 64, maxValueBytes: 8, maxKeyBytes: 4, leaseRecordCount: 64));
        Assert.Equal(StoreStatus.Success, store.TryPublish(Key(1), [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(Key(1), out var heldLease));
        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(2), 4, default, out var advanceReservation));
        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(3), 1, default, out var commitReservation));
        commitReservation.GetSpan()[0] = 3;
        Assert.Equal(StoreStatus.Success, commitReservation.Advance(1));
        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(4), 1, default, out var abortReservation));
        Assert.Equal(StoreStatus.Success, store.TryReserve(Key(5), 1, default, out var disposeReservation));

        var exceptions = new ConcurrentQueue<Exception>();
        var completed = 0;
        using var start = new ManualResetEventSlim();

        var workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            start.Wait();
            while (true)
            {
                var operation = Interlocked.Increment(ref completed);
                if (operation > RaceOperationCount)
                {
                    return;
                }

                try
                {
                    RecordDocumentedOutcome(InvokeOperation(store, worker, operation, heldLease, advanceReservation, commitReservation, abortReservation, disposeReservation));
                }
                catch (Exception ex)
                {
                    exceptions.Enqueue(ex);
                }
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(workers);

        Assert.Equal(RaceOperationCount + workers.Length, Volatile.Read(ref completed));
        Assert.Empty(exceptions);
    }

    private static StoreStatus InvokeOperation(
        SharedMemoryStore store,
        int worker,
        int operation,
        ValueLease heldLease,
        ValueReservation advanceReservation,
        ValueReservation commitReservation,
        ValueReservation abortReservation,
        ValueReservation disposeReservation)
    {
        var key = Key(10_000 + operation);
        return ((operation + worker) % 14) switch
        {
            0 => store.TryPublish(key, [(byte)operation]),
            1 => ReserveAndDispose(store, key),
            2 => store.TryAcquire(Key(1), out var lease) == StoreStatus.Success ? lease.Release() : StoreStatus.NotFound,
            3 => store.TryRemove(key),
            4 => store.TryRecoverLeases(new LeaseRecoveryOptions(true), out _),
            5 => store.TryRecoverReservations(new ReservationRecoveryOptions(true), out _),
            6 => ReadDiagnostics(store),
            7 => heldLease.Release(),
            8 => advanceReservation.Advance(0),
            9 => commitReservation.Commit(),
            10 => abortReservation.Abort(),
            11 => DisposeReservation(disposeReservation),
            12 => store.TryPublishSegments(key, new ReadOnlySequence<byte>([(byte)operation]), default, out _),
            _ => DisposeStore(store)
        };
    }

    private static StoreStatus ReadDiagnostics(SharedMemoryStore store)
    {
        _ = store.GetDiagnostics();
        return StoreStatus.Success;
    }

    private static StoreStatus ReserveAndDispose(SharedMemoryStore store, byte[] key)
    {
        var status = store.TryReserve(key, 1, default, out var reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        reservation.Dispose();
        return StoreStatus.Success;
    }

    private static StoreStatus DisposeReservation(ValueReservation reservation)
    {
        reservation.Dispose();
        return StoreStatus.Success;
    }

    private static StoreStatus DisposeStore(SharedMemoryStore store)
    {
        store.Dispose();
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
            throw new InvalidOperationException("Operation returned UnknownFailure during disposal race.");
        }
    }

    private static byte[] Key(int value) => BitConverter.GetBytes(value);
}
