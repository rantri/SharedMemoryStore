using System.Threading;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformSynchronizationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void NoWaitAndCanceledOperationsReturnDocumentedSynchronizationOutcomes()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        var options = IntegrationStoreFactory.Options();
        using var store = IntegrationStoreFactory.Create(options);
        using var held = PlatformCapabilityProbe.HoldStoreSynchronization(options.Name);

        StoreStatus busy = default;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            busy = store.TryPublish([1], [1], default, StoreWaitOptions.NoWait);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(StoreStatus.StoreBusy, busy);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryAcquire([1], new StoreWaitOptions(TimeSpan.FromSeconds(1), cts.Token), out _));
    }
}
