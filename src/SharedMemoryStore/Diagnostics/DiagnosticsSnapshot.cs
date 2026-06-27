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
        long capacityPressureCount,
        StoreStatus lastFailureStatus,
        ReadOnlySpan<long> failureCounts)
    {
        TotalBytes = totalBytes;
        SlotCount = slotCount;
        FreeSlotCount = freeSlotCount;
        PublishedSlotCount = publishedSlotCount;
        PendingRemovalCount = pendingRemovalCount;
        ActiveLeaseCount = activeLeaseCount;
        CapacityPressureCount = capacityPressureCount;
        LastFailureStatus = lastFailureStatus;
        DuplicateKeyFailures = failureCounts[(int)StoreStatus.DuplicateKey];
        NotFoundFailures = failureCounts[(int)StoreStatus.NotFound];
        KeyTooLargeFailures = failureCounts[(int)StoreStatus.KeyTooLarge];
        ValueTooLargeFailures = failureCounts[(int)StoreStatus.ValueTooLarge];
        DescriptorTooLargeFailures = failureCounts[(int)StoreStatus.DescriptorTooLarge];
        StoreFullFailures = failureCounts[(int)StoreStatus.StoreFull];
        LeaseTableFullFailures = failureCounts[(int)StoreStatus.LeaseTableFull];
        InvalidLeaseFailures = failureCounts[(int)StoreStatus.InvalidLease];
        LeaseAlreadyReleasedFailures = failureCounts[(int)StoreStatus.LeaseAlreadyReleased];
        RemovePendingFailures = failureCounts[(int)StoreStatus.RemovePending];
        UnsupportedPlatformFailures = failureCounts[(int)StoreStatus.UnsupportedPlatform];
        StoreDisposedFailures = failureCounts[(int)StoreStatus.StoreDisposed];
        CorruptStoreFailures = failureCounts[(int)StoreStatus.CorruptStore];
        AccessDeniedFailures = failureCounts[(int)StoreStatus.AccessDenied];
        UnknownFailureFailures = failureCounts[(int)StoreStatus.UnknownFailure];
    }

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

    /// <summary>Gets the number of capacity-pressure failures observed by this store handle.</summary>
    public long CapacityPressureCount { get; }

    /// <summary>Gets the last non-success status observed by this store handle.</summary>
    public StoreStatus LastFailureStatus { get; }

    /// <summary>Gets duplicate-key failure count.</summary>
    public long DuplicateKeyFailures { get; }

    /// <summary>Gets not-found failure count.</summary>
    public long NotFoundFailures { get; }

    /// <summary>Gets oversized-key failure count.</summary>
    public long KeyTooLargeFailures { get; }

    /// <summary>Gets oversized-value failure count.</summary>
    public long ValueTooLargeFailures { get; }

    /// <summary>Gets oversized-descriptor failure count.</summary>
    public long DescriptorTooLargeFailures { get; }

    /// <summary>Gets store-full failure count.</summary>
    public long StoreFullFailures { get; }

    /// <summary>Gets lease-table-full failure count.</summary>
    public long LeaseTableFullFailures { get; }

    /// <summary>Gets invalid-lease failure count.</summary>
    public long InvalidLeaseFailures { get; }

    /// <summary>Gets repeated-release failure count.</summary>
    public long LeaseAlreadyReleasedFailures { get; }

    /// <summary>Gets remove-pending count.</summary>
    public long RemovePendingFailures { get; }

    /// <summary>Gets unsupported-platform failure count.</summary>
    public long UnsupportedPlatformFailures { get; }

    /// <summary>Gets disposed-store failure count.</summary>
    public long StoreDisposedFailures { get; }

    /// <summary>Gets corrupt-store failure count.</summary>
    public long CorruptStoreFailures { get; }

    /// <summary>Gets access-denied failure count.</summary>
    public long AccessDeniedFailures { get; }

    /// <summary>Gets unknown-failure count.</summary>
    public long UnknownFailureFailures { get; }

    /// <summary>
    /// Returns the failure count for a deterministic operation status.
    /// </summary>
    public long GetFailureCount(StoreStatus status)
    {
        return status switch
        {
            StoreStatus.DuplicateKey => DuplicateKeyFailures,
            StoreStatus.NotFound => NotFoundFailures,
            StoreStatus.KeyTooLarge => KeyTooLargeFailures,
            StoreStatus.ValueTooLarge => ValueTooLargeFailures,
            StoreStatus.DescriptorTooLarge => DescriptorTooLargeFailures,
            StoreStatus.StoreFull => StoreFullFailures,
            StoreStatus.LeaseTableFull => LeaseTableFullFailures,
            StoreStatus.InvalidLease => InvalidLeaseFailures,
            StoreStatus.LeaseAlreadyReleased => LeaseAlreadyReleasedFailures,
            StoreStatus.RemovePending => RemovePendingFailures,
            StoreStatus.UnsupportedPlatform => UnsupportedPlatformFailures,
            StoreStatus.StoreDisposed => StoreDisposedFailures,
            StoreStatus.CorruptStore => CorruptStoreFailures,
            StoreStatus.AccessDenied => AccessDeniedFailures,
            StoreStatus.UnknownFailure => UnknownFailureFailures,
            _ => 0
        };
    }
}
