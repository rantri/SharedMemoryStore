using System.Diagnostics;
using System.Globalization;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.IntegrationTests.TestSupport;

internal sealed class LeaseOwnerProcessHarness : IDisposable
{
    private readonly Process _process;

    private LeaseOwnerProcessHarness(Process process, int processId)
    {
        _process = process;
        ProcessId = processId;
    }

    public int ProcessId { get; }

    public static LeaseOwnerProcessHarness StartLiveOwner(SharedMemoryStoreOptions options, int keyValue)
    {
        var process = StartTool(
            "live",
            options,
            keyValue.ToString(CultureInfo.InvariantCulture));
        var ready = ReadRequiredLine(process);
        var parts = ready.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0] != "READY")
        {
            throw new InvalidOperationException("Lease owner tool did not become ready: " + ready);
        }

        return new LeaseOwnerProcessHarness(process, int.Parse(parts[1], CultureInfo.InvariantCulture));
    }

    public static int CreateStaleLeases(SharedMemoryStoreOptions options, int firstKeyValue, int leaseCount)
    {
        using var process = StartTool(
            "stale",
            options,
            firstKeyValue.ToString(CultureInfo.InvariantCulture),
            leaseCount.ToString(CultureInfo.InvariantCulture));
        var ready = ReadRequiredLine(process);
        var parts = ready.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || parts[0] != "READY")
        {
            throw new InvalidOperationException("Lease owner tool did not create stale leases: " + ready);
        }

        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Lease owner tool did not exit after creating stale leases.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Lease owner tool failed with exit code " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
        }

        return int.Parse(parts[1], CultureInfo.InvariantCulture);
    }

    public bool CheckLeaseValid()
    {
        WriteCommand("CHECK");
        return string.Equals(ReadRequiredLine(_process), "VALID", StringComparison.Ordinal);
    }

    public StoreStatus Release()
    {
        WriteCommand("RELEASE");
        return Enum.Parse<StoreStatus>(ReadRequiredLine(_process));
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                WriteCommand("EXIT");
                if (!_process.WaitForExit(5_000))
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static Process StartTool(string mode, SharedMemoryStoreOptions options, params string[] extraArgs)
    {
        var toolAssembly = LocateToolAssembly();
        var arguments = new List<string>
        {
            "exec",
            toolAssembly,
            mode,
            options.Name,
            options.SlotCount.ToString(CultureInfo.InvariantCulture),
            options.MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            options.MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            options.MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            options.LeaseRecordCount.ToString(CultureInfo.InvariantCulture)
        };
        arguments.AddRange(extraArgs);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start lease owner tool process.");
    }

    private static string LocateToolAssembly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var root = FindRepositoryRoot();
        var path = Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LeaseOwnerTool",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LeaseOwnerTool.dll");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Lease owner tool assembly was not built.", path);
        }

        return path;
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

    private static string ReadRequiredLine(Process process)
    {
        var line = process.StandardOutput.ReadLine();
        if (!string.IsNullOrWhiteSpace(line))
        {
            return line;
        }

        var error = process.StandardError.ReadToEnd();
        throw new InvalidOperationException("Lease owner tool produced no output. stderr: " + error);
    }

    private void WriteCommand(string command)
    {
        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }
}
