using System.Diagnostics;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.LockFree;

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

    [Fact]
    public void ExactParticipantIdentityDistinguishesPidReuseAndUnknownIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(LeaseOwnerClassifier.TryCaptureCurrentProcessIdentity(
            out int identityKind,
            out long processStartValue,
            out ulong pidNamespaceId));

        var current = new ParticipantIncarnation(
            RecordIndex: 0,
            Generation: 1,
            Token: 1,
            State: LayoutV2Constants.ParticipantActive,
            ProcessId: Environment.ProcessId,
            IdentityKind: identityKind,
            ProcessStartValue: processStartValue,
            OpenSequence: 1,
            PidNamespaceId: pidNamespaceId,
            ReservedValue: 0,
            Control: 0);

        Assert.Equal(LeaseOwnerKind.CurrentProcess, LeaseOwnerClassifier.Classify(current).Kind);

        long reusedStartValue = processStartValue == long.MaxValue
            ? processStartValue - 1
            : processStartValue + 1;
        Assert.Equal(
            LeaseOwnerKind.StaleProcess,
            LeaseOwnerClassifier.Classify(current with { ProcessStartValue = reusedStartValue }).Kind);
        Assert.Equal(
            LeaseOwnerKind.Unsupported,
            LeaseOwnerClassifier.Classify(current with
            {
                IdentityKind = LayoutV2Constants.IdentityUnknown,
                ProcessStartValue = 0
            }).Kind);
        Assert.Equal(
            LeaseOwnerKind.UnsafeRecord,
            LeaseOwnerClassifier.Classify(current with { ProcessId = 0 }).Kind);
    }

    [Fact]
    public void LinuxParticipantClassificationRequiresExactCurrentPidNamespaceBeforePidLookup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(LeaseOwnerClassifier.TryCaptureCurrentProcessIdentity(
            out int identityKind,
            out long processStartValue,
            out ulong pidNamespaceId));
        Assert.NotEqual(0UL, pidNamespaceId);

        var current = new ParticipantIncarnation(
            RecordIndex: 0,
            Generation: 1,
            Token: 1,
            State: LayoutV2Constants.ParticipantActive,
            ProcessId: Environment.ProcessId,
            IdentityKind: identityKind,
            ProcessStartValue: processStartValue,
            OpenSequence: 1,
            PidNamespaceId: pidNamespaceId,
            ReservedValue: 0,
            Control: 0);

        Assert.Equal(LeaseOwnerKind.CurrentProcess, LeaseOwnerClassifier.Classify(current).Kind);
        Assert.Equal(
            LeaseOwnerKind.Unsupported,
            LeaseOwnerClassifier.Classify(current with { PidNamespaceId = 0 }).Kind);

        ulong differentNamespace = pidNamespaceId == ulong.MaxValue
            ? pidNamespaceId - 1
            : pidNamespaceId + 1;
        Assert.Equal(
            LeaseOwnerKind.Unsupported,
            LeaseOwnerClassifier.Classify(current with
            {
                ProcessId = int.MaxValue,
                PidNamespaceId = differentNamespace
            }).Kind);
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
