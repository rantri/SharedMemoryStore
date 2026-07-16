using SharedMemoryStore.Diagnostics;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Observational layout-v2 diagnostics. Every shared-state scan has a fixed
/// layout-derived bound, performs acquire reads only, and never helps or changes
/// protocol ownership in pursuit of a consistent snapshot.
/// </summary>
internal sealed unsafe class LockFreeDiagnostics
{
    private readonly byte* _mappingBase;
    private readonly StoreLayoutV2 _layout;
    private readonly StoreProtocolInfo _protocolInfo;
    private readonly StoreDiagnostics _local = new();
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;
    private MetricsCache _lastMetrics;

    internal LockFreeDiagnostics(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        StoreProtocolInfo protocolInfo)
        : this(region, layout, protocolInfo, new LockFreeTelemetry())
    {
    }

    internal LockFreeDiagnostics(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        StoreProtocolInfo protocolInfo,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (protocolInfo.Profile != StoreProfile.LockFree)
        {
            throw new ArgumentException("Layout-v2 diagnostics require the lock-free profile.", nameof(protocolInfo));
        }

        _mappingBase = region.Pointer;
        _layout = layout;
        _protocolInfo = protocolInfo;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
        _lastMetrics = new MetricsCache(new EngineMetrics
        {
            TotalBytes = layout.TotalBytes,
            SlotCount = layout.SlotCount,
            ParticipantRecordCount = layout.ParticipantRecordCount,
            IndexEntryCount = checked(layout.PrimaryLaneCount + layout.SlotCount)
        });
    }

    /// <summary>Creates a bounded, cross-instant public snapshot.</summary>
    internal DiagnosticsSnapshot CreateSnapshot()
    {
        ReachBeforeBoundedScan();
        _ = TryCreateSnapshotAfterBoundedPrecheck(
            LockFreeOperationBudget.UnboundedScan,
            out DiagnosticsSnapshot snapshot);
        return snapshot;
    }

    /// <summary>
    /// Creates a disposal-safe snapshot without dereferencing the mapped region.
    /// Dynamic shared occupancy is the last successfully scanned observation (or
    /// zero when no scan occurred); immutable sizing and all managed-local
    /// failure/telemetry counters remain observable.
    /// </summary>
    internal DiagnosticsSnapshot CreateDisposedSnapshot()
    {
        EngineMetrics metrics = Volatile.Read(ref _lastMetrics).Value with
        {
            LastObservedProbeLength = _telemetry.LastObservedOverflowScanLength,
            MaxObservedProbeLength = _telemetry.MaxObservedOverflowScanLength,
            OverflowScanCount = _telemetry.OverflowScanCount,
            MaxObservedOverflowScanLength = _telemetry.MaxObservedOverflowScanLength,
            CasRetryCount = _telemetry.CasRetryCount,
            HelpedTransitionCount = _telemetry.HelpedTransitionCount,
            ContentionBudgetExhaustionCount = _telemetry.ContentionBudgetExhaustionCount,
            InvalidTokenCount = _telemetry.InvalidTokenCount,
            StaleTokenCount = _telemetry.StaleTokenCount,
            RecoveryAttemptCount = _telemetry.RecoveryAttemptCount,
            RecoveredTransitionCount = _telemetry.RecoveredTransitionCount,
            CurrentOwnerClassificationCount = _telemetry.CurrentOwnerClassificationCount,
            LiveOwnerClassificationCount = _telemetry.LiveOwnerClassificationCount,
            StaleOwnerClassificationCount = _telemetry.StaleOwnerClassificationCount,
            UnsupportedOwnerClassificationCount = _telemetry.UnsupportedOwnerClassificationCount,
            InconsistentOwnerClassificationCount = _telemetry.InconsistentOwnerClassificationCount,
            ChangingOwnerClassificationCount = _telemetry.ChangingOwnerClassificationCount
        };

        return _local.CreateSnapshot(
            StoreProfile.LockFree,
            _protocolInfo,
            metrics);
    }

