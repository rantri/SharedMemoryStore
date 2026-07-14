using System.Buffers;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Leasing;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Cold-path participant registration and record-local participant recovery for
/// one mapped layout-v2 store. Data operations use only the immutable token
/// returned at registration and never mutate this registry on their hot path.
/// </summary>
internal sealed unsafe class LockFreeParticipantRegistry
{
    private const ulong ParticipantMask = 0x0fff_ffffUL;

    private readonly byte* _mappingBase;
    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeTelemetry _telemetry;
    private readonly ulong _pidNamespaceId;
    private readonly LockFreeStoreControl? _storeControl;

    internal LockFreeParticipantRegistry(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout)
        : this(region, layout, new LockFreeTelemetry())
    {
    }

    internal LockFreeParticipantRegistry(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        _mappingBase = region.Pointer;
        _layout = layout;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
        _pidNamespaceId = ((StoreHeaderV2*)_mappingBase)->PidNamespaceId;
    }

    internal void InitializeRecords()
    {
        _ = InitializeRecords(LockFreeOperationBudget.UnboundedScan);
    }

    internal StoreStatus InitializeRecords(in LockFreeOperationBudget budget)
    {
        long freeControl = ToSigned(AtomicControlWord.EncodeParticipant(
            LayoutV2Constants.ParticipantFree,
            incarnation: 1,
            pid: 0));

        for (var index = 0; index < _layout.ParticipantRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref ParticipantRecordV2 record = ref Record(index);
            record.IdentityKind = LayoutV2Constants.IdentityUnknown;
            record.Reserved = 0;
            record.ProcessStartValue = 0;
            record.OpenSequence = 0;
            record.PidNamespaceId = 0;
            AtomicControlWord.StoreRelease(ref record.Control, freeControl);
        }

        return StoreStatus.Success;
    }

