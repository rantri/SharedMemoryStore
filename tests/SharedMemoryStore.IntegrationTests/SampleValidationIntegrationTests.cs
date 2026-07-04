using System.Diagnostics;

namespace SharedMemoryStore.IntegrationTests;

public sealed class SampleValidationIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void SolutionAndSampleDocsIncludeDockerSharedMemorySample()
    {
        var root = FindRepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "SharedMemoryStore.slnx"));
        var samples = File.ReadAllText(Path.Combine(root, "docs", "samples.md"));
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));

        Assert.Contains("samples/DockerSharedMemory/DockerSharedMemory.csproj", solution);
        Assert.Contains("samples/DockerSharedMemory/README.md", samples);
        Assert.Contains("validate-docker-shared-memory.ps1", samples);
        Assert.Contains("samples/DockerSharedMemory/README.md", readme);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DockerSharedMemoryLocalSampleModeRuns()
    {
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "samples", "DockerSharedMemory", "DockerSharedMemory.csproj");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("all");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start DockerSharedMemory sample.");
        Assert.True(process.WaitForExit(120_000), "DockerSharedMemory local sample timed out.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, output);
        Assert.Contains("reservation recovery: Success", output);
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
