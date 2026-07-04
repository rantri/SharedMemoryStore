namespace SharedMemoryStore.IntegrationTests;

public sealed class DockerSampleValidationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void DockerSampleDefinesExpectedValidationModesAndOutput()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "Program.cs"));
        var readme = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "README.md"));

        Assert.Contains("\"writer\"", program, StringComparison.Ordinal);
        Assert.Contains("\"verifier\"", program, StringComparison.Ordinal);
        Assert.Contains("\"isolated-profile\"", program, StringComparison.Ordinal);
        Assert.Contains("\"advanced\"", program, StringComparison.Ordinal);
        Assert.Contains("\"recovery-verifier\"", program, StringComparison.Ordinal);
        Assert.Contains("\"contention-verifier\"", program, StringComparison.Ordinal);
        Assert.Contains("\"disposal-race\"", program, StringComparison.Ordinal);
        Assert.Contains("docker shared memory validation passed", program, StringComparison.Ordinal);
        Assert.Contains("docker recovery validation passed", program, StringComparison.Ordinal);
        Assert.Contains("docker contention validation passed", program, StringComparison.Ordinal);
        Assert.Contains("docker disposal race validation passed", program, StringComparison.Ordinal);
        Assert.Contains("isolated open:", program, StringComparison.Ordinal);
        Assert.Contains("docker shared memory validation passed", readme, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DockerSampleCoversReservationAndSegmentedPublishModes()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "samples", "DockerSharedMemory", "Program.cs"));

        Assert.Contains("\"reservation\"", program, StringComparison.Ordinal);
        Assert.Contains("TryReserve", program, StringComparison.Ordinal);
        Assert.Contains("\"segmented-publish\"", program, StringComparison.Ordinal);
        Assert.Contains("TryPublishSegments", program, StringComparison.Ordinal);
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
