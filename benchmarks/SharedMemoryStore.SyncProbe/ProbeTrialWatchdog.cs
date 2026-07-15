using System.Diagnostics;

internal sealed class ProbeTrialWatchdog : IAsyncDisposable
{
    private const int Armed = 0;
    private const int Completing = 1;
    private const int Completed = 2;
    private const int TimedOut = 3;

    private readonly long _deadlineTimestamp;
    private readonly Action? _afterCompletionLatch;
    private readonly Action _timeoutAction;
    private readonly Timer _timer;
    private int _state;

    internal ProbeTrialWatchdog(
        TimeSpan deadline,
        Action timeoutAction,
        TimeSpan? timerDueTime = null,
        Action? afterCompletionLatch = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(deadline, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeoutAction);
        TimeSpan dueTime = timerDueTime ?? deadline;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(dueTime, TimeSpan.Zero);

        long deadlineStopwatchTicks = checked((long)Math.Ceiling(
            deadline.TotalSeconds * Stopwatch.Frequency));
        _deadlineTimestamp = checked(Stopwatch.GetTimestamp() + deadlineStopwatchTicks);
        _afterCompletionLatch = afterCompletionLatch;
        _timeoutAction = timeoutAction;
        _timer = new Timer(
            static state => ((ProbeTrialWatchdog)state!).OnTimer(),
            this,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    internal async ValueTask CompleteAsync()
    {
        int previous = Interlocked.CompareExchange(ref _state, Completing, Armed);
        if (previous == Armed)
        {
            _afterCompletionLatch?.Invoke();
            if (Stopwatch.GetTimestamp() < _deadlineTimestamp)
            {
                if (Interlocked.CompareExchange(ref _state, Completed, Completing) == Completing)
                {
                    await _timer.DisposeAsync().ConfigureAwait(false);
                    return;
                }
            }
            else
            {
                _ = Interlocked.CompareExchange(ref _state, TimedOut, Completing);
            }
        }
        else if (previous == Completed)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await FailTimedOutCompletionAsync().ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _timer.DisposeAsync();

    private async ValueTask FailTimedOutCompletionAsync()
    {
        try
        {
            // Invoke on the completing thread too. This closes the race where the
            // timer committed TimedOut but its ThreadPool callback was preempted
            // before reaching the process-termination action.
            _timeoutAction();
        }
        finally
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        throw new TimeoutException("The probe trial timeout action returned without terminating the process.");
    }

    private void OnTimer()
    {
        long now = Stopwatch.GetTimestamp();
        if (now < _deadlineTimestamp)
        {
            int state = Volatile.Read(ref _state);
            if (state is Completed or TimedOut)
            {
                return;
            }

            long remainingStopwatchTicks = _deadlineTimestamp - now;
            double remainingSeconds = (double)remainingStopwatchTicks / Stopwatch.Frequency;
            try
            {
                _timer.Change(TimeSpan.FromSeconds(remainingSeconds), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Normal completion won the race and disposed the timer.
            }
            return;
        }

        if (TryBeginTimeout())
        {
            _timeoutAction();
        }
    }

    private bool TryBeginTimeout()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state is Completed or TimedOut)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _state, TimedOut, state) == state)
            {
                return true;
            }
        }
    }
}
