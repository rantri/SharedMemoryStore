using System.Diagnostics;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

internal enum ParticipantOwnerKind
{
    CurrentProcess,
    OtherLiveProcess,
    StaleProcess,
    Unsupported,
    UnsafeRecord
}

internal readonly record struct ParticipantOwnerClassification(
    ParticipantOwnerKind Kind,
    int OwnerProcessId);

/// <summary>
/// Conservatively classifies one exact SMS2 participant incarnation. Numeric
/// PID existence alone is never accepted as proof for an Active participant;
/// exact process-start and PID-namespace evidence are required.
/// </summary>
internal static class ParticipantOwnerClassifier
{
    internal static ParticipantOwnerClassification Classify(
        ParticipantIncarnation participant)
    {
        if (participant.ProcessId <= 0
            || participant.ProcessStartValue < 0
            || participant.IdentityKind is < LayoutV2Constants.IdentityUnknown
                or > LayoutV2Constants.IdentityLinuxProcStartTicks)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.UnsafeRecord,
                participant.ProcessId);
        }

        // A Linux PID has meaning only inside its PID namespace. Prove that
        // the caller observes the exact namespace captured by the owner before
        // consulting /proc/<pid>; otherwise the same numeric PID could name an
        // unrelated process or appear absent. Windows records retain zero.
        if (OperatingSystem.IsLinux())
        {
            if (participant.PidNamespaceId == 0
                || !TryObserveLinuxPidNamespaceId(
                    Environment.ProcessId,
                    out ulong currentNamespaceId)
                || currentNamespaceId != participant.PidNamespaceId)
            {
                return new ParticipantOwnerClassification(
                    ParticipantOwnerKind.Unsupported,
                    participant.ProcessId);
            }
        }
        else if (participant.PidNamespaceId != 0)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.UnsafeRecord,
                participant.ProcessId);
        }

        if (participant.IdentityKind == LayoutV2Constants.IdentityUnknown)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.Unsupported,
                participant.ProcessId);
        }

        if (participant.ProcessStartValue == 0)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.UnsafeRecord,
                participant.ProcessId);
        }

        IdentityObservation observation = ObserveIdentity(
            participant.ProcessId,
            participant.IdentityKind,
            out long observedStartValue);
        if (observation == IdentityObservation.Missing)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.StaleProcess,
                participant.ProcessId);
        }

        if (observation != IdentityObservation.Available)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.Unsupported,
                participant.ProcessId);
        }

        if (observedStartValue != participant.ProcessStartValue)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.StaleProcess,
                participant.ProcessId);
        }

        return new ParticipantOwnerClassification(
            participant.ProcessId == Environment.ProcessId
                ? ParticipantOwnerKind.CurrentProcess
                : ParticipantOwnerKind.OtherLiveProcess,
            participant.ProcessId);
    }

    /// <summary>
    /// Captures the current process identity published by an Active SMS2
    /// participant. Failure deliberately degrades registration to Unknown,
    /// which remains usable but cannot authorize stale-owner recovery.
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
            && !TryObserveLinuxPidNamespaceId(
                Environment.ProcessId,
                out pidNamespaceId))
        {
            identityKind = LayoutV2Constants.IdentityUnknown;
            processStartValue = 0;
            pidNamespaceId = 0;
            return false;
        }

        if (ObserveIdentity(
                Environment.ProcessId,
                identityKind,
                out processStartValue) == IdentityObservation.Available)
        {
            return true;
        }

        identityKind = LayoutV2Constants.IdentityUnknown;
        processStartValue = 0;
        return false;
    }

    /// <summary>
    /// Conservative fallback for a process stopped in Registering before it
    /// release-publishes coherent ordinary identity fields. Only definite PID
    /// absence is stale; a present noncurrent PID is ambiguous and preserved.
    /// </summary>
    internal static ParticipantOwnerClassification ClassifyPresenceOnly(
        int processId,
        ulong storePidNamespaceId)
    {
        if (processId <= 0)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.UnsafeRecord,
                processId);
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.Unsupported,
                processId);
        }

        if (OperatingSystem.IsLinux())
        {
            if (storePidNamespaceId == 0
                || !TryObserveLinuxPidNamespaceId(
                    Environment.ProcessId,
                    out ulong currentNamespaceId)
                || currentNamespaceId != storePidNamespaceId)
            {
                return new ParticipantOwnerClassification(
                    ParticipantOwnerKind.Unsupported,
                    processId);
            }
        }
        else if (storePidNamespaceId != 0)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.Unsupported,
                processId);
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return new ParticipantOwnerClassification(
                    ParticipantOwnerKind.StaleProcess,
                    processId);
            }

            return new ParticipantOwnerClassification(
                processId == Environment.ProcessId
                    ? ParticipantOwnerKind.CurrentProcess
                    : ParticipantOwnerKind.Unsupported,
                processId);
        }
        catch (ArgumentException)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.StaleProcess,
                processId);
        }
        catch (InvalidOperationException)
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.StaleProcess,
                processId);
        }
        catch
        {
            return new ParticipantOwnerClassification(
                ParticipantOwnerKind.Unsupported,
                processId);
        }
    }

    /// <summary>
    /// Reads Linux's stable numeric PID-namespace inode token from the procfs
    /// namespace symlink. No owner-liveness decision is made here.
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
