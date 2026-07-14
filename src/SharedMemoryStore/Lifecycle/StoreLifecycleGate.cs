using System.Diagnostics;

namespace SharedMemoryStore.Lifecycle;

internal sealed class StoreLifecycleGate
{
    private const long EntryClosed = long.MinValue;
    private const long ActiveCountMask = long.MaxValue;

    private readonly ManualResetEventSlim _disposeCompleted = new(initialState: false);
    private long _lifetimeWord;
    private int _isDisposed;

    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    public bool IsDisposingOrDisposed => (Volatile.Read(ref _lifetimeWord) & EntryClosed) != 0;

    public bool TryEnter(out Operation operation)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lifetimeWord);
            if ((observed & EntryClosed) != 0)
            {
                operation = default;
                return false;
            }

            if ((observed & ActiveCountMask) == ActiveCountMask)
            {
                throw new InvalidOperationException("The active operation count is exhausted.");
            }

            if (Interlocked.CompareExchange(ref _lifetimeWord, observed + 1, observed) == observed)
            {
                operation = new Operation(this);
                return true;
            }
        }
    }

    /// <summary>
    /// Attempts to enter a public operation without allowing process-local CAS
    /// contention to sit outside the caller's operation-wide wait/work bound.
    /// A zero timeout receives one CAS attempt; finite and infinite policies
    /// retain the same cancellation semantics as the shared protocol budget.
    /// </summary>
    public StoreStatus TryEnter(
        StoreWaitOptions waitOptions,
        long started,
        out Operation operation)
    {
        var spinner = new SpinWait();
        while (true)
        {
            var observed = Volatile.Read(ref _lifetimeWord);
            if ((observed & EntryClosed) != 0)
            {
                operation = default;
                return StoreStatus.StoreDisposed;
            }

            if (!waitOptions.IsValid)
            {
                operation = default;
                return StoreStatus.UnknownFailure;
            }

            if (waitOptions.CancellationToken.IsCancellationRequested)
            {
                operation = default;
                return StoreStatus.OperationCanceled;
            }

            if (!waitOptions.IsInfinite
                && waitOptions.Timeout > TimeSpan.Zero
                && Stopwatch.GetElapsedTime(started) >= waitOptions.Timeout)
            {
                operation = default;
                return StoreStatus.StoreBusy;
            }

            if ((observed & ActiveCountMask) == ActiveCountMask)
            {
                throw new InvalidOperationException("The active operation count is exhausted.");
            }

            if (Interlocked.CompareExchange(ref _lifetimeWord, observed + 1, observed) == observed)
            {
                operation = new Operation(this);
                return StoreStatus.Success;
            }

            if (waitOptions.Timeout == TimeSpan.Zero)
            {
                operation = default;
                return (Volatile.Read(ref _lifetimeWord) & EntryClosed) != 0
                    ? StoreStatus.StoreDisposed
                    : StoreStatus.StoreBusy;
            }

            spinner.SpinOnce();
        }
    }

    public bool TryBeginDispose()
    {
        while (true)
        {
            var observed = Volatile.Read(ref _lifetimeWord);
            if ((observed & EntryClosed) != 0)
            {
                if (Volatile.Read(ref _isDisposed) == 0)
                {
                    _disposeCompleted.Wait();
                }

                return false;
            }

            if (Interlocked.CompareExchange(ref _lifetimeWord, observed | EntryClosed, observed) != observed)
            {
                continue;
            }

            var spinner = new SpinWait();
            while ((Volatile.Read(ref _lifetimeWord) & ActiveCountMask) != 0)
            {
                spinner.SpinOnce();
            }

            return true;
        }
    }

    public void CompleteDispose()
    {
        Volatile.Write(ref _isDisposed, 1);
        _disposeCompleted.Set();
    }

    private void Exit()
    {
        var remaining = Interlocked.Decrement(ref _lifetimeWord);
        if ((remaining & ActiveCountMask) == ActiveCountMask)
        {
            throw new InvalidOperationException("An operation exited without a matching entry.");
        }
    }

    public readonly struct Operation : IDisposable
    {
        private readonly StoreLifecycleGate? _gate;

        internal Operation(StoreLifecycleGate gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            _gate?.Exit();
        }
    }
}
