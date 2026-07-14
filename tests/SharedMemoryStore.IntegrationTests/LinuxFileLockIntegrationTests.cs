using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.IntegrationTests;

[SupportedOSPlatform("linux")]
public sealed class LinuxFileLockIntegrationTests
{
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("operation")]
    [InlineData("lifecycle")]
    [Trait("Category", "Integration")]
    public void DisposingLocalContenderDoesNotReleaseForeignProcessExclusion(string lockKind)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        PlatformResourceName resource = PlatformResourceName.Create(
            $"sms-linux-file-lock-{lockKind}-{Guid.NewGuid():N}");
        string path = lockKind switch
        {
            "operation" => resource.LinuxSynchronizationPath,
            "lifecycle" => resource.LinuxLifecycleLockPath,
            _ => throw new ArgumentOutOfRangeException(nameof(lockKind))
        };

        LinuxFileLock? holder = null;
        LinuxFileLock? contender = null;
        LinuxFileLock? sameThreadContender = null;
        try
        {
            Assert.Equal(
                StoreStatus.Success,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.Infinite, out holder));
            Assert.NotNull(holder);

            Assert.Equal(
                StoreStatus.StoreBusy,
                LinuxFileLock.TryAcquire(path, StoreWaitOptions.NoWait, out sameThreadContender));
            Assert.Null(sameThreadContender);

            StoreStatus contenderStatus = StoreStatus.UnknownFailure;
            Exception? contenderFailure = null;
            var contenderThread = new Thread(() =>
            {
                try
                {
                    contenderStatus = LinuxFileLock.TryAcquire(
                        path,
                        new StoreWaitOptions(TimeSpan.FromMilliseconds(100)),
                        out contender);
                }
                catch (Exception exception)
                {
                    contenderFailure = exception;
                }
            })
            {
                IsBackground = true,
                Name = "SharedMemoryStore local file-lock contender"
            };

            contenderThread.Start();
            Assert.True(contenderThread.Join(TimeSpan.FromSeconds(5)), "The local contender did not finish.");
            Assert.Null(contenderFailure);
            Assert.Equal(StoreStatus.StoreBusy, contenderStatus);
            Assert.Null(contender);

            Assert.Equal("RESULT StoreBusy", RunForeignProbe(path));

            holder.Dispose();
            holder = null;

            Assert.Equal("RESULT Success", RunForeignProbe(path));
        }
        finally
        {
            sameThreadContender?.Dispose();
            contender?.Dispose();
            holder?.Dispose();
            File.Delete(path);
        }
    }

    private static string RunForeignProbe(string path)
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
        startInfo.ArgumentList.Add("linux-file-lock-probe");
        startInfo.ArgumentList.Add(path);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the Linux file-lock probe.");
        if (!process.WaitForExit((int)AgentTimeout.TotalMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("The Linux file-lock probe did not finish.");
        }

        string output = process.StandardOutput.ReadToEnd().Trim();
        string error = process.StandardError.ReadToEnd().Trim();
        Assert.True(
            process.ExitCode == 0,
            $"Linux file-lock probe exited {process.ExitCode}. stdout={output} stderr={error}");
        return output;
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
            : throw new FileNotFoundException("Lock-free agent was not built.", path);
    }
}
