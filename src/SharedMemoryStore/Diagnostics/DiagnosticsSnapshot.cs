namespace SharedMemoryStore;

/// <summary>
/// Allocation-conscious snapshot of store capacity, lifecycle state, and deterministic failures.
/// </summary>
public readonly struct DiagnosticsSnapshot
{
    internal DiagnosticsSnapshot(
        long totalBytes,
        int slotCount,
        int freeSlotCount,
        int publishedSlotCount,
        int pendingRemovalCount,
        int activeLeaseCount,
        int activeReservationCount,
        long abortedReservationCount,
        long recoveredLeaseCount,
        long activeLeaseRecoveryCount,
        long unsupportedLeaseRecoveryCount,
        long failedLeaseRecoveryCount,
        long recoveredReservationCount,
        long activeReservationRecoveryCount,
        long unsupportedReservationRecoveryCount,
        long failedReservationRecoveryCount,
        long capacityPressureCount,
        int indexEntryCount,
        int occupiedIndexEntryCount,
        int tombstoneIndexEntryCount,
        int emptyIndexEntryCount,
        int usableIndexCapacity,
        int lastObservedProbeLength,
        int maxObservedProbeLength,
        long indexCompactionCount,
        StoreStatus lastFailureStatus,
        ReadOnlySpan<long> failureCounts)
    {
        TotalBytes = totalBytes;
        SlotCount = slotCount;
        FreeSlotCount = freeSlotCount;
        PublishedSlotCount = publishedSlotCount;
        PendingRemovalCount = pendingRemovalCount;
        ActiveLeaseCount = activeLeaseCount;
        ActiveReservationCount = activeReservationCount;
        AbortedReservationCount = abortedReservationCount;
        RecoveredLeaseCount = recoveredLeaseCount;
        ActiveLeaseRecoveryCount = activeLeaseRecoveryCount;
        UnsupportedLeaseRecoveryCount = unsupportedLeaseRecoveryCount;
        FailedLeaseRecoveryCount = failedLeaseRecoveryCount;
        RecoveredReservationCount = recoveredReservationCount;
        ActiveReservationRecoveryCount = activeReservationRecoveryCount;
        UnsupportedReservationRecoveryCount = unsupportedReservationRecoveryCount;
        FailedReservationRecoveryCount = failedReservationRecoveryCount;
        CapacityPressureCount = capacityPressureCount;
        IndexEntryCount = indexEntryCount;
        OccupiedIndexEntryCount = occupiedIndexEntryCount;
        TombstoneIndexEntryCount = tombstoneIndexEntryCount;
        EmptyIndexEntryCount = emptyIndexEntryCount;
        UsableIndexCapacity = usableIndexCapacity;
        LastObservedProbeLength = lastObservedProbeLength;
        MaxObservedProbeLength = maxObservedProbeLength;
        IndexCompactionCount = indexCompactionCount;
        LastFailureStatus = lastFailureStatus;
        _duplicateKeyFailures = failureCounts[(int)StoreStatus.DuplicateKey];
        _notFoundFailures = failureCounts[(int)StoreStatus.NotFound];
        _keyTooLargeFailures = failureCounts[(int)StoreStatus.KeyTooLarge];
        _valueTooLargeFailures = failureCounts[(int)StoreStatus.ValueTooLarge];
        _descriptorTooLargeFailures = failureCounts[(int)StoreStatus.DescriptorTooLarge];
        _storeFullFailures = failureCounts[(int)StoreStatus.StoreFull];
        _leaseTableFullFailures = failureCounts[(int)StoreStatus.LeaseTableFull];
        _invalidLeaseFailures = failureCounts[(int)StoreStatus.InvalidLease];
        _leaseAlreadyReleasedFailures = failureCounts[(int)StoreStatus.LeaseAlreadyReleased];
        _removePendingFailures = failureCounts[(int)StoreStatus.RemovePending];
        _unsupportedPlatformFailures = failureCounts[(int)StoreStatus.UnsupportedPlatform];
        _storeDisposedFailures = failureCounts[(int)StoreStatus.StoreDisposed];
        _corruptStoreFailures = failureCounts[(int)StoreStatus.CorruptStore];
        _accessDeniedFailures = failureCounts[(int)StoreStatus.AccessDenied];
        _unknownFailureFailures = failureCounts[(int)StoreStatus.UnknownFailure];
        _invalidReservationFailures = failureCounts[(int)StoreStatus.InvalidReservation];
        _reservationIncompleteFailures = failureCounts[(int)StoreStatus.ReservationIncomplete];
        _reservationAlreadyCompletedFailures = failureCounts[(int)StoreStatus.ReservationAlreadyCompleted];
        _reservationWriteOutOfRangeFailures = failureCounts[(int)StoreStatus.ReservationWriteOutOfRange];
        _invalidKeyFailures = failureCounts[(int)StoreStatus.InvalidKey];
        _storeBusyFailures = failureCounts[(int)StoreStatus.StoreBusy];
        _operationCanceledFailures = failureCounts[(int)StoreStatus.OperationCanceled];
    }

    private readonly long _duplicateKeyFailures;
    private readonly long _notFoundFailures;
    private readonly long _keyTooLargeFailures;
    private readonly long _valueTooLargeFailures;
    private readonly long _descriptorTooLargeFailures;
    private readonly long _storeFullFailures;
    private readonly long _leaseTableFullFailures;
    private readonly long _invalidLeaseFailures;
    private readonly long _leaseAlreadyReleasedFailures;
    private readonly long _removePendingFailures;
    private readonly long _unsupportedPlatformFailures;
    private readonly long _storeDisposedFailures;
    private readonly long _corruptStoreFailures;
    private readonly long _accessDeniedFailures;
    private readonly long _unknownFailureFailures;
    private readonly long _invalidReservationFailures;
    private readonly long _reservationIncompleteFailures;
    private readonly long _reservationAlreadyCompletedFailures;
    private readonly long _reservationWriteOutOfRangeFailures;
    private readonly long _invalidKeyFailures;
    private readonly long _storeBusyFailures;
    private readonly long _operationCanceledFailures;

    /// <summary>Gets the configured mapped-region length.</summary>
    public long TotalBytes { get; }

    /// <summary>Gets the configured reusable slot count.</summary>
    public int SlotCount { get; }

    /// <summary>Gets the number of slots currently free for publishing.</summary>
    public int FreeSlotCount { get; }

    /// <summary>Gets the number of slots currently published.</summary>
    public int PublishedSlotCount { get; }

    /// <summary>Gets the number of slots waiting for final lease release before reuse.</summary>
    public int PendingRemovalCount { get; }

    /// <summary>Gets the number of active lease records.</summary>
    public int ActiveLeaseCount { get; }

    /// <summary>Gets the number of slots currently reserved but not committed.</summary>
    public int ActiveReservationCount { get; }

    /// <summary>Gets the number of reservations aborted through this store handle.</summary>
    public long AbortedReservationCount { get; }

    /// <summary>Gets the number of stale or eligible leases recovered through this store handle.</summary>
    public long RecoveredLeaseCount { get; }

    /// <summary>Gets the number of active leases skipped during explicit recovery scans.</summary>
    public long ActiveLeaseRecoveryCount { get; }

    /// <summary>Gets the number of lease records recovery could not evaluate safely on this platform.</summary>
    public long UnsupportedLeaseRecoveryCount { get; }

    /// <summary>Gets the number of lease records recovery could not reclaim because shared state was inconsistent.</summary>
    public long FailedLeaseRecoveryCount { get; }

    /// <summary>Gets the number of stale reservations recovered through this store handle.</summary>
    public long RecoveredReservationCount { get; }

    /// <summary>Gets the number of active reservations observed during explicit recovery scans.</summary>
    public long ActiveReservationRecoveryCount { get; }

    /// <summary>Gets the number of reservations recovery could not evaluate safely on this platform.</summary>
    public long UnsupportedReservationRecoveryCount { get; }

    /// <summary>Gets the number of reservations recovery could not reclaim because shared state was inconsistent.</summary>
    public long FailedReservationRecoveryCount { get; }

    /// <summary>Gets the number of capacity-pressure failures observed by this store handle.</summary>
    public long CapacityPressureCount { get; }

    /// <summary>Gets the configured key-index entry count.</summary>
    public int IndexEntryCount { get; }

    /// <summary>Gets the number of occupied key-index entries.</summary>
    public int OccupiedIndexEntryCount { get; }

    /// <summary>Gets the number of tombstone key-index entries.</summary>
    public int TombstoneIndexEntryCount { get; }

    /// <summary>Gets the number of empty key-index entries.</summary>
    public int EmptyIndexEntryCount { get; }

    /// <summary>Gets the ratio of tombstone entries to configured key-index entries.</summary>
    public double TombstonePressureRatio => IndexEntryCount == 0 ? 0 : (double)TombstoneIndexEntryCount / IndexEntryCount;

    /// <summary>Gets the number of key-index entries usable for future inserts before pressure management.</summary>
    public int UsableIndexCapacity { get; }

    /// <summary>Gets the most recent bounded key-index probe length observed by this handle.</summary>
    public int LastObservedProbeLength { get; }

    /// <summary>Gets the maximum bounded key-index probe length observed by this handle.</summary>
    public int MaxObservedProbeLength { get; }

    /// <summary>Gets the number of synchronous key-index compactions completed by this handle.</summary>
    public long IndexCompactionCount { get; }

    /// <summary>Gets the last non-success status observed by this store handle.</summary>
    public StoreStatus LastFailureStatus { get; }

    /// <summary>
    /// Returns the failure count for a deterministic operation status.
    /// </summary>
    public long GetFailureCount(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.DuplicateKey => _duplicateKeyFailures,
            StoreStatus.NotFound => _notFoundFailures,
            StoreStatus.KeyTooLarge => _keyTooLargeFailures,
            StoreStatus.ValueTooLarge => _valueTooLargeFailures,
            StoreStatus.DescriptorTooLarge => _descriptorTooLargeFailures,
            StoreStatus.StoreFull => _storeFullFailures,
            StoreStatus.LeaseTableFull => _leaseTableFullFailures,
            StoreStatus.InvalidLease => _invalidLeaseFailures,
            StoreStatus.LeaseAlreadyReleased => _leaseAlreadyReleasedFailures,
            StoreStatus.RemovePending => _removePendingFailures,
            StoreStatus.UnsupportedPlatform => _unsupportedPlatformFailures,
            StoreStatus.StoreDisposed => _storeDisposedFailures,
            StoreStatus.CorruptStore => _corruptStoreFailures,
            StoreStatus.AccessDenied => _accessDeniedFailures,
            StoreStatus.UnknownFailure => _unknownFailureFailures,
            StoreStatus.InvalidReservation => _invalidReservationFailures,
            StoreStatus.ReservationIncomplete => _reservationIncompleteFailures,
            StoreStatus.ReservationAlreadyCompleted => _reservationAlreadyCompletedFailures,
            StoreStatus.ReservationWriteOutOfRange => _reservationWriteOutOfRangeFailures,
            StoreStatus.InvalidKey => _invalidKeyFailures,
            StoreStatus.StoreBusy => _storeBusyFailures,
            StoreStatus.OperationCanceled => _operationCanceledFailures,
            _ => 0
        };
    }
}
