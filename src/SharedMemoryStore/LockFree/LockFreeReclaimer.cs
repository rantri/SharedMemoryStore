using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Cooperative exact-generation removal and slot reuse. No helper owns global
/// progress: every transition is either an exact CAS or a fully described
/// directory unlink that another participant can finish.
/// </summary>
internal sealed class LockFreeReclaimer
{
    private readonly StoreLayoutV2 _layout;
    private readonly LockFreeSlotTable _slots;
    private readonly LockFreeKeyDirectory _directory;
    private readonly LockFreeLeaseRegistry _leases;
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;

    internal LockFreeReclaimer(
        StoreLayoutV2 layout,
        LockFreeSlotTable slots,
        LockFreeKeyDirectory directory,
        LockFreeLeaseRegistry leases)
        : this(layout, slots, directory, leases, new LockFreeTelemetry())
    {
    }

    internal LockFreeReclaimer(
        StoreLayoutV2 layout,
        LockFreeSlotTable slots,
        LockFreeKeyDirectory directory,
        LockFreeLeaseRegistry leases,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        _layout = layout;
        _slots = slots;
        _directory = directory;
        _leases = leases;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
    }

    internal StoreStatus TryReclaim(ulong exactBinding) =>
        TryReclaim(exactBinding, LockFreeOperationBudget.StructuralAttempt);

    internal StoreStatus TryReclaim(
        ulong exactBinding,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryReclaim(exactBinding, budget, ref checkpoint);
    }

