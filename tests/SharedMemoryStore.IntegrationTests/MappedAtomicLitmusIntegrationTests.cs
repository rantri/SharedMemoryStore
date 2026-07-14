using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using SharedMemoryStore.Interop;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.IntegrationTests;

public sealed class MappedAtomicLitmusIntegrationTests
{
    private const int AgentTimeoutMilliseconds = 120_000;
    private const int AgentPollMilliseconds = 100;

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
        AtomicTestMapping.Execute(mapping =>
        {
            var results = RunAgents(
                () => mapping.DescribeWords(
                    ("sequence", 0),
                    ("complement", 8),
                    ("publication", 16),
                    ("acknowledgement", 24)),
                ["atomic-publication-consumer", mapping.Path, iterations.ToString(CultureInfo.InvariantCulture)],
                ["atomic-publication-producer", mapping.Path, iterations.ToString(CultureInfo.InvariantCulture)]);

            AssertAgentsSucceeded(results);
            Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
            Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 16));
            Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 24));
            Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 0));
            Assert.Equal(~(long)iterations, mapping.ReadInt64(byteOffset: 8));
        });
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
        var iterationArgument = iterationsPerProcess.ToString(CultureInfo.InvariantCulture);
        AtomicTestMapping.Execute(mapping =>
        {
            var results = RunAgents(
                () => mapping.DescribeWords(("counter", 32)),
                ["atomic-cas-worker", mapping.Path, iterationArgument],
                ["atomic-cas-worker", mapping.Path, iterationArgument]);

            AssertAgentsSucceeded(results);
            Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
            Assert.Equal(2L * iterationsPerProcess, mapping.ReadInt64(byteOffset: 32));
        });
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
        var iterationArgument = iterations.ToString(CultureInfo.InvariantCulture);
        AtomicTestMapping.Execute(mapping =>
        {
            var results = RunAgents(
                () => mapping.DescribeWords(
                    ("ready0", 0),
                    ("ready1", 8),
                    ("phase", 16),
                    ("done0", 24),
                    ("done1", 32),
                    ("word0", 40),
                    ("word1", 48),
                    ("seen0", 56),
                    ("seen1", 64)),
                ["atomic-dekker-worker", mapping.Path, iterationArgument, "0"],
                ["atomic-dekker-worker", mapping.Path, iterationArgument, "1"],
                ["atomic-dekker-coordinator", mapping.Path, iterationArgument]);

            AssertAgentsSucceeded(results);
            Assert.All(results, static result => Assert.Contains("aligned=1", result.StandardOutput, StringComparison.Ordinal));
            Assert.Contains("forbidden=0", results[2].StandardOutput, StringComparison.Ordinal);
            Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 24));
            Assert.Equal(iterations, mapping.ReadInt64(byteOffset: 32));
        });
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

    private static AgentResult[] RunAgents(Func<string> failureStateSnapshot, params string[][] commands)
    {
        var agents = new List<RunningAgent>(commands.Length);
        AgentResult[]? results = null;
        Exception? failure = null;
        string? stateBeforeStop = null;
        string? agentsBeforeStop = null;
        var captureFailureDiagnostics = false;
        try
        {
            foreach (var command in commands)
            {
                agents.Add(StartAgent(command));
            }

            var deadline = Stopwatch.GetTimestamp()
                + (long)(AgentTimeoutMilliseconds / 1000d * Stopwatch.Frequency);
            while (agents.Any(static agent => !agent.Process.HasExited))
            {
                var remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    captureFailureDiagnostics = true;
                    stateBeforeStop = TryDescribeState(failureStateSnapshot);
                    agentsBeforeStop = DescribeAgentStates(agents);
                    throw new TimeoutException(
                        "Mapped atomic agents did not complete within "
                        + AgentTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)
                        + " ms.");
                }

                var remainingMilliseconds = (int)Math.Min(
                    AgentPollMilliseconds,
                    Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)));
                Thread.Sleep(remainingMilliseconds);
            }

            foreach (var agent in agents)
            {
                agent.Process.WaitForExit();
            }

            results = agents.Select(ReadAgentResult).ToArray();
            if (results.Any(static result => result.ExitCode != 0))
            {
                captureFailureDiagnostics = true;
                stateBeforeStop = TryDescribeState(failureStateSnapshot);
                agentsBeforeStop = DescribeAgentStates(agents);
                throw new InvalidOperationException("One or more mapped atomic agents exited unsuccessfully.");
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        var cleanupFailures = StopAgents(agents);
        var stateAfterStop = captureFailureDiagnostics
            ? TryDescribeState(failureStateSnapshot)
            : null;
        var agentDiagnostics = captureFailureDiagnostics
            ? DescribeAgentDiagnostics(agents)
            : null;
        cleanupFailures.AddRange(DisposeAgents(agents));

        if (failure is not null)
        {
            if (captureFailureDiagnostics)
            {
                var diagnosticMessage = failure.Message
                    + Environment.NewLine
                    + "state-before-stop: " + stateBeforeStop
                    + Environment.NewLine
                    + "state-after-stop: " + stateAfterStop
                    + Environment.NewLine
                    + "agents-before-stop: " + agentsBeforeStop
                    + Environment.NewLine
                    + "agents-after-stop: " + agentDiagnostics;
                failure = failure is TimeoutException
                    ? new TimeoutException(diagnosticMessage, failure)
                    : new InvalidOperationException(diagnosticMessage, failure);
            }

            if (cleanupFailures.Count != 0)
            {
                failure = new AggregateException(
                    failure,
                    new InvalidOperationException(
                        "Mapped atomic agent cleanup failed: " + string.Join(" | ", cleanupFailures)));
            }

            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        if (cleanupFailures.Count != 0)
        {
            throw new InvalidOperationException(
                "Mapped atomic agent cleanup failed: " + string.Join(" | ", cleanupFailures));
        }

        return results ?? throw new InvalidOperationException("Mapped atomic agents produced no result.");
    }

    private static RunningAgent StartAgent(string[] command)
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

        Process? process = null;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Unable to start the lock-free atomic test agent.");
            return new RunningAgent(
                GetAgentRole(command),
                process,
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync());
        }
        catch (Exception startFailure)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (process is not null && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                if (process is not null && !process.WaitForExit(5_000))
                {
                    cleanupFailures.Add(new TimeoutException(
                        "Partially started atomic agent did not exit within 5000 ms."));
                }
            }
            catch (Exception cleanupFailure)
            {
                cleanupFailures.Add(cleanupFailure);
            }

            try
            {
                process?.Dispose();
            }
            catch (Exception disposeFailure)
            {
                cleanupFailures.Add(disposeFailure);
            }

            if (cleanupFailures.Count != 0)
            {
                throw new AggregateException(
                    new[] { startFailure }.Concat(cleanupFailures));
            }

            ExceptionDispatchInfo.Capture(startFailure).Throw();
            throw new UnreachableException();
        }
    }

    private static string GetAgentRole(string[] command)
    {
        return command.Length == 4 && command[0] == "atomic-dekker-worker"
            ? command[0] + "-" + command[3]
            : command[0];
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
            "Agent role: "
            + result.Role
            + Environment.NewLine
            + "exit code: "
            + result.ExitCode.ToString(CultureInfo.InvariantCulture)
            + Environment.NewLine
            + "stdout: "
            + result.StandardOutput
            + Environment.NewLine
            + "stderr: "
            + result.StandardError));
    }

    private static AgentResult ReadAgentResult(RunningAgent agent)
    {
        return new AgentResult(
            agent.Role,
            agent.Process.ExitCode,
            agent.StandardOutput.GetAwaiter().GetResult(),
            agent.StandardError.GetAwaiter().GetResult());
    }

    private static string TryDescribeState(Func<string> snapshot)
    {
        try
        {
            return snapshot();
        }
        catch (Exception exception)
        {
            return "unavailable(" + exception.GetType().Name + ": " + exception.Message + ")";
        }
    }

    private static string DescribeAgentStates(IEnumerable<RunningAgent> agents)
    {
        return string.Join("; ", agents.Select(static agent => DescribeAgent(agent, includeOutput: false, 0)));
    }

    private static string DescribeAgentDiagnostics(IEnumerable<RunningAgent> agents)
    {
        var drainDeadline = Stopwatch.GetTimestamp() + 5L * Stopwatch.Frequency;
        return string.Join(
            "; ",
            agents.Select(agent => DescribeAgent(agent, includeOutput: true, drainDeadline)));
    }

    private static string DescribeAgent(RunningAgent agent, bool includeOutput, long drainDeadline)
    {
        try
        {
            var description = agent.Role
                + "(pid=" + agent.Process.Id.ToString(CultureInfo.InvariantCulture)
                + ",state=" + (agent.Process.HasExited
                    ? "exited:" + agent.Process.ExitCode.ToString(CultureInfo.InvariantCulture)
                    : "running");
            if (includeOutput)
            {
                description += ",stdout=" + ReadCompletedOutput(agent.StandardOutput, drainDeadline)
                    + ",stderr=" + ReadCompletedOutput(agent.StandardError, drainDeadline);
            }

            return description + ")";
        }
        catch (Exception exception)
        {
            return agent.Role + "(diagnostic-failed:" + exception.GetType().Name + ":" + exception.Message + ")";
        }
    }

    private static string ReadCompletedOutput(Task<string> output, long drainDeadline)
    {
        try
        {
            var remaining = drainDeadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return "<shared-drain-timeout>";
            }

            var remainingMilliseconds = (int)Math.Min(
                int.MaxValue,
                Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)));
            return output.Wait(remainingMilliseconds)
                ? (output.Result.Length == 0 ? "<empty>" : output.Result.Trim())
                : "<shared-drain-timeout>";
        }
        catch (Exception exception)
        {
            return "<drain-failed:" + exception.GetType().Name + ">";
        }
    }

    private static List<string> StopAgents(IEnumerable<RunningAgent> agents)
    {
        var owned = agents.ToArray();
        var failures = new List<string>();
        foreach (var agent in owned)
        {
            try
            {
                if (!agent.Process.HasExited)
                {
                    agent.Process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception)
            {
                failures.Add(agent.Role + ": kill " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        var stopDeadline = Stopwatch.GetTimestamp() + 5L * Stopwatch.Frequency;
        foreach (var agent in owned)
        {
            try
            {
                var remaining = stopDeadline - Stopwatch.GetTimestamp();
                var remainingMilliseconds = remaining <= 0
                    ? 0
                    : (int)Math.Min(
                        int.MaxValue,
                        Math.Max(1, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency)));
                if (!agent.Process.WaitForExit(remainingMilliseconds))
                {
                    failures.Add(agent.Role + ": did not exit within the shared 5000 ms stop deadline");
                }
            }
            catch (Exception exception)
            {
                failures.Add(agent.Role + ": wait " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        return failures;
    }

    private static List<string> DisposeAgents(IEnumerable<RunningAgent> agents)
    {
        var failures = new List<string>();
        foreach (var agent in agents)
        {
            try
            {
                if (!agent.Process.HasExited)
                {
                    failures.Add(agent.Role + ": process still running when its diagnostic handle was disposed");
                }

                agent.Process.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(agent.Role + ": dispose " + exception.GetType().Name + ": " + exception.Message);
            }
        }

        return failures;
    }

    private sealed record RunningAgent(
        string Role,
        Process Process,
        Task<string> StandardOutput,
        Task<string> StandardError);

    private readonly record struct AgentResult(
        string Role,
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class AtomicTestMapping
    {
        private AtomicTestMapping(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static void Execute(Action<AtomicTestMapping> action)
        {
            var mapping = Create();
            Exception? failure = null;
            try
            {
                action(mapping);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            var cleanupFailure = mapping.TryDelete();
            if (failure is not null)
            {
                if (cleanupFailure is not null)
                {
                    throw new AggregateException(failure, cleanupFailure);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (cleanupFailure is not null)
            {
                throw cleanupFailure;
            }
        }

        public static AtomicTestMapping Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "sms-atomic-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".map");
            var ownsCreatedFile = false;
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                ownsCreatedFile = true;
                stream.SetLength(4096);
            }
            catch (Exception creationFailure)
            {
                if (ownsCreatedFile)
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception cleanupFailure)
                    {
                        throw new AggregateException(creationFailure, cleanupFailure);
                    }
                }

                ExceptionDispatchInfo.Capture(creationFailure).Throw();
                throw new UnreachableException();
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

        public string DescribeWords(params (string Name, int ByteOffset)[] words)
        {
            return string.Join(
                ",",
                words.Select(word => word.Name + "=" + ReadInt64(word.ByteOffset).ToString(CultureInfo.InvariantCulture)));
        }

        private Exception? TryDelete()
        {
            Exception? lastFailure = null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    File.Delete(Path);
                    return null;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    lastFailure = exception;
                    if (attempt < 4)
                    {
                        Thread.Sleep(25 * (attempt + 1));
                    }
                }
            }

            return new IOException("Unable to delete mapped atomic test file after five attempts: " + Path, lastFailure);
        }
    }
}
