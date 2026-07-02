using System.Threading;

namespace SharedMemoryStore.Diagnostics;

internal sealed class StoreDiagnostics
{
    private readonly long[] _failureCounts = new long[Enum.GetValues<StoreStatus>().Length];
    private long _capacityPressureCount;
    private long _abortedReservationCount;
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

    public void RecordReservationRecoveryResults(
        int recoveredCount,
        int activeCount,
        int unsupportedCount,
        int failedCount)
    {
        if (recoveredCount > 0)
        {
            Interlocked.Add(ref _recoveredReservationCount, recoveredCount);
        }

        if (activeCount > 0)
        {
            Interlocked.Add(ref _activeReservationRecoveryCount, activeCount);
        }

        if (unsupportedCount > 0)
        {
            Interlocked.Add(ref _unsupportedReservationRecoveryCount, unsupportedCount);
        }

        if (failedCount > 0)
        {
            Interlocked.Add(ref _failedReservationRecoveryCount, failedCount);
        }
    }

    public DiagnosticsSnapshot CreateSnapshot(
        long totalBytes,
        int slotCount,
        int freeSlotCount,
        int publishedSlotCount,
        int pendingRemovalCount,
        int activeReservationCount,
        int activeLeaseCount)
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
            Volatile.Read(ref _recoveredReservationCount),
            Volatile.Read(ref _activeReservationRecoveryCount),
            Volatile.Read(ref _unsupportedReservationRecoveryCount),
            Volatile.Read(ref _failedReservationRecoveryCount),
            Volatile.Read(ref _capacityPressureCount),
            (StoreStatus)Volatile.Read(ref _lastFailureStatus),
            counts);
    }
}
