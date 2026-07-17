using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeSampleValidationTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(12)]
    [Trait("Category", "Integration")]
    public void BrokerKeySampleKeepsDispatchOutsideStoreAndValidatesKvLifecycles(int workerCount)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        int frameCount = workerCount * 2;
        using Process process = StartSample(workerCount, frameCount);
        Assert.True(process.WaitForExit(30_000), "Broker-key sample exceeded its bounded timeout.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(
            process.ExitCode == 0,
            $"Sample exit={process.ExitCode}{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");

        string result = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("RESULT ", StringComparison.Ordinal));
        IReadOnlyDictionary<string, string> fields = result["RESULT ".Length..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        Assert.Equal(workerCount.ToString(), fields["workers"]);
        Assert.Equal(frameCount.ToString(), fields["frames"]);
        Assert.Equal(frameCount.ToString(), fields["processed"]);
        Assert.Equal(fields["workerChecksum"], fields["observerChecksum"]);
        Assert.Equal(nameof(StoreStatus.RemovePending), fields["pendingRemove"]);
        Assert.Equal(nameof(StoreStatus.NotFound), fields["missing"]);
        Assert.Equal(nameof(StoreStatus.Success), fields["diagnostics"]);
        Assert.Equal("2.0", fields["layout"]);
        Assert.Equal("0", fields["recoveredLeases"]);
        Assert.Equal("0", fields["recoveredReservations"]);
    }

    private static Process StartSample(int workerCount, int frameCount)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(LocateSampleAssembly());
        start.ArgumentList.Add("--workers");
        start.ArgumentList.Add(workerCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--frames");
        start.ArgumentList.Add(frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start broker-key sample.");
    }

    private static string LocateSampleAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SharedMemoryStore.slnx")))
        {
            root = root.Parent;
        }

        string path = Path.Combine(
            root?.FullName ?? throw new DirectoryNotFoundException("Repository root not found."),
            "samples",
            "LockFreeBrokerKeys",
            "bin",
            configuration,
            "net10.0",
            "LockFreeBrokerKeys.dll");
        return File.Exists(path) ? path : throw new FileNotFoundException("Sample assembly was not built.", path);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