    internal StoreStatus TryReclaim<TCheckpoint>(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        bool reportRemoveClassification = false)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryDecode(exactBinding, out int slotIndex, out long generation)
            || (uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeReclaimer));
        }

        ref ValueSlotMetadataV2 slot = ref _slots.Slot(slotIndex);
        long removeRequested = Control(LockFreeSlotTable.RemoveRequestedState, generation);
        long reclaiming = Control(LockFreeSlotTable.ReclaimingState, generation);
        long observed = AtomicControlWord.LoadAcquire(ref slot.Control);
        StoreStatus structure = _slots.ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed == removeRequested)
        {
            StoreStatus scan = _leases.ScanHasActiveLease(
                exactBinding,
                budget,
                out bool hasActiveLease);
            _ = ObserveStructuralStatus(scan);
            if (scan != StoreStatus.Success)
            {
                return scan;
            }

            if (hasActiveLease)
            {
                if (reportRemoveClassification)
                {
                    LockFreeCheckpoint.Reach(
                        ref checkpoint,
                        LockFreeCheckpointId.RemoveAfterLeaseClassification);
                }

                return StoreStatus.RemovePending;
            }

            // The lease scan establishes that claiming Reclaiming would be
            // safe, but it does not authorize starting new caller-bounded work
            // after the public deadline/cancellation point. Leaving the slot
            // in RemoveRequested keeps it universally helpable; the public
            // remove facade normalizes this terminal budget status to
            // RemovePending because logical removal already linearized.
            StoreStatus ownershipBudget = budget.Check();
            if (ownershipBudget != StoreStatus.Success)
            {
                return ownershipBudget;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas);
            structure = TryLifecycleTransition(
                ref slot.Control,
                removeRequested,
                reclaiming,
                generation,
                out _);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            structure = _slots.ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }
        }

        if (observed != removeRequested && observed != reclaiming)
        {
            return HasAdvancedOrRetired(observed, generation)
                ? StoreStatus.Success
                : StoreStatus.NotFound;
        }

        StoreStatus unlink = _directory.TryUnlink(exactBinding, budget, ref checkpoint);
        _ = ObserveStructuralStatus(unlink);
        if (unlink != StoreStatus.Success)
        {
            observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            structure = _slots.ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            if (HasAdvancedOrRetired(observed, generation))
            {
                return StoreStatus.Success;
            }

            return unlink;
        }

        // TryUnlink owns exact directory-cell and descriptor cleanup. A
        // reclaimer never performs delayed plain writes: another helper may
        // already have advanced and reused the slot by the time this one runs.
        if (AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation) != 0
            || AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation) != 0)
        {
            observed = AtomicControlWord.LoadAcquire(ref slot.Control);
            structure = _slots.ValidateStructuralControl(observed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            return HasAdvancedOrRetired(observed, generation)
                ? StoreStatus.Success
                : StoreStatus.StoreBusy;
        }

        LockFreeCheckpoint.Reach(ref checkpoint, LockFreeCheckpointId.ReclaimAfterMetadataValidation);
        observed = AtomicControlWord.LoadAcquire(ref slot.Control);
        structure = _slots.ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed != reclaiming
            || AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation) != 0
            || AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation) != 0)
        {
            return HasAdvancedOrRetired(observed, generation)
                ? StoreStatus.Success
                : StoreStatus.StoreBusy;
        }

        long reusable = unchecked((long)LockFreeSlotTable.AdvanceOrRetire(generation));
        structure = TryLifecycleTransition(
            ref slot.Control,
            reclaiming,
            reusable,
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
                generation == LockFreeSlotTable.TerminalGeneration
                    ? LockFreeSlotResourceEventKind.Retire
                    : LockFreeSlotResourceEventKind.Free,
                slotIndex,
                generation);
        }

        observed = AtomicControlWord.LoadAcquire(ref slot.Control);
        structure = _slots.ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        if (observed == reclaiming || HasAdvancedOrRetired(observed, generation))
        {
            return StoreStatus.Success;
        }

        return LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeReclaimer));
    }

    /// <summary>
    /// Performs one exact lifecycle transition and accepts only its desired
    /// control or a structurally valid later incarnation. A same/older
    /// incarnation observation is impossible after the source state was
    /// validated; an exact no-op CAS confirms it before poisoning the mapping.
    /// Movement during confirmation is retried as an ordinary race.
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
            StoreStatus structure = _slots.ValidateStructuralControl(observed);
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
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeReclaimer));
            }

            structure = _slots.ValidateStructuralControl(confirmed);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }
        }

        return StoreStatus.StoreBusy;
    }

    internal StoreStatus TryLogicalRemove(ulong exactBinding, out bool alreadyRemoved)
    {
        alreadyRemoved = false;
        if (!TryDecode(exactBinding, out int slotIndex, out long generation)
            || (uint)slotIndex >= (uint)_layout.SlotCount)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeReclaimer));
        }

        ref ValueSlotMetadataV2 slot = ref _slots.Slot(slotIndex);
        long published = Control(LockFreeSlotTable.PublishedState, generation);
        long removeRequested = Control(LockFreeSlotTable.RemoveRequestedState, generation);
        long observed = AtomicControlWord.CompareExchange(ref slot.Control, removeRequested, published);
        StoreStatus structure = _slots.ValidateStructuralControl(observed);
        if (structure != StoreStatus.Success)
        {
            return structure;
        }

        alreadyRemoved = observed == removeRequested;
        return observed == published || alreadyRemoved
            ? StoreStatus.Success
            : StoreStatus.NotFound;
    }

    internal int HelpReclaimableSlots()
    {
        _ = HelpReclaimableSlots(LockFreeOperationBudget.StructuralAttempt, out int reclaimed);
        return reclaimed;
    }

    internal StoreStatus HelpReclaimableSlots(
        in LockFreeOperationBudget budget,
        out int reclaimed)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return HelpReclaimableSlots(budget, ref checkpoint, out reclaimed);
    }

    internal StoreStatus HelpReclaimableSlots<TCheckpoint>(
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out int reclaimed)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        reclaimed = 0;
        for (var index = 0; index < _layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref ValueSlotMetadataV2 slot = ref _slots.Slot(index);
            long observedControl = AtomicControlWord.LoadAcquire(ref slot.Control);
            StoreStatus structure = _slots.ValidateStructuralControl(observedControl);
            if (structure != StoreStatus.Success)
            {
                return structure;
            }

            ulong control = unchecked((ulong)observedControl);
            int state = (int)(control & 0x7UL);
            if (state == LockFreeSlotTable.AbortingState)
            {
                long abortingGeneration = (long)((control >> 3) & 0x1_ffff_ffffUL);
                if (abortingGeneration is < 1 or > LockFreeSlotTable.TerminalGeneration)
                {
                    return LockFreeStoreControl.ReportCorruption(
                        _storeControl,
                        nameof(LockFreeReclaimer));
                }

                ulong abortingBinding = IndexBinding.Encode(index, abortingGeneration);
                for (var attempt = 0; ; attempt++)
                {
                    StoreStatus unlink = _directory.TryUnlink(
                        abortingBinding,
                        budget,
                        ref checkpoint);
                    _ = ObserveStructuralStatus(unlink);
                    StoreStatus normalizedUnlink = NormalizeAbortingUnlinkOutcome(unlink);
                    if (normalizedUnlink == StoreStatus.Success)
                    {
                        StoreStatus completion =
                            _slots.TryCompleteRecoveryReclaim(
                                abortingBinding,
                                budget,
                                ref checkpoint);
                        _ = ObserveStructuralStatus(completion);
                        if (completion == StoreStatus.Success)
                        {
                            reclaimed++;
                            _telemetry.RecordHelpedTransition();
                            break;
                        }

                        normalizedUnlink = completion;
                    }

                    if (normalizedUnlink != StoreStatus.StoreBusy)
                    {
                        return normalizedUnlink;
                    }

                    if (!budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
                    {
                        if (terminal == StoreStatus.StoreBusy)
                        {
                            _telemetry.RecordContentionBudgetExhaustion();
                        }

                        return terminal;
                    }
                }

                continue;
            }

            if (state is not (LockFreeSlotTable.RemoveRequestedState or LockFreeSlotTable.ReclaimingState))
            {
                continue;
            }

            long generation = (long)((control >> 3) & 0x1_ffff_ffffUL);
            if (generation is < 1 or > LockFreeSlotTable.TerminalGeneration)
            {
                return LockFreeStoreControl.ReportCorruption(
                    _storeControl,
                    nameof(LockFreeReclaimer));
            }

            // Control is the authoritative exact lifecycle. DirectoryBinding
            // is ordinary metadata and may be stale while another helper is
            // completing unlink; never use it to choose which generation to
            // reclaim under allocation pressure.
            ulong binding = IndexBinding.Encode(index, generation);
            for (var attempt = 0; ; attempt++)
            {
                StoreStatus reclaim = TryReclaim(binding, budget, ref checkpoint);
                if (reclaim == StoreStatus.Success)
                {
                    reclaimed++;
                    _telemetry.RecordHelpedTransition();
                    break;
                }

                StoreStatus normalized = NormalizeObservedReclaimOutcome(reclaim);
                if (normalized == StoreStatus.Success)
                {
                    break;
                }

                if (normalized != StoreStatus.StoreBusy)
                {
                    return normalized;
                }

                if (!budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
                {
                    if (terminal == StoreStatus.StoreBusy)
                    {
                        _telemetry.RecordContentionBudgetExhaustion();
                    }

                    return terminal;
                }
            }
        }

        return StoreStatus.Success;
    }

    internal static StoreStatus NormalizeAbortingUnlinkOutcome(StoreStatus status) =>
        status is StoreStatus.Success or StoreStatus.NotFound
            ? StoreStatus.Success
            : status;

    internal static StoreStatus NormalizeObservedReclaimOutcome(StoreStatus status) =>
        status is StoreStatus.Success or StoreStatus.NotFound or StoreStatus.RemovePending
            ? StoreStatus.Success
            : status;

    internal static ulong LogicalRemoveControl(long generation) =>
        AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.RemoveRequestedState,
            generation,
            participantToken: 0);

    private static long Control(int state, long generation) =>
        unchecked((long)AtomicControlWord.EncodeSlot(state, generation, participantToken: 0));

    private StoreStatus ObserveStructuralStatus(StoreStatus status)
    {
        if (status == StoreStatus.CorruptStore)
        {
            return LockFreeStoreControl.ReportCorruption(
                _storeControl,
                nameof(LockFreeReclaimer));
        }

        return status;
    }

    private static bool HasAdvancedOrRetired(long control, long generation)
    {
        ulong raw = unchecked((ulong)control);
        long observedGeneration = (long)((raw >> 3) & 0x1_ffff_ffffUL);
        int state = (int)(raw & 0x7UL);
        return observedGeneration > generation
            || (observedGeneration == generation && state == LockFreeSlotTable.RetiredState);
    }

    private static bool TryDecode(ulong binding, out int slotIndex, out long generation)
    {
        slotIndex = -1;
        generation = 0;
        try
        {
            IndexBinding decoded = IndexBinding.Decode(binding);
            slotIndex = decoded.SlotIndex;
            generation = decoded.Generation;
            return true;
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
}
