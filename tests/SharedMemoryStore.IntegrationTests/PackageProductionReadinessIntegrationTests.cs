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

    [Fact]
    [Trait("Category", "PackageConsumption")]
    public void ValidationScriptsUsePortablePowerShellAndRepositoryPaths()
    {
        var root = FindRepositoryRoot();
        var packageScript = File.ReadAllText(Path.Combine(root, "scripts", "validate-package-consumption.ps1"));
        var crossPlatformScript = File.ReadAllText(Path.Combine(root, "scripts", "validate-cross-platform.ps1"));
        var dockerScript = File.ReadAllText(Path.Combine(root, "scripts", "validate-docker-shared-memory.ps1"));

        Assert.Contains("Join-Path", packageScript);
        Assert.Contains("Resolve-Path", packageScript);
        Assert.Contains("pwsh", crossPlatformScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validate-package-consumption.ps1", crossPlatformScript);
        Assert.Contains("docker compose", dockerScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", packageScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", crossPlatformScript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C:\\", dockerScript, StringComparison.OrdinalIgnoreCase);
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
