using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Explicit, record-local recovery for layout-v2 resources.  Owner
/// classification is kept separate from universally safe helping, and every
/// owner-controlled reservation is relinquished by one exact control-word CAS.
/// </summary>
internal sealed class LockFreeRecovery
{
    private const int ClassificationRetryBudget = 64;
    private const int ClaimRetryBudget = 4;
    private const int HelpRetryBudget = 32;
    private const int IntentInsert = 1;
    private const int IntentUnlink = 2;
    private const int PhasePrepared = 1;
    private const int PhaseTargetSelected = 2;
    private const int PhaseBindingChanged = 3;
    private const int PhaseRejected = 4;
    private const int PhaseComplete = 5;
    private const int TargetPrimary = 1;
    private const int TargetOverflow = 2;

    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeSlotTable _slots;
    private readonly LockFreeKeyDirectory _directory;
    private readonly LockFreeParticipantRegistry _participants;
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;

    internal LockFreeRecovery(
        StoreLayoutV2 layout,
        LockFreeSlotTable slots,
        LockFreeKeyDirectory directory,
        LockFreeParticipantRegistry participants)
        : this(layout, slots, directory, participants, new LockFreeTelemetry())
    {
    }

    internal LockFreeRecovery(
        StoreLayoutV2 layout,
        LockFreeSlotTable slots,
        LockFreeKeyDirectory directory,
        LockFreeParticipantRegistry participants,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(participants);

        _layout = layout;
        _slots = slots;
        _directory = directory;
        _participants = participants;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
    }

