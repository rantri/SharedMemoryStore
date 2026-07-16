namespace SharedMemoryStore.UnitTests;

public sealed class SyncProbeTrialWatchdogTests
{
    [Fact]
    public async Task CompletionBeforeDeadlineCancelsTimeoutAction()
    {
        var invocations = 0;
        var watchdog = new ProbeTrialWatchdog(
            TimeSpan.FromSeconds(1),
            () => Interlocked.Increment(ref invocations));

        await watchdog.CompleteAsync();
        await Task.Delay(100);

        Assert.Equal(0, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task AbsoluteDeadlineRejectsLateCompletionWhenTimerDispatchIsDelayed()
    {
        var invocations = 0;
        var watchdog = new ProbeTrialWatchdog(
            TimeSpan.FromMilliseconds(25),
            () => Interlocked.Increment(ref invocations),
            timerDueTime: TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        TimeoutException timeout = await Assert.ThrowsAsync<TimeoutException>(
            () => watchdog.CompleteAsync().AsTask());

        Assert.Contains("timeout action returned", timeout.Message, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task PostLatchDeadlineCheckRejectsCompletionThatCrossesBoundary()
    {
        var invocations = 0;
        var watchdog = new ProbeTrialWatchdog(
            TimeSpan.FromMilliseconds(250),
            () => Interlocked.Increment(ref invocations),
            timerDueTime: TimeSpan.FromSeconds(5),
            afterCompletionLatch: () => Thread.Sleep(400));

        await Assert.ThrowsAsync<TimeoutException>(() => watchdog.CompleteAsync().AsTask());

        Assert.Equal(1, Volatile.Read(ref invocations));
    }

    [Fact]
    public async Task TimerCanTimeOutCompletionWhileCompletingLatchIsBlocked()
    {
        var completionLatchEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var timeoutInvoked = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseCompletionLatch = new ManualResetEventSlim();
        var watchdog = new ProbeTrialWatchdog(
            TimeSpan.FromMilliseconds(100),
            () => timeoutInvoked.TrySetResult(),
            afterCompletionLatch: () =>
            {
                completionLatchEntered.TrySetResult();
                releaseCompletionLatch.Wait();
            });

        Task completion = Task.Factory.StartNew(
            () => watchdog.CompleteAsync().AsTask(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        try
        {
            await completionLatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await timeoutInvoked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(completion.IsCompleted);
        }
        finally
        {
            releaseCompletionLatch.Set();
        }

        await Assert.ThrowsAsync<TimeoutException>(() => completion);
    }

    [Fact]
    public async Task TimerDispatchInvokesTimeoutAction()
    {
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocations = 0;
        await using var watchdog = new ProbeTrialWatchdog(
            TimeSpan.FromMilliseconds(25),
            () =>
            {
                Interlocked.Increment(ref invocations);
                invoked.TrySetResult();
            });

        await invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, Volatile.Read(ref invocations));
    }
}
