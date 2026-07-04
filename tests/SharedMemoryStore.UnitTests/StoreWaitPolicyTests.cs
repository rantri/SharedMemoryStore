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
    public void NoWaitPublishReturnsBusyWhenSharedMutexIsHeld()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        using var synchronization = PlatformTestEnvironment.HoldStoreSynchronization(options.Name);

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
        Assert.Equal(StoreStatus.StoreBusy, result);
        Assert.True(elapsed.Elapsed <= TimeSpan.FromMilliseconds(250));
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
    public void TryGetDiagnosticsReturnsBusyWhenSharedMutexIsHeld()
    {
        var options = StoreTestNames.Options();
        using var store = StoreTestNames.CreateStore(options);
        using var synchronization = PlatformTestEnvironment.HoldStoreSynchronization(options.Name);

        StoreStatus result = default;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            result = store.TryGetDiagnostics(StoreWaitOptions.NoWait, out _);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(StoreStatus.StoreBusy, result);
    }
}