    /// <summary>
    /// Scans owner-controlled value-slot lifecycles, classifies their exact
    /// participant incarnations (or consumes an exact claim-closed handoff), and
    /// hands safely recoverable slots to the ordinary generation-fenced
    /// abort/unlink helper.
    /// </summary>
    internal StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        in StoreWaitOptions waitOptions,
        out ReservationRecoveryReport report)
    {
        report = default;
        if (!waitOptions.IsValid)
        {
            return StoreStatus.UnknownFailure;
        }

        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions);
        return TryRecoverReservations(options, budget, out report);
    }

    internal StoreStatus TryRecoverReservations(
        in ReservationRecoveryOptions options,
        in LockFreeOperationBudget budget,
        out ReservationRecoveryReport report)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRecoverReservations(options, budget, ref checkpoint, out report);
    }

    internal StoreStatus TryRecoverReservations<TCheckpoint>(
        in ReservationRecoveryOptions options,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ReservationRecoveryReport report)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        var counts = new ReservationRecoveryAccumulator();
        report = default;
        LockFreeOperationBudget postOwnershipCleanup = default;
        bool postOwnershipCleanupStarted = false;

        for (var slotIndex = 0; slotIndex < _layout.SlotCount; slotIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(slotIndex);
            if (bound != StoreStatus.Success)
            {
                report = counts.ToReport();
                return counts.Recovered > 0 ? StoreStatus.Success : bound;
            }

            ref ValueSlotMetadataV2 slot = ref _slots.Slot(slotIndex);
            long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            StoreStatus structure = _slots.ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                counts.Failed++;
                report = counts.ToReport();
                return structure;
            }

            int state = State(observed);
            if (state is LockFreeSlotTable.AbortingState or LockFreeSlotTable.ReclaimingState)
            {
                StoreStatus metadata = ValidateReservationMetadata(
                    slotIndex,
                    observed,
                    ref slot,
                    budget,
                    out bool lifecycleStillCurrent,
                    out bool unreferencedPreMetadata);
                _ = ObserveStructuralStatus(metadata);
                if (metadata != StoreStatus.Success)
                {
                    if (metadata == StoreStatus.CorruptStore)
                    {
                        counts.Failed++;
                    }

                    report = counts.ToReport();
                    return metadata == StoreStatus.CorruptStore || counts.Recovered == 0
                        ? metadata
                        : StoreStatus.Success;
                }

                if (!lifecycleStillCurrent)
                {
                    continue;
                }

                StoreStatus help = unreferencedPreMetadata
                    ? HelpUnreferencedReservationRecovery(
                        IndexBinding.Encode(slotIndex, Generation(observed)),
                        budget,
                        ref checkpoint)
                    : HelpPublishedReservationTransition(
                        slotIndex,
                        observed,
                        budget,
                        ref checkpoint);
                if (help is StoreStatus.StoreBusy or StoreStatus.OperationCanceled)
                {
                    report = counts.ToReport();
                    return counts.Recovered > 0 ? StoreStatus.Success : help;
                }

                if (help != StoreStatus.Success)
                {
                    counts.Failed++;
                    if (help == StoreStatus.CorruptStore)
                    {
                        report = counts.ToReport();
                        return CorruptHere();
                    }
                }
                else
                {
                    _telemetry.RecordHelpedTransition();
                }

                continue;
            }

            if (state is not (LockFreeSlotTable.InitializingState
                or LockFreeSlotTable.ReservedState))
            {
                continue;
            }

            counts.Scanned++;
            ulong participantToken = Participant(observed);
            long generation = Generation(observed);
            StoreStatus metadataStatus = ValidateReservationMetadata(
                slotIndex,
                observed,
                ref slot,
                budget,
                out bool ownedLifecycleStillCurrent,
                out bool unreferencedOwnedPreMetadata);
            _ = ObserveStructuralStatus(metadataStatus);
            if (metadataStatus != StoreStatus.Success)
            {
                if (metadataStatus == StoreStatus.CorruptStore)
                {
                    counts.Failed++;
                }

                report = counts.ToReport();
                return metadataStatus == StoreStatus.CorruptStore || counts.Recovered == 0
                    ? metadataStatus
                    : StoreStatus.Success;
            }

            if (!ownedLifecycleStillCurrent)
            {
                continue;
            }

            LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.RecoveryBeforeOwnerClassification);
            bound = budget.Check();
            if (bound != StoreStatus.Success)
            {
                report = counts.ToReport();
                return counts.Recovered > 0 ? StoreStatus.Success : bound;
            }

            ParticipantClassification classification = default;
            bool classified = false;
            for (var attempt = 0; ; attempt++)
            {
                classification = ClassifyReservationParticipant(participantToken);
                if (_storeControl?.Validate() == StoreStatus.CorruptStore)
                {
                    counts.Failed++;
                    report = counts.ToReport();
                    return CorruptHere();
                }

                if (classification.Kind != ParticipantClassificationKind.Changing)
                {
                    classified = true;
                    break;
                }

                long current = AtomicControlWord.LoadAcquire(ref slot.Control);
                structure = _slots.ValidateStructuralControl(current);
                if (structure != StoreStatus.Success)
                {
                    counts.Failed++;
                    report = counts.ToReport();
                    return structure;
                }

                if (!IsSameOwnedLifecycle(current, observed))
                {
                    break;
                }

                bound = budget.CheckPeriodic(attempt);
                if (bound != StoreStatus.Success)
                {
                    report = counts.ToReport();
                    return counts.Recovered > 0 ? StoreStatus.Success : bound;
                }

                Thread.SpinWait(4 << Math.Min(attempt, 10));

                if (attempt + 1 >= ClassificationRetryBudget
                    && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
                {
                    report = counts.ToReport();
                    return counts.Recovered > 0 ? StoreStatus.Success : terminal;
                }
            }

            if (!classified)
            {
                long current = AtomicControlWord.LoadAcquire(ref slot.Control);
                structure = _slots.ValidateStructuralControl(current);
                if (structure != StoreStatus.Success)
                {
                    counts.Failed++;
                    report = counts.ToReport();
                    return structure;
                }

                if (IsSameOwnedLifecycle(current, observed))
                {
                    counts.Failed++;
                }

                continue;
            }

            bool recoverable = CanRecoverReservation(
                state,
                classification,
                options.RecoverCurrentProcessReservations);
            if (!recoverable)
            {
                long current = AtomicControlWord.LoadAcquire(ref slot.Control);
                structure = _slots.ValidateStructuralControl(current);
                if (structure != StoreStatus.Success)
                {
                    counts.Failed++;
                    report = counts.ToReport();
                    return structure;
                }

                CountPreservedReservation(
                    classification.Kind,
                    current,
                    observed,
                    ref counts);
                continue;
            }

            long expected = observed;
            bool currentUnreferencedPreMetadata = unreferencedOwnedPreMetadata;
            bool claimCompleted = false;
            for (var attempt = 0; ; attempt++)
            {
                // Check the public bound before the exact CAS. A pre-metadata
                // candidate is then revalidated as the final shared-state
                // observation; supported stale/quiescent ownership cannot
                // publish ordinary metadata after that proof. Once the CAS
                // wins, cancellation/deadline cannot strand the claimed abort.
                bound = budget.Check();
                if (bound != StoreStatus.Success)
                {
                    report = counts.ToReport();
                    return counts.Recovered > 0 ? StoreStatus.Success : bound;
                }

                if (currentUnreferencedPreMetadata)
                {
                    StoreStatus revalidation = ValidateReservationMetadata(
                        slotIndex,
                        expected,
                        ref slot,
                        budget,
                        out bool preMetadataLifecycleStillCurrent,
                        out bool stillUnreferencedPreMetadata);
                    _ = ObserveStructuralStatus(revalidation);
                    if (revalidation != StoreStatus.Success)
                    {
                        if (revalidation == StoreStatus.CorruptStore)
                        {
                            counts.Failed++;
                        }

                        report = counts.ToReport();
                        return revalidation == StoreStatus.CorruptStore || counts.Recovered == 0
                            ? revalidation
                            : StoreStatus.Success;
                    }

                    if (!preMetadataLifecycleStillCurrent)
                    {
                        claimCompleted = true;
                        break;
                    }

                    currentUnreferencedPreMetadata = stillUnreferencedPreMetadata;
                }

                ReservationRecoveryClaim claim = _slots.TryBeginReservationRecovery(
                    slotIndex,
                    expected);
                switch (claim.Kind)
                {
                    case ReservationRecoveryClaimKind.Acquired:
                        {
                            LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);
                            if (!postOwnershipCleanupStarted)
                            {
                                postOwnershipCleanup = LockFreeOperationBudget.StartPostOwnershipCleanup();
                                postOwnershipCleanupStarted = true;
                            }

                            StoreStatus help = HelpReservationRecovery(
                                claim.SlotBinding,
                                postOwnershipCleanup,
                                ref checkpoint,
                                currentUnreferencedPreMetadata);
                            _ = ObserveStructuralStatus(help);
                            if (help != StoreStatus.CorruptStore)
                            {
                                // The exact owner-to-Aborting CAS is the recovery
                                // ordering point. Deadline/cancellation may stop
                                // optional physical help, but cannot rewrite that
                                // durable, universally helpable recovery outcome.
                                counts.Recovered++;
                            }
                            else
                            {
                                counts.Failed++;
                                report = counts.ToReport();
                                return CorruptHere();
                            }

                            claimCompleted = true;
                            break;
                        }

                    case ReservationRecoveryClaimKind.HelpRequired:
                        {
                            StoreStatus help = HelpReservationRecovery(
                                claim.SlotBinding,
                                budget,
                                ref checkpoint,
                                currentUnreferencedPreMetadata);
                            _ = ObserveStructuralStatus(help);
                            if (help is StoreStatus.StoreBusy or StoreStatus.OperationCanceled)
                            {
                                report = counts.ToReport();
                                return counts.Recovered > 0 ? StoreStatus.Success : help;
                            }

                            if (help != StoreStatus.Success)
                            {
                                counts.Failed++;
                                if (help == StoreStatus.CorruptStore)
                                {
                                    report = counts.ToReport();
                                    return CorruptHere();
                                }
                            }
                            else
                            {
                                _telemetry.RecordHelpedTransition();
                            }

                            claimCompleted = true;
                            break;
                        }

                    case ReservationRecoveryClaimKind.CompletedRace:
                        claimCompleted = true;
                        break;

                    case ReservationRecoveryClaimKind.OwnerStateChanged:
                        {
                            expected = claim.ObservedControl;
                            StoreStatus revalidation = ValidateReservationMetadata(
                                slotIndex,
                                expected,
                                ref slot,
                                budget,
                                out bool changedLifecycleStillCurrent,
                                out bool changedUnreferencedPreMetadata);
                            _ = ObserveStructuralStatus(revalidation);
                            if (revalidation != StoreStatus.Success)
                            {
                                if (revalidation == StoreStatus.CorruptStore)
                                {
                                    counts.Failed++;
                                }

                                report = counts.ToReport();
                                return revalidation == StoreStatus.CorruptStore || counts.Recovered == 0
                                    ? revalidation
                                    : StoreStatus.Success;
                            }

                            if (!changedLifecycleStillCurrent)
                            {
                                claimCompleted = true;
                                break;
                            }

                            if (!CanRecoverReservation(
                                    State(expected),
                                    classification,
                                    options.RecoverCurrentProcessReservations))
                            {
                                long current = AtomicControlWord.LoadAcquire(ref slot.Control);
                                structure = _slots.ValidateStructuralControl(current);
                                if (structure != StoreStatus.Success)
                                {
                                    counts.Failed++;
                                    report = counts.ToReport();
                                    return structure;
                                }

                                CountPreservedReservation(
                                    classification.Kind,
                                    current,
                                    expected,
                                    ref counts);
                                claimCompleted = true;
                                break;
                            }

                            currentUnreferencedPreMetadata = changedUnreferencedPreMetadata;
                            break;
                        }

                    case ReservationRecoveryClaimKind.Inconsistent:
                    default:
                        counts.Failed++;
                        report = counts.ToReport();
                        return CorruptHere();
                }

                if (claimCompleted)
                {
                    break;
                }

                if (attempt + 1 >= ClaimRetryBudget
                    && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
                {
                    report = counts.ToReport();
                    return counts.Recovered > 0 ? StoreStatus.Success : terminal;
                }
            }

            if (!claimCompleted)
            {
                counts.Failed++;
            }
        }

        StoreStatus participantSweep = _participants.TryRecoverUnreferencedStaleParticipants(
            budget,
            ref checkpoint,
            out _);
        _ = ObserveStructuralStatus(participantSweep);
        if (participantSweep == StoreStatus.CorruptStore)
        {
            report = counts.ToReport();
            return CorruptHere();
        }

        if (participantSweep != StoreStatus.Success && counts.Recovered == 0)
        {
            report = counts.ToReport();
            return participantSweep;
        }

        report = counts.ToReport();
        return StoreStatus.Success;
    }

    private StoreStatus ValidateReservationMetadata(
        int slotIndex,
        long expectedControl,
        ref ValueSlotMetadataV2 slot,
        in LockFreeOperationBudget budget,
        out bool lifecycleStillCurrent,
        out bool unreferencedPreMetadata)
    {
        lifecycleStillCurrent = false;
        unreferencedPreMetadata = false;
        long generation = Generation(expectedControl);
        ulong exactBinding = IndexBinding.Encode(slotIndex, generation);
        int state = State(expectedControl);

        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ulong operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            ulong locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryLocation));
            ulong directoryBinding = slot.DirectoryBinding;

            if (operationRaw == 0)
            {
                StoreStatus referenceStatus = _directory.ContainsExactBindingReference(
                    exactBinding,
                    budget,
                    out bool hasDirectoryReference);
                _ = ObserveStructuralStatus(referenceStatus);
                if (referenceStatus != StoreStatus.Success)
                {
                    return referenceStatus;
                }

                long currentControl = AtomicControlWord.LoadAcquire(ref slot.Control);
                StoreStatus structure = _slots.ValidateStructuralControl(currentControl);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                ulong currentOperation = unchecked((ulong)AtomicControlWord.LoadAcquire(
                    ref slot.DirectoryOperation));
                ulong currentLocation = unchecked((ulong)AtomicControlWord.LoadAcquire(
                    ref slot.DirectoryLocation));
                ulong currentBinding = slot.DirectoryBinding;
                if (currentControl != expectedControl)
                {
                    return StoreStatus.Success;
                }

                if (currentOperation != operationRaw
                    || currentLocation != locationRaw
                    || currentBinding != directoryBinding)
                {
                    continue;
                }

                LocationReferenceStatus locationStatus = ClassifyLocationReference(
                    locationRaw,
                    generation);
                if (state == LockFreeSlotTable.ReservedState
                    || hasDirectoryReference
                    || locationStatus is LocationReferenceStatus.Current
                        or LocationReferenceStatus.Invalid)
                {
                    lifecycleStillCurrent = true;
                    return LockFreeStoreControl.ReportCorruption(
                        _storeControl,
                        nameof(LockFreeRecovery));
                }

                lifecycleStillCurrent = true;
                unreferencedPreMetadata = true;
                return StoreStatus.Success;
            }

            bool operationValid = TryDecodeRecoveryOperation(
                operationRaw,
                generation,
                state,
                out DirectoryOperation operation);
            bool locationValid = operationValid
                && IsRecoveryOperationLocationValid(operation, locationRaw, state);
            int publicationIntent = Volatile.Read(ref slot.PublicationIntent);
            long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
            StoreStatus controlStructure = _slots.ValidateStructuralControl(control2);
            if (controlStructure != StoreStatus.Success)
            {
                return controlStructure;
            }

            ulong operationRaw2 = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            ulong locationRaw2 = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryLocation));
            ulong directoryBinding2 = slot.DirectoryBinding;
            int publicationIntent2 = Volatile.Read(ref slot.PublicationIntent);
            if (control2 != expectedControl)
            {
                return StoreStatus.Success;
            }

            if (operationRaw2 != operationRaw
                || locationRaw2 != locationRaw
                || directoryBinding2 != directoryBinding
                || publicationIntent2 != publicationIntent)
            {
                continue;
            }

            if (!operationValid
                || !locationValid
                || directoryBinding != exactBinding
                || publicationIntent is not (
                    (int)SlotPublicationIntent.ExplicitReservation
                    or (int)SlotPublicationIntent.AtomicPublication))
            {
                lifecycleStillCurrent = true;
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeRecovery));
            }

            lifecycleStillCurrent = true;
            return StoreStatus.Success;
        }
    }

    private bool TryDecodeRecoveryOperation(
        ulong raw,
        long generation,
        int slotState,
        out DirectoryOperation operation)
    {
        try
        {
            operation = DirectoryOperation.Decode(raw);
        }
        catch (ArgumentOutOfRangeException)
        {
            operation = default;
            return false;
        }
        catch (OverflowException)
        {
            operation = default;
            return false;
        }

        bool owned = slotState is LockFreeSlotTable.InitializingState
            or LockFreeSlotTable.ReservedState;
        if (operation.Value != raw
            || operation.Generation != generation
            || operation.Intent is not (IntentInsert or IntentUnlink)
            || (owned && operation.Intent != IntentInsert))
        {
            return false;
        }

        return operation.Phase switch
        {
            PhasePrepared => operation.Kind == 0 && operation.Index == 0,
            PhaseRejected => operation.Intent == IntentInsert
                && operation.Kind == 0
                && operation.Index == 0,
            PhaseTargetSelected or PhaseBindingChanged =>
                IsDirectoryTargetInBounds(operation.Kind, operation.Index),
            PhaseComplete when operation.Intent == IntentUnlink && operation.Kind == 0 =>
                operation.Index == 0,
            PhaseComplete => IsDirectoryTargetInBounds(operation.Kind, operation.Index),
            _ => false,
        };
    }

    private bool IsDirectoryTargetInBounds(int kind, long index) =>
        kind switch
        {
            TargetPrimary => (ulong)index < (ulong)_layout.PrimaryLaneCount,
            TargetOverflow => (ulong)index < (ulong)_layout.SlotCount,
            _ => false,
        };

    private bool IsRecoveryOperationLocationValid(
        DirectoryOperation operation,
        ulong locationRaw,
        int slotState)
    {
        if (locationRaw == 0)
        {
            if (operation.Intent == IntentInsert)
            {
                return operation.Phase is PhasePrepared
                    or PhaseTargetSelected
                    or PhaseRejected
                    || ((slotState is LockFreeSlotTable.AbortingState
                            or LockFreeSlotTable.ReclaimingState)
                        && (operation.Phase is PhaseBindingChanged or PhaseComplete));
            }

            // Unlink helpers may reconstruct a missing location before target
            // selection, and clear it before publishing BindingChanged or
            // Complete. Zero is therefore valid throughout the unlink phases.
            return operation.Intent == IntentUnlink;
        }

        LocationReferenceStatus status = ClassifyLocationReference(
            locationRaw,
            operation.Generation);
        if (status == LocationReferenceStatus.Older)
        {
            // Older residue is tolerated only before a helper has ordered an
            // exact current-generation location. The publication helper owns
            // its exact-value cleanup.
            return operation.Intent == IntentInsert
                && operation.Phase is PhasePrepared or PhaseTargetSelected;
        }

        if (status != LocationReferenceStatus.Current)
        {
            return false;
        }

        if (operation.Phase == PhasePrepared)
        {
            // A prepared unlink starts from the location published by the
            // completed insert and has not copied that target into its own
            // descriptor yet.
            return operation.Intent == IntentUnlink;
        }

        if (operation.Phase == PhaseRejected
            || operation.Kind is not (TargetPrimary or TargetOverflow))
        {
            return false;
        }

        return locationRaw == DirectoryLocation.Encode(
            operation.Kind,
            operation.Index,
            operation.Generation);
    }

    private LocationReferenceStatus ClassifyLocationReference(ulong raw, long generation)
    {
        if (raw == 0)
        {
            return LocationReferenceStatus.None;
        }

        try
        {
            DirectoryLocation location = DirectoryLocation.Decode(raw);
            if (location.Generation < generation)
            {
                return LocationReferenceStatus.Older;
            }

            if (location.Generation > generation
                || !IsDirectoryTargetInBounds(location.Kind, location.Index))
            {
                return LocationReferenceStatus.Invalid;
            }

            return LocationReferenceStatus.Current;
        }
        catch (ArgumentOutOfRangeException)
        {
            return LocationReferenceStatus.Invalid;
        }
        catch (OverflowException)
        {
            return LocationReferenceStatus.Invalid;
        }
    }

    /// <summary>Stabilizes and classifies the participant named by a slot control.</summary>
    private ParticipantClassification ClassifyReservationParticipant(ulong participantToken) =>
        _participants.ClassifyParticipant(participantToken);

    /// <summary>
    /// Performs the optional post-resource zero-reference participant retirement
    /// pass. Current/live participants are preserved by the registry contract.
    /// </summary>
    private ParticipantTransitionResult TryReclaimReservationParticipant(ulong participantToken) =>
        _participants.TryRecoverParticipant(participantToken);

    /// <summary>Helps an already published abort/reclaim phase without classifying an owner.</summary>
    private StoreStatus HelpPublishedReservationTransition<TCheckpoint>(
        int slotIndex,
        long observedControl,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        long generation = Generation(observedControl);
        if (generation is < 1 or > LockFreeSlotTable.TerminalGeneration
            || Participant(observedControl) != 0)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeRecovery));
        }

        return HelpReservationRecovery(
            IndexBinding.Encode(slotIndex, generation),
            budget,
            ref checkpoint,
            unreferencedPreMetadata: false);
    }

    /// <summary>
    /// Uses the ordinary directory unlink protocol and slot-generation advance;
    /// no recovery-specific descriptor clear or metadata reset exists.
    /// </summary>
    private StoreStatus HelpReservationRecovery<TCheckpoint>(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        bool unreferencedPreMetadata)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (unreferencedPreMetadata)
        {
            return HelpUnreferencedReservationRecovery(
                exactBinding,
                budget,
                ref checkpoint);
        }

        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            StoreStatus unlink = _directory.TryUnlink(exactBinding, budget, ref checkpoint);
            _ = ObserveStructuralStatus(unlink);
            if (unlink is not (StoreStatus.Success or StoreStatus.NotFound))
            {
                if (unlink != StoreStatus.StoreBusy)
                {
                    return unlink;
                }

                Thread.SpinWait(4 << Math.Min(attempt, 10));
                continue;
            }

            StoreStatus completion = _slots.TryCompleteRecoveryReclaim(
                exactBinding,
                budget,
                ref checkpoint);
            _ = ObserveStructuralStatus(completion);
            if (completion == StoreStatus.Success)
            {
                return StoreStatus.Success;
            }

            if (completion != StoreStatus.StoreBusy)
            {
                return completion;
            }

            Thread.SpinWait(4 << Math.Min(attempt, 10));

            if (attempt + 1 >= HelpRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private StoreStatus HelpUnreferencedReservationRecovery<TCheckpoint>(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            StoreStatus completion = _slots.TryCompleteRecoveryReclaim(
                exactBinding,
                budget,
                ref checkpoint);
            _ = ObserveStructuralStatus(completion);
            if (completion != StoreStatus.StoreBusy)
            {
                return completion;
            }

            Thread.SpinWait(4 << Math.Min(attempt, 10));
            if (attempt + 1 >= HelpRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private StoreStatus ObserveStructuralStatus(StoreStatus status)
    {
        if (status == StoreStatus.CorruptStore)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeRecovery));
        }

        return status;
    }

    private StoreStatus CorruptHere() =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeRecovery));

    private static void CountPreservedReservation(
        ParticipantClassificationKind classification,
        long currentControl,
        long classifiedControl,
        ref ReservationRecoveryAccumulator counts)
    {
        if (!IsSameOwnedLifecycle(currentControl, classifiedControl))
        {
            return;
        }

        switch (classification)
        {
            case ParticipantClassificationKind.CurrentProcess:
            case ParticipantClassificationKind.Live:
                counts.Active++;
                break;
            case ParticipantClassificationKind.Unsupported:
                counts.Unsupported++;
                break;
            case ParticipantClassificationKind.Inconsistent:
            case ParticipantClassificationKind.Changing:
            case ParticipantClassificationKind.Stale:
            default:
                counts.Failed++;
                break;
        }
    }

    /// <summary>
    /// Initializing still permits owner-only ordinary metadata writes. A live
    /// current-process writer therefore cannot be reclaimed merely because a
    /// caller selected the controlled-shutdown override: the delayed writer
    /// could otherwise overwrite a reused generation. Closing/Recovering is the
    /// explicit quiescent handoff. Reserved has finished all ordinary metadata
    /// initialization and retains the documented explicit override.
    /// </summary>
    internal static bool CanRecoverReservation(
        int slotState,
        in ParticipantClassification classification,
        bool recoverCurrentProcessReservations)
    {
        if (classification.Kind == ParticipantClassificationKind.Stale)
        {
            return true;
        }

        bool handedOff = classification.Kind is not (
                ParticipantClassificationKind.Changing or
                ParticipantClassificationKind.Inconsistent)
            && classification.Incarnation.Token != 0
            && classification.Incarnation.State is
                LayoutV2Constants.ParticipantClosing or
                LayoutV2Constants.ParticipantRecovering;
        if (slotState == LockFreeSlotTable.InitializingState)
        {
            return handedOff;
        }

        return slotState == LockFreeSlotTable.ReservedState
            && (handedOff
                || (recoverCurrentProcessReservations
                    && classification.Kind == ParticipantClassificationKind.CurrentProcess));
    }

    private static bool IsSameOwnedLifecycle(long current, long classified)
    {
        int state = State(current);
        return state is LockFreeSlotTable.InitializingState or LockFreeSlotTable.ReservedState
            && Generation(current) == Generation(classified)
            && Participant(current) == Participant(classified)
            && Participant(current) != 0;
    }

    private static int State(long control) => (int)((ulong)control & 0x7UL);

    private static long Generation(long control) =>
        (long)(((ulong)control >> 3) & 0x1_ffff_ffffUL);

    private static ulong Participant(long control) => ((ulong)control >> 36) & 0x0fff_ffffUL;

    private enum LocationReferenceStatus
    {
        None,
        Older,
        Current,
        Invalid,
    }

    private struct ReservationRecoveryAccumulator
    {
        internal int Scanned;
        internal int Recovered;
        internal int Active;
        internal int Unsupported;
        internal int Failed;

        internal readonly ReservationRecoveryReport ToReport() => new(
            Scanned,
            Recovered,
            Active,
            Unsupported,
            Failed);
    }
}
