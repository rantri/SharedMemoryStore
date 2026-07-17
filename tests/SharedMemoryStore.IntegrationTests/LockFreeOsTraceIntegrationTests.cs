using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeOsTraceIntegrationTests
{
    private const int SlotCount = 12;
    private const int MaxValueBytes = 64;
    private const int MaxDescriptorBytes = 16;
    private const int MaxKeyBytes = 8;
    private const int LeaseRecordCount = 16;
    private const int ParticipantRecordCount = 8;
    private const int TraceIterations = 1_024;
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(75);

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "LinuxStrace")]
    public void LinuxMarkedSteadyIntervalDoesNotUseStoreOperationLock()
    {
        if (!IsLinuxX64() || !CommandSucceeds("strace", "--version"))
        {
            // scripts/validate-lock-free-os.ps1 reports this platform/tool
            // boundary as not-qualified. Returning here keeps ordinary local
            // test runs portable without misrepresenting qualification evidence.
            return;
        }

        string name = $"sms-v2-strace-{Guid.NewGuid():N}";
        string markerDirectory = Path.Combine(Path.GetTempPath(), $"sms-v2-strace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(markerDirectory);
        string readyPath = Path.Combine(markerDirectory, "ready");
        string goPath = Path.Combine(markerDirectory, "go");
        string donePath = Path.Combine(markerDirectory, "done");
        string tracePrefix = Path.Combine(markerDirectory, "trace");
        string synchronizationPath = PlatformResourceName.Create(name).LinuxSynchronizationPath;

        using MemoryStore store = CreateStore(name);
        using Process process = StartTracedAgent(
            name,
            readyPath,
            goPath,
            donePath,
            tracePrefix);
        try
        {
            Assert.True(WaitForFile(readyPath, AgentTimeout), AgentFailure(process, "The traced agent did not become ready."));

            // Give the cold open/unlock trace a distinct timestamp from the
            // measurement marker even on filesystems with coarse timestamps.
            Thread.Sleep(25);
            PublishMarker(goPath, "go");
            decimal intervalStart = ToUnixSeconds(File.GetLastWriteTimeUtc(goPath));

            Assert.True(WaitForFile(donePath, AgentTimeout), AgentFailure(process, "The traced agent did not finish its marked interval."));
            decimal intervalEnd = ToUnixSeconds(File.GetLastWriteTimeUtc(donePath));
            Assert.Equal("ok", File.ReadAllText(donePath));
            Assert.True(process.WaitForExit((int)AgentTimeout.TotalMilliseconds), "The traced agent did not exit after its done marker.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode == 0, $"The traced agent failed with exit {process.ExitCode}.\nstdout={output}\nstderr={error}");

            string[] traceFiles = Directory.GetFiles(markerDirectory, "trace*");
            Assert.NotEmpty(traceFiles);
            string[] lines = traceFiles.SelectMany(File.ReadLines).ToArray();
            TraceObservation observation = ClassifyTraceLines(
                lines,
                synchronizationPath,
                intervalStart,
                intervalEnd);

            Assert.True(
                observation.AllTargetLockCalls.Count > 0,
                "strace did not observe the expected cold-path operation-lock acquisition; "
                + "the no-lock assertion would otherwise be vacuous.\n"
                + string.Join(Environment.NewLine, lines));
            Assert.True(
                observation.MarkedTargetLockCalls.Count == 0,
                "The warmed layout-v2 interval touched the store operation lock.\n"
                + string.Join(Environment.NewLine, observation.MarkedTargetLockCalls));
        }
        finally
        {
            Kill(process);
            TryDeleteDirectory(markerDirectory);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "LinuxSignalPause")]
    public void LinuxSigStopAtProtocolCheckpointDoesNotBlockUnrelatedProgress()
    {
        if (!IsLinuxX64() || !CommandSucceeds("kill", "--version"))
        {
            return;
        }

        string name = $"sms-v2-sigstop-{Guid.NewGuid():N}";
        using MemoryStore store = CreateStore(name);
        byte[] tokenKey = Key(0x10);
        byte[] existingKey = Key(0x20);
        byte[] operationKey = Key(0x30);
        byte[] unrelatedKey = Key(0x40);
        Assert.Equal(StoreStatus.Success, store.TryPublish(tokenKey, [0x11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(existingKey, [0x21]));

        using Process process = StartCheckpointAgent(
            "dotnet",
            ["exec", LocateAgentAssembly()],
            name,
            LockFreeCheckpointId.PublishAfterCommitPublication,
            tokenKey,
            existingKey,
            operationKey);
        try
        {
            string checkpoint = ReadCheckpoint(process);
            Assert.Contains(nameof(LockFreeCheckpointId.PublishAfterCommitPublication), checkpoint, StringComparison.Ordinal);

            AssertCommand("kill", "-STOP", process.Id.ToString(CultureInfo.InvariantCulture));
            Assert.True(WaitForLinuxStoppedState(process.Id, TimeSpan.FromSeconds(5)), "SIGSTOP did not place the agent in a stopped state.");

            AssertHealthyProgress(store, unrelatedKey);

            AssertCommand("kill", "-CONT", process.Id.ToString(CultureInfo.InvariantCulture));
            process.StandardInput.WriteLine("CONTINUE");
            process.StandardInput.Flush();
            AssertSuccessfulExit(process, "SIGSTOP/SIGCONT checkpoint agent");
        }
        finally
        {
            _ = CommandSucceeds("kill", "-CONT", process.Id.ToString(CultureInfo.InvariantCulture));
            Kill(process);
            RemoveIfPresent(store, tokenKey);
            RemoveIfPresent(store, existingKey);
            RemoveIfPresent(store, operationKey);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "DockerPause")]
    public void LinuxDockerPauseAtProtocolCheckpointDoesNotBlockUnrelatedProgress()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SMS_RUN_LOCK_FREE_DOCKER_PAUSE_VALIDATION"),
                "1",
                StringComparison.Ordinal)
            || !IsLinuxX64())
        {
            return;
        }

        Assert.True(
            CommandSucceeds("docker", "info", "--format", "{{.ServerVersion}}"),
            "Docker was requested but its daemon is unavailable.");
        string image = Environment.GetEnvironmentVariable("SMS_LOCK_FREE_DOCKER_IMAGE")
            ?? "mcr.microsoft.com/dotnet/runtime:10.0";
        Assert.True(
            CommandSucceeds("docker", "image", "inspect", "--format", "{{.Id}}", image),
            $"Docker pause validation image is unavailable: {image}");

        string name = $"sms-v2-docker-pause-{Guid.NewGuid():N}";
        string containerName = $"sms-v2-pause-{Guid.NewGuid():N}";
        using MemoryStore store = CreateStore(name);
        byte[] tokenKey = Key(0x50);
        byte[] existingKey = Key(0x60);
        byte[] operationKey = Key(0x70);
        byte[] unrelatedKey = Key(0x80);
        Assert.Equal(StoreStatus.Success, store.TryPublish(tokenKey, [0x51]));
        Assert.Equal(StoreStatus.Success, store.TryPublish(existingKey, [0x61]));

        string repositoryRoot = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        string containerAgent = $"/repo/tests/SharedMemoryStore.LockFreeAgent/bin/{configuration}/net10.0/SharedMemoryStore.LockFreeAgent.dll";
        string sharedMemoryDirectory = Path.GetDirectoryName(PlatformResourceName.Create(name).LinuxRegionPath)
            ?? throw new DirectoryNotFoundException("Linux shared-memory resource directory was not resolved.");
        string[] dockerPrefix = BuildDockerCheckpointPrefix(
            containerName,
            repositoryRoot,
            sharedMemoryDirectory,
            image,
            containerAgent);

        using Process process = StartCheckpointAgent(
            "docker",
            dockerPrefix,
            name,
            LockFreeCheckpointId.PublishAfterCommitPublication,
            tokenKey,
            existingKey,
            operationKey);
        try
        {
            string checkpoint = ReadCheckpoint(process);
            Assert.Contains(nameof(LockFreeCheckpointId.PublishAfterCommitPublication), checkpoint, StringComparison.Ordinal);

            AssertCommand("docker", "pause", containerName);
            Assert.Equal("true", RunCommand("docker", "inspect", "--format", "{{.State.Paused}}", containerName).Trim());
            AssertHealthyProgress(store, unrelatedKey);

            AssertCommand("docker", "unpause", containerName);
            process.StandardInput.WriteLine("CONTINUE");
            process.StandardInput.Flush();
            AssertSuccessfulExit(process, "docker pause/unpause checkpoint agent");
        }
        finally
        {
            _ = CommandSucceeds("docker", "unpause", containerName);
            _ = CommandSucceeds("docker", "rm", "--force", containerName);
            Kill(process);
            RemoveIfPresent(store, tokenKey);
            RemoveIfPresent(store, existingKey);
            RemoveIfPresent(store, operationKey);
        }
    }

    [Fact]
    [Trait("Category", "TraceSelfTest")]
    public void TraceClassifierSeparatesColdAndMarkedStoreLockCalls()
    {
        const string path = "/dev/shm/SharedMemoryStore/sms-test.lock";
        string[] lines =
        [
            "1710000000.100000 fcntl(17</dev/shm/SharedMemoryStore/sms-test.lock>, F_OFD_SETLK, {l_type=F_WRLCK}) = 0",
            "1710000000.250000 fcntl(18</dev/shm/SharedMemoryStore/other.lock>, F_SETLKW, {l_type=F_WRLCK}) = 0",
            "1710000000.400000 flock(17</dev/shm/SharedMemoryStore/sms-test.lock>, LOCK_UN) = 0"
        ];

        TraceObservation observation = ClassifyTraceLines(lines, path, 1710000000.2m, 1710000000.3m);

        Assert.Equal(2, observation.AllTargetLockCalls.Count);
        Assert.Empty(observation.MarkedTargetLockCalls);
    }

    [Fact]
    [Trait("Category", "TraceSelfTest")]
    public void DockerCheckpointPrefixSharesTheHostPidNamespace()
    {
        string[] prefix = BuildDockerCheckpointPrefix(
            "container",
            "/repository",
            "/dev/shm/SharedMemoryStore",
            "runtime-image",
            "/repository/agent.dll");

        Assert.Equal(1, prefix.Count(static argument => string.Equals(argument, "--pid=host", StringComparison.Ordinal)));
        Assert.True(
            Array.IndexOf(prefix, "--pid=host") < Array.IndexOf(prefix, "runtime-image"),
            "The PID namespace option must be a docker-run option, not an agent argument.");
    }

    private static string[] BuildDockerCheckpointPrefix(
        string containerName,
        string repositoryRoot,
        string sharedMemoryDirectory,
        string image,
        string containerAgent) =>
    [
        "run", "--rm", "--interactive", "--pid=host", "--name", containerName,
        "--mount", $"type=bind,source={repositoryRoot},target=/repo,readonly",
        "--mount", $"type=bind,source={sharedMemoryDirectory},target={sharedMemoryDirectory}",
        image, "dotnet", "exec", containerAgent
    ];

    private static Process StartTracedAgent(
        string name,
        string readyPath,
        string goPath,
        string donePath,
        string tracePrefix)
    {
        var start = RedirectedStartInfo("strace");
        foreach (string argument in new[]
        {
            "-ff", "-ttt", "-yy", "-s", "4096",
            "-e", "trace=fcntl,flock", "-o", tracePrefix,
            "dotnet", "exec", LocateAgentAssembly(),
            "steady-no-lock", name,
            SlotCount.ToString(CultureInfo.InvariantCulture),
            MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
            ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
            TraceIterations.ToString(CultureInfo.InvariantCulture),
            readyPath, goPath, donePath
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Unable to launch strace.");
    }

    private static Process StartCheckpointAgent(
        string executable,
        IReadOnlyList<string> prefix,
        string name,
        LockFreeCheckpointId checkpoint,
        byte[] tokenKey,
        byte[] existingKey,
        byte[] operationKey)
    {
        var start = RedirectedStartInfo(executable);
        start.RedirectStandardInput = true;
        foreach (string argument in prefix)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string argument in new[]
        {
            "checkpoint-crash", name,
            SlotCount.ToString(CultureInfo.InvariantCulture),
            MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
            ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
            ((int)checkpoint).ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(tokenKey),
            Convert.ToHexString(existingKey),
            Convert.ToHexString(operationKey),
            Convert.ToHexString(Key(0x90)),
            Convert.ToHexString(new byte[] { 0xA1, 0xA2 }),
            Convert.ToHexString(new byte[] { 0xB1 }),
            "v1"
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"Unable to launch {executable} checkpoint agent.");
    }

    private static ProcessStartInfo RedirectedStartInfo(string executable) => new(executable)
    {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    private static string ReadCheckpoint(Process process)
    {
        string? line = process.StandardOutput.ReadLineAsync().WaitAsync(AgentTimeout).GetAwaiter().GetResult();
        if (line is null || !line.StartsWith("CHECKPOINT ", StringComparison.Ordinal))
        {
            string error = process.StandardError.ReadToEnd();
            throw new Xunit.Sdk.XunitException($"Checkpoint agent did not reach its target. stdout={line}\nstderr={error}");
        }

        return line;
    }

    private static void AssertHealthyProgress(MemoryStore store, byte[] key)
    {
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [0xC1, 0xC2], [0xC3]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease lease));
        Assert.Equal(new byte[] { 0xC1, 0xC2 }, lease.ValueSpan.ToArray());
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, store.TryRemove(key));
    }

    private static TraceObservation ClassifyTraceLines(
        IEnumerable<string> lines,
        string targetPath,
        decimal intervalStart,
        decimal intervalEnd)
    {
        var all = new List<string>();
        var marked = new List<string>();
        foreach (string line in lines)
        {
            bool fcntlLock = line.Contains("fcntl(", StringComparison.Ordinal)
                && (line.Contains("F_OFD_SETLK", StringComparison.Ordinal)
                    || line.Contains("F_OFD_SETLKW", StringComparison.Ordinal)
                    || line.Contains("F_SETLK", StringComparison.Ordinal)
                    || line.Contains("F_SETLKW", StringComparison.Ordinal));
            bool flock = line.Contains("flock(", StringComparison.Ordinal);
            if ((!fcntlLock && !flock) || !line.Contains(targetPath, StringComparison.Ordinal))
            {
                continue;
            }

            all.Add(line);
            if (TryReadTraceTimestamp(line, out decimal timestamp)
                && timestamp >= intervalStart
                && timestamp <= intervalEnd)
            {
                marked.Add(line);
            }
        }

        return new TraceObservation(all, marked);
    }

    private static bool TryReadTraceTimestamp(string line, out decimal timestamp)
    {
        timestamp = 0;
        int separator = line.IndexOf(' ');
        return separator > 0
            && decimal.TryParse(
                line.AsSpan(0, separator),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out timestamp);
    }

    private static decimal ToUnixSeconds(DateTime utc) =>
        (utc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / (decimal)TimeSpan.TicksPerSecond;

    private static MemoryStore CreateStore(string name)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            SharedMemoryStoreOptions.Create(
                name,
                SlotCount,
                MaxValueBytes,
                MaxDescriptorBytes,
                MaxKeyBytes,
                LeaseRecordCount,
                ParticipantRecordCount,
                OpenMode.CreateNew,
                enableLeaseRecovery: true),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetElapsedTime(started) >= timeout)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    private static void PublishMarker(string path, string content)
    {
        // Publish the complete marker atomically. This also fixes the marker's
        // mtime before the agent can observe it, so intervalStart cannot move
        // past any traced operation begun after the go signal.
        string temporaryPath = $"{path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";
        File.WriteAllText(temporaryPath, content);
        File.Move(temporaryPath, path);
    }

    private static bool WaitForLinuxStoppedState(int processId, TimeSpan timeout)
    {
        string statusPath = $"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/status";
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < timeout)
        {
            if (File.Exists(statusPath)
                && File.ReadLines(statusPath).Any(static line =>
                    line.StartsWith("State:", StringComparison.Ordinal)
                    && line.Contains('T')))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return false;
    }

    private static void AssertSuccessfulExit(Process process, string role)
    {
        Assert.True(process.WaitForExit((int)AgentTimeout.TotalMilliseconds), $"{role} timed out.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"{role} failed with exit {process.ExitCode}.\nstdout={output}\nstderr={error}");
    }

    private static string AgentFailure(Process process, string message)
    {
        if (!process.HasExited)
        {
            return message;
        }

        return $"{message} exit={process.ExitCode}; stdout={process.StandardOutput.ReadToEnd()}; stderr={process.StandardError.ReadToEnd()}";
    }

    private static bool CommandSucceeds(string executable, params string[] arguments)
    {
        try
        {
            using Process process = StartCommand(executable, arguments);
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            _ = output.GetAwaiter().GetResult();
            _ = error.GetAwaiter().GetResult();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AssertCommand(string executable, params string[] arguments)
    {
        string output = RunCommand(executable, arguments);
        _ = output;
    }

    private static string RunCommand(string executable, params string[] arguments)
    {
        using Process process = StartCommand(executable, arguments);
        Assert.True(process.WaitForExit(30_000), $"{executable} timed out.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        Assert.True(process.ExitCode == 0, $"{executable} failed with exit {process.ExitCode}.\nstdout={output}\nstderr={error}");
        return output;
    }

    private static Process StartCommand(string executable, IEnumerable<string> arguments)
    {
        ProcessStartInfo start = RedirectedStartInfo(executable);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException($"Unable to launch {executable}.");
    }

    private static string LocateAgentAssembly()
    {
        string root = FindRepositoryRoot();
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        string path = Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lock-free OS-trace agent was not built.", path);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private static void RemoveIfPresent(MemoryStore store, byte[] key)
    {
        StoreStatus status = store.TryRemove(key, new StoreWaitOptions(TimeSpan.FromMilliseconds(250)));
        Assert.Contains(status, new[] { StoreStatus.Success, StoreStatus.NotFound, StoreStatus.RemovePending });
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
            // Cleanup is best effort for a unique mapped-store name/process.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Trace preservation/cleanup failure must not hide assertion output.
        }
    }

    private static byte[] Key(byte prefix) => [prefix, 0x01];

    private static bool IsLinuxX64() =>
        OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed record TraceObservation(
        IReadOnlyList<string> AllTargetLockCalls,
        IReadOnlyList<string> MarkedTargetLockCalls);
}
