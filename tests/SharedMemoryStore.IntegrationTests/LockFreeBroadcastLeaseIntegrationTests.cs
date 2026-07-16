using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeBroadcastLeaseIntegrationTests
{
    private const int ReaderCount = 12;
    private const int SlotCount = 1;
    private const int MaxValueBytes = 4_096;
    private const int MaxDescriptorBytes = 16;
    private const int MaxKeyBytes = 16;
    private const int LeaseRecordCount = 16;
    private const int ParticipantRecordCount = 16;
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(45);

    [Fact]
    [Trait("Category", "Integration")]
    public void TwelveProcessesHoldThroughLogicalRemoveThenFinalReleaseReclaimsExactlyOneSlot()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-broadcast-leases-{Guid.NewGuid():N}";
        byte[] key = [0x42, 0x52, 0x4f, 0x41, 0x44, 0x43, 0x41, 0x53, 0x54];
        byte[] value = CreatePayload();
        byte[] descriptor = [0x12, 0x34, 0x56, 0x78, 0x9a, 0xbc, 0xde, 0xf0];
        using var store = CreateStore(name);
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, value, descriptor));

        string directory = Path.Combine(Path.GetTempPath(), $"sms-v2-broadcast-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string releasePath = Path.Combine(directory, "release.all");
        string[] readyPaths = Enumerable.Range(0, ReaderCount)
            .Select(index => Path.Combine(directory, $"reader-{index}.ready"))
            .ToArray();
        Process[] readers = readyPaths
            .Select(readyPath => StartAgent(HoldCommand(name, key, value, descriptor, readyPath, releasePath)))
            .ToArray();

        try
        {
            WaitForReadyFiles(readyPaths, readers, AgentTimeout);

            // Every ready file is written only after its process has activated
            // and validated a lease. At this point twelve independent processes
            // simultaneously protect the same immutable generation.
            Assert.All(readers, static process => Assert.False(process.HasExited));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var controllerLease));
            Assert.Equal(value.Length, controllerLease.ValueLength);
            Assert.Equal(descriptor.Length, controllerLease.DescriptorLength);
            Assert.Equal(Checksum(value, descriptor), Checksum(controllerLease.ValueSpan, controllerLease.DescriptorSpan));
            Assert.Equal(StoreStatus.Success, controllerLease.Release());

            Assert.Equal(StoreStatus.RemovePending, store.TryRemove(key));
            Assert.Equal(StoreStatus.NotFound, store.TryAcquire(key, out var rejected));
            Assert.False(rejected.IsValid);
            Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish(key, [0xff]));
            Assert.Equal(StoreStatus.StoreFull, store.TryPublish([0x7f], [0x7f]));

            File.WriteAllText(releasePath, "release");
            AgentResult[] results = WaitForAgents(readers, AgentTimeout);
            Assert.All(results, static result =>
            {
                Assert.True(
                    result.ExitCode == 0,
                    "Agent exit code: "
                    + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + "stdout: "
                    + result.StandardOutput
                    + Environment.NewLine
                    + "stderr: "
                    + result.StandardError);
                Assert.Contains("OK lease-hold released=1", result.StandardOutput);
            });

            // The last exact lease release cooperatively performs one safe
            // physical reclaim. With one configured slot, successful
            // republish proves that no second slot or global maintenance owner
            // masked a leak.
            byte[] replacement = value.ToArray();
            for (var index = 0; index < replacement.Length; index++)
            {
                replacement[index] ^= 0xa5;
            }

            Assert.Equal(StoreStatus.Success, store.TryPublish(key, replacement, descriptor));
            Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out var replacementLease));
            Assert.Equal(
                Checksum(replacement, descriptor),
                Checksum(replacementLease.ValueSpan, replacementLease.DescriptorSpan));
            Assert.Equal(StoreStatus.Success, replacementLease.Release());
        }
        finally
        {
            try
            {
                File.WriteAllText(releasePath, "release");
            }
            catch
            {
                // Best effort before process termination.
            }

            foreach (Process reader in readers)
            {
                Kill(reader);
                reader.Dispose();
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Unique temporary artifacts can be reclaimed after a racing process exit.
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void LinuxDefaultBoundedColdOpenConvergesAcrossThreeTwelveProcessWaves()
    {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        string name = $"sms-v2-linux-open-contention-{Guid.NewGuid():N}";
        byte[] key = [0x4f, 0x50, 0x45, 0x4e];
        byte[] value = [0x62, 0x6f, 0x75, 0x6e, 0x64, 0x65, 0x64];
        byte[] descriptor = [0x01];
        using var store = CreateStore(name);
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, value, descriptor));

        // Every agent uses MemoryStore.TryCreateOrOpen(options), so each open
        // retains the public one-second default bound. Repeated waves also force
        // normal owner removal and any bounded-release marker reconciliation
        // before the next twelve-way attach burst.
        for (var wave = 0; wave < 3; wave++)
        {
            RunDefaultBoundedOpenWave(name, key, value, descriptor, wave);
            MemoryStore verifier = OpenStore(name);
            verifier.Dispose();
            Assert.Equal(
                1,
                File.ReadAllLines(PlatformResourceName.Create(name).LinuxOwnersPath)
                    .Count(static line => line.Trim().Length != 0));
        }
    }

    private static void RunDefaultBoundedOpenWave(
        string name,
        byte[] key,
        byte[] value,
        byte[] descriptor,
        int wave)
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"sms-v2-linux-open-wave-{wave}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string releasePath = Path.Combine(directory, "release.all");
        string[] readyPaths = Enumerable.Range(0, ReaderCount)
            .Select(index => Path.Combine(directory, $"reader-{index}.ready"))
            .ToArray();
        Process[] readers = [];
        try
        {
            readers = readyPaths
                .Select(readyPath => StartAgent(
                    HoldCommand(name, key, value, descriptor, readyPath, releasePath)))
                .ToArray();
            WaitForReadyFiles(readyPaths, readers, AgentTimeout);
            Assert.All(readers, static process => Assert.False(process.HasExited));
            Assert.Equal(
                ReaderCount + 1,
                File.ReadAllLines(PlatformResourceName.Create(name).LinuxOwnersPath)
                    .Count(static line => line.Trim().Length != 0));

            File.WriteAllText(releasePath, "release");
            AgentResult[] results = WaitForAgents(readers, AgentTimeout);
            Assert.All(results, static result =>
            {
                Assert.True(
                    result.ExitCode == 0,
                    "Default-bounded opener failed. Exit="
                    + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + "stdout: "
                    + result.StandardOutput
                    + Environment.NewLine
                    + "stderr: "
                    + result.StandardError);
                Assert.Contains("OK lease-hold released=1", result.StandardOutput);
            });
        }
        finally
        {
            try
            {
                File.WriteAllText(releasePath, "release");
            }
            catch
            {
                // Best effort before process termination.
            }

            foreach (Process reader in readers)
            {
                Kill(reader);
                reader.Dispose();
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Unique temporary artifacts can be reclaimed after a racing process exit.
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

    private static MemoryStore OpenStore(string name)
    {
        var options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            SlotCount,
            MaxValueBytes,
            MaxDescriptorBytes,
            MaxKeyBytes,
            LeaseRecordCount,
            ParticipantRecordCount,
            OpenMode.OpenExisting,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

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
        Convert.ToHexString(value),
        Convert.ToHexString(descriptor),
        readyPath,
        releasePath
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
                throw new Xunit.Sdk.XunitException(
                    "Reader exited before the lease barrier. Exit="
                    + failed.ExitCode.ToString(CultureInfo.InvariantCulture)
                    + Environment.NewLine
                    + failed.StandardOutput.ReadToEnd()
                    + Environment.NewLine
                    + failed.StandardError.ReadToEnd());
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException("Twelve readers did not acquire their leases before the barrier timeout.");
            }

            spin.SpinOnce();
        }
    }

    private static AgentResult[] WaitForAgents(IReadOnlyList<Process> processes, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var results = new AgentResult[processes.Count];
        for (var index = 0; index < processes.Count; index++)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            int milliseconds = remaining <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1000d / Stopwatch.Frequency));
            Process process = processes[index];
            if (!process.WaitForExit(milliseconds))
            {
                throw new TimeoutException("Broadcast reader agents exceeded their shared timeout.");
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
            ?? throw new InvalidOperationException("Unable to start the lock-free lease agent.");
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
            throw new FileNotFoundException("Lock-free lease agent was not built.", path);
        }

        return path;
    }

    private static byte[] CreatePayload()
    {
        var payload = new byte[MaxValueBytes];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 31) ^ (index >> 3));
        }

        return payload;
    }

    private static ulong Checksum(ReadOnlySpan<byte> value, ReadOnlySpan<byte> descriptor)
    {
        ulong checksum = 14_695_981_039_346_656_037UL;
        foreach (byte item in value)
        {
            checksum = unchecked((checksum ^ item) * 1_099_511_628_211UL);
        }

        foreach (byte item in descriptor)
        {
            checksum = unchecked((checksum ^ item) * 1_099_511_628_211UL);
        }

        return checksum;
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

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct AgentResult(int ExitCode, string StandardOutput, string StandardError);
}
