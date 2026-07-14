using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeRawVisibilityIntegrationTests
{
    private const int SlotCount = 2;
    private const int MaxValueBytes = 2_048;
    private const int MaxDescriptorBytes = 48;
    private const int MaxKeyBytes = 16;
    private const int LeaseRecordCount = 32;
    private const int ParticipantRecordCount = 16;
    private const int Iterations = 256;
    private const int KeyCount = 4;
    private const int ReaderCount = 3;
    private static readonly TimeSpan WorkloadTimeout = TimeSpan.FromSeconds(75);

    [Theory]
    [InlineData(0x13579)]
    [InlineData(0x24680)]
    [Trait("Category", "Integration")]
    [Trait("Category", "RawVisibility")]
    public void ProductionNoOpFullProtocolPublicationIsVisibleAcrossReuse(int seed)
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-raw-visibility-{Guid.NewGuid():N}";
        using MemoryStore store = CreateStore(name);
        long startUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
        var commands = new List<string[]>(ReaderCount + 2);
        for (var reader = 0; reader < ReaderCount; reader++)
        {
            commands.Add(Command("raw-visibility-reader", name, seed, startUtcTicks));
        }

        commands.Add(Command("raw-visibility-remover", name, seed, startUtcTicks));
        commands.Add(Command("raw-visibility-publisher", name, seed, startUtcTicks));
        Process[] processes = commands.Select(StartAgent).ToArray();
        try
        {
            AgentResult[] results = WaitForAgents(processes, WorkloadTimeout);
            Assert.All(results, static result => Assert.True(
                result.ExitCode == 0,
                "Raw visibility agent failed."
                + Environment.NewLine
                + "exit=" + result.ExitCode.ToString(CultureInfo.InvariantCulture)
                + Environment.NewLine
                + "stdout: " + result.StandardOutput
                + Environment.NewLine
                + "stderr: " + result.StandardError));

            ResultPayload[] payloads = results.Select(ParseResult).ToArray();
            Assert.Equal(ReaderCount, payloads.Count(static result => result.Role == "reader"));
            Assert.Single(payloads, static result => result.Role == "remover");
            Assert.Single(payloads, static result => result.Role == "publisher");

            ResultPayload publisher = payloads.Single(static result => result.Role == "publisher");
            Assert.Equal(Iterations, publisher.Completed);
            Assert.Equal(Iterations, publisher.Observations);
            Assert.True(publisher.MinimumGeneration > 0);
            AssertAggressiveReuse(publisher);

            ResultPayload remover = payloads.Single(static result => result.Role == "remover");
            Assert.Equal(Iterations, remover.Completed);
            Assert.Equal(Iterations, remover.Observations);
            Assert.True(remover.MinimumGeneration > 0);
            AssertAggressiveReuse(remover);

            foreach (ResultPayload reader in payloads.Where(static result => result.Role == "reader"))
            {
                Assert.Equal(1, reader.Completed);
                Assert.True(reader.Observations > 0, "Every independent reader must validate at least one data generation.");
                Assert.True(reader.MinimumGeneration > 0);
                Assert.True(reader.MaximumGeneration >= reader.MinimumGeneration);
            }
        }
        finally
        {
            KillAll(processes);
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static MemoryStore CreateStore(string name)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            SlotCount,
            MaxValueBytes,
            MaxDescriptorBytes,
            MaxKeyBytes,
            LeaseRecordCount,
            ParticipantRecordCount,
            OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus open = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, open);
        return Assert.IsType<MemoryStore>(store);
    }

    private static void AssertAggressiveReuse(ResultPayload result)
    {
        long requiredAdvance = (Iterations / SlotCount) - 1;
        Assert.True(
            result.MaximumGeneration - result.MinimumGeneration >= requiredAdvance,
            $"{result.Role} observed insufficient physical reuse: min={result.MinimumGeneration}, "
            + $"max={result.MaximumGeneration}, required advance={requiredAdvance}.");
    }

    private static string[] Command(string command, string name, int seed, long startUtcTicks) =>
    [
        command,
        name,
        SlotCount.ToString(CultureInfo.InvariantCulture),
        MaxValueBytes.ToString(CultureInfo.InvariantCulture),
        MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
        MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
        LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
        ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
        Iterations.ToString(CultureInfo.InvariantCulture),
        KeyCount.ToString(CultureInfo.InvariantCulture),
        MaxValueBytes.ToString(CultureInfo.InvariantCulture),
        seed.ToString(CultureInfo.InvariantCulture),
        startUtcTicks.ToString(CultureInfo.InvariantCulture)
    ];

    private static AgentResult[] WaitForAgents(Process[] processes, TimeSpan timeout)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var results = new AgentResult[processes.Length];
        for (var index = 0; index < processes.Length; index++)
        {
            long remaining = deadline - Stopwatch.GetTimestamp();
            int milliseconds = remaining <= 0
                ? 0
                : (int)Math.Min(int.MaxValue, Math.Ceiling(remaining * 1_000d / Stopwatch.Frequency));
            Process process = processes[index];
            if (!process.WaitForExit(milliseconds))
            {
                throw new TimeoutException("Raw visibility agents exceeded their shared bounded timeout.");
            }

            results[index] = new AgentResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }

        return results;
    }

    private static ResultPayload ParseResult(AgentResult result)
    {
        string line = result.StandardOutput
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Single(static value => value.StartsWith("RESULT ", StringComparison.Ordinal));
        return JsonSerializer.Deserialize<ResultPayload>(line["RESULT ".Length..])
            ?? throw new Xunit.Sdk.XunitException("Raw visibility agent returned an empty result.");
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
            ?? throw new InvalidOperationException("Unable to start a raw visibility agent.");
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
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lock-free raw visibility agent was not built.", path);
    }

    private static void KillAll(IEnumerable<Process> processes)
    {
        foreach (Process process in processes)
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
                // Cleanup is best effort for a unique mapped-store name.
            }
        }
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private readonly record struct AgentResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed record ResultPayload(
        string Role,
        int Completed,
        long Observations,
        ulong Checksum,
        long MinimumGeneration,
        long MaximumGeneration);
}
