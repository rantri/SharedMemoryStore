using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class MultiOwnerLeaseRecoveryIntegrationTests
{
    private const int RecoveryCycleCount = 10_000;

    [Fact]
    [Trait("Category", "Integration")]
    public void CurrentProcessRecoverySkipsOtherLiveOwnerAcrossTenThousandCycles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = IntegrationStoreFactory.Options(slotCount: 4, maxValueBytes: 1, maxKeyBytes: 4, leaseRecordCount: 4);
        using var store = IntegrationStoreFactory.Create(options);
        using var owner = LeaseOwnerProcessHarness.StartLiveOwner(options, keyValue: 1);

        Assert.True(owner.ProcessId > 0);
        Assert.True(owner.CheckLeaseValid());

        for (var i = 0; i < RecoveryCycleCount; i++)
        {
            var key = BitConverter.GetBytes(i + 10);
            Assert.Equal(StoreStatus.Success, store.TryPublish(key, [(byte)i]));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var currentLease));
            Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key));

            Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report));

            Assert.Equal(1, report.RecoveredLeaseCount);
            Assert.True(report.ActiveLeaseCount >= 1);
            Assert.Equal(0, report.FailedRecoveryCount);
            Assert.False(currentLease.IsValid);

            if ((i + 1) % 1_000 == 0)
            {
                Assert.True(owner.CheckLeaseValid());
            }
        }

        Assert.True(owner.CheckLeaseValid());
        Assert.Equal(StoreStatus.Success, owner.Release());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void StaleOwnerRecoveryRecoversTenThousandActualChildOwnedLeases()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var options = IntegrationStoreFactory.Options(
            slotCount: RecoveryCycleCount,
            maxValueBytes: 1,
            maxKeyBytes: 4,
            leaseRecordCount: RecoveryCycleCount);
        using var store = IntegrationStoreFactory.Create(options);

        var staleProcessId = LeaseOwnerProcessHarness.CreateStaleLeases(
            options,
            firstKeyValue: 100_000,
            leaseCount: RecoveryCycleCount);

        Assert.True(staleProcessId > 0);
        Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var report));
        Assert.Equal(RecoveryCycleCount, report.RecoveredLeaseCount);
        Assert.Equal(0, report.ActiveLeaseCount);
        Assert.Equal(0, report.FailedRecoveryCount);
        Assert.Equal(0, report.UnsupportedLeaseCount);
    }
}
