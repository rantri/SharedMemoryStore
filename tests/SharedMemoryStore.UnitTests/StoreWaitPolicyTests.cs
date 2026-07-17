using System.Diagnostics;
using System.Threading;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class StoreWaitPolicyTests
{
    [Fact]
    public void StoreWaitOptionsExposeProductionPolicies()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), StoreWaitOptions.Default.Timeout);
        Assert.Equal(TimeSpan.Zero, StoreWaitOptions.NoWait.Timeout);
        Assert.Equal(System.Threading.Timeout.InfiniteTimeSpan, StoreWaitOptions.Infinite.Timeout);
        Assert.True(StoreWaitOptions.Default.IsValid);
        Assert.True(StoreWaitOptions.NoWait.IsValid);
        Assert.True(StoreWaitOptions.Infinite.IsValid);
        Assert.False(new StoreWaitOptions(TimeSpan.FromMilliseconds(-2)).IsValid);
    }

    [Fact]
    public void NoWaitPublishIgnoresHeldColdSynchronization()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        using var coldSynchronization = PlatformTestEnvironment.HoldStoreSynchronization(options.Name);

        var elapsed = Stopwatch.StartNew();
        StoreStatus result = default;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            result = store.TryPublish([1], [1], default, StoreWaitOptions.NoWait);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        thread.Join();
        Assert.Equal(StoreStatus.Success, result);
        Assert.True(elapsed.Elapsed <= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public void ConcurrentNoWaitPublishesIgnoreHeldColdSynchronization()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        using var coldSynchronization = PlatformTestEnvironment.HoldStoreSynchronization(options.Name);
        using var blockingStarted = new ManualResetEventSlim();
        using var blockingDone = new ManualResetEventSlim();
        using var noWaitDone = new ManualResetEventSlim();

        StoreStatus blockingResult = default;
        StoreStatus noWaitResult = default;
        var blockingThread = new Thread(() =>
        {
            blockingStarted.Set();
            blockingResult = store.TryPublish(
                [1],
                [1],
                default,
                new StoreWaitOptions(TimeSpan.FromSeconds(1)));
            blockingDone.Set();
        });
        blockingThread.Start();

        Assert.True(blockingStarted.Wait(TimeSpan.FromSeconds(1)));
        Thread.Sleep(50);

        var elapsed = Stopwatch.StartNew();
        var noWaitThread = new Thread(() =>
        {
            noWaitResult = store.TryPublish([2], [2], default, StoreWaitOptions.NoWait);
            noWaitDone.Set();
        });
        noWaitThread.Start();

        var completedWithinBudget = noWaitDone.Wait(TimeSpan.FromMilliseconds(250));
        Assert.True(blockingDone.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(noWaitDone.Wait(TimeSpan.FromSeconds(1)));
        blockingThread.Join();
        noWaitThread.Join();

        Assert.True(completedWithinBudget, $"No-wait operation took {elapsed.Elapsed} while the same handle was contended.");
        Assert.Equal(StoreStatus.Success, blockingResult);
        Assert.Equal(StoreStatus.Success, noWaitResult);
    }

    [Fact]
    public void CanceledAcquireReturnsOperationCanceledBeforeSynchronization()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var status = store.TryAcquire(
            [1],
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cts.Token),
            out _);

        Assert.Equal(StoreStatus.OperationCanceled, status);
        Assert.Equal(1, store.GetDiagnostics().GetFailureCount(StoreStatus.OperationCanceled));
    }

    [Fact]
    public void TryGetDiagnosticsIgnoresHeldColdSynchronization()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        using var coldSynchronization = PlatformTestEnvironment.HoldStoreSynchronization(options.Name);

        StoreStatus result = default;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            result = store.TryGetDiagnostics(StoreWaitOptions.NoWait, out _);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        thread.Join();
        Assert.Equal(StoreStatus.Success, result);
    }

    [Fact]
    public void LinuxOpenNoWaitIncludesLifecycleSynchronization()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var options = StoreTestNames.Options();
        var resourceName = PlatformTestEnvironment.ResourceNameFor(options.Name);
        var lockStatus = SharedMemoryStore.Interop.LinuxFileLock.TryAcquire(
            resourceName.LinuxLifecycleLockPath,
            StoreWaitOptions.Infinite,
            out var lifecycleLock);
        Assert.Equal(StoreStatus.Success, lockStatus);
        using var heldLock = Assert.IsType<SharedMemoryStore.Interop.LinuxFileLock>(lifecycleLock);
        using var done = new ManualResetEventSlim();

        StoreOpenStatus result = default;
        var elapsed = Stopwatch.StartNew();
        var thread = new Thread(() =>
        {
            result = MemoryStore.TryCreateOrOpen(options, StoreWaitOptions.NoWait, out _);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromMilliseconds(250)));
        thread.Join();
        Assert.Equal(StoreOpenStatus.StoreBusy, result);
        Assert.True(elapsed.Elapsed <= TimeSpan.FromMilliseconds(250));
    }
}
