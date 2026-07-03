namespace SharedMemoryStore.ContractTests;

public sealed class PackageConsumptionApiTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.PackageConsumption)]
    public void OptionsHelperAndMemoryStoreCanBeUsedWithoutAliases()
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 2);

        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out var store));
        using (store)
        {
            Assert.NotNull(store);
            Assert.Equal(StoreStatus.Success, store.TryPublish([1], [2]));
        }
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.PackageConsumption)]
    public void CoreProjectHasNoHostingDependencies()
    {
        var root = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(root, "src", "SharedMemoryStore", "SharedMemoryStore.csproj"));

        Assert.DoesNotContain("Microsoft.Extensions", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<PackageReference", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
