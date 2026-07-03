using System.Diagnostics;

namespace SharedMemoryStore.Leasing;

internal enum LeaseOwnerKind
{
    CurrentProcess,
    OtherLiveProcess,
    StaleProcess,
    Unsupported,
    UnsafeRecord
}

internal readonly record struct LeaseOwnerClassification(LeaseOwnerKind Kind, int OwnerProcessId)
{
    public bool IsRecoverable(bool recoverCurrentProcessLeases)
    {
        return Kind == LeaseOwnerKind.StaleProcess
            || (recoverCurrentProcessLeases && Kind == LeaseOwnerKind.CurrentProcess);
    }
}

internal static class LeaseOwnerClassifier
{
    public static LeaseOwnerClassification Classify(int ownerProcessId)
    {
        if (ownerProcessId <= 0)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, ownerProcessId);
        }

        if (ownerProcessId == Environment.ProcessId)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.CurrentProcess, ownerProcessId);
        }

        if (!OperatingSystem.IsWindows())
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, ownerProcessId);
        }

        try
        {
            using var process = Process.GetProcessById(ownerProcessId);
            return process.HasExited
                ? new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, ownerProcessId)
                : new LeaseOwnerClassification(LeaseOwnerKind.OtherLiveProcess, ownerProcessId);
        }
        catch (ArgumentException)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, ownerProcessId);
        }
        catch (InvalidOperationException)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, ownerProcessId);
        }
        catch (PlatformNotSupportedException)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, ownerProcessId);
        }
        catch
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, ownerProcessId);
        }
    }
}
