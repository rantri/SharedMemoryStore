using System.Diagnostics;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.InteropAgent;

internal sealed record AgentCheckpointSpec(
    LockFreeCheckpointId Checkpoint,
    int Occurrence,
    string Operation,
    SharedMemoryStoreOptions Options,
    byte[] Key,
    byte[] Value,
    byte[] Descriptor);

internal readonly record struct AgentCheckpointCompletion(
    StoreStatus Status,
    StoreOpenStatus OpenStatus);

/// <summary>
/// Owns one test-only instrumented operation while the JSON request loop stays
/// responsive. The checkpoint gate is process-local and never enters SMS2.
/// </summary>
internal sealed class AgentCheckpointOperation : IDisposable
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(10);
    private readonly AgentCheckpointSpec _spec;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly ManualResetEventSlim _paused = new(initialState: false);
    private readonly ManualResetEventSlim _resume = new(initialState: false);
    private readonly Task<AgentCheckpointCompletion> _operation;
    private int _observedOccurrences;
    private int _disposed;
    private LockFreeCheckpointEntry? _reached;

    internal AgentCheckpointOperation(AgentCheckpointSpec spec)
    {
        _spec = spec;
        _ = LockFreeCheckpointCatalog.Get(spec.Checkpoint);
        var checkpoint = LockFreeCheckpointFactory.CreateInstrumented(Observe);
        _operation = Task.Run(() => Execute(checkpoint));
    }

    internal LockFreeCheckpointEntry? Reached => _reached;

    internal bool WaitUntilPaused(TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp()
            + checked((long)(timeout.TotalSeconds * Stopwatch.Frequency));
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (_paused.Wait(TimeSpan.FromMilliseconds(10)))
            {
                return true;
            }

            if (_operation.IsCompleted)
            {
                return false;
            }
        }

        return _paused.IsSet;
    }

    internal AgentCheckpointCompletion Complete(bool cancel)
    {
        if (cancel)
        {
            _cancellation.Cancel();
        }

        _resume.Set();
        if (!_operation.Wait(CompletionTimeout))
        {
            return new AgentCheckpointCompletion(StoreStatus.StoreBusy, StoreOpenStatus.Success);
        }

        return _operation.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        _resume.Set();
        try
        {
            _operation.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The response path reports operation failures. Disposal is only
            // best-effort teardown after EOF, cancellation, or process exit.
        }

        _paused.Dispose();
        _resume.Dispose();
        _cancellation.Dispose();
    }

    private void Observe(LockFreeCheckpointEntry entry)
    {
        if (entry.Id != _spec.Checkpoint
            || Interlocked.Increment(ref _observedOccurrences) != _spec.Occurrence)
        {
            return;
        }

        _reached = entry;
        _paused.Set();
        _resume.Wait();
    }

    private AgentCheckpointCompletion Execute(InstrumentedLockFreeCheckpoint checkpoint)
    {
        try
        {
            StoreOpenStatus open = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                _spec.Options,
                checkpoint,
                out MemoryStore? store);
            if (open != StoreOpenStatus.Success || store is null)
            {
                return new AgentCheckpointCompletion(StoreStatus.UnknownFailure, open);
            }

            using (store)
            {
                return new AgentCheckpointCompletion(ExecuteOperation(store), open);
            }
        }
        catch (OperationCanceledException)
        {
            return new AgentCheckpointCompletion(
                StoreStatus.OperationCanceled,
                StoreOpenStatus.Success);
        }
        catch
        {
            return new AgentCheckpointCompletion(
                StoreStatus.UnknownFailure,
                StoreOpenStatus.MappingFailed);
        }
    }

    private StoreStatus ExecuteOperation(MemoryStore store)
    {
        var wait = new StoreWaitOptions(Timeout.InfiniteTimeSpan, _cancellation.Token);
        return _spec.Operation switch
        {
            "noop" => StoreStatus.Success,
            "publish" => store.TryPublish(
                _spec.Key,
                _spec.Value,
                _spec.Descriptor,
                wait),
            "reserve" => ReserveAndAbort(store, wait),
            "commit" => ReserveAndCommit(store, wait),
            "abort" => ReserveAndAbort(store, wait),
            "acquire" => AcquireAndRelease(store, wait),
            "release" => AcquireAndRelease(store, wait),
            "remove" => store.TryRemove(_spec.Key, wait),
            "diagnostics" => store.TryGetDiagnostics(wait, out _),
            "recoverLeases" => RecoverLease(store, wait),
            "recoverReservations" => RecoverReservation(store, wait),
            _ => StoreStatus.UnknownFailure
        };
    }

    private StoreStatus ReserveAndAbort(MemoryStore store, StoreWaitOptions wait)
    {
        StoreStatus reserve = store.TryReserve(
            _spec.Key,
            _spec.Value.Length,
            _spec.Descriptor,
            wait,
            out ValueReservation reservation);
        if (reserve != StoreStatus.Success)
        {
            return reserve;
        }

        StoreStatus abort = reservation.Abort(wait);
        return abort == StoreStatus.Success ? reserve : abort;
    }

    private StoreStatus ReserveAndCommit(MemoryStore store, StoreWaitOptions wait)
    {
        StoreStatus reserve = store.TryReserve(
            _spec.Key,
            _spec.Value.Length,
            _spec.Descriptor,
            wait,
            out ValueReservation reservation);
        if (reserve != StoreStatus.Success)
        {
            return reserve;
        }

        _spec.Value.CopyTo(reservation.GetSpan(_spec.Value.Length));
        StoreStatus advance = reservation.Advance(_spec.Value.Length, wait);
        return advance == StoreStatus.Success ? reservation.Commit(wait) : advance;
    }

    private StoreStatus AcquireAndRelease(MemoryStore store, StoreWaitOptions wait)
    {
        StoreStatus acquire = store.TryAcquire(_spec.Key, wait, out ValueLease lease);
        return acquire == StoreStatus.Success ? lease.Release(wait) : acquire;
    }

    private StoreStatus RecoverLease(MemoryStore store, StoreWaitOptions wait)
    {
        StoreStatus acquire = store.TryAcquire(_spec.Key, wait, out _);
        return acquire == StoreStatus.Success
            ? store.TryRecoverLeases(new LeaseRecoveryOptions(true), wait, out _)
            : acquire;
    }

    private StoreStatus RecoverReservation(MemoryStore store, StoreWaitOptions wait)
    {
        StoreStatus reserve = store.TryReserve(
            _spec.Key,
            _spec.Value.Length,
            _spec.Descriptor,
            wait,
            out _);
        return reserve == StoreStatus.Success
            ? store.TryRecoverReservations(new ReservationRecoveryOptions(true), wait, out _)
            : reserve;
    }
}
