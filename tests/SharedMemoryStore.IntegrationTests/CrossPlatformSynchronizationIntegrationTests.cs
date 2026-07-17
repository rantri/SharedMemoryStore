using System.Threading;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class CrossPlatformSynchronizationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void HeldColdSynchronizationDoesNotBlockHotOperationsAndCancellationWins()
    {
        if (!PlatformCapabilityProbe.IsSupportedHost)
        {
            return;
        }

        var options = IntegrationStoreFactory.Options();
        using var store = IntegrationStoreFactory.Create(options);
        using var held = PlatformCapabilityProbe.HoldStoreSynchronization(options.Name);

        StoreStatus publish = default;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            publish = store.TryPublish([1], [1], default, StoreWaitOptions.NoWait);
            done.Set();
        });
        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(1)));
        thread.Join();
        Assert.Equal(StoreStatus.Success, publish);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(
            StoreStatus.OperationCanceled,
            store.TryAcquire([1], new StoreWaitOptions(TimeSpan.FromSeconds(1), cts.Token), out _));
    }
}
