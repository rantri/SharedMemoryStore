namespace SharedMemoryStore.LockFree;

/// <summary>
/// Stable, double-read snapshot of one mapped participant-record incarnation.
/// The compact token identifies the record and incarnation; PID/start identity
/// is retained separately so PID reuse can be classified conservatively.
/// </summary>
internal readonly record struct ParticipantIncarnation(
    int RecordIndex,
    int Generation,
    ulong Token,
    int State,
    int ProcessId,
    int IdentityKind,
    long ProcessStartValue,
    long OpenSequence,
    ulong PidNamespaceId,
    int ReservedValue,
    long Control);

/// <summary>Outcome of stabilizing and classifying an exact participant token.</summary>
internal enum ParticipantClassificationKind
{
    CurrentProcess,
    Live,
    Stale,
    Unsupported,
    Inconsistent,
    Changing
}

/// <summary>Classification paired with the exact snapshot that justified it.</summary>
internal readonly record struct ParticipantClassification(
    ParticipantClassificationKind Kind,
    ParticipantIncarnation Incarnation)
{
    internal bool IsRecoverable(bool recoverCurrentProcess) =>
        Kind == ParticipantClassificationKind.Stale
        || (recoverCurrentProcess && Kind == ParticipantClassificationKind.CurrentProcess);
}

/// <summary>Record-local participant transition result used by recovery/help callers.</summary>
internal enum ParticipantTransitionResult
{
    Succeeded,
    AlreadyCompleted,
    ReferencesRemain,
    LiveOwner,
    Unsupported,
    Inconsistent,
    Changed
}
