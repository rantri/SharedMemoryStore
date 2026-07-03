using System.Reflection;

namespace SharedMemoryStore.ContractTests;

public sealed class ReliabilityApiContractTests
{
    [Fact]
    public void LeaseRecoveryReportExposesOwnerDecisionCategories()
    {
        var properties = typeof(LeaseRecoveryReport)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToArray();

        Assert.Contains(nameof(LeaseRecoveryReport.ScannedRecordCount), properties);
        Assert.Contains(nameof(LeaseRecoveryReport.RecoveredLeaseCount), properties);
        Assert.Contains(nameof(LeaseRecoveryReport.ActiveLeaseCount), properties);
        Assert.Contains(nameof(LeaseRecoveryReport.UnsupportedLeaseCount), properties);
        Assert.Contains(nameof(LeaseRecoveryReport.FailedRecoveryCount), properties);

        var legacyShape = new LeaseRecoveryReport(3, 1, 2);
        Assert.Equal(3, legacyShape.ScannedRecordCount);
        Assert.Equal(1, legacyShape.RecoveredLeaseCount);
        Assert.Equal(0, legacyShape.ActiveLeaseCount);
        Assert.Equal(2, legacyShape.UnsupportedLeaseCount);
        Assert.Equal(0, legacyShape.FailedRecoveryCount);
    }

    [Fact]
    public void DiagnosticsExposeLeaseRecoveryCounters()
    {
        using var store = ContractStoreFactory.Create(ContractStoreFactory.Options(enableRecovery: true));
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [1]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));

        var status = store.TryRecoverLeases(new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false), out var report);

        Assert.Equal(OperatingSystem.IsWindows() ? StoreStatus.Success : StoreStatus.UnsupportedPlatform, status);
        var diagnostics = store.GetDiagnostics();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(1, report.ActiveLeaseCount);
            Assert.Equal(1, diagnostics.ActiveLeaseRecoveryCount);
        }

        lease.Dispose();
    }
}
