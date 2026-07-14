namespace SharedMemoryStore.LockFree;

/// <summary>
/// Per-handle, process-local observational counters for layout-v2 protocol
/// activity. No protocol decision reads these values, so lost or delayed
/// updates can affect diagnostics only, never correctness or progress.
/// </summary>
internal sealed class LockFreeTelemetry
{
    private long _overflowScanCount;
    private int _lastObservedOverflowScanLength;
    private int _maxObservedOverflowScanLength;
    private long _casRetryCount;
    private long _helpedTransitionCount;
    private long _contentionBudgetExhaustionCount;
    private long _invalidTokenCount;
    private long _staleTokenCount;
    private long _recoveryAttemptCount;
    private long _recoveredTransitionCount;
    private long _currentOwnerClassificationCount;
    private long _liveOwnerClassificationCount;
    private long _staleOwnerClassificationCount;
    private long _unsupportedOwnerClassificationCount;
    private long _inconsistentOwnerClassificationCount;
    private long _changingOwnerClassificationCount;

    internal long OverflowScanCount => Volatile.Read(ref _overflowScanCount);
    internal int LastObservedOverflowScanLength => Volatile.Read(ref _lastObservedOverflowScanLength);
    internal int MaxObservedOverflowScanLength => Volatile.Read(ref _maxObservedOverflowScanLength);
    internal long CasRetryCount => Volatile.Read(ref _casRetryCount);
    internal long HelpedTransitionCount => Volatile.Read(ref _helpedTransitionCount);
    internal long ContentionBudgetExhaustionCount => Volatile.Read(ref _contentionBudgetExhaustionCount);
    internal long InvalidTokenCount => Volatile.Read(ref _invalidTokenCount);
    internal long StaleTokenCount => Volatile.Read(ref _staleTokenCount);
    internal long RecoveryAttemptCount => Volatile.Read(ref _recoveryAttemptCount);
    internal long RecoveredTransitionCount => Volatile.Read(ref _recoveredTransitionCount);
    internal long CurrentOwnerClassificationCount => Volatile.Read(ref _currentOwnerClassificationCount);
    internal long LiveOwnerClassificationCount => Volatile.Read(ref _liveOwnerClassificationCount);
    internal long StaleOwnerClassificationCount => Volatile.Read(ref _staleOwnerClassificationCount);
    internal long UnsupportedOwnerClassificationCount => Volatile.Read(ref _unsupportedOwnerClassificationCount);
    internal long InconsistentOwnerClassificationCount => Volatile.Read(ref _inconsistentOwnerClassificationCount);
    internal long ChangingOwnerClassificationCount => Volatile.Read(ref _changingOwnerClassificationCount);

    internal void RecordOverflowScan(int scannedCellCount)
    {
        if (scannedCellCount <= 0)
        {
            return;
        }

        Interlocked.Increment(ref _overflowScanCount);
        Volatile.Write(ref _lastObservedOverflowScanLength, scannedCellCount);

        int observed = Volatile.Read(ref _maxObservedOverflowScanLength);
        while (scannedCellCount > observed)
        {
            int exchanged = Interlocked.CompareExchange(
                ref _maxObservedOverflowScanLength,
                scannedCellCount,
                observed);
            if (exchanged == observed)
            {
                break;
            }

            observed = exchanged;
        }
    }

    internal void RecordCasLoss(int count = 1) => AddPositive(ref _casRetryCount, count);

    internal void RecordHelpedTransition(int count = 1) =>
        AddPositive(ref _helpedTransitionCount, count);

    internal void RecordContentionBudgetExhaustion() =>
        Interlocked.Increment(ref _contentionBudgetExhaustionCount);

    internal void RecordInvalidToken(bool stale)
    {
        if (stale)
        {
            Interlocked.Increment(ref _staleTokenCount);
        }
        else
        {
            Interlocked.Increment(ref _invalidTokenCount);
        }
    }

    internal void RecordRecoveryAttempt(int count = 1) =>
        AddPositive(ref _recoveryAttemptCount, count);

    internal void RecordRecoveredTransition(int count = 1) =>
        AddPositive(ref _recoveredTransitionCount, count);

    internal void RecordOwnerClassification(ParticipantClassificationKind kind)
    {
        switch (kind)
        {
            case ParticipantClassificationKind.CurrentProcess:
                Interlocked.Increment(ref _currentOwnerClassificationCount);
                break;
            case ParticipantClassificationKind.Live:
                Interlocked.Increment(ref _liveOwnerClassificationCount);
                break;
            case ParticipantClassificationKind.Stale:
                Interlocked.Increment(ref _staleOwnerClassificationCount);
                break;
            case ParticipantClassificationKind.Unsupported:
                Interlocked.Increment(ref _unsupportedOwnerClassificationCount);
                break;
            case ParticipantClassificationKind.Inconsistent:
                Interlocked.Increment(ref _inconsistentOwnerClassificationCount);
                break;
            case ParticipantClassificationKind.Changing:
                Interlocked.Increment(ref _changingOwnerClassificationCount);
                break;
        }
    }

    private static void AddPositive(ref long counter, int count)
    {
        if (count > 0)
        {
            Interlocked.Add(ref counter, count);
        }
    }
}
