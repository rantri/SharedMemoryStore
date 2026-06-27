using System.Threading;

namespace SharedMemoryStore.Diagnostics;

internal sealed class StoreDiagnostics
{
    private readonly long[] _failureCounts = new long[Enum.GetValues<StoreStatus>().Length];
    private long _capacityPressureCount;
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

    public DiagnosticsSnapshot CreateSnapshot(
        long totalBytes,
        int slotCount,
        int freeSlotCount,
        int publishedSlotCount,
        int pendingRemovalCount,
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
            Volatile.Read(ref _capacityPressureCount),
            (StoreStatus)Volatile.Read(ref _lastFailureStatus),
            counts);
    }
}
