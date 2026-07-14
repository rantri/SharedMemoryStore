using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests.TestSupport;

/// <summary>
/// Deterministically pauses an instrumented lock-free protocol invocation at a
/// selected canonical checkpoint. The scheduler never participates in ordinary
/// production construction.
/// </summary>
internal sealed class ControlledLockFreeScheduler : IDisposable
{
    private readonly object _sync = new();
    private readonly List<Observation> _observations = [];
    private readonly ManualResetEventSlim _paused = new(initialState: false);
    private readonly ManualResetEventSlim _resume = new(initialState: true);
    private LockFreeCheckpointId? _target;
    private int _targetOccurrence;
    private int _observedTargetOccurrences;
    private long _sequence;
    private bool _disposed;

    internal InstrumentedLockFreeCheckpoint CreateInstrumentedCheckpoint()
    {
        ThrowIfDisposed();
        return LockFreeCheckpointFactory.CreateInstrumented(Observe);
    }

    internal void PauseAt(LockFreeCheckpointId checkpoint, int occurrence = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        _ = LockFreeCheckpointCatalog.Get(checkpoint);

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_target.HasValue)
            {
                throw new InvalidOperationException("A checkpoint pause is already armed.");
            }

            _target = checkpoint;
            _targetOccurrence = occurrence;
            _observedTargetOccurrences = 0;
            _paused.Reset();
            _resume.Reset();
        }
    }

    internal bool WaitUntilPaused(TimeSpan timeout)
    {
        ThrowIfDisposed();
        return _paused.Wait(timeout);
    }

    internal bool WaitUntilPaused(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        return _paused.Wait(timeout, cancellationToken);
    }

    internal void Continue()
    {
        ThrowIfDisposed();
        _resume.Set();
    }

    internal IReadOnlyList<Observation> Snapshot()
    {
        lock (_sync)
        {
            return _observations.ToArray();
        }
    }

    internal void ClearObservations()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _observations.Clear();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _target = null;
            _resume.Set();
            _paused.Set();
        }

        _paused.Dispose();
        _resume.Dispose();
    }

    private void Observe(LockFreeCheckpointEntry entry)
    {
        bool pause;
        long sequence = Interlocked.Increment(ref _sequence);
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _observations.Add(new Observation(sequence, Environment.CurrentManagedThreadId, entry));
            pause = _target == entry.Id
                && ++_observedTargetOccurrences == _targetOccurrence;
        }

        if (!pause)
        {
            return;
        }

        _paused.Set();
        _resume.Wait();

        lock (_sync)
        {
            _target = null;
            _targetOccurrence = 0;
            _observedTargetOccurrences = 0;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal readonly record struct Observation(
        long Sequence,
        int ManagedThreadId,
        LockFreeCheckpointEntry Entry);
}
