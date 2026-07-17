using System.Threading;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class ContendedSynchronizationIntegrationTests
{
    [Fact]
    [Trait("Category", "ContendedSynchronization")]
    public void HeldColdSynchronizationBlocksOpenButNotHotPublish()
    {
        var options = IntegrationStoreFactory.Options();
        using var store = IntegrationStoreFactory.Create(options);
        using var coldSynchronization = PlatformCapabilityProbe.HoldStoreSynchronization(options.Name);

        var openOptions = new SharedMemoryStoreOptions
        {
            Name = options.Name,
            OpenMode = OpenMode.OpenExisting,
            SlotCount = options.SlotCount,
            MaxValueBytes = options.MaxValueBytes,
            MaxDescriptorBytes = options.MaxDescriptorBytes,
            MaxKeyBytes = options.MaxKeyBytes,
            LeaseRecordCount = options.LeaseRecordCount,
            ParticipantRecordCount = options.ParticipantRecordCount,
            EnableLeaseRecovery = options.EnableLeaseRecovery,
            TotalBytes = options.TotalBytes
        };
        StoreStatus publishResult = default;
        StoreOpenStatus openResult = default;
        using var publishDone = new ManualResetEventSlim();
        using var openDone = new ManualResetEventSlim();
        var publishThread = new Thread(() =>
        {
            publishResult = store.TryPublish([1], [1], default, StoreWaitOptions.NoWait);
            publishDone.Set();
        });
        var openThread = new Thread(() =>
        {
            openResult = MemoryStore.TryCreateOrOpen(openOptions, StoreWaitOptions.NoWait, out _);
            openDone.Set();
        });
        publishThread.Start();
        openThread.Start();

        Assert.True(publishDone.Wait(TimeSpan.FromSeconds(1)));
        Assert.True(openDone.Wait(TimeSpan.FromSeconds(1)));
        publishThread.Join();
        openThread.Join();
        Assert.Equal(StoreStatus.Success, publishResult);
        Assert.Equal(StoreOpenStatus.StoreBusy, openResult);
    }
}
