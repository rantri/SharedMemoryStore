namespace SharedMemoryStore.Engines;

/// <summary>
/// Profile-neutral instantaneous engine state consumed by facade diagnostics.
/// </summary>
/// <remarks>
/// Metrics are observational only. No correctness decision may depend on this
/// potentially cross-instant snapshot. Profile-specific fields that do not
/// apply to the legacy engine remain zero.
/// </remarks>
internal readonly record struct EngineMetrics
{
    internal long TotalBytes { get; init; }

    internal int SlotCount { get; init; }

    internal int FreeSlotCount { get; init; }

    internal int InitializingSlotCount { get; init; }

    internal int ReservedSlotCount { get; init; }

    internal int PublishedSlotCount { get; init; }

    internal int PendingRemovalCount { get; init; }

    internal int ReclaimingSlotCount { get; init; }

    internal int RetiredSlotCount { get; init; }

    internal int ActiveLeaseCount { get; init; }

    internal int ClaimingLeaseCount { get; init; }

    internal int RecoveringLeaseCount { get; init; }

    internal int FreeLeaseCount { get; init; }

    internal int RetiredLeaseCount { get; init; }

    internal int ParticipantRecordCount { get; init; }

    internal int FreeParticipantCount { get; init; }

    internal int RegisteringParticipantCount { get; init; }

    internal int ActiveParticipantCount { get; init; }

    internal int ClosingParticipantCount { get; init; }

    internal int RecoveringParticipantCount { get; init; }

    internal int ReclaimingParticipantCount { get; init; }

    internal int RetiredParticipantCount { get; init; }

    internal int IndexEntryCount { get; init; }

    internal int OccupiedIndexEntryCount { get; init; }

    internal int TombstoneIndexEntryCount { get; init; }

    internal int EmptyIndexEntryCount { get; init; }

    internal int UsableIndexCapacity { get; init; }

    internal int LastObservedProbeLength { get; init; }

    internal int MaxObservedProbeLength { get; init; }

    internal long IndexCompactionCount { get; init; }

    internal int PrimaryDirectoryOccupancy { get; init; }

    internal int SpilledBucketCount { get; init; }

    internal int OverflowDirectoryOccupancy { get; init; }

    internal long OverflowScanCount { get; init; }

    internal int MaxObservedOverflowScanLength { get; init; }

    internal long CasRetryCount { get; init; }

    internal long HelpedTransitionCount { get; init; }

    internal long ContentionBudgetExhaustionCount { get; init; }

    internal long InvalidTokenCount { get; init; }

    internal long StaleTokenCount { get; init; }

    internal long RecoveryAttemptCount { get; init; }

    internal long RecoveredTransitionCount { get; init; }

    internal long CurrentOwnerClassificationCount { get; init; }

    internal long LiveOwnerClassificationCount { get; init; }

    internal long StaleOwnerClassificationCount { get; init; }

    internal long UnsupportedOwnerClassificationCount { get; init; }

    internal long InconsistentOwnerClassificationCount { get; init; }

    internal long ChangingOwnerClassificationCount { get; init; }
}
