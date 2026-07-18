using System.Runtime.CompilerServices;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Result of attempting the exact owner-controlled slot transition used by
/// reservation recovery.  A failed compare/exchange is not itself an error:
/// the observed state determines whether the owner completed, another helper
/// already took over, or the slot must be classified again.
/// </summary>
internal enum ReservationRecoveryClaimKind
{
    Acquired,
    HelpRequired,
    CompletedRace,
    OwnerStateChanged,
    Inconsistent
}

/// <summary>Exact slot observation returned by a reservation recovery CAS.</summary>
internal readonly record struct ReservationRecoveryClaim(
    ReservationRecoveryClaimKind Kind,
    ulong SlotBinding,
    long ObservedControl);

/// <summary>
/// Generation-fenced value-slot state transitions for layout 2.0.
/// Directory placement/unlink remains the responsibility of
/// <c>LockFreeKeyDirectory</c>.
/// </summary>
internal sealed unsafe class LockFreeSlotTable
{
    internal const int FreeState = 0;
    internal const int InitializingState = 1;
    internal const int ReservedState = 2;
    internal const int PublishedState = 3;
    internal const int RemoveRequestedState = 4;
    internal const int AbortingState = 5;
    internal const int ReclaimingState = 6;
    internal const int RetiredState = 7;
    internal const long TerminalGeneration = 0x1_ffff_ffffL;

    private const ulong SlotIndexMask = 0x7fff_ffffUL;
    private const ulong SlotGenerationMask = 0x1_ffff_ffffUL;
    private const ulong ParticipantMask = 0x0fff_ffffUL;
    private const int ResidueCleanupRetryBudget = 128;
    private const int AdvanceRetryBudget = 128;

    private readonly byte* _mappingBase;
    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeParticipantRegistry.Registration _participant;
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;
    private readonly ulong _storeId;
    private readonly long[] _storeFullSnapshot;
    private int _nextSlot;
    private int _storeFullProofGate;

    internal LockFreeSlotTable(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeParticipantRegistry.Registration participant)
        : this(region, layout, participant, new LockFreeTelemetry())
    {
    }

    internal LockFreeSlotTable(
        MemoryMappedStoreRegion region,
        StoreLayoutV2 layout,
        in LockFreeParticipantRegistry.Registration participant,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!participant.IsValid || participant.Token > ParticipantMask)
        {
            throw new ArgumentOutOfRangeException(nameof(participant));
        }

