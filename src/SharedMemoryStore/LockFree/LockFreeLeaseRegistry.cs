using System.Runtime.CompilerServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Incarnation-fenced layout-v2 lease records. Claim and activation are
/// deliberately separate so the engine can relinquish an internal lease when
/// its final directory/slot revalidation fails.
/// </summary>
internal sealed unsafe class LockFreeLeaseRegistry
{
    internal const int FreeState = 0;
    internal const int ClaimingState = 1;
    internal const int ActiveState = 2;
    internal const int ReleasingState = 3;
    internal const int RecoveringState = 4;
    internal const int RetiredState = 5;
    internal const long TerminalIncarnation = 0x1_ffff_ffffL;

    private const ulong IncarnationMask = 0x1_ffff_ffffUL;
    private const ulong ParticipantMask = 0x0fff_ffffUL;
    private const ulong RecordIndexMask = 0x7fff_ffffUL;
    private const int MaxRecoveryAttemptsPerRecord = 8;

    private readonly byte* _mappingBase;
    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeParticipantRegistry.Registration _participant;
    private readonly LockFreeParticipantRegistry _participants;
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;
    private readonly ulong _storeId;
    private readonly long[] _leaseTableFullSnapshot;
    private int _leaseTableFullProofGate;

    internal LockFreeLeaseRegistry(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeParticipantRegistry.Registration participant,
        LockFreeParticipantRegistry participants)
        : this(region, layout, participant, participants, new LockFreeTelemetry())
    {
    }

    internal LockFreeLeaseRegistry(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeParticipantRegistry.Registration participant,
        LockFreeParticipantRegistry participants,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(participants);
        if (!participant.IsValid || participant.Token > ParticipantMask)
        {
            throw new ArgumentOutOfRangeException(nameof(participant));
        }

        _mappingBase = region.Pointer;
        _layout = layout;
        _participant = participant;
        _participants = participants;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
        // An exhausted claim scan is not a simultaneous-capacity witness: a
        // released record can rotate behind the scanner. Keep one eager,
        // process-local snapshot per open handle for the rare exact proof path.
        // The buffer adds eight bytes per configured lease record, is reused by
        // every operation, and never appears in mapped state.
        _leaseTableFullSnapshot =
            GC.AllocateUninitializedArray<long>(layout.LeaseRecordCount);
        _storeId = ((StoreHeaderV2*)_mappingBase)->StoreId;
        if (_storeId == 0)
        {
            throw new ArgumentException("The layout-v2 mapping has no store incarnation.", nameof(region));
        }
    }

