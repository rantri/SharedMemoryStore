using System.Diagnostics;
using SharedMemoryStore.Leasing;

namespace SharedMemoryStore.UnitTests;

public sealed class LeaseOwnerClassifierCrossPlatformTests
{
    [Fact]
    public void CurrentProcessIsClassifiedAsCurrentOwner()
    {
        var owner = LeaseOwnerClassifier.Classify(Environment.ProcessId);

        Assert.Equal(LeaseOwnerKind.CurrentProcess, owner.Kind);
        Assert.True(owner.IsRecoverable(recoverCurrentProcessLeases: true));
        Assert.False(owner.IsRecoverable(recoverCurrentProcessLeases: false));
    }

    [Fact]
    public void MissingProcessIsClassifiedAsStaleOnSupportedHosts()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        var owner = LeaseOwnerClassifier.Classify(int.MaxValue);

        Assert.Equal(LeaseOwnerKind.StaleProcess, owner.Kind);
        Assert.True(owner.IsRecoverable(recoverCurrentProcessLeases: false));
    }

    [Fact]
    public void LiveProcessIsClassifiedAsActiveOnSupportedHosts()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        using var process = StartLiveProcess();
        try
        {
            var owner = LeaseOwnerClassifier.Classify(process.Id);

            Assert.Equal(LeaseOwnerKind.OtherLiveProcess, owner.Kind);
            Assert.False(owner.IsRecoverable(recoverCurrentProcessLeases: true));
        }
        finally
        {
            Stop(process);
        }
    }

    private static Process StartLiveProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("powershell", "-NoProfile -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("sh", "-c \"sleep 30\"");

        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start test owner process.");
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