    internal bool TryRegister(ref StoreHeaderV2 header, out Registration registration)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRegister(
                ref header,
                LockFreeOperationBudget.StructuralAttempt,
                ref checkpoint,
                out registration)
            == StoreOpenStatus.Success;
    }

    internal StoreOpenStatus TryRegister(
        ref StoreHeaderV2 header,
        in LockFreeOperationBudget budget,
        out Registration registration)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRegister(ref header, budget, ref checkpoint, out registration);
    }

    internal StoreOpenStatus TryRegister<TCheckpoint>(
        ref StoreHeaderV2 header,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out Registration registration)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        registration = default;
        StoreOpenStatus storeState = ValidateStoreControlForOpen();
        if (storeState != StoreOpenStatus.Success)
        {
            return storeState;
        }

        int pid = Environment.ProcessId;
        bool capturedIdentity = LeaseOwnerClassifier.TryCaptureCurrentProcessIdentity(
                out int identityKind,
                out long processStartValue,
                out ulong pidNamespaceId);
        if (!capturedIdentity)
        {
            identityKind = LayoutV2Constants.IdentityUnknown;
            processStartValue = 0;
        }

        for (var index = 0; index < _layout.ParticipantRecordCount; index++)
        {
            storeState = ValidateStoreControlForOpen();
            if (storeState != StoreOpenStatus.Success)
            {
                return storeState;
            }

            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound == StoreStatus.OperationCanceled
                    ? StoreOpenStatus.OperationCanceled
                    : StoreOpenStatus.StoreBusy;
            }

            ref ParticipantRecordV2 record = ref Record(index);
            long observed = AtomicControlWord.LoadAcquire(ref record.Control);
            if (!ObserveParticipantControl(observed))
            {
                return StoreOpenStatus.IncompatibleLayout;
            }

            int state = DecodeState(observed);
            int generation = DecodeIncarnation(observed);

            if (state == LayoutV2Constants.ParticipantReclaiming
                && generation is >= 1
                && generation <= _layout.ParticipantGenerationMask)
            {
                _ = HelpReclaiming(index, generation);
                observed = AtomicControlWord.LoadAcquire(ref record.Control);
                if (!ObserveParticipantControl(observed))
                {
                    return StoreOpenStatus.IncompatibleLayout;
                }

                state = DecodeState(observed);
                generation = DecodeIncarnation(observed);
            }

            if (state != LayoutV2Constants.ParticipantFree
                || generation < 1
                || generation > _layout.ParticipantGenerationMask
                || DecodeProcessId(observed) != 0)
            {
                continue;
            }

            long registering = EncodeControl(
                LayoutV2Constants.ParticipantRegistering,
                generation,
                pid);
            LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantBeforeRegisteringCas);
            long claimObservation = AtomicControlWord.CompareExchange(
                ref record.Control,
                registering,
                observed);
            if (claimObservation != observed)
            {
                _telemetry.RecordCasLoss();
                if (!ObserveParticipantControl(claimObservation))
                {
                    return StoreOpenStatus.IncompatibleLayout;
                }

                continue;
            }

            long active = EncodeControl(LayoutV2Constants.ParticipantActive, generation, pid);
            try
            {
                // Registering grants exclusive initialization. Every ordinary
                // field is overwritten before Active release-publication, so
                // Free records may safely retain semantically dead fields from
                // an older owner.
                record.IdentityKind = identityKind;
                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantAfterIdentityKindWrite);
                record.Reserved = 0;
                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantAfterReservedWrite);
                record.ProcessStartValue = processStartValue;
                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantAfterProcessStartWrite);
                record.PidNamespaceId = pidNamespaceId;
                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.ParticipantAfterPidNamespaceWrite);
                record.OpenSequence = Interlocked.Increment(ref header.Sequence);
                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantAfterOpenSequenceWrite);

                AtomicControlWord.StoreRelease(ref record.Control, active);
                registration = new Registration(
                    index,
                    generation,
                    ParticipantToken.Encode(index, generation, _layout.ParticipantRecordCount),
                    active);
                LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ParticipantAfterActivePublication);
                return StoreOpenStatus.Success;
            }
            catch
            {
                // No token has escaped TryRegister, so neither Registering nor
                // the just-published Active control can have a legitimate data
                // reference. Retire the exact claim locally; never leave a live
                // PID record for an observer/injected construction failure.
                registration = default;
                RetireUnescapedRegistrationClaim(index, generation, pid, registering, active);
                throw;
            }
        }

        return StoreOpenStatus.ParticipantTableFull;
    }

    /// <summary>
    /// Closes and retires the local registration after its engine has stopped
    /// local entry and relinquished exact resources. If references remain, the
    /// record stays Closing and cannot be reused prematurely.
    /// </summary>
    internal void Unregister(in Registration registration)
    {
        ParticipantTransitionResult close = TryBeginClose(registration);
        if (close is not (ParticipantTransitionResult.Succeeded
            or ParticipantTransitionResult.AlreadyCompleted))
        {
            return;
        }

        _ = TryRetireClosingRegistration(
            registration,
            LockFreeOperationBudget.StartPostOwnershipCleanup());
    }

    /// <summary>
    /// Publishes the exact local Active-to-Closing handoff. The facade has
    /// already stopped and drained local entry before the engine calls this,
    /// so Closing is a durable proof that no owner-side ordinary write remains.
    /// </summary>
    internal ParticipantTransitionResult TryBeginClose(in Registration registration)
    {
        if (!registration.IsValid
            || (uint)registration.RecordIndex >= (uint)_layout.ParticipantRecordCount
            || registration.Generation > _layout.ParticipantGenerationMask)
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        int pid = DecodeProcessId(registration.ActiveControl);
        if (pid <= 0)
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        ref ParticipantRecordV2 record = ref Record(registration.RecordIndex);
        long closing = EncodeControl(
            LayoutV2Constants.ParticipantClosing,
            registration.Generation,
            pid);
        long observed = AtomicControlWord.CompareExchange(
            ref record.Control,
            closing,
            registration.ActiveControl);
        if (observed == registration.ActiveControl)
        {
            return ParticipantTransitionResult.Succeeded;
        }

        if (!ObserveParticipantControl(observed))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        return observed == closing
            ? ParticipantTransitionResult.AlreadyCompleted
            : ParticipantTransitionResult.Changed;
    }

    /// <summary>
    /// Attempts the final exact Closing-to-Reclaiming retirement within the
    /// caller's finite cleanup allowance. A timeout leaves Closing published
    /// and therefore recoverable by every other handle.
    /// </summary>
    internal StoreStatus TryRetireClosingRegistration(
        in Registration registration,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRetireClosingRegistration(registration, budget, ref checkpoint);
    }

    internal StoreStatus TryRetireClosingRegistration<TCheckpoint>(
        in Registration registration,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryGetRegistrationControls(
                registration,
                out long closing,
                out long reclaiming,
                out long terminal))
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeParticipantRegistry));
        }

        ref ParticipantRecordV2 record = ref Record(registration.RecordIndex);
        long observed = AtomicControlWord.LoadAcquire(ref record.Control);
        if (!ObserveParticipantControl(observed))
        {
            return StoreStatus.CorruptStore;
        }

        if (observed == reclaiming)
        {
            _ = HelpReclaiming(
                registration.RecordIndex,
                registration.Generation,
                ref checkpoint);
            return StoreStatus.Success;
        }

        if (observed == terminal)
        {
            return StoreStatus.Success;
        }

        if (observed != closing)
        {
            return StoreStatus.StoreBusy;
        }

        StoreStatus references = HasParticipantReferences(
            registration.Token,
            budget,
            out bool hasReferences);
        if (references != StoreStatus.Success || hasReferences)
        {
            return references == StoreStatus.Success ? StoreStatus.StoreBusy : references;
        }

        StoreStatus bound = budget.Check();
        if (bound != StoreStatus.Success)
        {
            return bound;
        }

        ParticipantTransitionResult handoff = TryAdvanceClaimClosedControl(
            ref record.Control,
            closing,
            reclaiming,
            registration.Generation);
        if (handoff == ParticipantTransitionResult.Inconsistent)
        {
            return StoreStatus.CorruptStore;
        }

        if (handoff == ParticipantTransitionResult.Changed)
        {
            return StoreStatus.StoreBusy;
        }

        observed = AtomicControlWord.LoadAcquire(ref record.Control);
        if (!ObserveParticipantControl(observed))
        {
            return StoreStatus.CorruptStore;
        }

        if (observed == reclaiming)
        {
            _ = HelpReclaiming(
                registration.RecordIndex,
                registration.Generation,
                ref checkpoint);
        }

        return StoreStatus.Success;
    }

    /// <summary>
    /// Exception-path cleanup after registration succeeded but before an
    /// engine escaped. No data claim can exist, so the construction owner can
    /// publish Closing and retire without an O(S+L) reference scan.
    /// </summary>
    internal void RetireUnreferencedRegistration(in Registration registration)
    {
        ParticipantTransitionResult close = TryBeginClose(registration);
        if (close is not (ParticipantTransitionResult.Succeeded
            or ParticipantTransitionResult.AlreadyCompleted))
        {
            return;
        }

        if (!TryGetRegistrationControls(
                registration,
                out long closing,
                out long reclaiming,
                out _))
        {
            return;
        }

        ref ParticipantRecordV2 record = ref Record(registration.RecordIndex);
        ParticipantTransitionResult handoff = TryAdvanceClaimClosedControl(
            ref record.Control,
            closing,
            reclaiming,
            registration.Generation);
        if (handoff is ParticipantTransitionResult.Succeeded
            or ParticipantTransitionResult.AlreadyCompleted)
        {
            _ = HelpReclaiming(registration.RecordIndex, registration.Generation);
        }
        else if (handoff == ParticipantTransitionResult.Changed)
        {
            _telemetry.RecordCasLoss();
        }
    }

    private void RetireUnescapedRegistrationClaim(
        int recordIndex,
        int generation,
        int pid,
        long registering,
        long active)
    {
        ref ParticipantRecordV2 record = ref Record(recordIndex);
        long reclaiming = EncodeControl(
            LayoutV2Constants.ParticipantReclaiming,
            generation,
            pid: 0);
        long terminal = AdvanceOrRetire(generation);
        long observed = AtomicControlWord.CompareExchange(
            ref record.Control,
            reclaiming,
            registering);
        if (observed == registering || observed == reclaiming)
        {
            _ = HelpReclaiming(recordIndex, generation);
            return;
        }

        long closing = EncodeControl(LayoutV2Constants.ParticipantClosing, generation, pid);
        if (observed == active)
        {
            observed = AtomicControlWord.CompareExchange(ref record.Control, closing, active);
        }

        if (observed == active || observed == closing)
        {
            ParticipantTransitionResult handoff = TryAdvanceClaimClosedControl(
                ref record.Control,
                closing,
                reclaiming,
                generation);
            if (handoff is ParticipantTransitionResult.Succeeded
                or ParticipantTransitionResult.AlreadyCompleted)
            {
                _ = HelpReclaiming(recordIndex, generation);
                return;
            }

            observed = AtomicControlWord.LoadAcquire(ref record.Control);
        }

        if (observed != terminal)
        {
            _ = ObserveParticipantControl(observed);
            _telemetry.RecordCasLoss();
        }
    }

    /// <summary>Stabilizes and conservatively classifies an exact compact token.</summary>
    internal ParticipantClassification ClassifyParticipant(ulong participantToken)
    {
        ParticipantSnapshotStatus snapshot = ReadSnapshot(
            participantToken,
            out ParticipantIncarnation incarnation);
        if (snapshot == ParticipantSnapshotStatus.Changing)
        {
            return ObserveClassification(new ParticipantClassification(
                ParticipantClassificationKind.Changing,
                incarnation));
        }

        if (snapshot == ParticipantSnapshotStatus.Inconsistent)
        {
            return ObserveClassification(new ParticipantClassification(
                ParticipantClassificationKind.Inconsistent,
                incarnation));
        }

        if (snapshot == ParticipantSnapshotStatus.Stale)
        {
            return ObserveClassification(new ParticipantClassification(
                ParticipantClassificationKind.Stale,
                incarnation));
        }

        LeaseOwnerClassification owner = ClassifySnapshotOwner(
            incarnation,
            _pidNamespaceId,
            IsPidNamespaceRecoveryEnabled());
        // Registering ordinary fields are not a coherent identity snapshot:
        // Free records deliberately retain the previous incarnation's values,
        // and a new claimant may be paused between overwrites.  PID presence is
        // therefore the only safe live-owner signal until Active publishes all
        // fields with release ordering. This may conservatively retain a
        // Registering record after PID reuse, but can never reclaim a live
        // opener from a mixed old/new identity.
        return ObserveClassification(new ParticipantClassification(Map(owner.Kind), incarnation));
    }

    /// <summary>
    /// Selects the only safe identity classifier for a stabilized participant
    /// control. Registering ordinary fields can be a mixture of the previous
    /// incarnation and the new claimant, so even apparently valid identity
    /// fields must never be compared until Active release-publication.
    /// </summary>
    internal static LeaseOwnerClassification ClassifySnapshotOwner(
        in ParticipantIncarnation incarnation,
        ulong storePidNamespaceId,
        bool presenceOnlyRecoveryEnabled = true) =>
        incarnation.State == LayoutV2Constants.ParticipantRegistering
            ? presenceOnlyRecoveryEnabled
                ? LeaseOwnerClassifier.ClassifyPresenceOnly(
                    incarnation.ProcessId,
                    storePidNamespaceId)
                : new LeaseOwnerClassification(
                    LeaseOwnerKind.Unsupported,
                    incarnation.ProcessId)
            : LeaseOwnerClassifier.Classify(incarnation);

    /// <summary>
    /// Performs conservative stale-participant retirement after resource-level
    /// recovery has removed every exact token reference.
    /// </summary>
    internal ParticipantTransitionResult TryRecoverParticipant(ulong participantToken)
    {
        return TryRecoverParticipantCore(participantToken, referencesKnownAbsent: false);
    }

    private ParticipantTransitionResult TryRecoverParticipantCore(
        ulong participantToken,
        bool referencesKnownAbsent)
    {
        ParticipantSnapshotStatus snapshot = ReadSnapshot(
            participantToken,
            out ParticipantIncarnation incarnation);
        if (snapshot == ParticipantSnapshotStatus.Changing)
        {
            return ParticipantTransitionResult.Changed;
        }

        if (snapshot == ParticipantSnapshotStatus.Inconsistent)
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        if (!TokenMatchesSnapshot(incarnation))
        {
            return ParticipantTransitionResult.AlreadyCompleted;
        }

        if (incarnation.State == LayoutV2Constants.ParticipantReclaiming)
        {
            return HelpReclaiming(incarnation.RecordIndex, incarnation.Generation);
        }

        if (incarnation.State is LayoutV2Constants.ParticipantFree
            or LayoutV2Constants.ParticipantRetired)
        {
            return ParticipantTransitionResult.AlreadyCompleted;
        }

        // Closing and Recovering are explicit claim-closed handoffs. Their
        // owner may still be a perfectly live process paused in Dispose, so OS
        // liveness classification is both unnecessary and actively harmful to
        // progress. Exact token/control fencing plus the fresh absence proof is
        // the complete retirement authority for these states.
        if (incarnation.State is LayoutV2Constants.ParticipantClosing
            or LayoutV2Constants.ParticipantRecovering)
        {
            if (!referencesKnownAbsent && HasParticipantReferences(participantToken))
            {
                return ParticipantTransitionResult.ReferencesRemain;
            }

            ParticipantTransitionResult handedOff = TryBeginReclaim(
                incarnation,
                referencesKnownAbsent);
            return handedOff is ParticipantTransitionResult.Succeeded
                or ParticipantTransitionResult.AlreadyCompleted
                ? HelpReclaiming(incarnation.RecordIndex, incarnation.Generation)
                : handedOff;
        }

        ParticipantClassification classification;
        if (snapshot == ParticipantSnapshotStatus.Stale)
        {
            classification = ObserveClassification(new ParticipantClassification(
                ParticipantClassificationKind.Stale,
                incarnation));
        }
        else
        {
            LeaseOwnerClassification owner = ClassifySnapshotOwner(
                incarnation,
                _pidNamespaceId,
                IsPidNamespaceRecoveryEnabled());
            classification = ObserveClassification(new ParticipantClassification(
                Map(owner.Kind),
                incarnation));
        }

        switch (classification.Kind)
        {
            case ParticipantClassificationKind.CurrentProcess:
            case ParticipantClassificationKind.Live:
                return ParticipantTransitionResult.LiveOwner;
            case ParticipantClassificationKind.Unsupported:
                return ParticipantTransitionResult.Unsupported;
            case ParticipantClassificationKind.Inconsistent:
                return ParticipantTransitionResult.Inconsistent;
            case ParticipantClassificationKind.Changing:
                return ParticipantTransitionResult.Changed;
            case ParticipantClassificationKind.Stale:
                break;
            default:
                return ParticipantTransitionResult.Inconsistent;
        }

        if (!referencesKnownAbsent && HasParticipantReferences(participantToken))
        {
            return ParticipantTransitionResult.ReferencesRemain;
        }

        ParticipantTransitionResult transition;
        if (incarnation.State == LayoutV2Constants.ParticipantRegistering)
        {
            transition = TryRecoverRegistering(incarnation, referencesKnownAbsent);
            return transition is ParticipantTransitionResult.Succeeded
                or ParticipantTransitionResult.AlreadyCompleted
                ? HelpReclaiming(incarnation.RecordIndex, incarnation.Generation)
                : transition;
        }

        if (incarnation.State == LayoutV2Constants.ParticipantActive)
        {
            transition = TryBeginRecovery(incarnation);
            if (transition != ParticipantTransitionResult.Succeeded)
            {
                return transition;
            }

            if (ReadSnapshot(participantToken, out incarnation) != ParticipantSnapshotStatus.Stable
                || incarnation.State != LayoutV2Constants.ParticipantRecovering)
            {
                return ParticipantTransitionResult.Changed;
            }
        }
        else if (incarnation.State != LayoutV2Constants.ParticipantRecovering)
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        if (!referencesKnownAbsent && HasParticipantReferences(participantToken))
        {
            return ParticipantTransitionResult.ReferencesRemain;
        }

        transition = TryBeginReclaim(incarnation, referencesKnownAbsent);
        return transition is ParticipantTransitionResult.Succeeded
            or ParticipantTransitionResult.AlreadyCompleted
            ? HelpReclaiming(incarnation.RecordIndex, incarnation.Generation)
            : transition;
    }

    /// <summary>Exact Active-to-Recovering CAS. The caller must have classified this snapshot stale.</summary>
    internal ParticipantTransitionResult TryBeginRecovery(ParticipantIncarnation expected)
    {
        if (!IsExactOwnedSnapshot(expected, LayoutV2Constants.ParticipantActive))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        if (!IsPidNamespaceRecoveryEnabled())
        {
            LeaseOwnerClassification owner = LeaseOwnerClassifier.Classify(expected);
            if (owner.Kind != LeaseOwnerKind.StaleProcess)
            {
                return owner.Kind is LeaseOwnerKind.CurrentProcess or LeaseOwnerKind.OtherLiveProcess
                    ? ParticipantTransitionResult.LiveOwner
                    : owner.Kind == LeaseOwnerKind.UnsafeRecord
                        ? ParticipantTransitionResult.Inconsistent
                        : ParticipantTransitionResult.Unsupported;
            }
        }

        ref ParticipantRecordV2 record = ref Record(expected.RecordIndex);
        long recovering = EncodeControl(
            LayoutV2Constants.ParticipantRecovering,
            expected.Generation,
            expected.ProcessId);
        long observed = AtomicControlWord.CompareExchange(
            ref record.Control,
            recovering,
            expected.Control);
        if (observed == expected.Control)
        {
            return ParticipantTransitionResult.Succeeded;
        }

        if (!ObserveParticipantControl(observed))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        return observed == recovering
            ? ParticipantTransitionResult.AlreadyCompleted
            : ParticipantTransitionResult.Changed;
    }

    /// <summary>
    /// Exact recovery of an incomplete Registering claim. Registering cannot be
    /// referenced by a data control, so definite owner absence permits direct
    /// publication of the universally helpable Reclaiming state.
    /// </summary>
    internal ParticipantTransitionResult TryRecoverRegistering(ParticipantIncarnation expected)
    {
        return TryRecoverRegistering(expected, referencesKnownAbsent: false);
    }

    private ParticipantTransitionResult TryRecoverRegistering(
        ParticipantIncarnation expected,
        bool referencesKnownAbsent)
    {
        if (!IsExactOwnedSnapshot(expected, LayoutV2Constants.ParticipantRegistering))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        if (!IsPidNamespaceRecoveryEnabled())
        {
            return ParticipantTransitionResult.Unsupported;
        }

        if (!referencesKnownAbsent && HasParticipantReferences(expected.Token))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        ref ParticipantRecordV2 record = ref Record(expected.RecordIndex);
        long reclaiming = EncodeControl(
            LayoutV2Constants.ParticipantReclaiming,
            expected.Generation,
            pid: 0);
        long observed = AtomicControlWord.CompareExchange(
            ref record.Control,
            reclaiming,
            expected.Control);
        if (observed == expected.Control)
        {
            return ParticipantTransitionResult.Succeeded;
        }

        if (!ObserveParticipantControl(observed))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        return observed == reclaiming
            ? ParticipantTransitionResult.AlreadyCompleted
            : ParticipantTransitionResult.Changed;
    }

    /// <summary>
    /// Exact Closing/Recovering-to-Reclaiming CAS after a zero-reference proof.
    /// The transition clears PID ownership atomically.
    /// </summary>
    internal ParticipantTransitionResult TryBeginReclaim(ParticipantIncarnation expected)
    {
        return TryBeginReclaim(expected, referencesKnownAbsent: false);
    }

    private ParticipantTransitionResult TryBeginReclaim(
        ParticipantIncarnation expected,
        bool referencesKnownAbsent)
    {
        if (!IsExactOwnedSnapshot(expected, LayoutV2Constants.ParticipantClosing)
            && !IsExactOwnedSnapshot(expected, LayoutV2Constants.ParticipantRecovering))
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        if (!referencesKnownAbsent && HasParticipantReferences(expected.Token))
        {
            return ParticipantTransitionResult.ReferencesRemain;
        }

        ref ParticipantRecordV2 record = ref Record(expected.RecordIndex);
        long reclaiming = EncodeControl(
            LayoutV2Constants.ParticipantReclaiming,
            expected.Generation,
            pid: 0);
        return TryAdvanceClaimClosedControl(
            ref record.Control,
            expected.Control,
            reclaiming,
            expected.Generation);
    }

    /// <summary>
    /// Advances an exact Closing/Recovering record to universally helpable
    /// Reclaiming. Once claim-closed, no same-generation owned state can be a
    /// successor. Exact-confirm such regressions before latching corruption;
    /// movement during confirmation remains an ordinary Changed race.
    /// </summary>
    private ParticipantTransitionResult TryAdvanceClaimClosedControl(
        ref long control,
        long expected,
        long reclaiming,
        int generation)
    {
        long terminal = AdvanceOrRetire(generation);
        const int confirmationAttempts = 8;
        for (var attempt = 0; attempt < confirmationAttempts; attempt++)
        {
            long observed = AtomicControlWord.CompareExchange(
                ref control,
                reclaiming,
                expected);
            if (observed == expected)
            {
                return ParticipantTransitionResult.Succeeded;
            }

            _telemetry.RecordCasLoss();
            if (!ObserveParticipantControl(observed))
            {
                return ParticipantTransitionResult.Inconsistent;
            }

            int observedGeneration = DecodeIncarnation(observed);
            if (observed == reclaiming
                || observed == terminal
                || observedGeneration > generation)
            {
                return ParticipantTransitionResult.AlreadyCompleted;
            }

            long confirmed = AtomicControlWord.CompareExchange(
                ref control,
                observed,
                observed);
            if (confirmed == observed)
            {
                _ = LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeParticipantRegistry));
                return ParticipantTransitionResult.Inconsistent;
            }

            if (!ObserveParticipantControl(confirmed))
            {
                return ParticipantTransitionResult.Inconsistent;
            }
        }

        return ParticipantTransitionResult.Changed;
    }

    /// <summary>
    /// Universally helps an unowned Reclaiming record advance or retire. No
    /// ordinary identity write occurs here: a delayed helper therefore cannot
    /// erase identity fields belonging to a later Active incarnation.
    /// </summary>
    internal ParticipantTransitionResult HelpReclaiming(int recordIndex, int generation)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return HelpReclaiming(recordIndex, generation, ref checkpoint);
    }

    internal ParticipantTransitionResult HelpReclaiming<TCheckpoint>(
        int recordIndex,
        int generation,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if ((uint)recordIndex >= (uint)_layout.ParticipantRecordCount
            || generation < 1
            || generation > _layout.ParticipantGenerationMask)
        {
            return ParticipantTransitionResult.Inconsistent;
        }

        ref ParticipantRecordV2 record = ref Record(recordIndex);
        long reclaiming = EncodeControl(
            LayoutV2Constants.ParticipantReclaiming,
            generation,
            pid: 0);
        long terminal = AdvanceOrRetire(generation);
        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.ParticipantBeforeReclaimGenerationAdvanceCas);

        const int confirmationAttempts = 8;
        for (var attempt = 0; attempt < confirmationAttempts; attempt++)
        {
            long observed = AtomicControlWord.CompareExchange(
                ref record.Control,
                terminal,
                reclaiming);
            if (observed == reclaiming)
            {
                return ParticipantTransitionResult.Succeeded;
            }

            _telemetry.RecordCasLoss();
            if (!ObserveParticipantControl(observed))
            {
                return ParticipantTransitionResult.Inconsistent;
            }

            int observedGeneration = DecodeIncarnation(observed);
            if (observed == terminal || observedGeneration > generation)
            {
                return ParticipantTransitionResult.AlreadyCompleted;
            }

            // A Reclaiming record has no legal same-generation transition
            // except the exact terminal word above. Confirm the impossible
            // regression against the mapped source word before latching; if it
            // moved meanwhile, retry and classify the new successor.
            long confirmed = AtomicControlWord.CompareExchange(
                ref record.Control,
                observed,
                observed);
            if (confirmed == observed)
            {
                _ = LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeParticipantRegistry));
                return ParticipantTransitionResult.Inconsistent;
            }

            if (!ObserveParticipantControl(confirmed))
            {
                return ParticipantTransitionResult.Inconsistent;
            }
        }

        return ParticipantTransitionResult.Changed;
    }

    /// <summary>Returns the exact next Free or terminal Retired participant control.</summary>
    internal long AdvanceOrRetire(int generation)
    {
        if (generation < 1 || generation > _layout.ParticipantGenerationMask)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return generation == _layout.ParticipantGenerationMask
            ? EncodeControl(LayoutV2Constants.ParticipantRetired, generation, pid: 0)
            : EncodeControl(LayoutV2Constants.ParticipantFree, generation + 1, pid: 0);
    }

    /// <summary>Conservative full scan for an exact compact participant token.</summary>
    internal bool HasParticipantReferences(ulong participantToken)
    {
        return HasParticipantReferences(
                participantToken,
                LockFreeOperationBudget.UnboundedScan,
                out bool hasReferences)
            != StoreStatus.Success
            || hasReferences;
    }

    internal StoreStatus HasParticipantReferences(
        ulong participantToken,
        in LockFreeOperationBudget budget,
        out bool hasReferences)
    {
        hasReferences = true;
        if (participantToken == 0 || participantToken > ParticipantMask)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeParticipantRegistry));
        }

        for (var index = 0; index < _layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref ValueSlotMetadataV2 slot = ref SlotRecord(index);
            long control = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (!LockFreeSlotTable.TryClassifyStructuralControl(
                    control,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeParticipantRegistry));
            }

            if (DecodeOwnedParticipant(control) == participantToken)
            {
                return StoreStatus.Success;
            }
        }

        for (var index = 0; index < _layout.LeaseRecordCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref LeaseRecordV2 lease = ref LeaseRecord(index);
            long control = AtomicControlWord.LoadAcquire(ref lease.Control);
            if (!LockFreeLeaseRegistry.TryClassifyStructuralControl(
                    control,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeParticipantRegistry));
            }

            if (DecodeOwnedParticipant(control) == participantToken)
            {
                return StoreStatus.Success;
            }
        }

        hasReferences = false;
        return StoreStatus.Success;
    }

    /// <summary>
    /// Bounded explicit-recovery sweep for stale participant incarnations that
    /// own no remaining slot or lease reference. This closes the crash window
    /// after Active participant publication but before the first resource claim.
    /// Live/current/unsupported identities are conservatively preserved.
    /// </summary>
    internal StoreStatus TryRecoverUnreferencedStaleParticipants(
        StoreWaitOptions waitOptions,
        long recoveryStarted,
        out int recoveredCount)
    {
        LockFreeOperationBudget budget = LockFreeOperationBudget.Start(waitOptions, recoveryStarted);
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRecoverUnreferencedStaleParticipants(
            budget,
            ref checkpoint,
            out recoveredCount);
    }

    internal StoreStatus TryRecoverUnreferencedStaleParticipants(
        in LockFreeOperationBudget budget,
        out int recoveredCount)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryRecoverUnreferencedStaleParticipants(
            budget,
            ref checkpoint,
            out recoveredCount);
    }

    internal StoreStatus TryRecoverUnreferencedStaleParticipants<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out int recoveredCount)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        recoveredCount = 0;
        StoreStatus initialBound = budget.Check();
        if (initialBound != StoreStatus.Success)
        {
            return initialBound;
        }

        int wordCount = checked((_layout.ParticipantRecordCount + 63) / 64);
        int candidateCount = _layout.ParticipantRecordCount;
        ulong[] sweepState = ArrayPool<ulong>.Shared.Rent(checked((candidateCount * 2) + wordCount));
        try
        {
            Span<ulong> candidates = sweepState.AsSpan(0, candidateCount);
            Span<ulong> candidateControls = sweepState.AsSpan(candidateCount, candidateCount);
            Span<ulong> referenced = sweepState.AsSpan(candidateCount * 2, wordCount);

            StoreStatus bound;
            for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
            {
                bound = budget.CheckPeriodic(wordIndex);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                referenced[wordIndex] = 0;
            }

            // Phase 1: publish claim-closed ownership before taking the absence
            // proof. A resource CAS that completed before Active->Recovering is
            // visible to the later scan; a live claim cannot begin afterward
            // because the claimant's exact Active post-check fails. Closing is
            // already locally gated and Recovering is already claim-closed.
            for (var index = 0; index < _layout.ParticipantRecordCount; index++)
            {
                bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                candidates[index] = 0;
                candidateControls[index] = 0;

                ref ParticipantRecordV2 record = ref Record(index);
                long control = AtomicControlWord.LoadAcquire(ref record.Control);
                if (!ObserveParticipantControl(control))
                {
                    return StoreStatus.CorruptStore;
                }

                int state = DecodeState(control);
                int generation = DecodeIncarnation(control);

                if (state == LayoutV2Constants.ParticipantReclaiming)
                {
                    ParticipantTransitionResult helped = HelpReclaiming(index, generation);
                    if (HasCorruptStoreControl())
                    {
                        return StoreStatus.CorruptStore;
                    }

                    if (helped == ParticipantTransitionResult.Succeeded)
                    {
                        recoveredCount++;
                    }

                    continue;
                }

                if (state is LayoutV2Constants.ParticipantFree
                    or LayoutV2Constants.ParticipantRetired)
                {
                    continue;
                }

                ulong token;
                try
                {
                    token = ParticipantToken.Encode(index, generation, _layout.ParticipantRecordCount);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                // Closing and Recovering already prove that the owner has
                // stopped all ordinary writes and that new claims fail their
                // Active recheck. Preserve the exact captured control as a
                // candidate without consulting process liveness: a live
                // disposer may intentionally be paused here.
                if (state is LayoutV2Constants.ParticipantClosing
                    or LayoutV2Constants.ParticipantRecovering)
                {
                    candidates[index] = token;
                    candidateControls[index] = unchecked((ulong)control);
                    continue;
                }

                if (state is not (LayoutV2Constants.ParticipantRegistering
                    or LayoutV2Constants.ParticipantActive))
                {
                    continue;
                }

                ParticipantClassification classification = ClassifyParticipant(token);
                if (HasCorruptStoreControl())
                {
                    return StoreStatus.CorruptStore;
                }

                bound = budget.Check();
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                ParticipantIncarnation incarnation = classification.Incarnation;
                if (classification.Kind != ParticipantClassificationKind.Stale
                    || !TokenMatchesSnapshot(incarnation)
                    || incarnation.Control != control
                    || incarnation.State != state)
                {
                    continue;
                }

                if (state == LayoutV2Constants.ParticipantRegistering)
                {
                    // Registering has never published an Active token, so the
                    // protocol forbids a slot or lease reference. Definite owner
                    // absence permits direct exact retirement of this partial open.
                    ParticipantTransitionResult registering =
                        TryRecoverRegistering(incarnation, referencesKnownAbsent: true);
                    if (HasCorruptStoreControl())
                    {
                        return StoreStatus.CorruptStore;
                    }

                    if (registering is ParticipantTransitionResult.Succeeded
                        or ParticipantTransitionResult.AlreadyCompleted)
                    {
                        ParticipantTransitionResult helped = HelpReclaiming(index, generation);
                        if (HasCorruptStoreControl())
                        {
                            return StoreStatus.CorruptStore;
                        }

                        if (helped == ParticipantTransitionResult.Succeeded)
                        {
                            recoveredCount++;
                        }
                    }

                    continue;
                }

                if (state == LayoutV2Constants.ParticipantActive)
                {
                    ParticipantTransitionResult fenced = TryBeginRecovery(incarnation);
                    if (HasCorruptStoreControl())
                    {
                        return StoreStatus.CorruptStore;
                    }

                    if (fenced == ParticipantTransitionResult.Succeeded)
                    {
                        LockFreeCheckpoint.Reach(
                            ref checkpoint,
                            LockFreeCheckpointId.ParticipantAfterRecoveryFenceBeforeReferenceScan);
                    }

                    if (fenced is ParticipantTransitionResult.Succeeded
                        or ParticipantTransitionResult.AlreadyCompleted)
                    {
                        candidates[index] = token;
                        candidateControls[index] = unchecked((ulong)EncodeControl(
                            LayoutV2Constants.ParticipantRecovering,
                            generation,
                            incarnation.ProcessId));
                    }

                    continue;
                }

            }

            // Phase 2: after every candidate is claim-closed, build one fresh
            // exact-token reference index in O(S+L). Each bit is set only when
            // the resource token equals the candidate captured for that record;
            // unrelated records and incarnations cannot affect its proof.
            for (var index = 0; index < _layout.SlotCount; index++)
            {
                bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref SlotRecord(index).Control);
                if (!LockFreeSlotTable.TryClassifyStructuralControl(
                        control,
                        _layout.ParticipantRecordCount,
                        out _))
                {
                    return LockFreeStoreControl.ReportCorruption(
                        _storeControl,
                        nameof(LockFreeParticipantRegistry));
                }

                ulong token = DecodeOwnedParticipant(control);
                MarkParticipantReference(candidates, referenced, token);
            }

            for (var index = 0; index < _layout.LeaseRecordCount; index++)
            {
                bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref LeaseRecord(index).Control);
                if (!LockFreeLeaseRegistry.TryClassifyStructuralControl(
                        control,
                        _layout.ParticipantRecordCount,
                        out _))
                {
                    return LockFreeStoreControl.ReportCorruption(
                        _storeControl,
                        nameof(LockFreeParticipantRegistry));
                }

                ulong token = DecodeOwnedParticipant(control);
                MarkParticipantReference(candidates, referenced, token);
            }

            // Phase 3: exact-token revalidation prevents a candidate bit from
            // applying to a later incarnation if another helper completed the
            // same retirement. Only absent, still claim-closed candidates may
            // publish Reclaiming and become reusable.
            for (var index = 0; index < _layout.ParticipantRecordCount; index++)
            {
                bound = budget.CheckPeriodic(index);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                ulong token = candidates[index];
                if (token == 0
                    || (referenced[index >> 6] & (1UL << (index & 63))) != 0)
                {
                    continue;
                }

                ParticipantToken decoded;
                try
                {
                    decoded = ParticipantToken.Decode(token, _layout.ParticipantRecordCount);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }
                catch (OverflowException)
                {
                    continue;
                }

                ref ParticipantRecordV2 record = ref Record(index);
                long control = AtomicControlWord.LoadAcquire(ref record.Control);
                if (!ObserveParticipantControl(control))
                {
                    return StoreStatus.CorruptStore;
                }

                int state = DecodeState(control);
                int generation = DecodeIncarnation(control);
                if (decoded.RecordIndex != index || decoded.Generation != generation)
                {
                    continue;
                }

                if (state == LayoutV2Constants.ParticipantReclaiming)
                {
                    ParticipantTransitionResult helped = HelpReclaiming(index, generation);
                    if (HasCorruptStoreControl())
                    {
                        return StoreStatus.CorruptStore;
                    }

                    if (helped == ParticipantTransitionResult.Succeeded)
                    {
                        recoveredCount++;
                    }

                    continue;
                }

                if (unchecked((ulong)control) != candidateControls[index])
                {
                    continue;
                }

                if (state is not (LayoutV2Constants.ParticipantClosing
                        or LayoutV2Constants.ParticipantRecovering)
                    || DecodeProcessId(control) <= 0
                    || ((ulong)control >> 63) != 0)
                {
                    continue;
                }

                var incarnation = new ParticipantIncarnation(
                    index,
                    generation,
                    token,
                    state,
                    DecodeProcessId(control),
                    IdentityKind: 0,
                    ProcessStartValue: 0,
                    OpenSequence: 0,
                    PidNamespaceId: 0,
                    ReservedValue: 0,
                    Control: control);
                ParticipantTransitionResult reclaim =
                    TryBeginReclaim(incarnation, referencesKnownAbsent: true);
                if (HasCorruptStoreControl())
                {
                    return StoreStatus.CorruptStore;
                }

                if (reclaim is ParticipantTransitionResult.Succeeded
                    or ParticipantTransitionResult.AlreadyCompleted)
                {
                    ParticipantTransitionResult helped = HelpReclaiming(index, generation);
                    if (HasCorruptStoreControl())
                    {
                        return StoreStatus.CorruptStore;
                    }

                    if (helped == ParticipantTransitionResult.Succeeded)
                    {
                        recoveredCount++;
                    }
                }
            }

            return HasCorruptStoreControl()
                ? StoreStatus.CorruptStore
                : StoreStatus.Success;
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(sweepState, clearArray: false);
        }
    }

    private void MarkParticipantReference(
        ReadOnlySpan<ulong> candidates,
        Span<ulong> referenced,
        ulong participantToken)
    {
        if (participantToken == 0 || participantToken > ParticipantMask)
        {
            return;
        }

        try
        {
            ParticipantToken decoded = ParticipantToken.Decode(
                participantToken,
                _layout.ParticipantRecordCount);
            if (candidates[decoded.RecordIndex] == participantToken)
            {
                referenced[decoded.RecordIndex >> 6] |= 1UL << (decoded.RecordIndex & 63);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            // Malformed owner controls are handled by their resource scanner.
            // They cannot justify participant retirement here.
        }
        catch (OverflowException)
        {
        }
    }

    private ParticipantClassification ObserveClassification(ParticipantClassification classification)
    {
        _telemetry.RecordOwnerClassification(classification.Kind);
        return classification;
    }

    private bool TryGetRegistrationControls(
        in Registration registration,
        out long closing,
        out long reclaiming,
        out long terminal)
    {
        closing = 0;
        reclaiming = 0;
        terminal = 0;
        if (!registration.IsValid
            || (uint)registration.RecordIndex >= (uint)_layout.ParticipantRecordCount
            || registration.Generation > _layout.ParticipantGenerationMask)
        {
            return false;
        }

        int pid = DecodeProcessId(registration.ActiveControl);
        if (pid <= 0)
        {
            return false;
        }

        closing = EncodeControl(
            LayoutV2Constants.ParticipantClosing,
            registration.Generation,
            pid);
        reclaiming = EncodeControl(
            LayoutV2Constants.ParticipantReclaiming,
            registration.Generation,
            pid: 0);
        terminal = AdvanceOrRetire(registration.Generation);
        return true;
    }

    private ParticipantSnapshotStatus ReadSnapshot(
        ulong participantToken,
        out ParticipantIncarnation incarnation)
    {
        incarnation = default;
        if (participantToken == 0 || participantToken > ParticipantMask)
        {
            return ParticipantSnapshotStatus.Inconsistent;
        }

        ParticipantToken decoded;
        try
        {
            decoded = ParticipantToken.Decode(participantToken, _layout.ParticipantRecordCount);
        }
        catch (ArgumentOutOfRangeException)
        {
            return ParticipantSnapshotStatus.Inconsistent;
        }
        catch (OverflowException)
        {
            return ParticipantSnapshotStatus.Inconsistent;
        }

        if (decoded.Generation > _layout.ParticipantGenerationMask)
        {
            return ParticipantSnapshotStatus.Inconsistent;
        }

        ref ParticipantRecordV2 record = ref Record(decoded.RecordIndex);
        long control1 = AtomicControlWord.LoadAcquire(ref record.Control);
        int identityKind = record.IdentityKind;
        int reserved = record.Reserved;
        long processStartValue = record.ProcessStartValue;
        long openSequence = record.OpenSequence;
        ulong pidNamespaceId = record.PidNamespaceId;
        long control2 = AtomicControlWord.LoadAcquire(ref record.Control);

        int state = DecodeState(control1);
        int generation = DecodeIncarnation(control1);
        int processId = DecodeProcessId(control1);
        incarnation = new ParticipantIncarnation(
            decoded.RecordIndex,
            generation,
            participantToken,
            state,
            processId,
            identityKind,
            processStartValue,
            openSequence,
            pidNamespaceId,
            reserved,
            control1);

        if (control1 != control2)
        {
            return ParticipantSnapshotStatus.Changing;
        }

        if (!ObserveParticipantControl(control1))
        {
            return ParticipantSnapshotStatus.Inconsistent;
        }

        if (reserved != 0
            || identityKind is < LayoutV2Constants.IdentityUnknown
                or > LayoutV2Constants.IdentityLinuxProcStartTicks
            || processStartValue < 0)
        {
            _ = LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeParticipantRegistry));
            return ParticipantSnapshotStatus.Inconsistent;
        }

        bool ownedState = state is LayoutV2Constants.ParticipantRegistering
            or LayoutV2Constants.ParticipantActive
            or LayoutV2Constants.ParticipantClosing
            or LayoutV2Constants.ParticipantRecovering;
        if ((ownedState && processId <= 0) || (!ownedState && processId != 0))
        {
            _ = LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeParticipantRegistry));
            return ParticipantSnapshotStatus.Inconsistent;
        }

        if (generation != decoded.Generation
            || state is LayoutV2Constants.ParticipantFree
                or LayoutV2Constants.ParticipantReclaiming
                or LayoutV2Constants.ParticipantRetired)
        {
            return ParticipantSnapshotStatus.Stale;
        }

        if ((state is LayoutV2Constants.ParticipantActive
                or LayoutV2Constants.ParticipantClosing
                or LayoutV2Constants.ParticipantRecovering)
            && (openSequence <= 0
                || (identityKind != LayoutV2Constants.IdentityUnknown
                    && processStartValue == 0)))
        {
            _ = LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeParticipantRegistry));
            return ParticipantSnapshotStatus.Inconsistent;
        }

        return ParticipantSnapshotStatus.Stable;
    }

    private bool IsExactOwnedSnapshot(ParticipantIncarnation snapshot, int requiredState)
    {
        if ((uint)snapshot.RecordIndex >= (uint)_layout.ParticipantRecordCount
            || snapshot.Generation < 1
            || snapshot.Generation > _layout.ParticipantGenerationMask
            || snapshot.Token == 0
            || snapshot.Token > ParticipantMask
            || snapshot.State != requiredState
            || snapshot.ProcessId <= 0)
        {
            return false;
        }

        try
        {
            ParticipantToken decoded = ParticipantToken.Decode(
                snapshot.Token,
                _layout.ParticipantRecordCount);
            return decoded.RecordIndex == snapshot.RecordIndex
                && decoded.Generation == snapshot.Generation
                && snapshot.Control == EncodeControl(
                    requiredState,
                    snapshot.Generation,
                    snapshot.ProcessId);
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

    private bool TokenMatchesSnapshot(ParticipantIncarnation snapshot)
    {
        if ((uint)snapshot.RecordIndex >= (uint)_layout.ParticipantRecordCount
            || snapshot.Token == 0
            || snapshot.Token > ParticipantMask)
        {
            return false;
        }

        try
        {
            ParticipantToken decoded = ParticipantToken.Decode(
                snapshot.Token,
                _layout.ParticipantRecordCount);
            return decoded.RecordIndex == snapshot.RecordIndex
                && decoded.Generation == snapshot.Generation;
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

    internal ref ParticipantRecordV2 Record(int index) =>
        ref *(ParticipantRecordV2*)(
            _mappingBase + _layout.ParticipantOffset + ((long)index * _layout.ParticipantStride));

    private bool IsPidNamespaceRecoveryEnabled() =>
        AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)_mappingBase)->PidNamespaceMode)
            == LayoutV2Constants.PidNamespaceRecoveryEnabled;

    private ref ValueSlotMetadataV2 SlotRecord(int index) =>
        ref *(ValueSlotMetadataV2*)(
            _mappingBase + _layout.SlotMetadataOffset + ((long)index * _layout.SlotMetadataStride));

    private ref LeaseRecordV2 LeaseRecord(int index) =>
        ref *(LeaseRecordV2*)(
            _mappingBase + _layout.LeaseRegistryOffset + ((long)index * _layout.LeaseStride));

    private static ParticipantClassificationKind Map(LeaseOwnerKind ownerKind) => ownerKind switch
    {
        LeaseOwnerKind.CurrentProcess => ParticipantClassificationKind.CurrentProcess,
        LeaseOwnerKind.OtherLiveProcess => ParticipantClassificationKind.Live,
        LeaseOwnerKind.StaleProcess => ParticipantClassificationKind.Stale,
        LeaseOwnerKind.Unsupported => ParticipantClassificationKind.Unsupported,
        LeaseOwnerKind.UnsafeRecord => ParticipantClassificationKind.Inconsistent,
        _ => ParticipantClassificationKind.Inconsistent
    };

    private static int DecodeState(long control) => (int)((ulong)control & 0x7UL);

    private static int DecodeIncarnation(long control) =>
        (int)(((ulong)control >> 3) & ParticipantMask);

    private static int DecodeProcessId(long control) =>
        unchecked((int)(((ulong)control >> 31) & 0xffff_ffffUL));

    private static ulong DecodeOwnedParticipant(long control) =>
        ((ulong)control >> 36) & ParticipantMask;

    private static long EncodeControl(int state, int generation, int pid) =>
        ToSigned(AtomicControlWord.EncodeParticipant(state, generation, pid));

    private bool ObserveParticipantControl(long control)
    {
        if (IsStructuralControlValid(control, _layout.ParticipantGenerationMask))
        {
            return true;
        }

        _ = LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeParticipantRegistry));
        return false;
    }

    private bool HasCorruptStoreControl() =>
        _storeControl?.Validate() == StoreStatus.CorruptStore;

    /// <summary>Pure canonical validation for a participant lifecycle word.</summary>
    internal static bool IsStructuralControlValid(long control, int generationMask)
    {
        int state = DecodeState(control);
        int generation = DecodeIncarnation(control);
        int processId = DecodeProcessId(control);
        bool ownedState = state is LayoutV2Constants.ParticipantRegistering
            or LayoutV2Constants.ParticipantActive
            or LayoutV2Constants.ParticipantClosing
            or LayoutV2Constants.ParticipantRecovering;
        return ((ulong)control >> 63) == 0
            && state is >= LayoutV2Constants.ParticipantFree
                and <= LayoutV2Constants.ParticipantRetired
            && generation is >= 1
                && generation <= generationMask
            && (ownedState ? processId > 0 : processId == 0)
            && (state != LayoutV2Constants.ParticipantRetired
                || generation == generationMask);
    }

    private StoreOpenStatus ValidateStoreControlForOpen()
    {
        StoreStatus state = _storeControl?.Validate() ?? StoreStatus.Success;
        return state switch
        {
            StoreStatus.Success => StoreOpenStatus.Success,
            StoreStatus.UnsupportedPlatform => StoreOpenStatus.UnsupportedPlatform,
            _ => StoreOpenStatus.IncompatibleLayout
        };
    }

    private static long ToSigned(ulong value) => unchecked((long)value);

    internal readonly record struct Registration(
        int RecordIndex,
        int Generation,
        ulong Token,
        long ActiveControl)
    {
        internal bool IsValid => Generation > 0 && Token != 0;
    }

    private enum ParticipantSnapshotStatus
    {
        Stable,
        Stale,
        Changing,
        Inconsistent
    }
}
