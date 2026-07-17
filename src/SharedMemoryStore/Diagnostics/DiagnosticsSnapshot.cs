using SharedMemoryStore.Engines;

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
        int emptyIndexEntryCount,
        int usableIndexCapacity,
        int lastObservedProbeLength,
        int maxObservedProbeLength,
        StoreStatus lastFailureStatus,
        ReadOnlySpan<long> failureCounts,
        StoreProtocolInfo protocolInfo,
        EngineMetrics engineMetrics)
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
        EmptyIndexEntryCount = emptyIndexEntryCount;
        UsableIndexCapacity = usableIndexCapacity;
        LastObservedProbeLength = lastObservedProbeLength;
        MaxObservedProbeLength = maxObservedProbeLength;
        LastFailureStatus = lastFailureStatus;
        ProtocolInfo = protocolInfo;
        InitializingSlotCount = engineMetrics.InitializingSlotCount;
        ReservedSlotCount = engineMetrics.ReservedSlotCount;
        ReclaimingSlotCount = engineMetrics.ReclaimingSlotCount;
        RetiredSlotCount = engineMetrics.RetiredSlotCount;
        ClaimingLeaseCount = engineMetrics.ClaimingLeaseCount;
        RecoveringLeaseCount = engineMetrics.RecoveringLeaseCount;
        FreeLeaseCount = engineMetrics.FreeLeaseCount;
        RetiredLeaseCount = engineMetrics.RetiredLeaseCount;
        ParticipantRecordCount = engineMetrics.ParticipantRecordCount;
        FreeParticipantCount = engineMetrics.FreeParticipantCount;
        RegisteringParticipantCount = engineMetrics.RegisteringParticipantCount;
        ActiveParticipantCount = engineMetrics.ActiveParticipantCount;
        ClosingParticipantCount = engineMetrics.ClosingParticipantCount;
        RecoveringParticipantCount = engineMetrics.RecoveringParticipantCount;
        ReclaimingParticipantCount = engineMetrics.ReclaimingParticipantCount;
        RetiredParticipantCount = engineMetrics.RetiredParticipantCount;
        PrimaryDirectoryOccupancy = engineMetrics.PrimaryDirectoryOccupancy;
        SpilledBucketCount = engineMetrics.SpilledBucketCount;
        OverflowDirectoryOccupancy = engineMetrics.OverflowDirectoryOccupancy;
        OverflowScanCount = engineMetrics.OverflowScanCount;
        MaxObservedOverflowScanLength = engineMetrics.MaxObservedOverflowScanLength;
        CasRetryCount = engineMetrics.CasRetryCount;
        HelpedTransitionCount = engineMetrics.HelpedTransitionCount;
        ContentionBudgetExhaustionCount = engineMetrics.ContentionBudgetExhaustionCount;
        InvalidTokenCount = engineMetrics.InvalidTokenCount;
        StaleTokenCount = engineMetrics.StaleTokenCount;
        RecoveryAttemptCount = engineMetrics.RecoveryAttemptCount;
        RecoveredTransitionCount = engineMetrics.RecoveredTransitionCount;
        CurrentOwnerClassificationCount = engineMetrics.CurrentOwnerClassificationCount;
        LiveOwnerClassificationCount = engineMetrics.LiveOwnerClassificationCount;
        StaleOwnerClassificationCount = engineMetrics.StaleOwnerClassificationCount;
        UnsupportedOwnerClassificationCount = engineMetrics.UnsupportedOwnerClassificationCount;
        InconsistentOwnerClassificationCount = engineMetrics.InconsistentOwnerClassificationCount;
        ChangingOwnerClassificationCount = engineMetrics.ChangingOwnerClassificationCount;
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

    /// <summary>Gets the persisted layout and resource-protocol identity of the observed store.</summary>
    public StoreProtocolInfo ProtocolInfo { get; }

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

    /// <summary>Gets the number of v2 slots whose owner is initializing reservation metadata.</summary>
    public int InitializingSlotCount { get; }

    /// <summary>Gets the number of v2 slots reserved for an unpublished value.</summary>
    public int ReservedSlotCount { get; }

    /// <summary>Gets the number of v2 slots in abort or physical-reclamation transitions.</summary>
    public int ReclaimingSlotCount { get; }

    /// <summary>Gets the number of v2 slots retired before generation reuse could wrap.</summary>
    public int RetiredSlotCount { get; }

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

    /// <summary>Gets the number of v2 lease records whose first ownership claim is in progress.</summary>
    public int ClaimingLeaseCount { get; }

    /// <summary>Gets the number of v2 lease records being released or explicitly recovered.</summary>
    public int RecoveringLeaseCount { get; }

    /// <summary>Gets the number of v2 lease records currently available for a new claim.</summary>
    public int FreeLeaseCount { get; }

    /// <summary>Gets the number of v2 lease records retired before incarnation reuse could wrap.</summary>
    public int RetiredLeaseCount { get; }

    /// <summary>Gets the configured participant-record capacity.</summary>
    public int ParticipantRecordCount { get; }

    /// <summary>Gets the number of v2 participant records currently available to open handles.</summary>
    public int FreeParticipantCount { get; }

    /// <summary>Gets the number of v2 participant records publishing a new process identity.</summary>
    public int RegisteringParticipantCount { get; }

    /// <summary>Gets the number of active v2 participant records.</summary>
    public int ActiveParticipantCount { get; }

    /// <summary>Gets the number of v2 participant records closing their local handle.</summary>
    public int ClosingParticipantCount { get; }

    /// <summary>Gets the number of v2 participant records undergoing stale-owner recovery.</summary>
    public int RecoveringParticipantCount { get; }

    /// <summary>Gets the number of unowned v2 participant records awaiting generation advance.</summary>
    public int ReclaimingParticipantCount { get; }

    /// <summary>Gets the number of v2 participant records retired before token reuse could wrap.</summary>
    public int RetiredParticipantCount { get; }

    /// <summary>
    /// Gets whether a v2 snapshot observed no immediately free participant record.
    /// The value is advisory because registrations may change concurrently.
    /// </summary>
    public bool IsParticipantTableExhausted =>
        ParticipantRecordCount > 0 && FreeParticipantCount == 0;

    /// <summary>Gets the number of capacity-pressure failures observed by this store handle.</summary>
    public long CapacityPressureCount { get; }

    /// <summary>Gets the configured key-index entry count.</summary>
    public int IndexEntryCount { get; }

    /// <summary>Gets the number of occupied key-index entries.</summary>
    public int OccupiedIndexEntryCount { get; }

    /// <summary>Gets the number of empty key-index entries.</summary>
    public int EmptyIndexEntryCount { get; }

    /// <summary>Gets the number of key-index entries usable for future inserts before pressure management.</summary>
    public int UsableIndexCapacity { get; }

    /// <summary>Gets the most recent bounded key-index probe length observed by this handle.</summary>
    public int LastObservedProbeLength { get; }

    /// <summary>Gets the maximum bounded key-index probe length observed by this handle.</summary>
    public int MaxObservedProbeLength { get; }

    /// <summary>Gets the number of non-empty v2 primary-directory lanes observed.</summary>
    public int PrimaryDirectoryOccupancy { get; }

    /// <summary>Gets the number of v2 primary buckets whose versioned spill summary is logically present.</summary>
    public int SpilledBucketCount { get; }

    /// <summary>Gets the number of non-empty v2 overflow-directory cells observed.</summary>
    public int OverflowDirectoryOccupancy { get; }

    /// <summary>Gets the number of overflow candidate scans recorded by this v2 handle.</summary>
    public long OverflowScanCount { get; }

    /// <summary>Gets the largest overflow candidate scan length recorded by this v2 handle.</summary>
    public int MaxObservedOverflowScanLength { get; }

    /// <summary>Gets the number of failed compare/exchange attempts recorded by this v2 handle.</summary>
    public long CasRetryCount { get; }

    /// <summary>Gets the number of cooperative protocol transitions completed by this v2 handle.</summary>
    public long HelpedTransitionCount { get; }

    /// <summary>Gets the number of local contention budgets exhausted by this v2 handle.</summary>
    public long ContentionBudgetExhaustionCount { get; }

    /// <summary>Gets the number of structurally invalid reservation or lease tokens observed by this v2 handle.</summary>
    public long InvalidTokenCount { get; }

    /// <summary>Gets the number of well-formed but no-longer-current tokens observed by this v2 handle.</summary>
    public long StaleTokenCount { get; }

    /// <summary>Gets the number of explicit recovery records attempted by this v2 handle.</summary>
    public long RecoveryAttemptCount { get; }

    /// <summary>Gets the number of exact recovery transitions completed by this v2 handle.</summary>
    public long RecoveredTransitionCount { get; }

    /// <summary>Gets current-process owner classifications made by this v2 handle.</summary>
    public long CurrentOwnerClassificationCount { get; }

    /// <summary>Gets other-live-process owner classifications made by this v2 handle.</summary>
    public long LiveOwnerClassificationCount { get; }

    /// <summary>Gets safely stale owner classifications made by this v2 handle.</summary>
    public long StaleOwnerClassificationCount { get; }

    /// <summary>Gets owner classifications that this v2 handle could not evaluate on the current platform.</summary>
    public long UnsupportedOwnerClassificationCount { get; }

    /// <summary>Gets structurally inconsistent owner classifications made by this v2 handle.</summary>
    public long InconsistentOwnerClassificationCount { get; }

    /// <summary>Gets owner classifications abandoned because the observed record changed concurrently.</summary>
    public long ChangingOwnerClassificationCount { get; }

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
