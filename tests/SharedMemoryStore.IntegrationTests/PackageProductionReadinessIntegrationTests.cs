namespace SharedMemoryStore.IntegrationTests;

public sealed class PackageProductionReadinessIntegrationTests
{
    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void PackageConsumptionScriptUsesProductionApiNames()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "validate-package-consumption.ps1"));

        Assert.Contains("MemoryStore", script);
        Assert.DoesNotContain("SharedMemoryStore.SharedMemoryStore", script);
        Assert.DoesNotContain("GetMemory", script);
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
