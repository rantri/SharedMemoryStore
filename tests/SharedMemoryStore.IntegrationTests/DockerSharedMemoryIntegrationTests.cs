using System.Diagnostics;
using SharedMemoryStore.IntegrationTests.TestSupport;

namespace SharedMemoryStore.IntegrationTests;

public sealed class DockerSharedMemoryIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void DockerSupportedProfileIsRunnableWhenDockerIsAvailable()
    {
        RunProfileWhenRequested("Supported");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DockerAdvancedProfileIsRunnableWhenDockerIsAvailable()
    {
        RunProfileWhenRequested("Advanced");
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void DockerCleanConsumerProfileIsRunnableWhenDockerIsAvailable()
    {
        RunProfileWhenRequested("CleanConsumer");
    }

    private static void RunProfileWhenRequested(string profile)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SMS_RUN_DOCKER_VALIDATION"), "1", StringComparison.Ordinal))
        {
            return;
        }

        if (!PlatformCapabilityProbe.IsDockerAvailable())
        {
            return;
        }

        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "scripts", "validate-docker-shared-memory.ps1");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Profile");
        startInfo.ArgumentList.Add(profile);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start Docker validation.");
        Assert.True(process.WaitForExit(420_000), "Docker validation timed out.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, output);
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
