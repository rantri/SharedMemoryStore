namespace SharedMemoryStore.ContractTests;

public sealed class PlatformCompatibilityContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void SupportedHostCreateOrOpenDoesNotReturnUnsupportedPlatform()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var options = SharedMemoryStoreOptions.Create(
            $"sms-platform-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2);

        var status = MemoryStore.TryCreateOrOpen(options, out var store);
        using (store)
        {
            Assert.NotEqual(StoreOpenStatus.UnsupportedPlatform, status);
            Assert.Equal(StoreOpenStatus.Success, status);
        }
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void PlatformSupportKeepsExistingPublicStatusNamesStable()
    {
        Assert.Contains(nameof(StoreOpenStatus.UnsupportedPlatform), Enum.GetNames<StoreOpenStatus>());
        Assert.Contains(nameof(StoreOpenStatus.AccessDenied), Enum.GetNames<StoreOpenStatus>());
        Assert.Contains(nameof(StoreOpenStatus.MappingFailed), Enum.GetNames<StoreOpenStatus>());
        Assert.Contains(nameof(StoreStatus.UnsupportedPlatform), Enum.GetNames<StoreStatus>());
        Assert.Contains(nameof(StoreStatus.StoreBusy), Enum.GetNames<StoreStatus>());
        Assert.Contains(nameof(StoreStatus.OperationCanceled), Enum.GetNames<StoreStatus>());
    }
}
