using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeChurnIntegrationTests
{
    private const int SlotCount = 64;
    private const int MaxValueBytes = 16;
    private const int MaxDescriptorBytes = 0;
    private const int MaxKeyBytes = 8;
    private const int LeaseRecordCount = 128;
    private const int ParticipantRecordCount = 8;
    private const int WorkerCount = 2;
    private const int DefaultIterationsPerWorker = 1_024;
    private static readonly int IterationsPerWorker = GetIterationsPerWorker();
    private static readonly TimeSpan WorkloadTimeout = GetWorkloadTimeout();

    [Fact]
    [Trait("Category", "Integration")]
    public void CollisionHeavyMultiProcessRemoveReuseRestoresCapacityAndKeepsLateLatencyBounded()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-collision-churn-{Guid.NewGuid():N}";
        byte[][] collisionKeys = GenerateCanonicalBucketCollisions(SlotCount, targetBucket: 0);
        Assert.Equal(SlotCount, collisionKeys.Length);
        Assert.All(collisionKeys, key => Assert.Equal(0, CanonicalBucket(key)));

        using var store = CreateStore(name);
        string directory = Path.Combine(Path.GetTempPath(), $"sms-v2-churn-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string goPath = Path.Combine(directory, "go");
        string[] readyPaths = Enumerable.Range(0, WorkerCount)
            .Select(index => Path.Combine(directory, $"worker-{index}.ready"))
            .ToArray();
        Process[] workers = Enumerable.Range(0, WorkerCount)
            .Select(worker => StartAgent(ChurnCommand(
                name,
                worker,
                collisionKeys.Where((_, index) => index % WorkerCount == worker).ToArray(),
                readyPaths[worker],
                goPath)))
            .ToArray();

        try
        {
            WaitForReadyFiles(readyPaths, workers, WorkloadTimeout);
            File.WriteAllText(goPath, "go");
            ChurnResult[] results = WaitForWorkers(workers, WorkloadTimeout)
                .Select(ParseResult)
                .ToArray();

            Assert.True(results.Length == WorkerCount);
            Assert.All(results, result =>
            {
                Assert.Equal(IterationsPerWorker, result.Iterations);
                Assert.Equal(SlotCount / WorkerCount, result.CollisionKeyCount);
                Assert.InRange(result.RemovePendingCount, 0, result.Iterations);
                AssertLatencyStable(result.EarlyPublishP99Ticks, result.LatePublishP99Ticks, "publish", result.WorkerId);
                AssertLatencyStable(result.EarlyMissingP99Ticks, result.LateMissingP99Ticks, "missing", result.WorkerId);
            });

            // Every churn lifecycle ended absent. Filling all configured slots
            // with keys sharing one canonical bucket proves exact reclamation,
            // overflow-directory reuse, and absence of owner-controlled leaks.
            for (var index = 0; index < collisionKeys.Length; index++)
            {
                Assert.Equal(StoreStatus.Success, store.TryPublish(collisionKeys[index], [(byte)index]));
            }

            Assert.Equal(StoreStatus.StoreFull, store.TryPublish([0xff], [0xff]));
            for (var index = 0; index < collisionKeys.Length; index++)
            {
                Assert.Equal(StoreStatus.Success, store.TryAcquire(collisionKeys[index], out var lease));
                Assert.Equal((byte)index, lease.ValueSpan[0]);
                Assert.Equal(StoreStatus.Success, lease.Release());
                Assert.Equal(StoreStatus.Success, store.TryRemove(collisionKeys[index]));
            }

            Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out var final));
            Assert.Equal(SlotCount, final.FreeSlotCount);
            Assert.Equal(0, final.PublishedSlotCount);
            Assert.Equal(0, final.PendingRemovalCount);
            Assert.Equal(0, final.ActiveLeaseCount);
            Assert.Equal(0, final.ActiveReservationCount);
            Assert.Equal(0, final.InitializingSlotCount);
            Assert.Equal(0, final.ReservedSlotCount);
            Assert.Equal(0, final.ReclaimingSlotCount);
            Assert.Equal(0, final.PrimaryDirectoryOccupancy);
            Assert.Equal(0, final.SpilledBucketCount);
            Assert.Equal(0, final.OverflowDirectoryOccupancy);
        }
        finally
        {
            try
            {
                File.WriteAllText(goPath, "go");
            }
            catch
            {
                // Best effort before worker termination.
            }

            foreach (Process worker in workers)
            {
                Kill(worker);
                worker.Dispose();
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Unique temporary artifacts can be reclaimed after process exit.
            }
        }
    }

    private static MemoryStore CreateStore(string name)
    {
        var options = SharedMemoryStoreOptions.CreateLockFree(
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

    private static string[] ChurnCommand(
        string name,
        int workerId,
        byte[][] keys,
        string readyPath,
        string goPath) =>
    [
        "churn-worker",
        name,
        SlotCount.ToString(CultureInfo.InvariantCulture),
        MaxValueBytes.ToString(CultureInfo.InvariantCulture),
        MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
        MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
        LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
        ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
        workerId.ToString(CultureInfo.InvariantCulture),
        IterationsPerWorker.ToString(CultureInfo.InvariantCulture),
        string.Join(';', keys.Select(Convert.ToHexString)),
        readyPath,
        goPath
    ];

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
                throw Failure(failed, "Churn worker exited before the start barrier.");
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Churn workers did not reach the start barrier.");
            }

            spin.SpinOnce();
        }
    }

    private static AgentOutput[] WaitForWorkers(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var results = new AgentOutput[processes.Count];
        for (var index = 0; index < processes.Count; index++)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            int milliseconds = remaining <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
            Process process = processes[index];
            if (!process.WaitForExit(milliseconds))
            {
                throw new TimeoutException("Collision churn workers exceeded their shared bounded timeout.");
            }

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (process.ExitCode != 0)
            {
                throw new Xunit.Sdk.XunitException(
                    $"Churn worker exit={process.ExitCode}{Environment.NewLine}stdout: {stdout}{Environment.NewLine}stderr: {stderr}");
            }

            results[index] = new AgentOutput(stdout, stderr);
        }

        return results;
    }

    private static ChurnResult ParseResult(AgentOutput output)
    {
        string line = output.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("RESULT ", StringComparison.Ordinal));
        ChurnResult? result = JsonSerializer.Deserialize<ChurnResult>(line["RESULT ".Length..]);
        return result ?? throw new Xunit.Sdk.XunitException("Churn worker returned an empty result.");
    }

    private static void AssertLatencyStable(long early, long late, string operation, int worker)
    {
        Assert.True(early > 0 && late > 0);
        long timerSlack = Math.Max(1, Stopwatch.Frequency / 1_000); // one millisecond for timer quantization/scheduling
        long allowed = Math.Max(checked(early * 2), checked(early + timerSlack));
        Assert.True(
            late <= allowed,
            $"Worker {worker} {operation} p99 regressed: early={early} ticks, late={late} ticks, allowed={allowed} ticks.");
    }

    private static byte[][] GenerateCanonicalBucketCollisions(int count, int targetBucket)
    {
        var keys = new List<byte[]>(count);
        for (long candidate = 0; keys.Count < count && candidate < 10_000_000; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            if (CanonicalBucket(key) == targetBucket)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static int CanonicalBucket(ReadOnlySpan<byte> key)
    {
        int primaryLanes = NextPowerOfTwo(Math.Max(32, SlotCount * 4));
        int bucketMask = (primaryLanes / 8) - 1;
        return (int)(Mix(Hash(key)) & (uint)bucketMask);
    }

    private static ulong Hash(ReadOnlySpan<byte> key)
    {
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (byte value in key)
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }

        return hash;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
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
            ?? throw new InvalidOperationException("Unable to start collision churn worker.");
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
        return File.Exists(path) ? path : throw new FileNotFoundException("Lock-free churn agent was not built.", path);
    }

    private static Xunit.Sdk.XunitException Failure(Process process, string message) =>
        new(
            message
            + Environment.NewLine
            + process.StandardOutput.ReadToEnd()
            + Environment.NewLine
            + process.StandardError.ReadToEnd());

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

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static int GetIterationsPerWorker()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_LOCK_FREE_CHURN_CYCLES");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultIterationsPerWorker;
        }

        if (!long.TryParse(configured, NumberStyles.None, CultureInfo.InvariantCulture, out long totalCycles)
            || totalCycles < WorkerCount * 512L
            || totalCycles > WorkerCount * 100_000_000L)
        {
            throw new InvalidOperationException(
                "SMS_LOCK_FREE_CHURN_CYCLES must be an integer between "
                + (WorkerCount * 512L).ToString(CultureInfo.InvariantCulture)
                + " and "
                + (WorkerCount * 100_000_000L).ToString(CultureInfo.InvariantCulture)
                + ".");
        }

        return checked((int)((totalCycles + WorkerCount - 1) / WorkerCount));
    }

    private static TimeSpan GetWorkloadTimeout()
    {
        // Retain the short-test floor while allowing the configured qualification
        // tiers to execute millions of full publish/acquire/release/remove cycles.
        double scaledSeconds = 60d + (IterationsPerWorker / 20_000d);
        return TimeSpan.FromSeconds(Math.Min(7_200d, scaledSeconds));
    }

    private readonly record struct AgentOutput(string StandardOutput, string StandardError);

    private sealed record ChurnResult(
        int WorkerId,
        int Iterations,
        int CollisionKeyCount,
        int RemovePendingCount,
        long EarlyPublishP99Ticks,
        long LatePublishP99Ticks,
        long EarlyMissingP99Ticks,
        long LateMissingP99Ticks);
}
