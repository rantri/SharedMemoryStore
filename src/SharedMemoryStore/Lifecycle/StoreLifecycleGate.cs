namespace SharedMemoryStore.Lifecycle;

internal sealed class StoreLifecycleGate
{
    private const int Open = 0;
    private const int Disposing = 1;
    private const int Disposed = 2;

    private readonly object _sync = new();
    private int _state;
    private int _activeOperations;

    public bool IsDisposed => Volatile.Read(ref _state) == Disposed;

    public bool IsDisposingOrDisposed => Volatile.Read(ref _state) != Open;

    public bool TryEnter(out Operation operation)
    {
        lock (_sync)
        {
            if (_state != Open)
            {
                operation = default;
                return false;
            }

            _activeOperations++;
            operation = new Operation(this);
            return true;
        }
    }

    public bool TryBeginDispose()
    {
        lock (_sync)
        {
            if (_state == Disposed)
            {
                return false;
            }

            if (_state == Disposing)
            {
                while (_state != Disposed)
                {
                    Monitor.Wait(_sync);
                }

                return false;
            }

            _state = Disposing;
            while (_activeOperations != 0)
            {
                Monitor.Wait(_sync);
            }

            return true;
        }
    }

    public void CompleteDispose()
    {
        lock (_sync)
        {
            _state = Disposed;
            Monitor.PulseAll(_sync);
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            _activeOperations--;
            if (_activeOperations == 0)
            {
                Monitor.PulseAll(_sync);
            }
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
