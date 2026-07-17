using System.Diagnostics;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.UnitTests;

public sealed class ParticipantOwnerClassifierCrossPlatformTests
{
    [Fact]
    public void ExactParticipantIdentityDistinguishesPidReuseAndUnknownIdentity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(ParticipantOwnerClassifier.TryCaptureCurrentProcessIdentity(
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

        Assert.Equal(
            ParticipantOwnerKind.CurrentProcess,
            ParticipantOwnerClassifier.Classify(current).Kind);

        long reusedStartValue = processStartValue == long.MaxValue
            ? processStartValue - 1
            : processStartValue + 1;
        Assert.Equal(
            ParticipantOwnerKind.StaleProcess,
            ParticipantOwnerClassifier.Classify(
                current with { ProcessStartValue = reusedStartValue }).Kind);
        Assert.Equal(
            ParticipantOwnerKind.Unsupported,
            ParticipantOwnerClassifier.Classify(current with
            {
                IdentityKind = LayoutV2Constants.IdentityUnknown,
                ProcessStartValue = 0
            }).Kind);
        Assert.Equal(
            ParticipantOwnerKind.UnsafeRecord,
            ParticipantOwnerClassifier.Classify(current with { ProcessId = 0 }).Kind);
    }

    [Fact]
    public void RegisteringPresenceOnlyPreservesAmbiguousLivePid()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(ParticipantOwnerClassifier.TryCaptureCurrentProcessIdentity(
            out _,
            out _,
            out ulong pidNamespaceId));
        Assert.Equal(
            ParticipantOwnerKind.CurrentProcess,
            ParticipantOwnerClassifier.ClassifyPresenceOnly(
                Environment.ProcessId,
                pidNamespaceId).Kind);
        Assert.Equal(
            ParticipantOwnerKind.StaleProcess,
            ParticipantOwnerClassifier.ClassifyPresenceOnly(
                int.MaxValue,
                pidNamespaceId).Kind);

        using Process process = StartLiveProcess();
        try
        {
            Assert.Equal(
                ParticipantOwnerKind.Unsupported,
                ParticipantOwnerClassifier.ClassifyPresenceOnly(
                    process.Id,
                    pidNamespaceId).Kind);
        }
        finally
        {
            Stop(process);
        }
    }

    [Fact]
    public void LinuxParticipantRequiresExactCurrentPidNamespaceBeforePidLookup()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Assert.True(ParticipantOwnerClassifier.TryCaptureCurrentProcessIdentity(
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

        Assert.Equal(
            ParticipantOwnerKind.CurrentProcess,
            ParticipantOwnerClassifier.Classify(current).Kind);
        Assert.Equal(
            ParticipantOwnerKind.Unsupported,
            ParticipantOwnerClassifier.Classify(
                current with { PidNamespaceId = 0 }).Kind);

        ulong differentNamespace = pidNamespaceId == ulong.MaxValue
            ? pidNamespaceId - 1
            : pidNamespaceId + 1;
        Assert.Equal(
            ParticipantOwnerKind.Unsupported,
            ParticipantOwnerClassifier.Classify(current with
            {
                ProcessId = int.MaxValue,
                PidNamespaceId = differentNamespace
            }).Kind);
    }

    [Fact]
    public void WindowsParticipantRequiresProtocolZeroPidNamespace()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(ParticipantOwnerClassifier.TryCaptureCurrentProcessIdentity(
            out int identityKind,
            out long processStartValue,
            out ulong pidNamespaceId));
        Assert.Equal(0UL, pidNamespaceId);
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

        Assert.Equal(
            ParticipantOwnerKind.CurrentProcess,
            ParticipantOwnerClassifier.Classify(current).Kind);
        Assert.Equal(
            ParticipantOwnerKind.UnsafeRecord,
            ParticipantOwnerClassifier.Classify(
                current with { PidNamespaceId = 1 }).Kind);
    }

    private static Process StartLiveProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(
                "powershell",
                "-NoProfile -Command Start-Sleep -Seconds 30")
            : new ProcessStartInfo("sh", "-c \"sleep 30\"");

        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start test owner process.");
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
