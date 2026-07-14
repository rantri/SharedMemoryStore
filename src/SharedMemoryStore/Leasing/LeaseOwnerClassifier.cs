using System.Diagnostics;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

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

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
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

    /// <summary>
    /// Classifies an exact participant identity. Unlike the legacy PID-only
    /// overload, this overload never treats PID existence alone as proof that
    /// the stored incarnation is live.
    /// </summary>
    public static LeaseOwnerClassification Classify(ParticipantIncarnation participant)
    {
        if (participant.ProcessId <= 0
            || participant.ProcessStartValue < 0
            || participant.IdentityKind is < LayoutV2Constants.IdentityUnknown
                or > LayoutV2Constants.IdentityLinuxProcStartTicks)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.UnsafeRecord, participant.ProcessId);
        }

        // A Linux PID has meaning only inside its PID namespace.  Prove that
        // the caller observes the exact namespace captured by the owner before
        // consulting /proc/<pid> or Process, otherwise the same numeric PID
        // could name an unrelated process (or appear absent) in this caller's
        // namespace.  Windows records must retain the protocol's zero value.
        if (OperatingSystem.IsLinux())
        {
            if (participant.PidNamespaceId == 0
                || !TryObserveLinuxPidNamespaceId(Environment.ProcessId, out ulong currentNamespaceId)
                || currentNamespaceId != participant.PidNamespaceId)
            {
                return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, participant.ProcessId);
            }
        }
        else if (participant.PidNamespaceId != 0)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.UnsafeRecord, participant.ProcessId);
        }

        if (participant.IdentityKind == LayoutV2Constants.IdentityUnknown)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, participant.ProcessId);
        }

        if (participant.ProcessStartValue == 0)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.UnsafeRecord, participant.ProcessId);
        }

        IdentityObservation observation = ObserveIdentity(
            participant.ProcessId,
            participant.IdentityKind,
            out long observedStartValue);
        if (observation == IdentityObservation.Missing)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, participant.ProcessId);
        }

        if (observation != IdentityObservation.Available)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, participant.ProcessId);
        }

        if (observedStartValue != participant.ProcessStartValue)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, participant.ProcessId);
        }

        return new LeaseOwnerClassification(
            participant.ProcessId == Environment.ProcessId
                ? LeaseOwnerKind.CurrentProcess
                : LeaseOwnerKind.OtherLiveProcess,
            participant.ProcessId);
    }

    /// <summary>
    /// Captures the current process identity used when publishing an Active
    /// participant record. Failure deliberately degrades registration to the
    /// Unknown identity kind, which remains usable but unrecoverable.
    /// </summary>
    internal static bool TryCaptureCurrentProcessIdentity(
        out int identityKind,
        out long processStartValue,
        out ulong pidNamespaceId)
    {
        pidNamespaceId = 0;
        identityKind = OperatingSystem.IsWindows()
            ? LayoutV2Constants.IdentityWindowsProcessCreationFileTime
            : OperatingSystem.IsLinux()
                ? LayoutV2Constants.IdentityLinuxProcStartTicks
                : LayoutV2Constants.IdentityUnknown;
        processStartValue = 0;
        if (identityKind == LayoutV2Constants.IdentityUnknown)
        {
            return false;
        }

        if (OperatingSystem.IsLinux()
            && !TryObserveLinuxPidNamespaceId(Environment.ProcessId, out pidNamespaceId))
        {
            identityKind = LayoutV2Constants.IdentityUnknown;
            processStartValue = 0;
            pidNamespaceId = 0;
            return false;
        }

        if (ObserveIdentity(Environment.ProcessId, identityKind, out processStartValue)
            == IdentityObservation.Available)
        {
            return true;
        }

        identityKind = LayoutV2Constants.IdentityUnknown;
        processStartValue = 0;
        return false;
    }

    internal static bool TryCaptureCurrentProcessIdentity(
        out int identityKind,
        out long processStartValue) =>
        TryCaptureCurrentProcessIdentity(
            out identityKind,
            out processStartValue,
            out _);

    /// <summary>
    /// Conservative fallback for a process stopped in Registering before it
    /// wrote a process-start identity. Only definite PID absence is stale;
    /// PID presence cannot distinguish reuse and is therefore unsupported.
    /// </summary>
    internal static LeaseOwnerClassification ClassifyPresenceOnly(
        int processId,
        ulong pidNamespaceId)
    {
        if (processId <= 0)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.UnsafeRecord, processId);
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, processId);
        }

        if (OperatingSystem.IsLinux())
        {
            if (pidNamespaceId == 0
                || !TryObserveLinuxPidNamespaceId(Environment.ProcessId, out ulong currentNamespaceId)
                || currentNamespaceId != pidNamespaceId)
            {
                return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, processId);
            }
        }
        else if (pidNamespaceId != 0)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, processId);
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, processId);
            }

            return new LeaseOwnerClassification(
                processId == Environment.ProcessId
                    ? LeaseOwnerKind.CurrentProcess
                    : LeaseOwnerKind.Unsupported,
                processId);
        }
        catch (ArgumentException)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, processId);
        }
        catch (InvalidOperationException)
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.StaleProcess, processId);
        }
        catch
        {
            return new LeaseOwnerClassification(LeaseOwnerKind.Unsupported, processId);
        }
    }

    /// <summary>
    /// Reads Linux's stable numeric PID-namespace inode token from the procfs
    /// namespace symlink (for example, <c>pid:[4026531836]</c>).  No PID/start
    /// liveness decision is made here.
    /// </summary>
    internal static bool TryObserveLinuxPidNamespaceId(
        int processId,
        out ulong pidNamespaceId)
    {
        pidNamespaceId = 0;
        if (!OperatingSystem.IsLinux() || processId <= 0)
        {
            return false;
        }

        try
        {
            string? target = new FileInfo($"/proc/{processId}/ns/pid").LinkTarget;
            const string prefix = "pid:[";
            if (target is null
                || !target.StartsWith(prefix, StringComparison.Ordinal)
                || target.Length <= prefix.Length + 1
                || target[^1] != ']'
                || !ulong.TryParse(
                    target.AsSpan(prefix.Length, target.Length - prefix.Length - 1),
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out pidNamespaceId)
                || pidNamespaceId == 0)
            {
                pidNamespaceId = 0;
                return false;
            }

            return true;
        }
        catch
        {
            pidNamespaceId = 0;
            return false;
        }
    }

    private static IdentityObservation ObserveIdentity(
        int processId,
        int identityKind,
        out long processStartValue)
    {
        processStartValue = 0;
        if (identityKind == LayoutV2Constants.IdentityWindowsProcessCreationFileTime)
        {
            return OperatingSystem.IsWindows()
                ? ObserveWindowsIdentity(processId, out processStartValue)
                : IdentityObservation.Unsupported;
        }

        if (identityKind == LayoutV2Constants.IdentityLinuxProcStartTicks)
        {
            return OperatingSystem.IsLinux()
                ? ObserveLinuxIdentity(processId, out processStartValue)
                : IdentityObservation.Unsupported;
        }

        return IdentityObservation.Unsupported;
    }

    private static IdentityObservation ObserveWindowsIdentity(
        int processId,
        out long processStartValue)
    {
        processStartValue = 0;
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return IdentityObservation.Missing;
            }

            processStartValue = process.StartTime.ToUniversalTime().ToFileTimeUtc();
            return processStartValue > 0
                ? IdentityObservation.Available
                : IdentityObservation.Unsupported;
        }
        catch (ArgumentException)
        {
            return IdentityObservation.Missing;
        }
        catch (InvalidOperationException)
        {
            return IdentityObservation.Missing;
        }
        catch (PlatformNotSupportedException)
        {
            return IdentityObservation.Unsupported;
        }
        catch
        {
            return IdentityObservation.Unsupported;
        }
    }

    private static IdentityObservation ObserveLinuxIdentity(
        int processId,
        out long processStartValue)
    {
        processStartValue = 0;
        string processDirectory = $"/proc/{processId}";
        try
        {
            string stat = File.ReadAllText($"{processDirectory}/stat");
            int commandEnd = stat.LastIndexOf(')');
            if (commandEnd < 0 || commandEnd + 2 >= stat.Length)
            {
                return IdentityObservation.Unsupported;
            }

            string[] fields = stat[(commandEnd + 2)..]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length <= 19
                || !long.TryParse(
                    fields[19],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out processStartValue)
                || processStartValue <= 0)
            {
                processStartValue = 0;
                return IdentityObservation.Unsupported;
            }

            return IdentityObservation.Available;
        }
        catch (FileNotFoundException)
        {
            return IdentityObservation.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return IdentityObservation.Missing;
        }
        catch (UnauthorizedAccessException)
        {
            return IdentityObservation.Unsupported;
        }
        catch (IOException)
        {
            return Directory.Exists(processDirectory)
                ? IdentityObservation.Unsupported
                : IdentityObservation.Missing;
        }
        catch
        {
            return IdentityObservation.Unsupported;
        }
    }

    private enum IdentityObservation
    {
        Available,
        Missing,
        Unsupported
    }
}
