using System.Diagnostics;

namespace SharedMemoryStore.IntegrationTests;

public sealed class PackageConsumptionIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void CleanConsumerProjectCanUseLocalPackage()
    {
        var root = FindRepositoryRoot();
        var script = Path.Combine(root, "scripts", "validate-package-consumption.ps1");
        var shell = OperatingSystem.IsWindows() ? "powershell" : "pwsh";
        var arguments = OperatingSystem.IsWindows()
            ? "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\""
            : "-NoProfile -File \"" + script + "\"";
        var startInfo = new ProcessStartInfo(shell, arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start package validation.");
        Assert.True(process.WaitForExit(120_000), "Package validation timed out.");
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
