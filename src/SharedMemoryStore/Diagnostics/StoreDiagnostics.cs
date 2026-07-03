using System.Threading;
using SharedMemoryStore.Layout;

namespace SharedMemoryStore.Diagnostics;

internal sealed class StoreDiagnostics
{
    private readonly long[] _failureCounts = new long[Enum.GetValues<StoreStatus>().Length];
    private long _capacityPressureCount;
    private long _abortedReservationCount;
    private long _recoveredLeaseCount;
    private long _activeLeaseRecoveryCount;
    private long _unsupportedLeaseRecoveryCount;
    private long _failedLeaseRecoveryCount;
    private long _recoveredReservationCount;
    private long _activeReservationRecoveryCount;
    private long _unsupportedReservationRecoveryCount;
    private long _failedReservationRecoveryCount;
    private int _lastFailureStatus;

    public void Record(StoreStatus status)
    {
        if (status == StoreStatus.Success)
        {
            return;
        }

        Interlocked.Increment(ref _failureCounts[(int)status]);
        Volatile.Write(ref _lastFailureStatus, (int)status);

        if (status is StoreStatus.StoreFull or StoreStatus.LeaseTableFull)
        {
            Interlocked.Increment(ref _capacityPressureCount);
        }
    }

    public void RecordReservationAbort()
    {
        Interlocked.Increment(ref _abortedReservationCount);
    }

    public void RecordLeaseRecoveryResults(
        int recoveredCount,
        int activeCount,
        int unsupportedCount,
        int failedCount)
    {
        AddPositive(ref _recoveredLeaseCount, recoveredCount);
        AddPositive(ref _activeLeaseRecoveryCount, activeCount);
        AddPositive(ref _unsupportedLeaseRecoveryCount, unsupportedCount);
        AddPositive(ref _failedLeaseRecoveryCount, failedCount);
    }

    public void RecordReservationRecoveryResults(
        int recoveredCount,
        int activeCount,
        int unsupportedCount,
        int failedCount)
    {
        AddPositive(ref _recoveredReservationCount, recoveredCount);
        AddPositive(ref _activeReservationRecoveryCount, activeCount);
        AddPositive(ref _unsupportedReservationRecoveryCount, unsupportedCount);
        AddPositive(ref _failedReservationRecoveryCount, failedCount);
    }

    public DiagnosticsSnapshot CreateSnapshot(
        long totalBytes,
        int slotCount,
        int freeSlotCount,
        int publishedSlotCount,
        int pendingRemovalCount,
        int activeReservationCount,
        int activeLeaseCount,
        IndexStateCounts indexState,
        long indexCompactionCount)
    {
        Span<long> counts = stackalloc long[_failureCounts.Length];
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = Volatile.Read(ref _failureCounts[i]);
        }

        return new DiagnosticsSnapshot(
            totalBytes,
            slotCount,
            freeSlotCount,
            publishedSlotCount,
            pendingRemovalCount,
            activeLeaseCount,
            activeReservationCount,
            Volatile.Read(ref _abortedReservationCount),
            Volatile.Read(ref _recoveredLeaseCount),
            Volatile.Read(ref _activeLeaseRecoveryCount),
            Volatile.Read(ref _unsupportedLeaseRecoveryCount),
            Volatile.Read(ref _failedLeaseRecoveryCount),
            Volatile.Read(ref _recoveredReservationCount),
            Volatile.Read(ref _activeReservationRecoveryCount),
            Volatile.Read(ref _unsupportedReservationRecoveryCount),
            Volatile.Read(ref _failedReservationRecoveryCount),
            Volatile.Read(ref _capacityPressureCount),
            indexState.EntryCount,
            indexState.OccupiedCount,
            indexState.TombstoneCount,
            indexState.EmptyCount,
            indexState.UsableCapacity,
            indexState.LastObservedProbeLength,
            indexState.MaxObservedProbeLength,
            indexCompactionCount,
            (StoreStatus)Volatile.Read(ref _lastFailureStatus),
            counts);
    }

    private static void AddPositive(ref long field, int value)
    {
        if (value > 0)
        {
            Interlocked.Add(ref field, value);
        }
    }
}
