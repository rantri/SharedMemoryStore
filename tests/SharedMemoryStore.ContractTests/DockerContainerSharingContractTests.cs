namespace SharedMemoryStore.ContractTests;

public sealed class DockerContainerSharingContractTests
{
    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.PackageConsumption)]
    public void DockerSampleDocumentsSupportedAndIsolatedProfiles()
    {
        var root = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "README.md"));
        var supported = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "docker-compose.yml"));
        var isolated = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "docker-compose.isolated.yml"));

        Assert.Contains("same-host Docker", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("isolated", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ipc: shareable", supported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ipc: \"service:writer\"", supported, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pid: \"service:writer\"", supported, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ipc: \"service:writer\"", isolated, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", ProductionReadinessTestCategories.PackageConsumption)]
    public void DockerValidationScriptExercisesSupportedIsolatedAndAdvancedWorkflows()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(root, "scripts", "validate-docker-shared-memory.ps1"));
        var program = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "Program.cs"));

        Assert.Contains("Supported", script);
        Assert.Contains("Isolated", script);
        Assert.Contains("Advanced", script);
        Assert.Contains("Recovery", script);
        Assert.Contains("Contention", script);
        Assert.Contains("DisposalRace", script);
        Assert.Contains("CleanConsumer", script);
        Assert.Contains("docker compose", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"reservation\"", program, StringComparison.Ordinal);
        Assert.Contains("\"segmented-publish\"", program, StringComparison.Ordinal);
        Assert.Contains("\"recovery-verifier\"", program, StringComparison.Ordinal);
        Assert.Contains("\"contention-verifier\"", program, StringComparison.Ordinal);
        Assert.Contains("\"disposal-race\"", program, StringComparison.Ordinal);
        Assert.Contains("SMS_CHURN_CYCLES", program, StringComparison.Ordinal);
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
