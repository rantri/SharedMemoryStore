using System.Diagnostics;
using SharedMemoryStore.Layout;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LeaseRecoveryOwnershipTests
{
    [Fact]
    public void CurrentProcessRecoveryRecoversOnlyWhenRequested()
    {
        if (LeaseRecoveryUnsupported())
        {
            return;
        }

        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(enableRecovery: true));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var skipped));
        Assert.Equal(0, skipped.RecoveredLeaseCount);
        Assert.Equal(1, skipped.ActiveLeaseCount);
        Assert.True(lease.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var recovered));
        Assert.Equal(1, recovered.RecoveredLeaseCount);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public void CurrentProcessRecoverySkipsOtherLiveOwner()
    {
        if (LeaseRecoveryUnsupported())
        {
            return;
        }

        using var owner = StartLiveProcess();
        try
        {
            using var store = StoreTestNames.CreateStore(StoreTestNames.Options(enableRecovery: true));
            Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
            Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

            ref var record = ref store.GetLeaseRecordForTesting(lease.LeaseRecordIdForTesting);
            record.OwnerProcessId = owner.Id;

            Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report));

            Assert.Equal(0, report.RecoveredLeaseCount);
            Assert.Equal(1, report.ActiveLeaseCount);
            Assert.True(lease.IsValid);
            Assert.Equal(StoreStatus.Success, lease.Release());
        }
        finally
        {
            Stop(owner);
        }
    }

    [Fact]
    public void StaleOwnerIsRecoveredWhenLivenessCanBeEvaluated()
    {
        if (LeaseRecoveryUnsupported())
        {
            return;
        }

        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(enableRecovery: true));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        ref var record = ref store.GetLeaseRecordForTesting(lease.LeaseRecordIdForTesting);
        record.OwnerProcessId = int.MaxValue;

        Assert.Equal(StoreStatus.Success, store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var report));

        Assert.Equal(1, report.RecoveredLeaseCount);
        Assert.False(lease.IsValid);
    }

    [Fact]
    public void DisabledAndUnsafeRecoveryDoNotMutateActiveLease()
    {
        using var disabled = StoreTestNames.CreateStore(StoreTestNames.Options(enableRecovery: false));
        Assert.Equal(StoreStatus.Success, disabled.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, disabled.TryAcquire([1], out var disabledLease));

        Assert.Equal(StoreStatus.UnsupportedPlatform, disabled.TryRecoverLeases(new LeaseRecoveryOptions(true), out var disabledReport));
        Assert.Equal(0, disabledReport.RecoveredLeaseCount);
        Assert.True(disabledLease.IsValid);
        disabledLease.Dispose();

        if (LeaseRecoveryUnsupported())
        {
            return;
        }

        using var unsafeStore = StoreTestNames.CreateStore(StoreTestNames.Options(enableRecovery: true));
        Assert.Equal(StoreStatus.Success, unsafeStore.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, unsafeStore.TryAcquire([1], out var unsafeLease));
        ref var record = ref unsafeStore.GetLeaseRecordForTesting(unsafeLease.LeaseRecordIdForTesting);
        record.SlotIndex = int.MaxValue;

        Assert.Equal(StoreStatus.Success, unsafeStore.TryRecoverLeases(new LeaseRecoveryOptions(true), out var unsafeReport));
        Assert.Equal(0, unsafeReport.RecoveredLeaseCount);
        Assert.Equal(1, unsafeReport.FailedRecoveryCount);
    }

    private static bool LeaseRecoveryUnsupported()
    {
        return !OperatingSystem.IsWindows();
    }

    private static Process StartLiveProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell", "-NoProfile -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("sh", "-c \"sleep 30\"");

        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start test owner process.");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
