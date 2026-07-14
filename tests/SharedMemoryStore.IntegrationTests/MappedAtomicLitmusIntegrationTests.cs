using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class MappedAtomicLitmusIntegrationTests
{
    private const int AgentTimeoutMilliseconds = 45_000;

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "AtomicLitmus")]
    public void VolatilePublicationMakesOrdinaryMappedWritesVisibleAcrossProcesses()
    {
        if (!IsSupportedAtomicHost())
        {
            return;
        }

        const int iterations = 10_000;
        using var mapping = AtomicTestMapping.Create();

        var results = RunAgents(
            ["atomic-publication-consumer", mapping.Path, iterations.ToString(CultureInfo.InvariantCulture)],
            ["atomic-publication-producer", mapping.Path, iterations.ToString(CultureInfo.InvariantCulture)]);

        AssertAgentsSucceeded(results);
        Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
        Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 16));
        Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 24));
        Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 0));
        Assert.Equal(~(long)iterations, mapping.ReadInt64(byteOffset: 8));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "AtomicLitmus")]
    public void CompareExchangeIsAtomicAcrossIndependentMappedViews()
    {
        if (!IsSupportedAtomicHost())
        {
            return;
        }

        const int iterationsPerProcess = 50_000;
        using var mapping = AtomicTestMapping.Create();
        var iterationArgument = iterationsPerProcess.ToString(CultureInfo.InvariantCulture);

        var results = RunAgents(
            ["atomic-cas-worker", mapping.Path, iterationArgument],
            ["atomic-cas-worker", mapping.Path, iterationArgument]);

        AssertAgentsSucceeded(results);
        Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
        Assert.Equal(2L * iterationsPerProcess, mapping.ReadInt64(byteOffset: 32));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "AtomicLitmus")]
    public void SequentiallyConsistentRmwForbidsTwoWordDekkerOldOldOutcome()
    {
        if (!IsSupportedAtomicHost())
        {
            return;
        }

        const int iterations = 10_000;
        using var mapping = AtomicTestMapping.Create();
        var iterationArgument = iterations.ToString(CultureInfo.InvariantCulture);

        var results = RunAgents(
            ["atomic-dekker-worker", mapping.Path, iterationArgument, "0"],
            ["atomic-dekker-worker", mapping.Path, iterationArgument, "1"],
            ["atomic-dekker-coordinator", mapping.Path, iterationArgument]);

        AssertAgentsSucceeded(results);
        Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains("forbidden=0", results[2].StandardOutput, StringComparison.Ordinal);
        Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 24));
        Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 32));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "AtomicLitmus")]
    public void LockFreeProfileRejectsNonX64BeforeCreatingMappedData()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            return;
        }

        var name = $"sms-non-x64-rejection-{Guid.NewGuid():N}";
        var options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 4,
            leaseRecordCount: 2,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew);

        var status = Store.TryCreateOrOpen(options, out var store);
        store?.Dispose();

        Assert.Equal(StoreOpenStatus.UnsupportedPlatform, status);
        Assert.Null(store);
        if (OperatingSystem.IsLinux())
        {
            var resources = PlatformResourceName.Create(name);
            Assert.False(File.Exists(resources.LinuxRegionPath));
            Assert.False(File.Exists(resources.LinuxOwnersPath));
        }
    }

    private static bool IsSupportedAtomicHost()
    {
        return (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    }

    private static AgentResult[] RunAgents(params string[][] commands)
    {
        var processes = commands.Select(StartAgent).ToArray();
        try
        {
            var deadline = Stopwatch.GetTimestamp()
                + (long)(AgentTimeoutMilliseconds / 1000d * Stopwatch.Frequency);
            foreach (var process in processes)
            {
                var remaining = deadline - Stopwatch.GetTimestamp();
                var remainingMilliseconds = remaining <= 0
                    ? 0
                    : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
                if (!process.WaitForExit(remainingMilliseconds))
                {
                    KillAll(processes);
                    throw new TimeoutException(
                        "Mapped atomic agents did not complete within "
                        + AgentTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)
                        + " ms.");
                }
            }

            return processes.Select(static process => new AgentResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd())).ToArray();
        }
        finally
        {
            KillAll(processes);
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static Process StartAgent(string[] command)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(LocateAgentAssembly());
        foreach (var argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the lock-free atomic test agent.");
    }

    private static string LocateAgentAssembly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        var path = System.IO.Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Lock-free atomic test agent was not built.", path);
        }

        return path;
    }

    private static void AssertAgentsSucceeded(AgentResult[] results)
    {
        Assert.All(results, static result => Assert.True(
            result.ExitCode == 0,
            "Agent exit code: "
            + result.ExitCode.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "stdout: "
            + result.StandardOutput
            + Environment.NewLine
            + "stderr: "
            + result.StandardError));
    }

    private static void KillAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5_000);
                }
            }
            catch
            {
                // Cleanup is best effort; the unique temporary mapping is deleted on test disposal.
            }
        }
    }

    private readonly record struct AgentResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class AtomicTestMapping : IDisposable
    {
        private AtomicTestMapping(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static AtomicTestMapping Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sms-atomic-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".map");
            using (var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.ReadWrite | FileShare.Delete))
            {
                stream.SetLength(4096);
            }

            return new AtomicTestMapping(path);
        }

        public long ReadInt64(int byteOffset)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            using var stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            stream.Position = byteOffset;
            stream.ReadExactly(bytes);
            return BitConverter.ToInt64(bytes);
        }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch
            {
                // A failed agent may release its mapped view just after the controller timeout.
            }
        }
    }
}
