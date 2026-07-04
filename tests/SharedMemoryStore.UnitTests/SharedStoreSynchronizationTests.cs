using System.Threading;
using SharedMemoryStore.Interop;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class SharedStoreSynchronizationTests
{
    [Fact]
    public void NoWaitReturnsBusyWhenSharedSynchronizationIsHeld()
    {
        var storeName = StoreTestNames.Create();
        using var held = PlatformTestEnvironment.HoldStoreSynchronization(storeName);
        using var synchronization = SharedStorePlatform.CreateSynchronization(PlatformResourceName.Create(storeName));
        StoreStatus status = default;
        using var done = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            status = synchronization.TryEnter(StoreWaitOptions.NoWait);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(StoreStatus.StoreBusy, status);
    }

    [Fact]
    public void CanceledWaitReturnsOperationCanceledBeforeAcquiringSynchronization()
    {
        var storeName = StoreTestNames.Create();
        using var synchronization = SharedStorePlatform.CreateSynchronization(PlatformResourceName.Create(storeName));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var status = synchronization.TryEnter(new StoreWaitOptions(TimeSpan.FromSeconds(10), cts.Token));

        Assert.Equal(StoreStatus.OperationCanceled, status);
    }
}