    /// <summary>
    /// Claims a free record with the complete participant token in the first
    /// shared CAS, revalidates that participant, and fills the exact binding.
    /// </summary>
    internal StoreStatus TryClaim(
        ulong slotBinding,
        long acquireSequence,
        out LeaseHandle lease)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryClaim(
            slotBinding,
            acquireSequence,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint,
            out lease);
    }

    internal StoreStatus TryClaim(
        ulong slotBinding,
        long acquireSequence,
        in LockFreeOperationBudget budget,
        out LeaseHandle lease)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryClaim(
            slotBinding,
            acquireSequence,
            budget,
            ref checkpoint,
            out lease);
    }

    internal StoreStatus TryClaim<TCheckpoint>(
        ulong slotBinding,
        long acquireSequence,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out LeaseHandle lease)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        lease = default;
        if (!IsParticipantActive())
        {
            return ParticipantUnavailableStatus();
        }

        if (!IsValidSlotBinding(slotBinding))
        {
            return CorruptHere();
        }

        var capacityRetryAttempt = 0;
    RetryClaim:
        if (capacityRetryAttempt != 0 && !IsParticipantActive())
        {
            return ParticipantUnavailableStatus();
        }

        // Prefer this participant's stable home record. Sequential readers then
        // reuse a cache line that no other live participant normally writes,
        // while multiple outstanding leases still fall through to the complete
        // bounded table scan. This is placement only, never exclusive ownership.
        int start = _participant.RecordIndex % _layout.LeaseRecordCount;
        for (var visited = 0; visited < _layout.LeaseRecordCount; visited++)
        {
            StoreStatus bound = budget.CheckPeriodic(visited);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            var index = start + visited;
            if (index >= _layout.LeaseRecordCount)
            {
                index -= _layout.LeaseRecordCount;
            }

            ref var record = ref Record(index);
            var observed = AtomicControlWord.LoadAcquire(ref record.Control);
            StoreStatus structure = ValidateControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            int observedState = State(observed);
            long observedIncarnation = Incarnation(observed);
            if (observedState is ReleasingState or RecoveringState
                && Participant(observed) == 0
                && observedIncarnation is >= 1 and <= TerminalIncarnation)
            {
                // Releasing/Recovering has no owner-only writes left. Any
                // claimant may finish the exact incarnation before deciding
                // that the table is full; a delayed recycler is fenced by its
                // old expected control once this record is claimed again.
                structure = TryRecycle(
                    index,
                    observedIncarnation,
                    observed,
                    out bool recycled);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                if (recycled)
                {
                    _telemetry.RecordHelpedTransition();
                }

                observed = AtomicControlWord.LoadAcquire(ref record.Control);
                structure = ValidateControl(observed);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }
            }

            if (State(observed) != FreeState)
            {
                continue;
            }

            var incarnation = Incarnation(observed);
            var claiming = OwnedControl(ClaimingState, incarnation, _participant.Token);
            long claimObservation = AtomicControlWord.CompareExchange(
                ref record.Control,
                claiming,
                observed);
            if (claimObservation != observed)
            {
                _telemetry.RecordCasLoss();
                structure = ValidateControl(claimObservation);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                continue;
            }

            var leaseToken = IndexBinding.Encode(index, incarnation);
            lease = new LeaseHandle(_storeId, _participant.Token, slotBinding, leaseToken);

            // A crash at the CAS above is recoverable because its complete owner
            // token is already in shared state. A participant that begins closing
            // here cannot leave a legitimate owner-controlled claim behind.
            if (!IsParticipantActive())
            {
                StoreStatus unavailable = ParticipantUnavailableStatus();
                StoreStatus cancel = unavailable == StoreStatus.CorruptStore
                    ? unavailable
                    : TryCancelClaim(lease);
                lease = default;
                return unavailable == StoreStatus.CorruptStore
                    || cancel == StoreStatus.CorruptStore
                    ? CorruptHere()
                    : StoreStatus.StoreDisposed;
            }

            record.SlotBinding = slotBinding;
            record.AcquireSequence = acquireSequence;
            return StoreStatus.Success;
        }

        // A sequential exhausted scan is only a candidate: a free record can
        // rotate behind it while another reader releases and reclaims records.
        // Only two identical, structurally valid all-occupied collects expose
        // LeaseTableFull. Every other result is ordinary contention governed by
        // the caller's operation-wide wait budget.
        StoreStatus proof = TryProveLeaseTableFull(
            budget,
            ref checkpoint,
            out bool provenFull);
        if (proof != StoreStatus.Success)
        {
            return proof;
        }

        if (provenFull)
        {
            return StoreStatus.LeaseTableFull;
        }

        if (!budget.TryContinueAfterContention(
                capacityRetryAttempt++,
                out StoreStatus capacityTerminal))
        {
            return capacityTerminal;
        }

        goto RetryClaim;
    }

    /// <summary>
    /// Converts scan exhaustion into an exact physical lease-capacity result.
    /// Equal all-occupied controls in the same order prove a common point
    /// between the collects at which no lease record was reusable. A free
    /// record, movement, or another local proof holding the buffer is transient.
    /// </summary>
    private StoreStatus TryProveLeaseTableFull<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out bool provenFull)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        provenFull = false;
        if (Interlocked.CompareExchange(ref _leaseTableFullProofGate, 1, 0) != 0)
        {
            return StoreStatus.Success;
        }

        long proofToken = 0;
        var candidateObserved = false;
        var proofConfirmed = false;
        try
        {
            for (var index = 0; index < _layout.LeaseRecordCount; index++)
            {
                StoreStatus bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref Record(index).Control);
                StoreStatus classification = ClassifyCapacityControl(
                    control,
                    out bool occupied);
                if (classification != StoreStatus.Success)
                {
                    return classification;
                }

                if (!occupied)
                {
                    return StoreStatus.Success;
                }

                _leaseTableFullSnapshot[index] = control;
            }

            proofToken = LockFreeCheckpoint.BeginLeaseTableFullProof(
                ref checkpoint,
                _layout.LeaseRecordCount);
            candidateObserved = true;

            for (var index = 0; index < _layout.LeaseRecordCount; index++)
            {
                StoreStatus bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref Record(index).Control);
                StoreStatus classification = ClassifyCapacityControl(
                    control,
                    out bool occupied);
                if (classification != StoreStatus.Success)
                {
                    return classification;
                }

                if (!occupied || control != _leaseTableFullSnapshot[index])
                {
                    return StoreStatus.Success;
                }
            }

            proofConfirmed = true;
            LockFreeCheckpoint.CompleteLeaseTableFullProof(
                ref checkpoint,
                proofToken,
                confirmed: true);
            provenFull = true;
            return StoreStatus.Success;
        }
        finally
        {
            if (candidateObserved && !proofConfirmed)
            {
                LockFreeCheckpoint.CompleteLeaseTableFullProof(
                    ref checkpoint,
                    proofToken,
                    confirmed: false);
            }

            Volatile.Write(ref _leaseTableFullProofGate, 0);
        }
    }

    /// <summary>Publishes a fully initialized claimed record as active.</summary>
    internal StoreStatus TryActivate(in LeaseHandle lease)
    {
        if (!TryDecodeHandle(lease, out var index, out var incarnation))
        {
            return StoreStatus.InvalidLease;
        }

        ref var record = ref Record(index);
        var claiming = OwnedControl(ClaimingState, incarnation, lease.ParticipantToken);
        long observed = AtomicControlWord.LoadAcquire(ref record.Control);
        StoreStatus structure = ValidateControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed != claiming || record.SlotBinding != lease.SlotBinding)
        {
            StoreStatus cancel = TryCancelClaim(lease);
            return cancel == StoreStatus.CorruptStore
                ? CorruptHere()
                : StoreStatus.InvalidLease;
        }

        if (!IsParticipantActive())
        {
            StoreStatus unavailable = ParticipantUnavailableStatus();
            StoreStatus cancel = unavailable == StoreStatus.CorruptStore
                ? unavailable
                : TryCancelClaim(lease);
            return cancel == StoreStatus.CorruptStore
                ? CorruptHere()
                : StoreStatus.StoreDisposed;
        }

        var active = OwnedControl(ActiveState, incarnation, lease.ParticipantToken);
        observed = AtomicControlWord.CompareExchange(ref record.Control, active, claiming);
        if (observed != claiming)
        {
            return LeaseStatus(observed, incarnation);
        }

        if (IsParticipantActive())
        {
            return StoreStatus.Success;
        }

        StoreStatus participantStatus = ParticipantUnavailableStatus();
        if (participantStatus == StoreStatus.CorruptStore)
        {
            return CorruptHere();
        }

        var recovering = UnownedControl(RecoveringState, incarnation);
        observed = AtomicControlWord.CompareExchange(ref record.Control, recovering, active);
        if (observed == active)
        {
            _ = TryRecycle(index, incarnation, recovering, out _);
        }
        else if (ValidateControl(observed) != StoreStatus.Success)
        {
            return CorruptHere();
        }

        return participantStatus;
    }

    internal StoreStatus TryClaimAndActivate(
        ulong slotBinding,
        long acquireSequence,
        out LeaseHandle lease)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryClaimAndActivate(
            slotBinding,
            acquireSequence,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint,
            out lease);
    }

    internal StoreStatus TryClaimAndActivate(
        ulong slotBinding,
        long acquireSequence,
        in LockFreeOperationBudget budget,
        out LeaseHandle lease)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryClaimAndActivate(
            slotBinding,
            acquireSequence,
            budget,
            ref checkpoint,
            out lease);
    }

    internal StoreStatus TryClaimAndActivate<TCheckpoint>(
        ulong slotBinding,
        long acquireSequence,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out LeaseHandle lease)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        var status = TryClaim(
            slotBinding,
            acquireSequence,
            budget,
            ref checkpoint,
            out lease);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        status = TryActivate(lease);
        if (status != StoreStatus.Success)
        {
            lease = default;
        }

        return status;
    }

    /// <summary>Relinquishes a claim that never became a public acquire.</summary>
    internal StoreStatus TryCancelClaim(in LeaseHandle lease)
    {
        if (!TryDecodeHandle(lease, out var index, out var incarnation))
        {
            return StoreStatus.InvalidLease;
        }

        ref var record = ref Record(index);
        var claiming = OwnedControl(ClaimingState, incarnation, lease.ParticipantToken);
        var recovering = UnownedControl(RecoveringState, incarnation);
        var observed = AtomicControlWord.CompareExchange(ref record.Control, recovering, claiming);
        StoreStatus structure = ValidateControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed == claiming || observed == recovering)
        {
            _ = TryRecycle(index, incarnation, recovering, out _);
            return StoreStatus.Success;
        }

        return LeaseStatus(observed, incarnation);
    }

    internal bool IsActive(in LeaseHandle lease)
    {
        return TryReadActiveSlotBinding(lease, out _);
    }

    internal bool TryGetActiveSlotBinding(in LeaseHandle lease, out ulong slotBinding)
    {
        return TryReadActiveSlotBinding(lease, out slotBinding);
    }

    private bool TryReadActiveSlotBinding(in LeaseHandle lease, out ulong slotBinding)
    {
        slotBinding = 0;
        if (!TryDecodeHandle(lease, out int index, out long incarnation)
            || !IsParticipantActive())
        {
            return false;
        }

        ref LeaseRecordV2 record = ref Record(index);
        long active = OwnedControl(ActiveState, incarnation, lease.ParticipantToken);
        long control1 = AtomicControlWord.LoadAcquire(ref record.Control);
        if (ValidateControl(control1) != StoreStatus.Success || control1 != active)
        {
            return false;
        }

        ulong observedBinding = Volatile.Read(ref record.SlotBinding);
        long control2 = AtomicControlWord.LoadAcquire(ref record.Control);
        if (ValidateControl(control2) != StoreStatus.Success || control2 != control1)
        {
            return false;
        }

        if (!IsValidSlotBinding(observedBinding) || observedBinding != lease.SlotBinding)
        {
            _ = CorruptHere();
            return false;
        }

        if (!IsParticipantActive()
            || !TryConfirmStructuralControl(ref record.Control, control1))
        {
            return false;
        }

        slotBinding = observedBinding;
        return true;
    }

    /// <summary>Ends protection at the exact Active-to-Releasing CAS.</summary>
    internal StoreStatus TryRelease(in LeaseHandle lease)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRelease(lease, ref checkpoint);
    }

    internal StoreStatus TryRelease<TCheckpoint>(
        in LeaseHandle lease,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryDecodeHandle(lease, out var index, out var incarnation))
        {
            return StoreStatus.InvalidLease;
        }

        ref var record = ref Record(index);
        var active = OwnedControl(ActiveState, incarnation, lease.ParticipantToken);
        var releasing = UnownedControl(ReleasingState, incarnation);
        const int confirmationAttempts = 8;
        for (var attempt = 0; attempt < confirmationAttempts; attempt++)
        {
            var observed = AtomicControlWord.CompareExchange(ref record.Control, releasing, active);
            if (observed == active)
            {
                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas);

                // Active-to-Releasing is the public release ordering point.
                // Cleanup corruption is latched by TryRecycle but cannot undo
                // the already-completed release observed by this caller.
                _ = TryRecycle(index, incarnation, releasing, out _);
                return StoreStatus.Success;
            }

            _telemetry.RecordCasLoss();

            StoreStatus structure = ValidateControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            long observedIncarnation = Incarnation(observed);
            int observedState = State(observed);
            if (observedIncarnation == incarnation
                && observedState is ReleasingState or RecoveringState)
            {
                StoreStatus recycle = TryRecycle(
                    index,
                    incarnation,
                    observed,
                    out _);
                return recycle == StoreStatus.Success
                    ? StoreStatus.LeaseAlreadyReleased
                    : recycle;
            }

            if ((observedState == FreeState && observedIncarnation == incarnation + 1)
                || (observedState == RetiredState && observedIncarnation == incarnation))
            {
                return StoreStatus.LeaseAlreadyReleased;
            }

            if (observedIncarnation > incarnation)
            {
                return StoreStatus.InvalidLease;
            }

            // Active with the exact token would have won the CAS. Every other
            // same/older-incarnation word is a protocol regression, not a
            // stale-handle outcome. Confirm the exact mapped word before
            // poisoning; movement during confirmation retries the release.
            long confirmed = AtomicControlWord.CompareExchange(
                ref record.Control,
                observed,
                observed);
            if (confirmed == observed)
            {
                return CorruptHere();
            }

            structure = ValidateControl(confirmed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }
        }

        return StoreStatus.StoreBusy;
    }

    /// <summary>
    /// Stable double-read classification used by remove/reclamation. Claiming
    /// records are not protection; only exact Active records count.
    /// </summary>
    internal StoreStatus ScanHasActiveLease(
        ulong slotBinding,
        in LockFreeOperationBudget budget,
        out bool hasActiveLease)
    {
        hasActiveLease = false;
        for (var index = 0; index < _layout.LeaseRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref var record = ref Record(index);
            var control1 = AtomicControlWord.LoadAcquire(ref record.Control);
            StoreStatus structure = ValidateControl(control1);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (State(control1) != ActiveState)
            {
                continue;
            }

            var observedBinding = record.SlotBinding;
            var control2 = AtomicControlWord.LoadAcquire(ref record.Control);
            structure = ValidateControl(control2);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (control1 != control2)
            {
                continue;
            }

            if (!IsValidSlotBinding(observedBinding))
            {
                return CorruptFrom(nameof(LockFreeLeaseRegistry));
            }

            if (observedBinding == slotBinding)
            {
                hasActiveLease = true;
                return StoreStatus.Success;
            }
        }

        return StoreStatus.Success;
    }

    /// <summary>Releases every exact claim/lease owned by one closing participant.</summary>
    internal int ReleaseParticipantLeases(
        ulong participantToken,
        LockFreeReclaimer reclaimer)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return ReleaseParticipantLeases(participantToken, reclaimer, ref checkpoint);
    }

    internal int ReleaseParticipantLeases<TCheckpoint>(
        ulong participantToken,
        LockFreeReclaimer reclaimer,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        _ = ReleaseParticipantLeases(
            participantToken,
            reclaimer,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint,
            out int released);
        return released;
    }

    internal StoreStatus ReleaseParticipantLeases<TCheckpoint>(
        ulong participantToken,
        LockFreeReclaimer reclaimer,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out int released)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        ArgumentNullException.ThrowIfNull(reclaimer);
        released = 0;
        for (var index = 0; index < _layout.LeaseRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref var record = ref Record(index);
            long observed = AtomicControlWord.LoadAcquire(ref record.Control);
            StoreStatus structure = ValidateControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            int state = State(observed);
            ulong slotBinding = 0;
            if (state == ActiveState)
            {
                slotBinding = unchecked((ulong)Volatile.Read(ref record.SlotBinding));
                long confirmed = AtomicControlWord.LoadAcquire(ref record.Control);
                structure = ValidateControl(confirmed);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                if (confirmed != observed)
                {
                    continue;
                }

                if (!IsValidSlotBinding(slotBinding))
                {
                    return CorruptFrom(nameof(LockFreeLeaseRegistry));
                }
            }

            if (Participant(observed) != participantToken
                || state is not (ClaimingState or ActiveState))
            {
                continue;
            }

            long incarnation = Incarnation(observed);
            long recovering = UnownedControl(RecoveringState, incarnation);
            long releaseObservation = AtomicControlWord.CompareExchange(
                ref record.Control,
                recovering,
                observed);
            if (releaseObservation != observed)
            {
                _telemetry.RecordCasLoss();
                structure = ValidateControl(releaseObservation);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                continue;
            }

            // The exact participant-owned lease is now universally helpable.
            // Do not spend another retry before publishing this checkpoint or
            // recycling the exact incarnation.
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas);
            _ = TryRecycle(index, incarnation, recovering, out _);
            released++;

            if (slotBinding != 0 && budget.Check() == StoreStatus.Success)
            {
                _ = reclaimer.TryReclaim(slotBinding, budget, ref checkpoint);
            }
        }

        return StoreStatus.Success;
    }

    /// <summary>
    /// Performs an explicit bounded scan. Owner-controlled records are changed
    /// only by an exact participant-token/incarnation CAS; published unowned
    /// release phases are safe for every caller to help.
    /// </summary>
    internal StoreStatus TryRecover(
        LeaseRecoveryOptions options,
        StoreWaitOptions waitOptions,
        LockFreeReclaimer reclaimer,
        out LeaseRecoveryReport report)
    {
        report = default;
        if (!waitOptions.IsValid)
        {
            return StoreStatus.UnknownFailure;
        }

        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions);
        return TryRecover(options, budget, reclaimer, out report);
    }

    internal StoreStatus TryRecover(
        LeaseRecoveryOptions options,
        in LockFreeOperationBudget budget,
        LockFreeReclaimer reclaimer,
        out LeaseRecoveryReport report)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRecover(options, budget, reclaimer, ref checkpoint, out report);
    }

    internal StoreStatus TryRecover<TCheckpoint>(
        LeaseRecoveryOptions options,
        in LockFreeOperationBudget budget,
        LockFreeReclaimer reclaimer,
        ref TCheckpoint checkpoint,
        out LeaseRecoveryReport report)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        ArgumentNullException.ThrowIfNull(reclaimer);
        report = default;

        var scanned = 0;
        var recovered = 0;
        var active = 0;
        var unsupported = 0;
        var failed = 0;
        LockFreeOperationBudget postOwnershipCleanup = default;
        bool postOwnershipCleanupStarted = false;
        var initialAttemptBudget = budget.IsNoWait
            ? 1
            : MaxRecoveryAttemptsPerRecord;

        for (var index = 0; index < _layout.LeaseRecordCount; index++)
        {
            var bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                return recovered > 0 ? StoreStatus.Success : bound;
            }

            scanned++;
            ref var record = ref Record(index);
            var initial = AtomicControlWord.LoadAcquire(ref record.Control);
            StoreStatus structure = ValidateControl(initial);
            if (structure != StoreStatus.Success)
            {
                report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                return structure;
            }

            var targetIncarnation = Incarnation(initial);
            var initialState = State(initial);
            var targetParticipant = Participant(initial);

            if (initialState is FreeState or RetiredState)
            {
                continue;
            }

            if (initialState is not (ClaimingState
                or ActiveState
                or ReleasingState
                or RecoveringState))
            {
                report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                return CorruptHere();
            }

            var completed = false;
            for (var attempt = 0; ; attempt++)
            {
                if (attempt >= initialAttemptBudget
                    && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return recovered > 0 ? StoreStatus.Success : terminal;
                }

                bound = budget.CheckPeriodic(attempt);
                if (bound != StoreStatus.Success)
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return recovered > 0 ? StoreStatus.Success : bound;
                }

                var observed = AtomicControlWord.LoadAcquire(ref record.Control);
                structure = ValidateControl(observed);
                if (structure != StoreStatus.Success)
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return structure;
                }

                if (Incarnation(observed) != targetIncarnation)
                {
                    // The record was recycled and may already protect a new
                    // lease. One visit is permanently fenced to the incarnation
                    // first observed, so it never follows reuse.
                    completed = true;
                    break;
                }

                var state = State(observed);
                if (state is FreeState or RetiredState)
                {
                    completed = true;
                    break;
                }

                if (state is ReleasingState or RecoveringState)
                {
                    structure = TryRecycle(
                        index,
                        targetIncarnation,
                        observed,
                        out bool recycled);
                    if (structure != StoreStatus.Success)
                    {
                        report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                        return structure;
                    }

                    if (recycled)
                    {
                        _telemetry.RecordHelpedTransition();
                    }

                    completed = true;
                    break;
                }

                if (state is not (ClaimingState or ActiveState)
                    || Participant(observed) != targetParticipant
                    || (initialState == ActiveState && state != ActiveState))
                {
                    failed++;
                    completed = true;
                    break;
                }

                var slotBinding = record.SlotBinding;
                long confirmed = AtomicControlWord.LoadAcquire(ref record.Control);
                structure = ValidateControl(confirmed);
                if (structure != StoreStatus.Success)
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return structure;
                }

                if (confirmed != observed)
                {
                    continue;
                }

                // Claiming may have stopped before initializing either ordinary
                // field. Active must always carry a structurally valid exact
                // binding before recovery is allowed to end its protection.
                if ((state == ActiveState && !IsValidSlotBinding(slotBinding))
                    || (state == ClaimingState
                        && slotBinding != 0
                        && !IsValidSlotBinding(slotBinding)))
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return CorruptHere();
                }

                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.RecoveryBeforeOwnerClassification);
                bound = budget.Check();
                if (bound != StoreStatus.Success)
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return recovered > 0 ? StoreStatus.Success : bound;
                }

                var classification = _participants.ClassifyParticipant(targetParticipant);
                if (_storeControl?.Validate() == StoreStatus.CorruptStore)
                {
                    failed++;
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return CorruptHere();
                }

                confirmed = AtomicControlWord.LoadAcquire(ref record.Control);
                structure = ValidateControl(confirmed);
                if (structure != StoreStatus.Success)
                {
                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                    return structure;
                }

                if (confirmed != observed)
                {
                    continue;
                }

                var participantHandoffPublished =
                    classification.Incarnation.Token == targetParticipant
                    && classification.Kind is not (
                        ParticipantClassificationKind.Changing or
                        ParticipantClassificationKind.Inconsistent)
                    && classification.Incarnation.State is
                        LayoutV2Constants.ParticipantClosing or
                        LayoutV2Constants.ParticipantRecovering;
                var disposition = RecoveryDispositionFor(
                    state,
                    classification.Kind,
                    classification.Incarnation.State,
                    participantHandoffPublished,
                    options.RecoverCurrentProcessLeases);
                switch (disposition)
                {
                    case RecoveryDisposition.Retry:
                        continue;
                    case RecoveryDisposition.Failed:
                        failed++;
                        completed = true;
                        break;
                    case RecoveryDisposition.Unsupported:
                        unsupported++;
                        completed = true;
                        break;
                    case RecoveryDisposition.Active:
                        active++;
                        completed = true;
                        break;
                    case RecoveryDisposition.Recover:
                        bound = budget.Check();
                        if (bound != StoreStatus.Success)
                        {
                            report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                            return recovered > 0 ? StoreStatus.Success : bound;
                        }

                        var recovering = UnownedControl(RecoveringState, targetIncarnation);
                        long recoveryObservation = AtomicControlWord.CompareExchange(
                                ref record.Control,
                                recovering,
                                observed);
                        structure = ValidateControl(recoveryObservation);
                        if (structure != StoreStatus.Success)
                        {
                            report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                            return structure;
                        }

                        if (recoveryObservation != observed)
                        {
                            _telemetry.RecordCasLoss();
                            continue;
                        }

                        // This exact CAS is the recovery/release point. From here
                        // cancellation cannot strand owner-controlled state.
                        LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);
                        structure = TryRecycle(
                            index,
                            targetIncarnation,
                            recovering,
                            out _);
                        if (structure != StoreStatus.Success)
                        {
                            report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                            return structure;
                        }

                        recovered++;

                        if (slotBinding != 0)
                        {
                            StoreStatus removal = TryIsRemoveRequested(
                                slotBinding,
                                out bool isRemoveRequested);
                            if (removal != StoreStatus.Success)
                            {
                                report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                                return removal;
                            }

                            if (isRemoveRequested)
                            {
                                if (!postOwnershipCleanupStarted)
                                {
                                    postOwnershipCleanup = LockFreeOperationBudget.StartPostOwnershipCleanup();
                                    postOwnershipCleanupStarted = true;
                                }

                                // TryReclaim performs the required fresh stable
                                // no-active-lease scan before changing slot control.
                                StoreStatus reclaim = reclaimer.TryReclaim(
                                    slotBinding,
                                    postOwnershipCleanup,
                                    ref checkpoint);
                                if (reclaim == StoreStatus.CorruptStore)
                                {
                                    report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                                    return CorruptHere();
                                }
                            }
                        }

                        completed = true;
                        break;
                    default:
                        failed++;
                        completed = true;
                        break;
                }

                if (completed)
                {
                    break;
                }
            }

            if (!completed)
            {
                failed++;
                report = RecoveryReport(scanned, recovered, active, unsupported, failed);
                return StoreStatus.StoreBusy;
            }
        }

        var participantSweep = _participants.TryRecoverUnreferencedStaleParticipants(
            budget,
            ref checkpoint,
            out _);
        if (participantSweep == StoreStatus.CorruptStore)
        {
            report = RecoveryReport(scanned, recovered, active, unsupported, failed);
            return CorruptHere();
        }

        if (participantSweep != StoreStatus.Success && recovered == 0)
        {
            report = RecoveryReport(scanned, recovered, active, unsupported, failed);
            return participantSweep;
        }

        report = RecoveryReport(scanned, recovered, active, unsupported, failed);
        return StoreStatus.Success;
    }

    /// <summary>
    /// Resolves only recovery authority; the caller still exact-CASes the lease
    /// control observed before classification. A live current-process Claiming
    /// record remains ineligible even with the test/shutdown override because
    /// its claimant may still have ordinary initialization writes in flight.
    /// Exact stable Closing or published Recovering participant control provides
    /// unconditional quiescence/handoff; stale ownership is safe because its
    /// process is gone.
    /// </summary>
    internal static RecoveryDisposition RecoveryDispositionFor(
        int leaseState,
        ParticipantClassificationKind classification,
        int participantState,
        bool participantHandoffPublished,
        bool recoverCurrentProcessLeases)
    {
        if (leaseState is not (ClaimingState or ActiveState))
        {
            return RecoveryDisposition.Failed;
        }

        // Exact stable Closing/Recovering is the participant owner's durable
        // quiescent handoff. It overrides live/current/unsupported liveness
        // outcomes for both Claiming and Active; the lease mutation remains
        // fenced by the exact control word observed before classification.
        if (participantHandoffPublished
            && participantState is LayoutV2Constants.ParticipantClosing
                or LayoutV2Constants.ParticipantRecovering)
        {
            return RecoveryDisposition.Recover;
        }

        return classification switch
        {
            ParticipantClassificationKind.Changing => RecoveryDisposition.Retry,
            ParticipantClassificationKind.Inconsistent => RecoveryDisposition.Failed,
            ParticipantClassificationKind.Unsupported => RecoveryDisposition.Unsupported,
            ParticipantClassificationKind.Live => RecoveryDisposition.Active,
            ParticipantClassificationKind.CurrentProcess
                when leaseState == ClaimingState
                    && participantState != LayoutV2Constants.ParticipantClosing => RecoveryDisposition.Active,
            ParticipantClassificationKind.CurrentProcess =>
                recoverCurrentProcessLeases
                    ? RecoveryDisposition.Recover
                    : RecoveryDisposition.Active,
            ParticipantClassificationKind.Stale => RecoveryDisposition.Recover,
            _ => RecoveryDisposition.Failed
        };
    }

    /// <summary>Pure rollover transition used by layout and ABA tests.</summary>
    internal static ulong AdvanceOrRetire(long incarnation)
    {
        if (incarnation is < 1 or > TerminalIncarnation)
        {
            throw new ArgumentOutOfRangeException(nameof(incarnation));
        }

        return incarnation == TerminalIncarnation
            ? AtomicControlWord.EncodeLease(RetiredState, incarnation, participantToken: 0)
            : AtomicControlWord.EncodeLease(FreeState, incarnation + 1, participantToken: 0);
    }

    private StoreStatus TryRecycle(
        int index,
        long incarnation,
        long expectedTransition,
        out bool recycled)
    {
        recycled = false;
        ref var record = ref Record(index);
        // Do not write non-atomic fields while an unowned helper may be paused:
        // after the generation CAS a stale helper must have no write left that
        // could corrupt a newly claimed incarnation. The next Claiming owner
        // overwrites both fields before activation; Free/Retired readers ignore
        // their incarnation-fenced stale contents.
        long terminal = unchecked((long)AdvanceOrRetire(incarnation));
        const int confirmationAttempts = 8;
        for (var attempt = 0; attempt < confirmationAttempts; attempt++)
        {
            long observed = AtomicControlWord.CompareExchange(
                ref record.Control,
                terminal,
                expectedTransition);
            if (observed == expectedTransition)
            {
                recycled = true;
                return StoreStatus.Success;
            }

            _telemetry.RecordCasLoss();
            StoreStatus structure = ValidateControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (observed == terminal || Incarnation(observed) > incarnation)
            {
                return StoreStatus.Success;
            }

            // Releasing/Recovering has only one legal successor: the exact
            // Free-next-generation or Retired word above. A stable lateral,
            // same-generation reactivation, or generation regression is
            // persistent structural corruption. If the source moved during
            // confirmation, retry against the original exact transition.
            long confirmed = AtomicControlWord.CompareExchange(
                ref record.Control,
                observed,
                observed);
            if (confirmed == observed)
            {
                return CorruptHere();
            }

            structure = ValidateControl(confirmed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }
        }

        return StoreStatus.StoreBusy;
    }

    private bool TryDecodeHandle(in LeaseHandle lease, out int recordIndex, out long incarnation)
    {
        recordIndex = -1;
        incarnation = 0;
        if (lease.StoreId != _storeId
            || lease.ParticipantToken != _participant.Token
            || lease.SlotBinding == 0
            || lease.LeaseToken == 0)
        {
            return false;
        }

        var indexPlusOne = lease.LeaseToken & RecordIndexMask;
        var rawIncarnation = lease.LeaseToken >> 31;
        if (indexPlusOne == 0 || indexPlusOne > (ulong)_layout.LeaseRecordCount
            || rawIncarnation is 0 or > IncarnationMask)
        {
            return false;
        }

        recordIndex = checked((int)indexPlusOne - 1);
        incarnation = checked((long)rawIncarnation);
        return IsValidSlotBinding(lease.SlotBinding);
    }

    private bool IsParticipantActive()
    {
        ref var record = ref *(ParticipantRecordV2*)(
            _mappingBase
            + _layout.ParticipantOffset
            + ((long)_participant.RecordIndex * _layout.ParticipantStride));
        long control = AtomicControlWord.LoadAcquire(ref record.Control);
        if (!LockFreeParticipantRegistry.IsStructuralControlValid(
                control,
                _layout.ParticipantGenerationMask))
        {
            _ = CorruptHere();
            return false;
        }

        return control == _participant.ActiveControl;
    }

    private StoreStatus ParticipantUnavailableStatus() =>
        _storeControl?.Validate() == StoreStatus.CorruptStore
            ? StoreStatus.CorruptStore
            : StoreStatus.StoreDisposed;

    private bool IsValidSlotBinding(ulong binding)
    {
        return TryDecodeSlotBinding(binding, out _, out _);
    }

    private StoreStatus TryIsRemoveRequested(ulong binding, out bool isRemoveRequested)
    {
        isRemoveRequested = false;
        if (!TryDecodeSlotBinding(binding, out var slotIndex, out var generation))
        {
            return CorruptHere();
        }

        ref var slot = ref *(ValueSlotMetadataV2*)(
            _mappingBase
            + _layout.SlotMetadataOffset
            + ((long)slotIndex * _layout.SlotMetadataStride));
        var removeRequested = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.RemoveRequestedState,
            generation,
            participantToken: 0));
        long control = AtomicControlWord.LoadAcquire(ref slot.Control);
        if (!LockFreeSlotTable.TryClassifyStructuralControl(
                control,
                _layout.ParticipantRecordCount,
                out _))
        {
            return CorruptHere();
        }

        isRemoveRequested = control == removeRequested;
        return StoreStatus.Success;
    }

    private bool TryConfirmStructuralControl(ref long location, long expected)
    {
        long control = AtomicControlWord.LoadAcquire(ref location);
        return ValidateControl(control) == StoreStatus.Success && control == expected;
    }

    private bool TryDecodeSlotBinding(ulong binding, out int slotIndex, out long generation)
    {
        slotIndex = -1;
        generation = 0;
        try
        {
            var decoded = IndexBinding.Decode(binding);
            if (decoded.SlotIndex >= _layout.SlotCount)
            {
                return false;
            }

            slotIndex = decoded.SlotIndex;
            generation = decoded.Generation;
            return generation is >= 1 and <= TerminalIncarnation;
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

    private static LeaseRecoveryReport RecoveryReport(
        int scanned,
        int recovered,
        int active,
        int unsupported,
        int failed) =>
        new(scanned, recovered, active, unsupported, failed);

    private StoreStatus LeaseStatus(long observed, long expectedIncarnation)
    {
        StoreStatus structure = ValidateControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (Incarnation(observed) != expectedIncarnation)
        {
            return StoreStatus.InvalidLease;
        }

        return State(observed) is FreeState or ReleasingState or RecoveringState
            ? StoreStatus.LeaseAlreadyReleased
            : StoreStatus.InvalidLease;
    }

    internal StoreStatus ValidateStructuralControl(long control) =>
        ClassifyCapacityControl(control, out _);

    private StoreStatus ValidateControl(long control) =>
        ValidateStructuralControl(control);

    internal ref LeaseRecordV2 Record(int index) =>
        ref *(LeaseRecordV2*)(
            _mappingBase + _layout.LeaseRegistryOffset + ((long)index * _layout.LeaseStride));

    private static int State(long control) => (int)((ulong)control & 0x7UL);

    private static long Incarnation(long control) =>
        (long)(((ulong)control >> 3) & IncarnationMask);

    private static ulong Participant(long control) => ((ulong)control >> 36) & ParticipantMask;

    private StoreStatus ClassifyCapacityControl(long control, out bool occupied)
    {
        if (TryClassifyStructuralControl(
                control,
                _layout.ParticipantRecordCount,
                out occupied))
        {
            return StoreStatus.Success;
        }

        return CorruptFrom(nameof(LockFreeLeaseRegistry));
    }

    /// <summary>Pure canonical validation for a lease lifecycle word.</summary>
    internal static bool TryClassifyStructuralControl(
        long control,
        int participantRecordCount,
        out bool occupied)
    {
        occupied = true;
        int state = State(control);
        long incarnation = Incarnation(control);
        ulong participant = Participant(control);
        if (incarnation is < 1 or > TerminalIncarnation)
        {
            return false;
        }

        switch (state)
        {
            case FreeState:
                if (participant != 0)
                {
                    return false;
                }

                occupied = false;
                return true;

            case ClaimingState:
            case ActiveState:
                return ParticipantToken.IsStructurallyValid(
                    participant,
                    participantRecordCount);

            case ReleasingState:
            case RecoveringState:
                return participant == 0;

            case RetiredState:
                return participant == 0 && incarnation == TerminalIncarnation;

            default:
                return false;
        }
    }

    private StoreStatus CorruptHere(
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeLeaseRegistry),
            member,
            line);

    private StoreStatus CorruptFrom(
        string component,
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            component,
            member,
            line);

    private static long OwnedControl(int state, long incarnation, ulong participantToken) =>
        unchecked((long)AtomicControlWord.EncodeLease(
            state,
            incarnation,
            checked((int)participantToken)));

    private static long UnownedControl(int state, long incarnation) =>
        unchecked((long)AtomicControlWord.EncodeLease(state, incarnation, participantToken: 0));

    internal enum RecoveryDisposition
    {
        Recover,
        Active,
        Unsupported,
        Failed,
        Retry
    }

}