        _mappingBase = region.Pointer;
        _layout = layout;
        _participant = participant;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
        // A failed allocation scan is not by itself a linearizable StoreFull
        // witness: a reusable slot can rotate behind a sequential scanner. The
        // per-open, process-local buffer supports an exact double collect only
        // on that rare slow path, with no per-operation allocation or shared
        // cache line. Maximum configured cost is eight bytes per value slot.
        _storeFullSnapshot = GC.AllocateUninitializedArray<long>(layout.SlotCount);
        // Spread the first local claim of concurrently opened publishers over
        // the bounded slot table. Later claims remain a local round-robin scan.
        _nextSlot = (participant.RecordIndex % layout.SlotCount) - 1;
        _storeId = ((StoreHeaderV2*)_mappingBase)->StoreId;
        if (_storeId == 0)
        {
            throw new ArgumentException("The layout-v2 mapping has no store incarnation.", nameof(region));
        }
    }

    internal StoreStatus TryClaimReservation(
        ulong keyHash,
        int keyLength,
        int descriptorLength,
        int payloadLength,
        out ReservationHandle reservation) =>
        TryClaimReservationNoCheckpoint(
            keyHash,
            keyLength,
            descriptorLength,
            payloadLength,
            SlotPublicationIntent.ExplicitReservation,
            LockFreeOperationBudget.StructuralAttempt,
            out reservation);

    internal StoreStatus TryClaimReservation(
        ulong keyHash,
        int keyLength,
        int descriptorLength,
        int payloadLength,
        in LockFreeOperationBudget budget,
        out ReservationHandle reservation)
    {
        return TryClaimReservationNoCheckpoint(
            keyHash,
            keyLength,
            descriptorLength,
            payloadLength,
            SlotPublicationIntent.ExplicitReservation,
            budget,
            out reservation);
    }

    private StoreStatus TryClaimReservationNoCheckpoint(
        ulong keyHash,
        int keyLength,
        int descriptorLength,
        int payloadLength,
        SlotPublicationIntent publicationIntent,
        in LockFreeOperationBudget budget,
        out ReservationHandle reservation)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryClaimReservation(
            keyHash,
            keyLength,
            descriptorLength,
            payloadLength,
            publicationIntent,
            budget,
            ref checkpoint,
            out reservation);
    }

    internal StoreStatus TryClaimReservation<TCheckpoint>(
        ulong keyHash,
        int keyLength,
        int descriptorLength,
        int payloadLength,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ReservationHandle reservation)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint> =>
        TryClaimReservation(
            keyHash,
            keyLength,
            descriptorLength,
            payloadLength,
            SlotPublicationIntent.ExplicitReservation,
            budget,
            ref checkpoint,
            out reservation);

    internal StoreStatus TryClaimReservation<TCheckpoint>(
        ulong keyHash,
        int keyLength,
        int descriptorLength,
        int payloadLength,
        SlotPublicationIntent publicationIntent,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ReservationHandle reservation)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        reservation = default;
        if (!IsParticipantActive())
        {
            return ParticipantUnavailableStatus();
        }

        if (publicationIntent is not (
                SlotPublicationIntent.ExplicitReservation
                or SlotPublicationIntent.AtomicPublication)
            || keyLength <= 0 || keyLength > _layout.MaxKeyBytes
            || descriptorLength < 0 || descriptorLength > _layout.MaxDescriptorBytes
            || payloadLength < 0 || payloadLength > _layout.MaxValueBytes)
        {
            return StoreStatus.InvalidReservation;
        }

        int start = (int)((uint)Interlocked.Increment(ref _nextSlot) % (uint)_layout.SlotCount);
        for (var visited = 0; visited < _layout.SlotCount; visited++)
        {
            StoreStatus bound = budget.CheckPeriodic(visited);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            int slotIndex = start + visited;
            if (slotIndex >= _layout.SlotCount)
            {
                slotIndex -= _layout.SlotCount;
            }

            ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
            long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            StoreStatus structure = ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (State(observed) != FreeState)
            {
                continue;
            }

            long generation = Generation(observed);
            long initializing = Signed(AtomicControlWord.EncodeSlot(
                InitializingState,
                generation,
                checked((int)_participant.Token)));
            long claimObservation = AtomicControlWord.CompareExchange(
                ref slot.Control,
                initializing,
                observed);
            if (claimObservation != observed)
            {
                _telemetry.RecordCasLoss();
                structure = ValidateStructuralControl(claimObservation);
                if (structure != StoreStatus.Success)
                {
                    return structure;
                }

                continue;
            }

            LockFreeCheckpoint.ObserveSlotResource(
                ref checkpoint,
                LockFreeSlotResourceEventKind.Claim,
                slotIndex,
                generation);

            ulong binding = IndexBinding.Encode(slotIndex, generation);
            reservation = new ReservationHandle(
                _storeId,
                _participant.Token,
                binding,
                payloadLength);
            StoreStatus residueStatus = SanitizeOlderDirectoryResidue(
                ref slot,
                generation,
                budget,
                exactGenerationIsBusy: false);
            if (residueStatus != StoreStatus.Success)
            {
                // Never roll Initializing(g) back to Free(g). Reusing the same
                // control word would introduce ABA into slot snapshots and let
                // a delayed observer confuse two claims. Relinquish through the
                // normal helpable lifecycle so every reuse advances generation.
                StoreStatus beginAbort = TryBeginAbort(reservation);
                if (residueStatus == StoreStatus.CorruptStore)
                {
                    reservation = default;
                    return CorruptFrom(nameof(LockFreeSlotTable));
                }

                StoreStatus cleanup = TryCompleteRecoveryReclaim(
                    binding,
                    LockFreeOperationBudget.StructuralAttempt,
                    ref checkpoint);
                reservation = default;
                if (beginAbort is not (StoreStatus.Success or StoreStatus.InvalidReservation)
                    || cleanup == StoreStatus.CorruptStore)
                {
                    return CorruptFrom(nameof(LockFreeSlotTable));
                }

                return residueStatus;
            }

            // The first claim CAS already contains the complete token. Its exact
            // participant control is acquire-revalidated before metadata becomes
            // usable, closing the crash/participant-retirement window.
            if (!IsParticipantActive())
            {
                StoreStatus unavailable = ParticipantUnavailableStatus();
                if (unavailable != StoreStatus.CorruptStore)
                {
                    _ = TryBeginAbort(reservation);
                    _ = TryCompleteReclaim(
                        reservation,
                        LockFreeOperationBudget.StructuralAttempt,
                        ref checkpoint);
                }

                reservation = default;
                return unavailable;
            }

            LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.SlotClaimAfterParticipantRecheck);
            bound = budget.Check();
            if (bound != StoreStatus.Success)
            {
                _ = TryBeginAbort(reservation);
                _ = TryCompleteReclaim(reservation, LockFreeOperationBudget.StructuralAttempt, ref checkpoint);
                reservation = default;
                return bound;
            }

            // This ordinary field is immutable for the claimed generation and
            // is published by the later directory-operation/cell release chain.
            // A bare Initializing control predates it and is not a read witness.
            Volatile.Write(ref slot.PublicationIntent, (int)publicationIntent);
            slot.DirectoryBinding = binding;
            slot.KeyHash = keyHash;
            slot.KeyLength = keyLength;
            slot.DescriptorLength = descriptorLength;
            slot.ValueLength = payloadLength;
            AtomicControlWord.StoreRelease(ref slot.BytesAdvanced, 0);
            slot.CommitSequence = 0;
            return StoreStatus.Success;
        }

        return StoreStatus.StoreFull;
    }

    /// <summary>
    /// Converts a scan-exhaustion candidate into an exact physical-capacity
    /// result. Equal all-occupied collects in the same order prove a common
    /// point between the two collects at which every slot was unavailable.
    /// The method distinguishes an unconfirmed transient from a caller-budget
    /// terminal so finite and infinite callers can apply their wait policy.
    /// </summary>
    internal StoreStatus TryProveStoreFull<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out bool provenFull)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        provenFull = false;
        if (Interlocked.CompareExchange(ref _storeFullProofGate, 1, 0) != 0)
        {
            return StoreStatus.Success;
        }

        long proofToken = 0;
        var candidateObserved = false;
        var proofConfirmed = false;
        try
        {
            for (var slotIndex = 0; slotIndex < _layout.SlotCount; slotIndex++)
            {
                StoreStatus bound = budget.CheckPeriodic(slotIndex);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref Slot(slotIndex).Control);
                StoreStatus classification = ClassifyFullnessControl(control, out bool occupied);
                if (classification != StoreStatus.Success)
                {
                    return classification;
                }

                if (!occupied)
                {
                    return StoreStatus.Success;
                }

                _storeFullSnapshot[slotIndex] = control;
            }

            proofToken = LockFreeCheckpoint.BeginStoreFullProof(
                ref checkpoint,
                _layout.SlotCount);
            candidateObserved = true;
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification);

            for (var slotIndex = 0; slotIndex < _layout.SlotCount; slotIndex++)
            {
                StoreStatus bound = budget.CheckPeriodic(slotIndex);
                if (bound != StoreStatus.Success)
                {
                    return bound;
                }

                long control = AtomicControlWord.LoadAcquire(ref Slot(slotIndex).Control);
                StoreStatus classification = ClassifyFullnessControl(control, out bool occupied);
                if (classification != StoreStatus.Success)
                {
                    return classification;
                }

                if (!occupied || control != _storeFullSnapshot[slotIndex])
                {
                    return StoreStatus.Success;
                }
            }

            proofConfirmed = true;
            LockFreeCheckpoint.CompleteStoreFullProof(
                ref checkpoint,
                proofToken,
                confirmed: true);
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.StoreFullAfterExactDoubleCollect);
            provenFull = true;
            return StoreStatus.Success;
        }
        finally
        {
            if (candidateObserved && !proofConfirmed)
            {
                LockFreeCheckpoint.CompleteStoreFullProof(
                    ref checkpoint,
                    proofToken,
                    confirmed: false);
            }

            Volatile.Write(ref _storeFullProofGate, 0);
        }
    }

    internal Span<byte> GetInitializingKeySpan(in ReservationHandle reservation)
    {
        return TryReadInitializingProjection(
                reservation,
                out int slotIndex,
                out int keyLength,
                out _)
            ? new Span<byte>(
                _mappingBase + _layout.KeyStorageOffset + ((long)slotIndex * _layout.KeyStride),
                keyLength)
            : Span<byte>.Empty;
    }

    internal Span<byte> GetInitializingDescriptorSpan(in ReservationHandle reservation)
    {
        return TryReadInitializingProjection(
                reservation,
                out int slotIndex,
                out _,
                out int descriptorLength)
            ? new Span<byte>(
                _mappingBase + _layout.DescriptorStorageOffset
                    + ((long)slotIndex * _layout.DescriptorStride),
                descriptorLength)
            : Span<byte>.Empty;
    }

    private bool TryReadInitializingProjection(
        in ReservationHandle reservation,
        out int slotIndex,
        out int keyLength,
        out int descriptorLength)
    {
        slotIndex = -1;
        keyLength = 0;
        descriptorLength = 0;
        if (_storeControl is not null && !_storeControl.IsReady)
        {
            return false;
        }

        if (!TryDecodeHandle(reservation, out slotIndex, out long generation))
        {
            return false;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long expected = OwnedControl(InitializingState, generation, reservation.ParticipantToken);
        long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
        if (ValidateStructuralControl(control1) != StoreStatus.Success
            || control1 != expected
            || !IsParticipantActive())
        {
            return false;
        }

        ulong directoryBinding = Volatile.Read(ref slot.DirectoryBinding);
        int observedKeyLength = Volatile.Read(ref slot.KeyLength);
        int observedDescriptorLength = Volatile.Read(ref slot.DescriptorLength);
        int observedValueLength = Volatile.Read(ref slot.ValueLength);
        int publicationIntent = Volatile.Read(ref slot.PublicationIntent);
        long advanced = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
        long keyOffset = Volatile.Read(ref slot.KeyOffset);
        long descriptorOffset = Volatile.Read(ref slot.DescriptorOffset);
        long payloadOffset = Volatile.Read(ref slot.PayloadOffset);
        long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
        if (control2 != control1 || !IsParticipantActive())
        {
            _ = ValidateStructuralControl(control2);
            return false;
        }

        long expectedKeyOffset = _layout.KeyStorageOffset + ((long)slotIndex * _layout.KeyStride);
        long expectedDescriptorOffset =
            _layout.DescriptorStorageOffset + ((long)slotIndex * _layout.DescriptorStride);
        long expectedPayloadOffset =
            _layout.PayloadStorageOffset + ((long)slotIndex * _layout.PayloadStride);
        if (directoryBinding != reservation.SlotBinding
            || observedKeyLength is < 1 || observedKeyLength > _layout.MaxKeyBytes
            || observedDescriptorLength < 0
            || observedDescriptorLength > _layout.MaxDescriptorBytes
            || observedValueLength < 0 || observedValueLength > _layout.MaxValueBytes
            || observedValueLength != reservation.PayloadLength
            || publicationIntent is not (
                (int)SlotPublicationIntent.ExplicitReservation
                or (int)SlotPublicationIntent.AtomicPublication)
            || advanced != 0
            || keyOffset != expectedKeyOffset
            || descriptorOffset != expectedDescriptorOffset
            || payloadOffset != expectedPayloadOffset)
        {
            _ = CorruptHere();
            return false;
        }

        if (_storeControl is not null && !_storeControl.IsReady)
        {
            return false;
        }

        keyLength = observedKeyLength;
        descriptorLength = observedDescriptorLength;
        return true;
    }

    internal StoreStatus TryMarkReserved(in ReservationHandle reservation)
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            return StoreStatus.InvalidReservation;
        }

        if (!IsParticipantActive())
        {
            return ParticipantUnavailableStatus();
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long initializing = OwnedControl(InitializingState, generation, reservation.ParticipantToken);
        long reserved = OwnedControl(ReservedState, generation, reservation.ParticipantToken);
        long observed = AtomicControlWord.CompareExchange(ref slot.Control, reserved, initializing);
        return observed == initializing
            ? StoreStatus.Success
            : ReservationStatus(observed, generation);
    }

    internal bool IsReservationPending(in ReservationHandle reservation)
    {
        return TryReadReservationProjection(
            reservation,
            out _,
            out _,
            out _,
            out _);
    }

    internal StoreStatus ClassifyDirectoryBinding(
        ulong exactBinding,
        out int state,
        out SlotPublicationIntent publicationIntent)
    {
        state = FreeState;
        publicationIntent = SlotPublicationIntent.None;
        if (!TryDecodeSlotBinding(exactBinding, out int slotIndex, out long generation))
        {
            return StoreStatus.CorruptStore;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (!TryClassifyStructuralControl(
                    control1,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return StoreStatus.CorruptStore;
            }

            long snapshotGeneration = Generation(control1);
            if (snapshotGeneration > generation)
            {
                return StoreStatus.NotFound;
            }

            if (snapshotGeneration < generation)
            {
                return StoreStatus.CorruptStore;
            }

            int observedState = State(control1);
            if (observedState == RetiredState)
            {
                return generation == TerminalGeneration && Participant(control1) == 0
                    ? StoreStatus.NotFound
                    : StoreStatus.CorruptStore;
            }

            ulong observedBinding = Volatile.Read(ref slot.DirectoryBinding);
            ulong observedOperation = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            int rawIntent = Volatile.Read(ref slot.PublicationIntent);
            long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
            ulong confirmedBinding = Volatile.Read(ref slot.DirectoryBinding);
            ulong confirmedOperation = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            if (control1 != control2
                || observedBinding != confirmedBinding
                || observedOperation != confirmedOperation)
            {
                continue;
            }

            if (observedState == FreeState
                || observedBinding != exactBinding
                || !IsCurrentReferencedOperationValid(
                    observedOperation,
                    generation,
                    observedState))
            {
                return StoreStatus.CorruptStore;
            }

            bool owned = observedState is InitializingState or ReservedState;
            ulong participant = Participant(control1);
            if (owned
                    ? !ParticipantToken.IsStructurallyValid(
                        participant,
                        _layout.ParticipantRecordCount)
                    : participant != 0)
            {
                return StoreStatus.CorruptStore;
            }

            publicationIntent = (SlotPublicationIntent)rawIntent;
            if (publicationIntent is not (
                    SlotPublicationIntent.ExplicitReservation
                    or SlotPublicationIntent.AtomicPublication))
            {
                publicationIntent = SlotPublicationIntent.None;
                return StoreStatus.CorruptStore;
            }

            state = observedState;
            return StoreStatus.Success;
        }

        return StoreStatus.StoreBusy;
    }

    /// <summary>
    /// Classifies the exact lifecycle observed after the directory has reported
    /// a completed insert. Normal recovery preserves a live Active owner, but
    /// an exact adversarial cancellation or correctly quiesced administrative
    /// recovery can follow this boundary. Generation advancement is therefore
    /// an ordinary invalid-reservation observation, not evidence of corruption.
    /// </summary>
    internal StoreStatus ClassifyReservationAfterDirectoryInsert(
        in ReservationHandle reservation)
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            // The engine created this handle in the same operation. Failure to
            // decode it cannot be caused by cross-process recovery.
            return CorruptHere();
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
            StoreStatus structure = ValidateStructuralControl(control1);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            long observedGeneration = Generation(control1);
            if (observedGeneration > generation)
            {
                return StoreStatus.InvalidReservation;
            }

            if (observedGeneration < generation)
            {
                return CorruptHere();
            }

            ulong observedBinding = Volatile.Read(ref slot.DirectoryBinding);
            SlotPublicationIntent publicationIntent =
                (SlotPublicationIntent)Volatile.Read(ref slot.PublicationIntent);
            long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
            ulong confirmedBinding = Volatile.Read(ref slot.DirectoryBinding);
            if (control1 != control2 || observedBinding != confirmedBinding)
            {
                continue;
            }

            if (observedBinding != reservation.SlotBinding
                || publicationIntent is not (
                    SlotPublicationIntent.ExplicitReservation
                    or SlotPublicationIntent.AtomicPublication))
            {
                return CorruptHere();
            }

            int state = State(control1);
            ulong participant = Participant(control1);
            if (state == ReservedState)
            {
                if (participant != reservation.ParticipantToken)
                {
                    return CorruptHere();
                }

                // Participant retirement may precede the recovery CAS that
                // changes Reserved to Aborting. Treat that legal interval as
                // cancellation rather than accepting a dead owner.
                if (IsParticipantActive())
                {
                    return StoreStatus.Success;
                }

                return ParticipantUnavailableStatus() == StoreStatus.CorruptStore
                    ? StoreStatus.CorruptStore
                    : StoreStatus.InvalidReservation;
            }

            if (participant != 0)
            {
                return CorruptHere();
            }

            return state switch
            {
                AbortingState or ReclaimingState
                    when publicationIntent == SlotPublicationIntent.ExplicitReservation
                        && HasCompletedInsertWitness(ref slot, generation) =>
                    StoreStatus.ReservationAlreadyCompleted,
                AbortingState or ReclaimingState => StoreStatus.InvalidReservation,
                RetiredState when generation == TerminalGeneration =>
                    StoreStatus.InvalidReservation,
                _ => StoreStatus.CorruptStore,
            };
        }

        return StoreStatus.StoreBusy;
    }

    internal TentativeReservationAbortResult TryBeginTentativeAbort(
        in ReservationHandle reservation)
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            return TentativeReservationAbortResult.Invalid;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long initializing = OwnedControl(
            InitializingState,
            generation,
            reservation.ParticipantToken);
        long aborting = UnownedControl(AbortingState, generation);
        long observed = AtomicControlWord.CompareExchange(
            ref slot.Control,
            aborting,
            initializing);
        if (ValidateStructuralControl(observed) != StoreStatus.Success)
        {
            return TentativeReservationAbortResult.Corrupt;
        }

        if (observed == initializing || observed == aborting)
        {
            return TentativeReservationAbortResult.Aborted;
        }

        if (observed == OwnedControl(
                ReservedState,
                generation,
                reservation.ParticipantToken))
        {
            return TentativeReservationAbortResult.Ordered;
        }

        long observedGeneration = Generation(observed);
        if (observedGeneration > generation)
        {
            return TentativeReservationAbortResult.Invalid;
        }

        if (observedGeneration < generation)
        {
            return TentativeReservationAbortResult.Corrupt;
        }

        int observedState = State(observed);
        if (observedState == ReclaimingState
            || (observedState == RetiredState && generation == TerminalGeneration))
        {
            return TentativeReservationAbortResult.Invalid;
        }

        // Aborting was accepted above. Every other same-generation state is
        // impossible for this private candidate: ownership cannot transfer,
        // no public handle can commit it, Free advances generation, and only
        // terminal generation may retire.
        return TentativeReservationAbortResult.Corrupt;
    }

    /// <summary>
    /// Aborts an atomic convenience-publication candidate that has not escaped
    /// the reserving engine. Unlike an explicit reservation, Reserved remains
    /// private staging and is abortable; it is never an ordered public result.
    /// Impossible same-generation observations fail closed instead of being
    /// normalized through the public reservation-handle status mapping.
    /// </summary>
    internal TentativeReservationAbortResult TryBeginAtomicCandidateAbort(
        in ReservationHandle reservation)
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            return TentativeReservationAbortResult.Corrupt;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        var metadataValidated = false;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (ValidateStructuralControl(control1) != StoreStatus.Success)
            {
                return TentativeReservationAbortResult.Corrupt;
            }

            long snapshotGeneration = Generation(control1);
            if (snapshotGeneration > generation)
            {
                return TentativeReservationAbortResult.Invalid;
            }

            if (snapshotGeneration < generation)
            {
                return TentativeReservationAbortResult.Corrupt;
            }

            ulong binding = Volatile.Read(ref slot.DirectoryBinding);
            int intent = Volatile.Read(ref slot.PublicationIntent);
            long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (control1 != control2)
            {
                if (ValidateStructuralControl(control2) != StoreStatus.Success)
                {
                    return TentativeReservationAbortResult.Corrupt;
                }

                continue;
            }

            if (binding != reservation.SlotBinding
                || intent != (int)SlotPublicationIntent.AtomicPublication)
            {
                return TentativeReservationAbortResult.Corrupt;
            }

            metadataValidated = true;
            break;
        }

        if (!metadataValidated)
        {
            // Persistent movement can only be concurrent recovery of this
            // private lifecycle; no public outcome has been ordered.
            return TentativeReservationAbortResult.Invalid;
        }

        long aborting = UnownedControl(AbortingState, generation);
        long initializing = OwnedControl(
            InitializingState,
            generation,
            reservation.ParticipantToken);
        long observed = AtomicControlWord.CompareExchange(
            ref slot.Control,
            aborting,
            initializing);
        if (ValidateStructuralControl(observed) != StoreStatus.Success)
        {
            return TentativeReservationAbortResult.Corrupt;
        }

        if (observed == initializing || observed == aborting)
        {
            return TentativeReservationAbortResult.Aborted;
        }

        long reserved = OwnedControl(
            ReservedState,
            generation,
            reservation.ParticipantToken);
        if (observed == reserved)
        {
            observed = AtomicControlWord.CompareExchange(
                ref slot.Control,
                aborting,
                reserved);
            if (ValidateStructuralControl(observed) != StoreStatus.Success)
            {
                return TentativeReservationAbortResult.Corrupt;
            }

            if (observed == reserved || observed == aborting)
            {
                return TentativeReservationAbortResult.Aborted;
            }
        }

        long observedGeneration = Generation(observed);
        if (observedGeneration > generation)
        {
            return TentativeReservationAbortResult.Invalid;
        }

        if (observedGeneration < generation)
        {
            return TentativeReservationAbortResult.Corrupt;
        }

        int observedState = State(observed);
        ulong observedParticipant = Participant(observed);
        if (observedState == AbortingState && observedParticipant == 0)
        {
            return TentativeReservationAbortResult.Aborted;
        }

        if ((observedState == ReclaimingState && observedParticipant == 0)
            || (observedState == RetiredState
                && generation == TerminalGeneration
                && observedParticipant == 0))
        {
            return TentativeReservationAbortResult.Invalid;
        }

        // Published/RemoveRequested, same-generation Free, wrong-owner owned
        // states, malformed ownership, and nonterminal Retired are impossible
        // for this private candidate.
        return TentativeReservationAbortResult.Corrupt;
    }

    internal int GetBytesAdvanced(in ReservationHandle reservation)
    {
        if (!TryReadReservationProjection(
                reservation,
                out _,
                out _,
                out long advanced,
                out _))
        {
            return 0;
        }

        return (int)advanced;
    }

    internal bool TryGetWritableRange(
        in ReservationHandle reservation,
        int sizeHint,
        out int slotIndex,
        out int offset,
        out int length)
    {
        slotIndex = -1;
        offset = 0;
        length = 0;
        if (sizeHint < 0
            || !TryReadReservationProjection(
                reservation,
                out slotIndex,
                out int valueLength,
                out long advanced,
                out _))
        {
            return false;
        }

        int remaining = valueLength - (int)advanced;
        if (remaining <= 0 || sizeHint > remaining)
        {
            return false;
        }

        offset = (int)advanced;
        length = remaining;
        return true;
    }

    private bool TryReadReservationProjection(
        in ReservationHandle reservation,
        out int slotIndex,
        out int valueLength,
        out long advanced,
        out StoreStatus failure)
    {
        slotIndex = -1;
        valueLength = 0;
        advanced = 0;
        failure = StoreStatus.Success;
        StoreStatus storeState = _storeControl?.Validate() ?? StoreStatus.Success;
        if (storeState != StoreStatus.Success)
        {
            failure = storeState;
            return false;
        }

        if (!TryDecodeHandle(reservation, out slotIndex, out long generation))
        {
            failure = StoreStatus.InvalidReservation;
            return false;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long expected = OwnedControl(ReservedState, generation, reservation.ParticipantToken);
        long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
        StoreStatus structure = ValidateStructuralControl(control1);
        if (structure != StoreStatus.Success)
        {
            failure = structure;
            return false;
        }

        if (control1 != expected)
        {
            failure = ReservationStatus(control1, generation);
            return false;
        }

        if (!IsParticipantActive())
        {
            failure = ParticipantUnavailableStatus();
            return false;
        }

        ulong directoryBinding = Volatile.Read(ref slot.DirectoryBinding);
        int keyLength = Volatile.Read(ref slot.KeyLength);
        int descriptorLength = Volatile.Read(ref slot.DescriptorLength);
        int observedValueLength = Volatile.Read(ref slot.ValueLength);
        int publicationIntent = Volatile.Read(ref slot.PublicationIntent);
        long observedAdvanced = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
        long keyOffset = Volatile.Read(ref slot.KeyOffset);
        long descriptorOffset = Volatile.Read(ref slot.DescriptorOffset);
        long payloadOffset = Volatile.Read(ref slot.PayloadOffset);
        long control2 = AtomicControlWord.LoadAcquire(ref slot.Control);
        if (control2 != control1)
        {
            failure = ValidateStructuralControl(control2);
            if (failure == StoreStatus.Success)
            {
                failure = ReservationStatus(control2, generation);
            }

            return false;
        }


        if (!IsParticipantActive())
        {
            failure = ParticipantUnavailableStatus();
            return false;
        }

        long expectedKeyOffset = _layout.KeyStorageOffset + ((long)slotIndex * _layout.KeyStride);
        long expectedDescriptorOffset =
            _layout.DescriptorStorageOffset + ((long)slotIndex * _layout.DescriptorStride);
        long expectedPayloadOffset =
            _layout.PayloadStorageOffset + ((long)slotIndex * _layout.PayloadStride);
        if (directoryBinding != reservation.SlotBinding
            || keyLength is < 1 || keyLength > _layout.MaxKeyBytes
            || descriptorLength < 0 || descriptorLength > _layout.MaxDescriptorBytes
            || observedValueLength < 0 || observedValueLength > _layout.MaxValueBytes
            || observedValueLength != reservation.PayloadLength
            || publicationIntent is not (
                (int)SlotPublicationIntent.ExplicitReservation
                or (int)SlotPublicationIntent.AtomicPublication)
            || observedAdvanced < 0 || observedAdvanced > observedValueLength
            || keyOffset != expectedKeyOffset
            || descriptorOffset != expectedDescriptorOffset
            || payloadOffset != expectedPayloadOffset)
        {
            _ = CorruptHere();
            failure = StoreStatus.CorruptStore;
            return false;
        }

        storeState = _storeControl?.Validate() ?? StoreStatus.Success;
        if (storeState != StoreStatus.Success)
        {
            failure = storeState;
            return false;
        }

        valueLength = observedValueLength;
        advanced = observedAdvanced;
        return true;
    }

    internal StoreStatus AdvanceReservation(in ReservationHandle reservation, int byteCount)
    {
        return AdvanceReservation(
            reservation,
            byteCount,
            LockFreeOperationBudget.StructuralAttempt);
    }

    internal StoreStatus AdvanceReservation(
        in ReservationHandle reservation,
        int byteCount,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return AdvanceReservation(reservation, byteCount, budget, ref checkpoint);
    }

    internal StoreStatus AdvanceReservation<TCheckpoint>(
        in ReservationHandle reservation,
        int byteCount,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryReadReservationProjection(
                reservation,
                out int slotIndex,
                out int valueLength,
                out _,
                out StoreStatus validation))
        {
            return validation;
        }

        _ = TryDecodeHandle(reservation, out _, out long generation);
        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long expected = OwnedControl(ReservedState, generation, reservation.ParticipantToken);

        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            long observed = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
            if (observed < 0
                || observed > valueLength
                || Volatile.Read(ref slot.ValueLength) != valueLength)
            {
                return CorruptHere();
            }

            if (byteCount < 0 || byteCount > valueLength - observed)
            {
                return StoreStatus.ReservationWriteOutOfRange;
            }

            long next = observed + byteCount;
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas);
            bound = budget.Check();
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            long observedControl = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (observedControl != expected)
            {
                return ReservationStatus(observedControl, generation);
            }

            if (AtomicControlWord.CompareExchange(ref slot.BytesAdvanced, next, observed) == observed)
            {
                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.AdvanceAfterBytesAdvancedCas);
                observedControl = AtomicControlWord.LoadAcquire(ref slot.Control);
                return observedControl == expected
                    ? StoreStatus.Success
                    : ReservationStatus(observedControl, generation);
            }

            _telemetry.RecordCasLoss();

            if (AtomicControlWord.LoadAcquire(ref slot.Control) != expected)
            {
                return ReservationStatus(AtomicControlWord.LoadAcquire(ref slot.Control), generation);
            }

            if (attempt + 1 >= AdvanceRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    internal StoreStatus CommitReservation(in ReservationHandle reservation, long commitSequence)
    {
        if (!TryReadReservationProjection(
                reservation,
                out int slotIndex,
                out int valueLength,
                out _,
                out StoreStatus validation))
        {
            return validation;
        }

        _ = TryDecodeHandle(reservation, out _, out long generation);
        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long reserved = OwnedControl(ReservedState, generation, reservation.ParticipantToken);
        long observedControl = AtomicControlWord.LoadAcquire(ref slot.Control);
        StoreStatus structure = ValidateStructuralControl(observedControl);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observedControl != reserved)
        {
            return ReservationStatus(observedControl, generation);
        }

        if (!IsParticipantActive())
        {
            return ParticipantUnavailableStatus();
        }

        if (Volatile.Read(ref slot.ValueLength) != valueLength)
        {
            return CorruptHere();
        }

        long currentAdvanced = AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced);
        if (currentAdvanced < 0 || currentAdvanced > valueLength)
        {
            return CorruptHere();
        }

        if (currentAdvanced != valueLength)
        {
            return StoreStatus.ReservationIncomplete;
        }

        slot.CommitSequence = commitSequence;
        long published = UnownedControl(PublishedState, generation);
        observedControl = AtomicControlWord.CompareExchange(ref slot.Control, published, reserved);
        return observedControl == reserved
            ? StoreStatus.Success
            : ReservationStatus(observedControl, generation);
    }

    internal StoreStatus TryBeginAbort(in ReservationHandle reservation)
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            return StoreStatus.InvalidReservation;
        }

        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long aborting = UnownedControl(AbortingState, generation);
        long initializing = OwnedControl(InitializingState, generation, reservation.ParticipantToken);
        long observed = AtomicControlWord.CompareExchange(ref slot.Control, aborting, initializing);
        if (observed == initializing || observed == aborting)
        {
            return StoreStatus.Success;
        }

        long reserved = OwnedControl(ReservedState, generation, reservation.ParticipantToken);
        observed = AtomicControlWord.CompareExchange(ref slot.Control, aborting, reserved);
        return observed == reserved || observed == aborting
            ? StoreStatus.Success
            : ReservationStatus(observed, generation);
    }

    internal StoreStatus AbortUnboundReservation(in ReservationHandle reservation)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return AbortUnboundReservation(reservation, ref checkpoint);
    }

    internal StoreStatus AbortUnboundReservation<TCheckpoint>(
        in ReservationHandle reservation,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        StoreStatus begin = TryBeginAbort(reservation);
        if (begin != StoreStatus.Success)
        {
            return begin;
        }

        return TryCompleteReclaim(
                reservation,
                LockFreeOperationBudget.StructuralAttempt,
                ref checkpoint)
            ? StoreStatus.Success
            : StoreStatus.StoreBusy;
    }

    internal bool TryBeginRecoveryAbort(
        int slotIndex,
        long expectedOwnedControl,
        out ReservationHandle reservation)
    {
        reservation = default;
        ReservationRecoveryClaim claim = TryBeginReservationRecovery(
            slotIndex,
            expectedOwnedControl);
        if (claim.Kind != ReservationRecoveryClaimKind.Acquired)
        {
            return false;
        }

        long generation = Generation(expectedOwnedControl);
        ulong participant = Participant(expectedOwnedControl);
        reservation = new ReservationHandle(
            _storeId,
            participant,
            claim.SlotBinding,
            Slot(slotIndex).ValueLength);
        return true;
    }

    /// <summary>
    /// Changes one exact <c>Initializing</c>/<c>Reserved</c> lifecycle to the
    /// unowned helpable <c>Aborting</c> state.  The returned observation makes
    /// compare/exchange losses explicit so recovery can report benign commit,
    /// abort, and generation-advance races without treating them as failures.
    /// </summary>
    internal ReservationRecoveryClaim TryBeginReservationRecovery(
        int slotIndex,
        long expectedOwnedControl)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Inconsistent,
                0,
                expectedOwnedControl);
        }

        int expectedState = State(expectedOwnedControl);
        long expectedGeneration = Generation(expectedOwnedControl);
        ulong expectedParticipant = Participant(expectedOwnedControl);
        if (ValidateStructuralControl(expectedOwnedControl) != StoreStatus.Success
            || expectedState is not (InitializingState or ReservedState)
            || expectedParticipant == 0)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Inconsistent,
                0,
                expectedOwnedControl);
        }

        ulong binding = IndexBinding.Encode(slotIndex, expectedGeneration);
        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long aborting = UnownedControl(AbortingState, expectedGeneration);
        long observed = AtomicControlWord.CompareExchange(
            ref slot.Control,
            aborting,
            expectedOwnedControl);
        if (observed == expectedOwnedControl)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Acquired,
                binding,
                aborting);
        }

        _telemetry.RecordCasLoss();

        if (ValidateStructuralControl(observed) != StoreStatus.Success)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Inconsistent,
                binding,
                observed);
        }

        long observedGeneration = Generation(observed);
        int observedState = State(observed);
        ulong observedParticipant = Participant(observed);
        bool observedOwned = observedState is InitializingState or ReservedState;
        if (observedGeneration is < 1 or > TerminalGeneration
            || (observedOwned ? observedParticipant == 0 : observedParticipant != 0)
            || (observedState == RetiredState && observedGeneration != TerminalGeneration))
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Inconsistent,
                binding,
                observed);
        }

        if (observedGeneration > expectedGeneration)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.CompletedRace,
                binding,
                observed);
        }

        if (observedGeneration < expectedGeneration)
        {
            return new ReservationRecoveryClaim(
                ReservationRecoveryClaimKind.Inconsistent,
                binding,
                observed);
        }

        if (observedOwned)
        {
            return new ReservationRecoveryClaim(
                observedParticipant == expectedParticipant
                    ? ReservationRecoveryClaimKind.OwnerStateChanged
                    : ReservationRecoveryClaimKind.Inconsistent,
                binding,
                observed);
        }

        ReservationRecoveryClaimKind kind = observedState switch
        {
            AbortingState or ReclaimingState => ReservationRecoveryClaimKind.HelpRequired,
            PublishedState or RemoveRequestedState =>
                ReservationRecoveryClaimKind.CompletedRace,
            RetiredState when expectedGeneration == TerminalGeneration =>
                ReservationRecoveryClaimKind.CompletedRace,
            _ => ReservationRecoveryClaimKind.Inconsistent,
        };
        if (kind == ReservationRecoveryClaimKind.Inconsistent)
        {
            _ = CorruptHere();
        }

        return new ReservationRecoveryClaim(kind, binding, observed);
    }

    internal bool TryCompleteReclaim(in ReservationHandle reservation)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryCompleteReclaim(
            reservation,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint);
    }

    internal bool TryCompleteReclaim(
        in ReservationHandle reservation,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryCompleteReclaim(reservation, budget, ref checkpoint);
    }

    internal bool TryCompleteReclaim<TCheckpoint>(
        in ReservationHandle reservation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryDecodeHandle(reservation, out int slotIndex, out long generation))
        {
            return false;
        }

        return TryCompleteReclaim(slotIndex, generation, budget, ref checkpoint);
    }

    /// <summary>
    /// Completes an already unowned abort/reclaim lifecycle identified only by
    /// its exact slot binding.  This is the cross-participant recovery entry;
    /// ordinary reservation actions still validate their local participant
    /// token before reaching the shared completion routine.
    /// </summary>
    internal StoreStatus TryCompleteRecoveryReclaim(ulong exactBinding)
    {
        return TryCompleteRecoveryReclaim(
            exactBinding,
            LockFreeOperationBudget.StructuralAttempt);
    }

    internal StoreStatus TryCompleteRecoveryReclaim(
        ulong exactBinding,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryCompleteRecoveryReclaim(exactBinding, budget, ref checkpoint);
    }

    internal StoreStatus TryCompleteRecoveryReclaim<TCheckpoint>(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        return TryDecodeSlotBinding(exactBinding, out int slotIndex, out long generation)
            ? TryCompleteReclaimStatus(slotIndex, generation, budget, ref checkpoint)
            : CorruptHere();
    }

    private bool TryCompleteReclaim<TCheckpoint>(
        int slotIndex,
        long generation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint> =>
        TryCompleteReclaimStatus(slotIndex, generation, budget, ref checkpoint) == StoreStatus.Success;

    private StoreStatus TryCompleteReclaimStatus<TCheckpoint>(
        int slotIndex,
        long generation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        ref ValueSlotMetadataV2 slot = ref Slot(slotIndex);
        long aborting = UnownedControl(AbortingState, generation);
        long reclaiming = UnownedControl(ReclaimingState, generation);
        StoreStatus structure = TryLifecycleTransition(
            ref slot.Control,
            aborting,
            reclaiming,
            generation,
            out _);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
        structure = ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed != reclaiming)
        {
            return HasAdvancedOrRetired(observed, generation)
                ? StoreStatus.Success
                : StoreStatus.StoreBusy;
        }

        // Directory code must clear the exact cell and generation-tagged
        // descriptors before storage can be made reusable. An exact-generation
        // descriptor can still belong to a concurrent directory helper, so it
        // is ordinary incomplete cleanup rather than corruption. Only older
        // residue is safe to CAS-clear here; a future generation remains a
        // structural violation. Free/Retired metadata is deliberately ignored:
        // a delayed helper must have no plain write it can resume after another
        // helper advances and reuses the generation.
        StoreStatus residue = SanitizeOlderDirectoryResidue(
            ref slot,
            generation,
            budget,
            exactGenerationIsBusy: true);
        if (residue != StoreStatus.Success)
        {
            return residue == StoreStatus.CorruptStore
                ? CorruptHere()
                : residue;
        }

        if (AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation) != 0
            || AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation) != 0)
        {
            return StoreStatus.StoreBusy;
        }

        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.ReclaimAfterMetadataValidation);
        long terminal = Signed(AdvanceOrRetire(generation));
        structure = TryLifecycleTransition(
            ref slot.Control,
            reclaiming,
            terminal,
            generation,
            out bool advanced);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (advanced)
        {
            LockFreeCheckpoint.ObserveSlotResource(
                ref checkpoint,
                generation == TerminalGeneration
                    ? LockFreeSlotResourceEventKind.Retire
                    : LockFreeSlotResourceEventKind.Free,
                slotIndex,
                generation);
        }

        observed = AtomicControlWord.LoadAcquire(ref slot.Control);
        structure = ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        return observed == reclaiming || HasAdvancedOrRetired(observed, generation)
            ? StoreStatus.Success
            : CorruptHere();
    }

    /// <summary>
    /// Completes an exact unowned slot lifecycle transition. Only the desired
    /// word and a structurally valid later generation can supersede it. A
    /// stable same/older-generation observation is confirmed by an exact no-op
    /// CAS before the store-wide corruption latch is published.
    /// </summary>
    private StoreStatus TryLifecycleTransition(
        ref long control,
        long expected,
        long desired,
        long generation,
        out bool transitioned)
    {
        transitioned = false;
        const int confirmationAttempts = 8;
        for (var attempt = 0; attempt < confirmationAttempts; attempt++)
        {
            long observed = AtomicControlWord.CompareExchange(ref control, desired, expected);
            if (observed == expected)
            {
                transitioned = true;
                return StoreStatus.Success;
            }

            _telemetry.RecordCasLoss();
            StoreStatus structure = ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (observed == desired || HasAdvancedOrRetired(observed, generation))
            {
                return StoreStatus.Success;
            }

            long confirmed = AtomicControlWord.CompareExchange(ref control, observed, observed);
            if (confirmed == observed)
            {
                return CorruptHere();
            }

            structure = ValidateStructuralControl(confirmed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }
        }

        return StoreStatus.StoreBusy;
    }

    internal ref ValueSlotMetadataV2 Slot(int slotIndex)
    {
        if ((uint)slotIndex >= (uint)_layout.SlotCount)
        {
            throw new ArgumentOutOfRangeException(nameof(slotIndex));
        }

        return ref *(ValueSlotMetadataV2*)(
            _mappingBase + _layout.SlotMetadataOffset + ((long)slotIndex * _layout.SlotMetadataStride));
    }

    internal static ulong AdvanceOrRetire(long generation)
    {
        if (generation is < 1 or > TerminalGeneration)
        {
            throw new ArgumentOutOfRangeException(nameof(generation));
        }

        return generation == TerminalGeneration
            ? AtomicControlWord.EncodeSlot(RetiredState, generation, participantToken: 0)
            : AtomicControlWord.EncodeSlot(FreeState, generation + 1, participantToken: 0);
    }

    private bool TryGetOwnedSlot(
        in ReservationHandle reservation,
        int requiredState,
        out int slotIndex,
        out ValueSlotMetadataV2* slot)
    {
        slotIndex = -1;
        slot = null;
        if (!TryDecodeHandle(reservation, out slotIndex, out long generation))
        {
            return false;
        }

        slot = (ValueSlotMetadataV2*)(
            _mappingBase + _layout.SlotMetadataOffset + ((long)slotIndex * _layout.SlotMetadataStride));
        long control = AtomicControlWord.LoadAcquire(ref slot->Control);
        return ValidateStructuralControl(control) == StoreStatus.Success
            && control == OwnedControl(requiredState, generation, reservation.ParticipantToken)
            && IsParticipantActive();
    }

    private bool TryDecodeHandle(
        in ReservationHandle reservation,
        out int slotIndex,
        out long generation)
    {
        slotIndex = -1;
        generation = 0;
        if (reservation.StoreId != _storeId
            || reservation.ParticipantToken != _participant.Token
            || reservation.SlotBinding == 0)
        {
            return false;
        }

        return TryDecodeSlotBinding(reservation.SlotBinding, out slotIndex, out generation);
    }

    private bool TryDecodeSlotBinding(
        ulong slotBinding,
        out int slotIndex,
        out long generation)
    {
        slotIndex = -1;
        generation = 0;

        ulong indexPlusOne = slotBinding & SlotIndexMask;
        ulong rawGeneration = slotBinding >> 31;
        if (indexPlusOne == 0 || indexPlusOne > (ulong)_layout.SlotCount
            || rawGeneration is 0 or > SlotGenerationMask)
        {
            return false;
        }

        slotIndex = checked((int)indexPlusOne - 1);
        generation = checked((long)rawGeneration);
        return true;
    }

    private bool IsParticipantActive()
    {
        ref ParticipantRecordV2 record = ref *(ParticipantRecordV2*)(
            _mappingBase + _layout.ParticipantOffset
            + ((long)_participant.RecordIndex * _layout.ParticipantStride));
        long control = AtomicControlWord.LoadAcquire(ref record.Control);
        if (!LockFreeParticipantRegistry.IsStructuralControlValid(
                control,
                _layout.ParticipantGenerationMask))
        {
            _ = LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeSlotTable));
            return false;
        }

        return control == _participant.ActiveControl;
    }

    private StoreStatus ParticipantUnavailableStatus() =>
        _storeControl?.Validate() == StoreStatus.CorruptStore
            ? StoreStatus.CorruptStore
            : StoreStatus.StoreDisposed;

    private static int State(long control) => (int)((ulong)control & 0x7UL);

    private static long Generation(long control) =>
        (long)(((ulong)control >> 3) & SlotGenerationMask);

    private static ulong Participant(long control) => ((ulong)control >> 36) & ParticipantMask;

    private StoreStatus ClassifyFullnessControl(long control, out bool occupied)
    {
        if (TryClassifyStructuralControl(
                control,
                _layout.ParticipantRecordCount,
                out occupied))
        {
            return StoreStatus.Success;
        }

        return CorruptFrom(nameof(LockFreeSlotTable));
    }

    /// <summary>
    /// Pure canonical validation for a slot lifecycle word. Callers that observe
    /// false must report the persistent mapped corruption through their own
    /// store-control boundary before returning it.
    /// </summary>
    internal static bool TryClassifyStructuralControl(
        long control,
        int participantRecordCount,
        out bool occupied)
    {
        occupied = true;
        int state = State(control);
        long generation = Generation(control);
        ulong participant = Participant(control);
        if (generation is < 1 or > TerminalGeneration)
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

            case InitializingState:
            case ReservedState:
                return ParticipantToken.IsStructurallyValid(
                    participant,
                    participantRecordCount);

            case PublishedState:
            case RemoveRequestedState:
            case AbortingState:
            case ReclaimingState:
                return participant == 0;

            case RetiredState:
                return participant == 0 && generation == TerminalGeneration;

            default:
                return false;
        }
    }

    internal StoreStatus ValidateStructuralControl(long control) =>
        ClassifyFullnessControl(control, out _);

    private static StoreStatus SanitizeOlderDirectoryResidue(
        ref ValueSlotMetadataV2 slot,
        long claimedGeneration,
        in LockFreeOperationBudget budget,
        bool exactGenerationIsBusy)
    {
        StoreStatus location = SanitizeOlderLocation(
            ref slot.DirectoryLocation,
            claimedGeneration,
            budget,
            exactGenerationIsBusy);
        return location == StoreStatus.Success
            ? SanitizeOlderOperation(
                ref slot.DirectoryOperation,
                claimedGeneration,
                budget,
                exactGenerationIsBusy)
            : location;
    }

    private static StoreStatus SanitizeOlderLocation(
        ref long word,
        long claimedGeneration,
        in LockFreeOperationBudget budget,
        bool exactGenerationIsBusy)
    {
        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ulong raw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref word));
            if (raw == 0)
            {
                return StoreStatus.Success;
            }

            DirectoryLocation location;
            try
            {
                location = DirectoryLocation.Decode(raw);
            }
            catch (ArgumentOutOfRangeException)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }
            catch (OverflowException)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            if (location.Generation > claimedGeneration)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            if (location.Generation == claimedGeneration)
            {
                return exactGenerationIsBusy
                    ? StoreStatus.StoreBusy
                    : LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            AtomicControlWord.CompareExchange(ref word, 0, unchecked((long)raw));

            if ((attempt + 1) % ResidueCleanupRetryBudget == 0
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private static StoreStatus SanitizeOlderOperation(
        ref long word,
        long claimedGeneration,
        in LockFreeOperationBudget budget,
        bool exactGenerationIsBusy)
    {
        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ulong raw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref word));
            if (raw == 0)
            {
                return StoreStatus.Success;
            }

            DirectoryOperation operation;
            try
            {
                operation = DirectoryOperation.Decode(raw);
            }
            catch (ArgumentOutOfRangeException)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }
            catch (OverflowException)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            if (operation.Generation > claimedGeneration)
            {
                return LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            if (operation.Generation == claimedGeneration)
            {
                return exactGenerationIsBusy
                    ? StoreStatus.StoreBusy
                    : LockFreeCorruptionTrace.Corrupt(nameof(LockFreeSlotTable));
            }

            AtomicControlWord.CompareExchange(ref word, 0, unchecked((long)raw));

            if ((attempt + 1) % ResidueCleanupRetryBudget == 0
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private static bool HasAdvancedOrRetired(long control, long generation)
    {
        long observedGeneration = Generation(control);
        return observedGeneration > generation
            || (observedGeneration == generation && State(control) == RetiredState);
    }

    private static long OwnedControl(int state, long generation, ulong participantToken) =>
        Signed(AtomicControlWord.EncodeSlot(state, generation, checked((int)participantToken)));

    private static long UnownedControl(int state, long generation) =>
        Signed(AtomicControlWord.EncodeSlot(state, generation, participantToken: 0));

    private StoreStatus ReservationStatus(long observedControl, long expectedGeneration)
    {
        StoreStatus structure = ClassifyFullnessControl(observedControl, out _);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (Generation(observedControl) != expectedGeneration)
        {
            return StoreStatus.InvalidReservation;
        }

        return State(observedControl) == PublishedState
            ? StoreStatus.ReservationAlreadyCompleted
            : StoreStatus.InvalidReservation;
    }

    private bool IsCurrentReferencedOperationValid(
        ulong raw,
        long generation,
        int slotState)
    {
        DirectoryOperation operation;
        try
        {
            operation = DirectoryOperation.Decode(raw);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        const int insertIntent = 1;
        const int unlinkIntent = 2;
        if (operation.Value != raw
            || operation.Generation != generation
            || (operation.Intent != insertIntent
                && !(operation.Intent == unlinkIntent
                    && slotState is AbortingState or ReclaimingState)))
        {
            return false;
        }

        bool shapeValid = operation.Phase switch
        {
            1 => operation.Kind == 0 && operation.Index == 0,
            2 or 3 => IsDirectoryTargetInBounds(operation.Kind, operation.Index),
            4 => operation.Intent == insertIntent
                && operation.Kind == 0
                && operation.Index == 0,
            5 when operation.Intent == unlinkIntent && operation.Kind == 0 =>
                operation.Index == 0,
            5 => IsDirectoryTargetInBounds(operation.Kind, operation.Index),
            _ => false,
        };
        if (!shapeValid)
        {
            return false;
        }

        return slotState switch
        {
            InitializingState => operation.Intent == insertIntent
                && operation.Phase is 2 or 3,
            ReservedState => operation.Intent == insertIntent
                && operation.Phase is 3 or 5,
            PublishedState or RemoveRequestedState =>
                operation.Intent == insertIntent && operation.Phase == 5,
            AbortingState or ReclaimingState => true,
            _ => false,
        };
    }

    private bool IsDirectoryTargetInBounds(int kind, long index) =>
        kind switch
        {
            1 => (ulong)index < (ulong)_layout.PrimaryLaneCount,
            2 => (ulong)index < (ulong)_layout.SlotCount,
            _ => false,
        };

    private bool HasCompletedInsertWitness(
        ref ValueSlotMetadataV2 slot,
        long generation)
    {
        try
        {
            ulong operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            DirectoryOperation operation = DirectoryOperation.Decode(operationRaw);
            if (operation.Value != operationRaw
                || operation.Intent != 1
                || operation.Phase != 5
                || operation.Generation != generation
                || operation.Kind is < 1 or > 2)
            {
                return false;
            }

            long limit = operation.Kind == 1
                ? _layout.PrimaryLaneCount
                : _layout.SlotCount;
            if (operation.Index < 0 || operation.Index >= limit)
            {
                return false;
            }

            ulong locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryLocation));
            DirectoryLocation location = DirectoryLocation.Decode(locationRaw);
            return location.Value == locationRaw
                && location.Kind == operation.Kind
                && location.Index == operation.Index
                && location.Generation == generation;
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

    private StoreStatus CorruptHere(
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeSlotTable),
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

    private static long Signed(ulong value) => unchecked((long)value);
}

internal enum TentativeReservationAbortResult
{
    Aborted,
    Ordered,
    Invalid,
    Corrupt,
}
