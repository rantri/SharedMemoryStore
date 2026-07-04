namespace SharedMemoryStore.ContractTests;

public sealed class PlatformRuntimeContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void SupportedHostsHonorCreateNewOpenExistingAndCreateOrOpenModes()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var name = $"sms-platform-open-{Guid.NewGuid():N}";
        var createNew = Options(name, OpenMode.CreateNew);

        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(createNew, out var created));
        using (created)
        {
            Assert.NotNull(created);
            Assert.Equal(StoreOpenStatus.AlreadyExists, MemoryStore.TryCreateOrOpen(createNew, out var duplicate));
            duplicate?.Dispose();

            var openExisting = Options(name, OpenMode.OpenExisting);
            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(openExisting, out var opened));
            opened?.Dispose();

            var createOrOpen = Options(name, OpenMode.CreateOrOpen);
            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(createOrOpen, out var reused));
            reused?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.ConfigurationContract)]
    public void OpenExistingMissingStoreReturnsNotFoundOnSupportedHosts()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var status = MemoryStore.TryCreateOrOpen(
            Options($"sms-platform-missing-{Guid.NewGuid():N}", OpenMode.OpenExisting),
            out var store);

        store?.Dispose();
        Assert.Equal(StoreOpenStatus.NotFound, status);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode mode)
    {
        return SharedMemoryStoreOptions.Create(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            openMode: mode,
            enableLeaseRecovery: true);
    }
}