    /// <summary>Creates the bounded engine-neutral portion of a snapshot.</summary>
    internal EngineMetrics ScanMetrics()
    {
        ReachBeforeBoundedScan();
        _ = TryScanMetricsAfterBoundedPrecheck(
            LockFreeOperationBudget.UnboundedScan,
            out EngineMetrics metrics);
        return metrics;
    }

    internal void ReachBeforeBoundedScan()
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        ReachBeforeBoundedScan(ref checkpoint);
    }

    internal void ReachBeforeBoundedScan<TCheckpoint>(ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint> =>
        LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.DiagnosticsBeforeBoundedScan);

    internal DiagnosticsSnapshot CreateSnapshotAfterBoundedPrecheck()
    {
        _ = TryCreateSnapshotAfterBoundedPrecheck(
            LockFreeOperationBudget.UnboundedScan,
            out DiagnosticsSnapshot snapshot);
        return snapshot;
    }

    internal StoreStatus TryCreateSnapshotAfterBoundedPrecheck(
        in LockFreeOperationBudget budget,
        out DiagnosticsSnapshot snapshot)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryCreateSnapshotAfterBoundedPrecheck(budget, ref checkpoint, out snapshot);
    }

    internal StoreStatus TryCreateSnapshotAfterBoundedPrecheck<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out DiagnosticsSnapshot snapshot)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        snapshot = default;
        StoreStatus status = TryScanMetricsCore(budget, out EngineMetrics metrics);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        snapshot = _local.CreateSnapshot(
            StoreProfile.LockFree,
            _protocolInfo,
            metrics);
        LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.DiagnosticsAfterSnapshotAssembly);
        return StoreStatus.Success;
    }

    internal EngineMetrics ScanMetricsAfterBoundedPrecheck()
    {
        _ = TryScanMetricsAfterBoundedPrecheck(
            LockFreeOperationBudget.UnboundedScan,
            out EngineMetrics metrics);
        return metrics;
    }

    internal StoreStatus TryScanMetricsAfterBoundedPrecheck(
        in LockFreeOperationBudget budget,
        out EngineMetrics metrics)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryScanMetricsAfterBoundedPrecheck(budget, ref checkpoint, out metrics);
    }

    internal StoreStatus TryScanMetricsAfterBoundedPrecheck<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out EngineMetrics metrics)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        StoreStatus status = TryScanMetricsCore(budget, out metrics);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.DiagnosticsAfterSnapshotAssembly);
        return StoreStatus.Success;
    }

    private StoreStatus TryScanMetricsCore(
        in LockFreeOperationBudget budget,
        out EngineMetrics metrics)
    {
        metrics = default;
        var freeSlots = 0;
        var initializingSlots = 0;
        var reservedSlots = 0;
        var publishedSlots = 0;
        var pendingRemovalSlots = 0;
        var reclaimingSlots = 0;
        var retiredSlots = 0;
        for (var index = 0; index < _layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            long control = AtomicControlWord.LoadAcquire(ref Slot(index).Control);
            if (!LockFreeSlotTable.TryClassifyStructuralControl(
                    control,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }

            int state = State(control);
            switch (state)
            {
                case LockFreeSlotTable.FreeState:
                    freeSlots++;
                    break;
                case LockFreeSlotTable.InitializingState:
                    initializingSlots++;
                    break;
                case LockFreeSlotTable.ReservedState:
                    reservedSlots++;
                    break;
                case LockFreeSlotTable.PublishedState:
                    publishedSlots++;
                    break;
                case LockFreeSlotTable.RemoveRequestedState:
                    pendingRemovalSlots++;
                    break;
                case LockFreeSlotTable.AbortingState:
                case LockFreeSlotTable.ReclaimingState:
                    reclaimingSlots++;
                    break;
                case LockFreeSlotTable.RetiredState:
                    retiredSlots++;
                    break;
            }
        }

        var freeLeases = 0;
        var claimingLeases = 0;
        var activeLeases = 0;
        var recoveringLeases = 0;
        var retiredLeases = 0;
        for (var index = 0; index < _layout.LeaseRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            long control = AtomicControlWord.LoadAcquire(ref Lease(index).Control);
            if (!LockFreeLeaseRegistry.TryClassifyStructuralControl(
                    control,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }

            int state = State(control);
            switch (state)
            {
                case LockFreeLeaseRegistry.FreeState:
                    freeLeases++;
                    break;
                case LockFreeLeaseRegistry.ClaimingState:
                    claimingLeases++;
                    break;
                case LockFreeLeaseRegistry.ActiveState:
                    activeLeases++;
                    break;
                case LockFreeLeaseRegistry.ReleasingState:
                case LockFreeLeaseRegistry.RecoveringState:
                    recoveringLeases++;
                    break;
                case LockFreeLeaseRegistry.RetiredState:
                    retiredLeases++;
                    break;
            }
        }

        var freeParticipants = 0;
        var registeringParticipants = 0;
        var activeParticipants = 0;
        var closingParticipants = 0;
        var recoveringParticipants = 0;
        var reclaimingParticipants = 0;
        var retiredParticipants = 0;
        for (var index = 0; index < _layout.ParticipantRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            long control = AtomicControlWord.LoadAcquire(ref Participant(index).Control);
            if (!LockFreeParticipantRegistry.IsStructuralControlValid(
                    control,
                    _layout.ParticipantGenerationMask))
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }

            int state = State(control);
            switch (state)
            {
                case LayoutV2Constants.ParticipantFree:
                    freeParticipants++;
                    break;
                case LayoutV2Constants.ParticipantRegistering:
                    registeringParticipants++;
                    break;
                case LayoutV2Constants.ParticipantActive:
                    activeParticipants++;
                    break;
                case LayoutV2Constants.ParticipantClosing:
                    closingParticipants++;
                    break;
                case LayoutV2Constants.ParticipantRecovering:
                    recoveringParticipants++;
                    break;
                case LayoutV2Constants.ParticipantReclaiming:
                    reclaimingParticipants++;
                    break;
                case LayoutV2Constants.ParticipantRetired:
                    retiredParticipants++;
                    break;
            }
        }

        var primaryOccupancy = 0;
        var spilledBuckets = 0;
        for (var bucket = 0; bucket < _layout.PrimaryBucketCount; bucket++)
        {
            StoreStatus bound = budget.CheckPeriodic(bucket);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ulong spillSummaryRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref BucketSpillSummary(bucket)));
            SpillSummary spillSummary;
            try
            {
                spillSummary = SpillSummary.Decode(spillSummaryRaw);
            }
            catch (ArgumentOutOfRangeException)
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }
            catch (OverflowException)
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }

            if (!spillSummary.IsInitial
                && (uint)spillSummary.SlotIndex >= (uint)_layout.SlotCount)
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }

            spilledBuckets += spillSummary.IsPresent ? 1 : 0;
            StoreStatus mutationStatus = TryReadStructurallyValidBindingReference(
                ref BucketMutation(bucket),
                out _);
            if (mutationStatus != StoreStatus.Success)
            {
                return mutationStatus;
            }

            for (var lane = 0; lane < LayoutV2Constants.PrimaryLanesPerBucket; lane++)
            {
                StoreStatus laneStatus = TryReadStructurallyValidBindingReference(
                    ref PrimaryLane(bucket, lane),
                    out ulong laneBinding);
                if (laneStatus != StoreStatus.Success)
                {
                    return laneStatus;
                }

                primaryOccupancy += laneBinding == 0 ? 0 : 1;
            }
        }

        var overflowOccupancy = 0;
        for (var index = 0; index < _layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            StoreStatus cellStatus = TryReadStructurallyValidBindingReference(
                ref OverflowCell(index),
                out ulong cellBinding);
            if (cellStatus != StoreStatus.Success)
            {
                return cellStatus;
            }

            overflowOccupancy += cellBinding == 0 ? 0 : 1;
        }

        int indexEntryCount = checked(_layout.PrimaryLaneCount + _layout.SlotCount);
        int occupiedIndexEntryCount = checked(primaryOccupancy + overflowOccupancy);
        int lastOverflowScanLength = _telemetry.LastObservedOverflowScanLength;
        int maxOverflowScanLength = _telemetry.MaxObservedOverflowScanLength;
        metrics = new EngineMetrics
        {
            TotalBytes = _layout.TotalBytes,
            SlotCount = _layout.SlotCount,
            FreeSlotCount = freeSlots,
            InitializingSlotCount = initializingSlots,
            ReservedSlotCount = reservedSlots,
            PublishedSlotCount = publishedSlots,
            PendingRemovalCount = pendingRemovalSlots,
            ReclaimingSlotCount = reclaimingSlots,
            RetiredSlotCount = retiredSlots,
            ActiveLeaseCount = activeLeases,
            ClaimingLeaseCount = claimingLeases,
            RecoveringLeaseCount = recoveringLeases,
            FreeLeaseCount = freeLeases,
            RetiredLeaseCount = retiredLeases,
            ParticipantRecordCount = _layout.ParticipantRecordCount,
            FreeParticipantCount = freeParticipants,
            RegisteringParticipantCount = registeringParticipants,
            ActiveParticipantCount = activeParticipants,
            ClosingParticipantCount = closingParticipants,
            RecoveringParticipantCount = recoveringParticipants,
            ReclaimingParticipantCount = reclaimingParticipants,
            RetiredParticipantCount = retiredParticipants,
            IndexEntryCount = indexEntryCount,
            OccupiedIndexEntryCount = occupiedIndexEntryCount,
            TombstoneIndexEntryCount = 0,
            EmptyIndexEntryCount = Math.Max(0, indexEntryCount - occupiedIndexEntryCount),
            UsableIndexCapacity = freeSlots,
            LastObservedProbeLength = lastOverflowScanLength,
            MaxObservedProbeLength = maxOverflowScanLength,
            IndexCompactionCount = 0,
            PrimaryDirectoryOccupancy = primaryOccupancy,
            SpilledBucketCount = spilledBuckets,
            OverflowDirectoryOccupancy = overflowOccupancy,
            OverflowScanCount = _telemetry.OverflowScanCount,
            MaxObservedOverflowScanLength = maxOverflowScanLength,
            CasRetryCount = _telemetry.CasRetryCount,
            HelpedTransitionCount = _telemetry.HelpedTransitionCount,
            ContentionBudgetExhaustionCount = _telemetry.ContentionBudgetExhaustionCount,
            InvalidTokenCount = _telemetry.InvalidTokenCount,
            StaleTokenCount = _telemetry.StaleTokenCount,
            RecoveryAttemptCount = _telemetry.RecoveryAttemptCount,
            RecoveredTransitionCount = _telemetry.RecoveredTransitionCount,
            CurrentOwnerClassificationCount = _telemetry.CurrentOwnerClassificationCount,
            LiveOwnerClassificationCount = _telemetry.LiveOwnerClassificationCount,
            StaleOwnerClassificationCount = _telemetry.StaleOwnerClassificationCount,
            UnsupportedOwnerClassificationCount = _telemetry.UnsupportedOwnerClassificationCount,
            InconsistentOwnerClassificationCount = _telemetry.InconsistentOwnerClassificationCount,
            ChangingOwnerClassificationCount = _telemetry.ChangingOwnerClassificationCount
        };

        Volatile.Write(ref _lastMetrics, new MetricsCache(metrics));

        return StoreStatus.Success;
    }

    private sealed class MetricsCache(EngineMetrics value)
    {
        internal EngineMetrics Value { get; } = value;
    }

    /// <summary>Records one public status and returns it unchanged for call-site composition.</summary>
    internal StoreStatus RecordStatus(StoreStatus status)
    {
        _local.Record(status);
        if (status == StoreStatus.StoreBusy)
        {
            _telemetry.RecordContentionBudgetExhaustion();
        }

        return status;
    }

    internal void RecordReservationAbort() => _local.RecordReservationAbort();

    internal void RecordLeaseRecoveryResults(in LeaseRecoveryReport report)
    {
        _local.RecordLeaseRecoveryResults(
            report.RecoveredLeaseCount,
            report.ActiveLeaseCount,
            report.UnsupportedLeaseCount,
            report.FailedRecoveryCount);
        RecordRecoveryAttempt(report.ScannedRecordCount);
        RecordRecoveredTransition(report.RecoveredLeaseCount);
    }

    internal void RecordReservationRecoveryResults(in ReservationRecoveryReport report)
    {
        _local.RecordReservationRecoveryResults(
            report.RecoveredReservationCount,
            report.ActiveReservationCount,
            report.UnsupportedReservationCount,
            report.FailedRecoveryCount);
        RecordRecoveryAttempt(report.ScannedReservationCount);
        RecordRecoveredTransition(report.RecoveredReservationCount);
    }

    internal void RecordOverflowScan(int scannedCellCount) =>
        _telemetry.RecordOverflowScan(scannedCellCount);

    internal void RecordCasRetry(int count = 1) => _telemetry.RecordCasLoss(count);

    internal void RecordHelpedTransition(int count = 1) =>
        _telemetry.RecordHelpedTransition(count);

    internal void RecordInvalidToken(bool stale)
    {
        if (stale)
        {
            _telemetry.RecordInvalidToken(stale: true);
        }
        else
        {
            _telemetry.RecordInvalidToken(stale: false);
        }
    }

    internal void RecordRecoveryAttempt(int count = 1) =>
        _telemetry.RecordRecoveryAttempt(count);

    internal void RecordRecoveredTransition(int count = 1) =>
        _telemetry.RecordRecoveredTransition(count);

    internal void RecordOwnerClassification(ParticipantClassificationKind kind)
    {
        _telemetry.RecordOwnerClassification(kind);
    }

    private ref ParticipantRecordV2 Participant(int index) =>
        ref *(ParticipantRecordV2*)(
            _mappingBase + _layout.ParticipantOffset + ((long)index * _layout.ParticipantStride));

    private ref LeaseRecordV2 Lease(int index) =>
        ref *(LeaseRecordV2*)(
            _mappingBase + _layout.LeaseRegistryOffset + ((long)index * _layout.LeaseStride));

    private ref ValueSlotMetadataV2 Slot(int index) =>
        ref *(ValueSlotMetadataV2*)(
            _mappingBase + _layout.SlotMetadataOffset + ((long)index * _layout.SlotMetadataStride));

    private ref long BucketSpillSummary(int bucket) =>
        ref *(long*)(
            _mappingBase + _layout.PrimaryDirectoryOffset + ((long)bucket * _layout.PrimaryBucketStride));

    private ref long BucketMutation(int bucket) =>
        ref *(long*)(
            _mappingBase
            + _layout.PrimaryDirectoryOffset
            + ((long)bucket * _layout.PrimaryBucketStride)
            + sizeof(long));

    private ref long PrimaryLane(int bucket, int lane) =>
        ref *(long*)(
            _mappingBase
            + _layout.PrimaryDirectoryOffset
            + ((long)bucket * _layout.PrimaryBucketStride)
            + 16
            + (lane * sizeof(long)));

    private ref long OverflowCell(int index) =>
        ref *(long*)(
            _mappingBase + _layout.OverflowDirectoryOffset + ((long)index * _layout.OverflowStride));

    private StoreStatus TryReadStructurallyValidBindingReference(
        ref long reference,
        out ulong observed)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference));
            if (observed == 0 || IsStructurallyValidBinding(observed))
            {
                return StoreStatus.Success;
            }

            if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference)) == observed)
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeDiagnostics));
            }
        }

        observed = 0;
        return StoreStatus.StoreBusy;
    }

    private bool IsStructurallyValidBinding(ulong raw)
    {
        try
        {
            IndexBinding binding = IndexBinding.Decode(raw);
            return (uint)binding.SlotIndex < (uint)_layout.SlotCount;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static int State(long control) => (int)((ulong)control & 0x7UL);
}
