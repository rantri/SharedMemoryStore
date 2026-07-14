using System.Diagnostics;
using System.Reflection;
using SharedMemoryStore.Lifecycle;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeLifecycleGateTests
{
    [Fact]
    public async Task OperationEntryDoesNotWaitForTheFormerMonitor()
    {
        var gate = new StoreLifecycleGate();
        var formerMonitor = typeof(StoreLifecycleGate)
            .GetField("_sync", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(gate);

        if (formerMonitor is null)
        {
            Assert.True(gate.TryEnter(out var operation));
            operation.Dispose();
            return;
        }

        using var releaseMonitor = new ManualResetEventSlim();
        var monitorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitorReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = new Thread(() =>
        {
            Monitor.Enter(formerMonitor);
            monitorEntered.SetResult();
            releaseMonitor.Wait();
            Monitor.Exit(formerMonitor);
            monitorReleased.SetResult();
        });
        holder.Start();
        await monitorEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        StoreLifecycleGate.Operation enteredOperation = default;
        var entry = Task.Run(() => gate.TryEnter(out enteredOperation));
        var completedWithoutMonitor = await CompletesWithinAsync(entry, TimeSpan.FromMilliseconds(250));
        releaseMonitor.Set();

        await monitorReleased.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var entered = await entry.WaitAsync(TimeSpan.FromSeconds(5));
        if (entered)
        {
            enteredOperation.Dispose();
        }

        Assert.True(completedWithoutMonitor, "Operation entry still depends on a process-local Monitor instead of the atomic lifetime word.");
    }

    [Fact]
    public async Task DisposePausedBehindEnteredOperationRejectsNewEntriesThenCompletes()
    {
        var gate = new StoreLifecycleGate();
        Assert.True(gate.TryEnter(out var enteredOperation));

        var disposer = Task.Run(gate.TryBeginDispose);
        Assert.True(
            SpinWait.SpinUntil(() => gate.IsDisposingOrDisposed, TimeSpan.FromSeconds(5)),
            "Disposal did not publish its entry-closed state.");

        Assert.False(gate.TryEnter(out _));
        Assert.False(await CompletesWithinAsync(disposer, TimeSpan.FromMilliseconds(100)));

        enteredOperation.Dispose();
        Assert.True(await disposer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.IsDisposed);

        gate.CompleteDispose();
        Assert.True(gate.IsDisposed);
        Assert.False(gate.TryEnter(out _));
    }

    [Fact]
    public async Task SecondDisposePausedDuringFirstDisposeObservesCompletionAndDoesNotOwnCleanup()
    {
        var gate = new StoreLifecycleGate();
        Assert.True(gate.TryBeginDispose());

        var secondDisposer = Task.Run(gate.TryBeginDispose);
        Assert.False(await CompletesWithinAsync(secondDisposer, TimeSpan.FromMilliseconds(100)));
        Assert.False(gate.TryEnter(out _));

        gate.CompleteDispose();
        Assert.False(await secondDisposer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(gate.IsDisposed);
    }

    [Fact]
    public async Task OperationExitUnblocksOnlyAfterEveryEnteredOperationLeaves()
    {
        var gate = new StoreLifecycleGate();
        Assert.True(gate.TryEnter(out var first));
        Assert.True(gate.TryEnter(out var second));

        var disposer = Task.Run(gate.TryBeginDispose);
        Assert.True(SpinWait.SpinUntil(() => gate.IsDisposingOrDisposed, TimeSpan.FromSeconds(5)));

        first.Dispose();
        Assert.False(await CompletesWithinAsync(disposer, TimeSpan.FromMilliseconds(100)));

        second.Dispose();
        Assert.True(await disposer.WaitAsync(TimeSpan.FromSeconds(5)));
        gate.CompleteDispose();
    }

    [Fact]
    public void WarmOperationEntryAndExitAllocateNoManagedMemory()
    {
        var gate = new StoreLifecycleGate();
        for (var index = 0; index < 1_000; index++)
        {
            Assert.True(gate.TryEnter(out var warmup));
            warmup.Dispose();
        }

        const int Iterations = 100_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < Iterations; index++)
        {
            if (!gate.TryEnter(out var operation))
            {
                throw new UnreachableException("An undisposed gate rejected an operation.");
            }

            operation.Dispose();
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
    {
        return await Task.WhenAny(task, Task.Delay(timeout)) == task;
    }
}
