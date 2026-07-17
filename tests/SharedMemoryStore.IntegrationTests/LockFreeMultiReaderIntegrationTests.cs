using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeMultiReaderIntegrationTests
{
    private const int SlotCount = 16;
    private const int MaxValueBytes = 128;
    private const int MaxDescriptorBytes = 8;
    private const int MaxKeyBytes = 8;
    private const int LeaseRecordCount = 64;
    private const int ParticipantRecordCount = 32;
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(45);

    [Theory]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(12, false)]
    [InlineData(1, true)]
    [InlineData(6, true)]
    [InlineData(12, true)]
    [Trait("Category", "Integration")]
    public void ReadersVerifySameKeyAndDistributedKeyBytesAcrossProcesses(int readerCount, bool distributedKeys)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-readers-{readerCount}-{distributedKeys}-{Guid.NewGuid():N}";
        using var store = CreateStore(name);
        int keyCount = distributedKeys ? readerCount : 1;
        for (var index = 0; index < keyCount; index++)
        {
            Assert.Equal(
                StoreStatus.Success,
                store.TryPublish(Key(index), Value(index), Descriptor(index)));
        }

        using var gate = AgentGate.Create(readerCount);
        string[][] commands = Enumerable.Range(0, readerCount)
            .Select(index => ReadCommand(
                name,
                Key(distributedKeys ? index : 0),
                Value(distributedKeys ? index : 0),
                Descriptor(distributedKeys ? index : 0),
                iterations: 64,
                gate.GoPath,
                gate.ReadyPaths[index]))
            .ToArray();

        AgentResult[] results = RunReaders(commands, gate);

        AssertAgentsSucceeded(results, readerCount);
        Assert.All(results, static result => Assert.Contains("OK lease-read", result.StandardOutput));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PausedObserverLeaseDoesNotStopSameKeyOrUnrelatedReaders()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-paused-observer-{Guid.NewGuid():N}";
        using var store = CreateStore(name);
        Assert.Equal(StoreStatus.Success, store.TryPublish(Key(1), Value(1), Descriptor(1)));
        Assert.Equal(StoreStatus.Success, store.TryPublish(Key(2), Value(2), Descriptor(2)));

        string directory = Path.Combine(Path.GetTempPath(), $"sms-v2-observer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string observerReady = Path.Combine(directory, "observer.ready");
        string observerRelease = Path.Combine(directory, "observer.release");
        Process? observer = null;
        try
        {
            observer = StartAgent(HoldCommand(
                name,
                Key(1),
                Value(1),
                Descriptor(1),
                observerReady,
                observerRelease));
            WaitForReadyFiles([observerReady], [observer], AgentTimeout);

            const int workerCount = 6;
            using var gate = AgentGate.Create(workerCount, directory);
            string[][] commands = Enumerable.Range(0, workerCount)
                .Select(index =>
                {
                    int keyIndex = index % 2 == 0 ? 1 : 2;
                    return ReadCommand(
                        name,
                        Key(keyIndex),
                        Value(keyIndex),
                        Descriptor(keyIndex),
                        iterations: 64,
                        gate.GoPath,
                        gate.ReadyPaths[index]);
                })
                .ToArray();

            AgentResult[] workers = RunReaders(commands, gate);
            AssertAgentsSucceeded(workers, workerCount);
            Assert.False(observer.HasExited, "The observer should remain paused while healthy readers finish.");

            File.WriteAllText(observerRelease, "continue");
            AgentResult observerResult = WaitForAgent(observer, AgentTimeout);
            AssertAgentsSucceeded([observerResult], expectedCount: 1);
            Assert.Contains("OK lease-hold", observerResult.StandardOutput);
        }
        finally
        {
            try
            {
                File.WriteAllText(observerRelease, "continue");
            }
            catch
            {
                // Best-effort release before terminating the test process.
            }

            if (observer is not null)
            {
                Kill(observer);
                observer.Dispose();
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Unique test artifacts can be reclaimed by the OS if cleanup races a process exit.
            }
        }
    }

    private static MemoryStore CreateStore(string name)
    {
        var options = SharedMemoryStoreOptions.Create(
            name,
            SlotCount,
            MaxValueBytes,
            MaxDescriptorBytes,
            MaxKeyBytes,
            LeaseRecordCount,
            ParticipantRecordCount,
            OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static string[] ReadCommand(
        string name,
        byte[] key,
        byte[] value,
        byte[] descriptor,
        int iterations,
        string goPath,
        string readyPath) =>
    [
        "lease-read",
        name,
        SlotCount.ToString(CultureInfo.InvariantCulture),
        MaxValueBytes.ToString(CultureInfo.InvariantCulture),
        MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
        MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
        LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
        ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
        Convert.ToHexString(key),
        Encode(value),
        Encode(descriptor),
        iterations.ToString(CultureInfo.InvariantCulture),
        goPath,
        readyPath
    ];

    private static string[] HoldCommand(
        string name,
        byte[] key,
        byte[] value,
        byte[] descriptor,
        string readyPath,
        string releasePath) =>
    [
        "lease-hold",
        name,
        SlotCount.ToString(CultureInfo.InvariantCulture),
        MaxValueBytes.ToString(CultureInfo.InvariantCulture),
        MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
        MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
        LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
        ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
        Convert.ToHexString(key),
        Encode(value),
        Encode(descriptor),
        readyPath,
        releasePath
    ];

    private static AgentResult[] RunReaders(string[][] commands, AgentGate gate)
    {
        Process[] processes = commands.Select(StartAgent).ToArray();
        try
        {
            WaitForReadyFiles(gate.ReadyPaths, processes, AgentTimeout);
            File.WriteAllText(gate.GoPath, "go");
            return WaitForAgents(processes, AgentTimeout);
        }
        finally
        {
            foreach (Process process in processes)
            {
                Kill(process);
                process.Dispose();
            }
        }
    }

    private static void WaitForReadyFiles(
        IReadOnlyList<string> readyPaths,
        IReadOnlyList<Process> processes,
        TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (readyPaths.Any(static path => !File.Exists(path)))
        {
            Process? failed = processes.FirstOrDefault(static process => process.HasExited);
            if (failed is not null)
            {
                var result = new AgentResult(
                    failed.ExitCode,
                    failed.StandardOutput.ReadToEnd(),
                    failed.StandardError.ReadToEnd());
                AssertAgentsSucceeded([result], expectedCount: 1);
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Reader agents did not reach their start checkpoint.");
            }

            spin.SpinOnce();
        }
    }

    private static AgentResult WaitForAgent(Process process, TimeSpan timeout)
    {
        if (!process.WaitForExit(checked((int)timeout.TotalMilliseconds)))
        {
            Kill(process);
            throw new TimeoutException("Lock-free reader agent exceeded its bounded timeout.");
        }

        return new AgentResult(
            process.ExitCode,
            process.StandardOutput.ReadToEnd(),
            process.StandardError.ReadToEnd());
    }

    private static AgentResult[] WaitForAgents(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var results = new AgentResult[processes.Count];
        for (var index = 0; index < processes.Count; index++)
        {
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            int remainingMilliseconds = remainingTicks <= 0
                ? 0
                : (int)Math.Min(
                    int.MaxValue,
                    Math.Ceiling(remainingTicks * 1000d / Stopwatch.Frequency));
            Process process = processes[index];
            if (!process.WaitForExit(remainingMilliseconds))
            {
                Kill(process);
                throw new TimeoutException("Lock-free reader agents exceeded their shared bounded timeout.");
            }

            results[index] = new AgentResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }

        return results;
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
        foreach (string argument in command)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the lock-free reader agent.");
    }

    private static string LocateAgentAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        string path = Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Lock-free reader agent was not built.", path);
        }

        return path;
    }

    private static void AssertAgentsSucceeded(AgentResult[] results, int expectedCount)
    {
        Assert.Equal(expectedCount, results.Length);
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

    private static void Kill(Process process)
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
            // Cleanup is best effort for unique test resources.
        }
    }

    private static byte[] Key(int index) =>
        [(byte)(0x80 | (index & 0x7f)), (byte)(index >> 8), (byte)index];

    private static byte[] Value(int index)
    {
        var value = new byte[64];
        for (var offset = 0; offset < value.Length; offset++)
        {
            value[offset] = (byte)(index * 17 + offset);
        }

        return value;
    }

    private static byte[] Descriptor(int index) =>
        [(byte)index, (byte)~index, 0x5a, 0xa5];

    private static string Encode(byte[] value) => value.Length == 0 ? "-" : Convert.ToHexString(value);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct AgentResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class AgentGate : IDisposable
    {
        private AgentGate(string directory, bool ownsDirectory, int participantCount)
        {
            DirectoryPath = directory;
            OwnsDirectory = ownsDirectory;
            GoPath = Path.Combine(directory, $"readers-{Guid.NewGuid():N}.go");
            ReadyPaths = Enumerable.Range(0, participantCount)
                .Select(index => Path.Combine(directory, $"reader-{Guid.NewGuid():N}-{index}.ready"))
                .ToArray();
        }

        public string DirectoryPath { get; }

        public bool OwnsDirectory { get; }

        public string GoPath { get; }

        public string[] ReadyPaths { get; }

        public static AgentGate Create(int participantCount, string? existingDirectory = null)
        {
            string directory = existingDirectory
                ?? Path.Combine(Path.GetTempPath(), $"sms-v2-reader-gate-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            return new AgentGate(directory, existingDirectory is null, participantCount);
        }

        public void Dispose()
        {
            foreach (string path in ReadyPaths.Append(GoPath))
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                    // Best effort after all reader processes have exited.
                }
            }

            if (!OwnsDirectory)
            {
                return;
            }

            try
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
            catch
            {
                // Unique temporary directory may be reclaimed later.
            }
        }
    }
}
