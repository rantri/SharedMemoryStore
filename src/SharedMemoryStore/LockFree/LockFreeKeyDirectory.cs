using System.Runtime.CompilerServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Owns the layout-v2 binding directory. Slot lifecycle ownership remains in
/// the slot table; this component validates exact slot/key
/// incarnations and makes directory mutations helpable through the canonical
/// bucket descriptor.
/// </summary>
internal sealed unsafe class LockFreeKeyDirectory
{
    private const int IntentInsert = 1;
    private const int IntentUnlink = 2;
    private const int PhasePrepared = 1;
    private const int PhaseTargetSelected = 2;
    private const int PhaseBindingChanged = 3;
    private const int PhaseRejected = 4;
    private const int PhaseComplete = 5;
    private const int TargetPrimary = 1;
    private const int TargetOverflow = 2;
    private const int SlotInitializing = 1;
    private const int SlotReserved = 2;
    private const int SlotPublished = 3;
    private const int SlotRemoveRequested = 4;
    private const int SlotAborting = 5;
    private const int SlotReclaiming = 6;
    private const int SlotRetired = 7;
    private const int DefaultRetryBudget = 128;
    private const ulong SlotGenerationMask = 0x1_ffff_ffffUL;
    private const ulong SlotParticipantMask = 0x0fff_ffffUL;

    private readonly ISharedStoreRegion _region;
    private readonly StoreLayoutV2 _layout;
    private readonly int _bucketMask;
    private readonly LockFreeTelemetry _telemetry;
    private readonly LockFreeStoreControl? _storeControl;

    internal LockFreeKeyDirectory(
        ISharedStoreRegion region,
        StoreLayoutV2 layout)
        : this(region, layout, new LockFreeTelemetry())
    {
    }

    internal LockFreeKeyDirectory(
        ISharedStoreRegion region,
        StoreLayoutV2 layout,
        LockFreeTelemetry telemetry,
        LockFreeStoreControl? storeControl = null)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!layout.FitsWithinTotalBytes() || region.Capacity < layout.RequiredBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(layout));
        }

        _region = region;
        _layout = layout;
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _storeControl = storeControl;
        _bucketMask = layout.PrimaryBucketCount - 1;
        if (layout.PrimaryBucketCount < 2
            || (layout.PrimaryBucketCount & _bucketMask) != 0
            || layout.OverflowDirectoryLength / layout.OverflowStride != layout.SlotCount)
        {
            throw new ArgumentException("Layout-v2 directory dimensions are inconsistent.", nameof(layout));
        }
    }

    internal StoreStatus TryLookup(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        out ulong binding,
        out DirectoryLocation location) =>
        TryLookup(key, keyHash, LockFreeOperationBudget.StructuralAttempt, out binding, out location);

    internal StoreStatus TryLookup(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        in LockFreeOperationBudget budget,
        out ulong binding,
        out DirectoryLocation location)
    {
        return FindExact(key, keyHash, excludedBinding: 0, budget, out binding, out location);
    }

    /// <summary>
    /// Reads the exact directory cell that supplied a successful lookup. This
    /// is deliberately a source-word check only: slot lifecycle
    /// classification remains owned by <see cref="LockFreeSlotTable"/>, while
    /// the engine composes the two observations when a cached lookup would
    /// otherwise be reported as structural corruption.
    /// </summary>
    internal StoreStatus TryConfirmExactLookupReference(
        DirectoryLocation location,
        ulong exactBinding,
        out bool remainsExact)
    {
        remainsExact = false;
        if (!TryDecodeBinding(exactBinding, out IndexBinding binding)
            || binding.SlotIndex >= _layout.SlotCount
            || location.Value == 0
            || location.Generation != binding.Generation
            || !TryGetTargetCell(location.Kind, location.Index, out CellReference cell))
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        remainsExact = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell.Value))
            == exactBinding;
        return StoreStatus.Success;
    }

    internal StoreStatus TryInsert(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong candidateBinding,
        out DirectoryLocation location)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryInsert(
            key,
            keyHash,
            candidateBinding,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint,
            out location);
    }

    internal StoreStatus TryInsert(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong candidateBinding,
        in LockFreeOperationBudget budget,
        out DirectoryLocation location)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryInsert(key, keyHash, candidateBinding, budget, ref checkpoint, out location);
    }

    internal StoreStatus TryInsert<TCheckpoint>(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong candidateBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out DirectoryLocation location)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        location = default;
        if (!TryDecodeBinding(candidateBinding, out var decoded)
            || decoded.SlotIndex >= _layout.SlotCount)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        ref var slot = ref Slot(decoded.SlotIndex);
        StoreStatus validationStatus = ValidateBinding(
            candidateBinding,
            keyHash,
            key,
            budget,
            out BindingValidation validation);
        if (validationStatus != StoreStatus.Success)
        {
            return validationStatus;
        }

        if (validation == BindingValidation.Stale)
        {
            return StoreStatus.InvalidReservation;
        }

        CurrentSlotStatus currentSlot = TryReadCurrentSlotStatus(
            candidateBinding,
            ref slot,
            out int currentState,
            out _);
        if (validation != BindingValidation.Exact
            || currentSlot == CurrentSlotStatus.Invalid)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        if (currentSlot != CurrentSlotStatus.Current)
        {
            return currentSlot == CurrentSlotStatus.Retry
                ? StoreStatus.StoreBusy
                : StoreStatus.InvalidReservation;
        }

        if (currentState is not (SlotInitializing or SlotReserved))
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        var prepared = DirectoryOperation.Encode(
            IntentInsert,
            PhasePrepared,
            targetKind: 0,
            targetIndex: 0,
            generation: decoded.Generation);
        var prepareStatus = PrepareOperation(
            ref slot,
            candidateBinding,
            prepared,
            IntentInsert,
            budget);
        if (prepareStatus != StoreStatus.Success)
        {
            return prepareStatus;
        }

        GetBuckets(keyHash, out var canonicalBucket, out _);
        var claimStatus = ClaimMutation(canonicalBucket, candidateBinding, budget, ref checkpoint);
        if (claimStatus != StoreStatus.Success)
        {
            return claimStatus;
        }

        for (var attempt = 0; ; attempt++)
        {
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck);
            StoreStatus observedOutcome = ObserveInsertOutcomeBeforeBudget(
                candidateBinding,
                decoded.Generation,
                ref slot,
                ref checkpoint,
                out bool hasObservedOutcome,
                out location);
            if (hasObservedOutcome)
            {
                return observedOutcome;
            }

            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            var helpStatus = HelpMutation(canonicalBucket, budget, ref checkpoint, maxSteps: 8);
            if (helpStatus is not (StoreStatus.Success or StoreStatus.StoreBusy))
            {
                return helpStatus;
            }

            observedOutcome = ObserveInsertOutcomeBeforeBudget(
                candidateBinding,
                decoded.Generation,
                ref slot,
                ref checkpoint,
                out hasObservedOutcome,
                out location);
            if (hasObservedOutcome)
            {
                return observedOutcome;
            }

            var operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
            bool operationDecoded = TryDecodeOperation(operationRaw, out var operation);
            if (!operationDecoded
                || operation.Intent != IntentInsert
                || operation.Generation != decoded.Generation)
            {
                if (IsCanceledInsertObservation(
                        candidateBinding,
                        ref slot,
                        operationRaw,
                        operationDecoded,
                        operation))
                {
                    return StoreStatus.InvalidReservation;
                }

                CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                    candidateBinding,
                    ref slot,
                    out _,
                    out _);
                return bindingStatus switch
                {
                    CurrentSlotStatus.Current or CurrentSlotStatus.Invalid =>
                        CorruptFrom(nameof(LockFreeKeyDirectory)),
                    CurrentSlotStatus.Retry => StoreStatus.StoreBusy,
                    _ => StoreStatus.InvalidReservation,
                };
            }

            if (IsCanceledInsertObservation(
                    candidateBinding,
                    ref slot,
                    operationRaw,
                    operationDecoded,
                    operation))
            {
                return StoreStatus.InvalidReservation;
            }

            if (operation.Phase == PhaseRejected)
            {
                return StoreStatus.DuplicateKey;
            }

            if (operation.Phase == PhaseComplete)
            {
                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead);
                var locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
                if (!TryDecodeLocation(locationRaw, out location)
                    || location.Generation != decoded.Generation)
                {
                    StoreStatus revalidatedOutcome = ObserveInsertOutcomeBeforeBudget(
                        candidateBinding,
                        decoded.Generation,
                        ref slot,
                        ref checkpoint,
                        out bool hasRevalidatedOutcome,
                        out location);
                    if (hasRevalidatedOutcome)
                    {
                        return revalidatedOutcome;
                    }

                    continue;
                }

                return StoreStatus.Success;
            }

            if (attempt + 1 >= DefaultRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }

            Thread.SpinWait(4 << Math.Min(attempt, 10));
        }
    }

    internal StoreStatus TryUnlink(ulong exactBinding)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryUnlink(
            exactBinding,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint);
    }

    internal StoreStatus TryUnlink(
        ulong exactBinding,
        in LockFreeOperationBudget budget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return TryUnlink(exactBinding, budget, ref checkpoint);
    }

    internal StoreStatus TryUnlink<TCheckpoint>(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (!TryDecodeBinding(exactBinding, out var decoded) || decoded.SlotIndex >= _layout.SlotCount)
        {
            return CorruptHere();
        }

        ref var slot = ref Slot(decoded.SlotIndex);
        CurrentSlotStatus currentSlot = TryReadCurrentSlotStatus(
            exactBinding,
            ref slot,
            out int currentState,
            out ulong keyHash);
        if (currentSlot == CurrentSlotStatus.Invalid)
        {
            return CorruptHere();
        }

        if (currentSlot != CurrentSlotStatus.Current)
        {
            return currentSlot == CurrentSlotStatus.Retry
                ? StoreStatus.StoreBusy
                : StoreStatus.NotFound;
        }

        if (currentState is not (SlotAborting or SlotReclaiming))
        {
            return StoreStatus.StoreBusy;
        }

        var prepared = DirectoryOperation.Encode(
            IntentUnlink,
            PhasePrepared,
            targetKind: 0,
            targetIndex: 0,
            generation: decoded.Generation);
        var prepareStatus = PrepareOperation(
            ref slot,
            exactBinding,
            prepared,
            IntentUnlink,
            budget);
        if (prepareStatus != StoreStatus.Success)
        {
            return prepareStatus;
        }

        GetBuckets(keyHash, out var canonicalBucket, out _);
        var claimStatus = ClaimMutation(canonicalBucket, exactBinding, budget, ref checkpoint);
        if (claimStatus != StoreStatus.Success)
        {
            return claimStatus;
        }

        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ulong mutationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref BucketMutation(canonicalBucket)));
            if (mutationRaw != exactBinding)
            {
                StoreStatus reclaimMutation = ClaimMutation(
                    canonicalBucket,
                    exactBinding,
                    budget,
                    ref checkpoint);
                if (reclaimMutation == StoreStatus.StoreBusy)
                {
                    Thread.SpinWait(4 << Math.Min(attempt, 10));
                    continue;
                }

                if (reclaimMutation != StoreStatus.Success)
                {
                    return reclaimMutation;
                }
            }

            var helpStatus = HelpMutation(canonicalBucket, budget, ref checkpoint, maxSteps: 8);
            if (helpStatus is not (StoreStatus.Success or StoreStatus.StoreBusy))
            {
                return helpStatus;
            }

            var operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
            if (operationRaw == 0)
            {
                return StoreStatus.Success;
            }

            if (!TryDecodeOperation(operationRaw, out var operation)
                || operation.Intent != IntentUnlink
                || operation.Generation != decoded.Generation)
            {
                CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                    exactBinding,
                    ref slot,
                    out _,
                    out _);
                return bindingStatus is CurrentSlotStatus.Current or CurrentSlotStatus.Invalid
                    ? CorruptHere()
                    : bindingStatus == CurrentSlotStatus.Retry
                        ? StoreStatus.StoreBusy
                        : StoreStatus.Success;
            }

            if (operation.Phase == PhaseComplete)
            {
                StoreStatus finishStatus = FinishUnlink(
                    canonicalBucket,
                    exactBinding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint);
                if (finishStatus is not (StoreStatus.Success or StoreStatus.StoreBusy))
                {
                    return finishStatus;
                }
            }

            if (attempt + 1 >= DefaultRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }

            Thread.SpinWait(4 << Math.Min(attempt, 10));
        }
    }

    internal StoreStatus HelpMutation(int canonicalBucketIndex, int maxSteps = DefaultRetryBudget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return HelpMutation(
            canonicalBucketIndex,
            LockFreeOperationBudget.StructuralAttempt,
            ref checkpoint,
            maxSteps);
    }

    internal StoreStatus HelpMutation(
        int canonicalBucketIndex,
        in LockFreeOperationBudget budget,
        int maxSteps = DefaultRetryBudget)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return HelpMutation(canonicalBucketIndex, budget, ref checkpoint, maxSteps);
    }

    internal StoreStatus HelpMutationForKeyHash<TCheckpoint>(
        ulong keyHash,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        int maxSteps = DefaultRetryBudget)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        GetBuckets(keyHash, out int canonicalBucketIndex, out _);
        return HelpMutation(
            canonicalBucketIndex,
            budget,
            ref checkpoint,
            maxSteps);
    }

    internal StoreStatus HelpMutation<TCheckpoint>(
        int canonicalBucketIndex,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        int maxSteps = DefaultRetryBudget)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if ((uint)canonicalBucketIndex >= (uint)_layout.PrimaryBucketCount || maxSteps <= 0)
        {
            return CorruptHere();
        }

        ref var mutation = ref BucketMutation(canonicalBucketIndex);
        for (var step = 0; step < maxSteps; step++)
        {
            StoreStatus bound = budget.CheckPeriodic(step);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            var mutationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref mutation));
            if (mutationRaw == 0)
            {
                return StoreStatus.Success;
            }

            if (!TryDecodeBinding(mutationRaw, out var decoded) || decoded.SlotIndex >= _layout.SlotCount)
            {
                if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref mutation)) == mutationRaw)
                {
                    return CorruptHere();
                }

                continue;
            }

            ref var slot = ref Slot(decoded.SlotIndex);
            MutationSnapshotStatus snapshotStatus = TryReadMutationSnapshot(
                mutationRaw,
                ref slot,
                out ulong keyHash,
                out ulong operationRaw,
                out DirectoryOperation operation,
                out int slotState,
                out _);
            if (snapshotStatus == MutationSnapshotStatus.Retry)
            {
                continue;
            }

            if (snapshotStatus == MutationSnapshotStatus.Stale)
            {
                StoreStatus cleanup = TryClearBindingReference(
                    ref mutation,
                    mutationRaw,
                    out _);
                if (cleanup != StoreStatus.Success)
                {
                    return cleanup;
                }

                continue;
            }

            if (snapshotStatus == MutationSnapshotStatus.Invalid)
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (operationRaw == 0)
            {
                if (slotState is SlotAborting or SlotReclaiming)
                {
                    StoreStatus cleanup = TryClearBindingReference(
                        ref mutation,
                        mutationRaw,
                        out _);
                    if (cleanup != StoreStatus.Success)
                    {
                        return cleanup;
                    }

                    continue;
                }

                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (operation.Generation != decoded.Generation)
            {
                if (operation.Generation > decoded.Generation)
                {
                    CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                        mutationRaw,
                        ref slot,
                        out _,
                        out _);
                    if (bindingStatus == CurrentSlotStatus.Stale)
                    {
                        StoreStatus cleanup = TryClearBindingReference(
                            ref mutation,
                            mutationRaw,
                            out _);
                        if (cleanup != StoreStatus.Success)
                        {
                            return cleanup;
                        }

                        continue;
                    }

                    if (bindingStatus == CurrentSlotStatus.Retry)
                    {
                        continue;
                    }

                    // Never erase a future-generation descriptor on behalf of
                    // an older mutation. Seeing both as current is structural
                    // corruption rather than cleanup authority.
                    return CorruptFrom(nameof(LockFreeKeyDirectory));
                }

                StoreStatus operationCleanup = TryClearOperationReference(
                    ref slot.DirectoryOperation,
                    operationRaw,
                    out _);
                if (operationCleanup != StoreStatus.Success)
                {
                    return operationCleanup;
                }

                continue;
            }

            GetBuckets(keyHash, out var actualCanonical, out _);
            if (actualCanonical != canonicalBucketIndex)
            {
                CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                    mutationRaw,
                    ref slot,
                    out _,
                    out _);
                if (bindingStatus == CurrentSlotStatus.Stale)
                {
                    StoreStatus cleanup = TryClearBindingReference(
                        ref mutation,
                        mutationRaw,
                        out _);
                    if (cleanup != StoreStatus.Success)
                    {
                        return cleanup;
                    }

                    continue;
                }

                if (bindingStatus == CurrentSlotStatus.Retry)
                {
                    continue;
                }

                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterOperationValidation);
            StoreStatus currentOperation = ClassifyCurrentOperation(
                mutationRaw,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentOperation != StoreStatus.Success)
            {
                if (currentOperation == StoreStatus.CorruptStore)
                {
                    return currentOperation;
                }

                // The operation word may have advanced legitimately after the
                // snapshot. Retain the discoverable mutation while this exact
                // binding generation is current, and help its newer phase.
                CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                    mutationRaw,
                    ref slot,
                    out _,
                    out _);
                if (bindingStatus == CurrentSlotStatus.Invalid)
                {
                    return CorruptFrom(nameof(LockFreeKeyDirectory));
                }

                if (bindingStatus == CurrentSlotStatus.Stale)
                {
                    StoreStatus cleanup = TryClearBindingReference(
                        ref mutation,
                        mutationRaw,
                        out _);
                    if (cleanup != StoreStatus.Success)
                    {
                        return cleanup;
                    }
                }

                continue;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch);

            StoreStatus status = operation.Intent switch
            {
                IntentInsert => HelpInsert(
                    canonicalBucketIndex,
                    mutationRaw,
                    ref slot,
                    operationRaw,
                    operation,
                    slotState,
                    budget,
                    ref checkpoint),
                IntentUnlink => HelpUnlink(
                    canonicalBucketIndex,
                    mutationRaw,
                    ref slot,
                    operationRaw,
                    operation,
                    keyHash,
                    budget,
                    ref checkpoint),
                _ => CorruptFrom(nameof(LockFreeKeyDirectory))
            };
            if (status != StoreStatus.Success)
            {
                return status;
            }
        }

        return unchecked((ulong)AtomicControlWord.LoadAcquire(ref mutation)) == 0
            ? StoreStatus.Success
            : StoreStatus.StoreBusy;
    }

    internal int PrimaryOccupancy
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _layout.PrimaryLaneCount; index++)
            {
                count += AtomicControlWord.LoadAcquire(ref PrimaryCell(index)) == 0 ? 0 : 1;
            }

            return count;
        }
    }

    internal int OverflowOccupancy
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _layout.SlotCount; index++)
            {
                count += AtomicControlWord.LoadAcquire(ref OverflowCell(index)) == 0 ? 0 : 1;
            }

            return count;
        }
    }

    internal int SpilledBucketCount
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _layout.PrimaryBucketCount; index++)
            {
                ulong raw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref BucketSpillSummary(index)));
                count += TryDecodeSpillSummary(raw, out SpillSummary summary) && summary.IsPresent ? 1 : 0;
            }

            return count;
        }
    }

    internal ulong ReadSpillSummary(int canonicalBucketIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalBucketIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            canonicalBucketIndex,
            _layout.PrimaryBucketCount);
        return unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref BucketSpillSummary(canonicalBucketIndex)));
    }

    internal ulong ReadCanonicalMutation(int canonicalBucketIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(canonicalBucketIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(
            canonicalBucketIndex,
            _layout.PrimaryBucketCount);
        return unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref BucketMutation(canonicalBucketIndex)));
    }

    internal StoreStatus ContainsExactBindingReference(
        ulong exactBinding,
        in LockFreeOperationBudget budget,
        out bool containsReference)
    {
        containsReference = false;
        if (!TryDecodeBinding(exactBinding, out IndexBinding binding)
            || binding.SlotIndex >= _layout.SlotCount)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        var probe = 0;
        for (var bucketIndex = 0; bucketIndex < _layout.PrimaryBucketCount; bucketIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(probe++);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref long mutation = ref BucketMutation(bucketIndex);
            StoreStatus referenceStatus = TryReadStructurallyValidBindingReference(
                ref mutation,
                out ulong observed);
            if (referenceStatus != StoreStatus.Success)
            {
                return referenceStatus;
            }

            if (observed == exactBinding)
            {
                containsReference = true;
                return StoreStatus.Success;
            }
        }

        for (var cellIndex = 0; cellIndex < _layout.PrimaryLaneCount; cellIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(probe++);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref long cell = ref PrimaryCell(cellIndex);
            StoreStatus referenceStatus = TryReadStructurallyValidBindingReference(
                ref cell,
                out ulong observed);
            if (referenceStatus != StoreStatus.Success)
            {
                return referenceStatus;
            }

            if (observed == exactBinding)
            {
                containsReference = true;
                return StoreStatus.Success;
            }
        }

        for (var cellIndex = 0; cellIndex < _layout.SlotCount; cellIndex++)
        {
            StoreStatus bound = budget.CheckPeriodic(probe++);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            ref long cell = ref OverflowCell(cellIndex);
            StoreStatus referenceStatus = TryReadStructurallyValidBindingReference(
                ref cell,
                out ulong observed);
            if (referenceStatus != StoreStatus.Success)
            {
                return referenceStatus;
            }

            if (observed == exactBinding)
            {
                containsReference = true;
                return StoreStatus.Success;
            }
        }

        return StoreStatus.Success;
    }

    private StoreStatus ClaimMutation<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        ref var mutation = ref BucketMutation(canonicalBucketIndex);
        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            var observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref mutation));
            if (observed == binding)
            {
                return StoreStatus.Success;
            }

            if (observed == 0
                && AtomicControlWord.CompareExchange(
                    ref mutation,
                    unchecked((long)binding),
                    comparand: 0) == 0)
            {
                return StoreStatus.Success;
            }

            var helpStatus = HelpMutation(
                canonicalBucketIndex,
                budget,
                ref checkpoint,
                maxSteps: 8);
            if (helpStatus is not (StoreStatus.Success or StoreStatus.StoreBusy))
            {
                return helpStatus;
            }

            if (attempt + 1 >= DefaultRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private StoreStatus HelpInsert<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        int slotState,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (IsInsertCancellationState(slotState))
        {
            return CancelInsert(
                canonicalBucketIndex,
                binding,
                ref slot,
                operationRaw,
                operation,
                budget,
                ref checkpoint);
        }

        if (operation.Phase is PhaseRejected or PhaseComplete)
        {
            return CompleteMutationRelease(
                canonicalBucketIndex,
                binding,
                ref slot,
                operationRaw,
                operation,
                budget,
                ref checkpoint);
        }

        if (operation.Phase == PhasePrepared)
        {
            StoreStatus currentAfterCellClaim = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentAfterCellClaim != StoreStatus.Success)
            {
                return currentAfterCellClaim == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentAfterCellClaim;
            }

            if (IsInsertCancellationState(slotState))
            {
                return CancelInsert(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint);
            }

            if (slotState != SlotInitializing || !TryGetKey(ref slot, out var key))
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            ulong keyHash = slot.KeyHash;
            var lookupStatus = FindExact(
                key,
                keyHash,
                excludedBinding: 0,
                budget,
                ref checkpoint,
                out var existing,
                out var existingLocation);
            ulong next;
            if (lookupStatus == StoreStatus.Success)
            {
                next = existing == binding
                    ? DirectoryOperation.Encode(
                        IntentInsert,
                        PhaseTargetSelected,
                        existingLocation.Kind,
                        existingLocation.Index,
                        operation.Generation)
                    : DirectoryOperation.Encode(
                        IntentInsert,
                        PhaseRejected,
                        targetKind: 0,
                        targetIndex: 0,
                        generation: operation.Generation);
            }
            else if (lookupStatus == StoreStatus.NotFound)
            {
                var targetStatus = SelectInsertTarget(
                    keyHash,
                    operation.Generation,
                    budget,
                    ref checkpoint,
                    out var target);
                if (targetStatus != StoreStatus.Success)
                {
                    return targetStatus;
                }

                next = DirectoryOperation.Encode(
                    IntentInsert,
                    PhaseTargetSelected,
                    target.Kind,
                    target.Index,
                    operation.Generation);
            }
            else
            {
                return lookupStatus;
            }

            ulong observedNext = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)next),
                unchecked((long)operationRaw)));
            return ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                next,
                observedNext);
        }

        if (operation.Phase == PhaseTargetSelected)
        {
            if (!TryGetTargetCell(operation.Kind, operation.Index, out var cell))
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            StoreStatus currentAfterCellClaim = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentAfterCellClaim != StoreStatus.Success)
            {
                return currentAfterCellClaim == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentAfterCellClaim;
            }

            if (IsInsertCancellationState(slotState))
            {
                return CancelInsert(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint);
            }

            if (operation.Kind == TargetOverflow)
            {
                StoreStatus summaryStatus = PrepareOverflowPublication(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint,
                    out bool mayPublish);
                if (summaryStatus != StoreStatus.Success || !mayPublish)
                {
                    return summaryStatus;
                }
            }

            var observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell.Value));
            if (observed != binding)
            {
                if (observed == 0)
                {
                    observed = unchecked((ulong)AtomicControlWord.CompareExchange(
                        ref cell.Value,
                        unchecked((long)binding),
                        comparand: 0));
                }

                if (observed != 0 && observed != binding)
                {
                    StoreStatus validationStatus = ValidateBinding(
                        observed,
                        expectedHash: null,
                        expectedKey: default,
                        budget,
                        out BindingValidation observedValidation);
                    if (validationStatus != StoreStatus.Success)
                    {
                        return validationStatus;
                    }

                    if (observedValidation == BindingValidation.Stale)
                    {
                        return TryClearBindingReference(
                            ref cell.Value,
                            observed,
                            out _);
                    }

                    var prepared = DirectoryOperation.Encode(
                        IntentInsert,
                        PhasePrepared,
                        targetKind: 0,
                        targetIndex: 0,
                        generation: operation.Generation);
                    ulong observedPrepared = unchecked((ulong)AtomicControlWord.CompareExchange(
                        ref slot.DirectoryOperation,
                        unchecked((long)prepared),
                        unchecked((long)operationRaw)));
                    return ValidateOperationCasObservation(
                        ref slot.DirectoryOperation,
                        operationRaw,
                        prepared,
                        observedPrepared);
                }
            }

            StoreStatus postClaimOperationStatus = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (postClaimOperationStatus != StoreStatus.Success)
            {
                // Another helper may have consumed this exact cell and advanced
                // the same insert. In that case the cell is now the published
                // directory entry, not residue owned by this delayed helper.
                // Only roll it back when the descriptor did not reach the
                // side-effect-committed phase.
                ulong currentOperationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                    ref slot.DirectoryOperation));
                if (!IsSameOrLaterInsertPhase(currentOperationRaw, operation))
                {
                    StoreStatus cleanup = TryClearBindingReference(
                        ref cell.Value,
                        binding,
                        out _);
                    if (cleanup != StoreStatus.Success)
                    {
                        return cleanup;
                    }
                }

                return postClaimOperationStatus == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : postClaimOperationStatus;
            }

            if (IsInsertCancellationState(slotState))
            {
                return CancelInsert(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint);
            }

            ulong locationRaw = DirectoryLocation.Encode(
                operation.Kind,
                operation.Index,
                operation.Generation);
            StoreStatus locationPublication = TryPublishExactLocation(
                ref slot,
                binding,
                locationRaw,
                ref checkpoint,
                out bool locationPublished);
            if (locationPublication != StoreStatus.Success)
            {
                return locationPublication;
            }

            if (!locationPublished)
            {
                return TryClearBindingReference(
                    ref cell.Value,
                    binding,
                    out _);
            }

            var changed = DirectoryOperation.Encode(
                IntentInsert,
                PhaseBindingChanged,
                operation.Kind,
                operation.Index,
                operation.Generation);
            ulong observedOperation = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)changed),
                unchecked((long)operationRaw)));
            StoreStatus operationCas = ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                changed,
                observedOperation);
            if (operationCas != StoreStatus.Success)
            {
                return operationCas;
            }

            if (observedOperation != operationRaw
                && !IsSameOrLaterInsertPhase(observedOperation, operation))
            {
                StoreStatus cellCleanup = TryClearBindingReference(
                    ref cell.Value,
                    binding,
                    out _);
                if (cellCleanup != StoreStatus.Success)
                {
                    return cellCleanup;
                }

                StoreStatus locationCleanup = TryClearLocationReference(
                    ref slot.DirectoryLocation,
                    locationRaw,
                    out _);
                if (locationCleanup != StoreStatus.Success)
                {
                    return locationCleanup;
                }
            }

            return StoreStatus.Success;
        }

        if (operation.Phase == PhaseBindingChanged)
        {
            StoreStatus currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentOperation != StoreStatus.Success)
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            if (IsInsertCancellationState(slotState))
            {
                return CancelInsert(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    budget,
                    ref checkpoint);
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication);

            var reserveStatus = PublishReserved(ref slot, binding);
            if (reserveStatus != StoreStatus.Success)
            {
                currentOperation = ClassifyCurrentOperation(
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    out int currentState);
                if (currentOperation != StoreStatus.Success)
                {
                    return currentOperation == StoreStatus.NotFound
                        ? StoreStatus.Success
                        : currentOperation;
                }

                if (IsInsertCancellationState(currentState))
                {
                    return CancelInsert(
                        canonicalBucketIndex,
                        binding,
                        ref slot,
                        operationRaw,
                        operation,
                        budget,
                        ref checkpoint);
                }

                return reserveStatus == StoreStatus.CorruptStore
                    ? CorruptFrom(nameof(LockFreeKeyDirectory))
                    : reserveStatus;
            }

            var complete = DirectoryOperation.Encode(
                IntentInsert,
                PhaseComplete,
                operation.Kind,
                operation.Index,
                operation.Generation);
            ulong observedComplete = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)complete),
                unchecked((long)operationRaw)));
            return ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                complete,
                observedComplete);
        }

        return CorruptFrom(nameof(LockFreeKeyDirectory));
    }

    private StoreStatus PrepareOverflowPublication<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out bool mayPublish)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        mayPublish = false;
        ulong desired;
        try
        {
            desired = SpillSummary.EncodePresent(binding);
        }
        catch (ArgumentOutOfRangeException)
        {
            return CorruptHere();
        }
        catch (OverflowException)
        {
            return CorruptHere();
        }

        ref long summaryWord = ref BucketSpillSummary(canonicalBucketIndex);
        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
                canonicalBucketIndex,
                binding,
                ref slot,
                operationRaw,
                operation,
                out int initialState);
            if (canonicalOperation != StoreStatus.Success
                || IsInsertCancellationState(initialState))
            {
                return canonicalOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : canonicalOperation;
            }

            ulong observedRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref summaryWord));
            if (!TryDecodeSpillSummary(observedRaw, out SpillSummary observed))
            {
                return CorruptHere();
            }

            if (observed.Binding == binding)
            {
                if (!observed.IsPresent)
                {
                    // Empty(binding) is a terminal version for this exact
                    // insertion lifecycle. Cancellation may have published it
                    // while this TargetSelected helper was between state
                    // validations; that legal transition suppresses further
                    // publication. Otherwise re-publishing it would recreate
                    // an ABA value and therefore fails closed.
                    StoreStatus currentOperation = ClassifyCurrentOperation(
                        binding,
                        ref slot,
                        operationRaw,
                        operation,
                        out int currentState);
                    if (currentOperation != StoreStatus.Success
                        || IsInsertCancellationState(currentState))
                    {
                        return currentOperation == StoreStatus.NotFound
                            ? StoreStatus.Success
                            : currentOperation;
                    }

                    return CorruptHere();
                }

                LockFreeCheckpoint.Reach(
                    ref checkpoint,
                    LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication);
                canonicalOperation = ClassifyCurrentCanonicalOperation(
                        canonicalBucketIndex,
                        binding,
                        ref slot,
                        operationRaw,
                        operation,
                        out int publishedState);
                if (canonicalOperation == StoreStatus.Success
                    && !IsInsertCancellationState(publishedState)
                    && unchecked((ulong)AtomicControlWord.LoadAcquire(ref summaryWord)) == desired)
                {
                    mayPublish = true;
                }

                if (canonicalOperation is not (StoreStatus.Success or StoreStatus.NotFound))
                {
                    return canonicalOperation;
                }

                return StoreStatus.Success;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas);
            canonicalOperation = ClassifyCurrentCanonicalOperation(
                canonicalBucketIndex,
                binding,
                ref slot,
                operationRaw,
                operation,
                out int preCasState);
            if (canonicalOperation != StoreStatus.Success
                || IsInsertCancellationState(preCasState))
            {
                return canonicalOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : canonicalOperation;
            }

            ulong exchanged = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref summaryWord,
                unchecked((long)desired),
                unchecked((long)observedRaw)));
            StoreStatus summaryCas = ValidateSpillSummaryCasObservation(
                ref summaryWord,
                observedRaw,
                desired,
                exchanged);
            if (summaryCas != StoreStatus.Success)
            {
                return summaryCas;
            }

            if (exchanged != observedRaw)
            {
                _telemetry.RecordCasLoss();
                continue;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication);
            canonicalOperation = ClassifyCurrentCanonicalOperation(
                    canonicalBucketIndex,
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    out int postCasState);
            if (canonicalOperation == StoreStatus.Success
                && !IsInsertCancellationState(postCasState)
                && unchecked((ulong)AtomicControlWord.LoadAcquire(ref summaryWord)) == desired)
            {
                mayPublish = true;
            }

            if (canonicalOperation is not (StoreStatus.Success or StoreStatus.NotFound))
            {
                return canonicalOperation;
            }

            // A failed post-CAS validation deliberately retains Present(binding).
            // It is a conservative positive and must never be rolled back by a
            // setter that no longer owns the exact canonical mutation.
            return StoreStatus.Success;
        }
    }

    private StoreStatus HelpUnlink<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        ulong keyHash,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (operation.Phase == PhaseComplete)
        {
            return FinishUnlink(
                canonicalBucketIndex,
                binding,
                ref slot,
                operationRaw,
                operation,
                budget,
                ref checkpoint);
        }

        if (operation.Phase == PhasePrepared)
        {
            StoreStatus currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out int slotState);
            if (currentOperation != StoreStatus.Success
                || slotState is not (SlotAborting or SlotReclaiming))
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead);
            var locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
            DirectoryLocation location;
            if (locationRaw == 0)
            {
                StoreStatus findLocation = TryFindExactBindingLocation(
                    binding,
                    keyHash,
                    operation.Generation,
                    budget,
                    out location);
                if (findLocation == StoreStatus.NotFound)
                {
                    var completeWithoutTarget = DirectoryOperation.Encode(
                        IntentUnlink,
                        PhaseComplete,
                        targetKind: 0,
                        targetIndex: 0,
                        generation: operation.Generation);
                    ulong observedOperation = unchecked((ulong)AtomicControlWord.CompareExchange(
                        ref slot.DirectoryOperation,
                        unchecked((long)completeWithoutTarget),
                        unchecked((long)operationRaw)));
                    return ValidateOperationCasObservation(
                        ref slot.DirectoryOperation,
                        operationRaw,
                        completeWithoutTarget,
                        observedOperation);
                }

                if (findLocation != StoreStatus.Success)
                {
                    return findLocation;
                }

                ulong recoveredLocation = location.Value;
                StoreStatus locationPublication = TryPublishExactLocation(
                    ref slot,
                    binding,
                    recoveredLocation,
                    ref checkpoint,
                    out bool locationPublished);
                if (locationPublication != StoreStatus.Success)
                {
                    return locationPublication;
                }

                if (!locationPublished)
                {
                    return StoreStatus.Success;
                }

                locationRaw = recoveredLocation;
            }
            else if (!TryDecodeLocation(locationRaw, out location))
            {
                currentOperation = ClassifyCurrentOperation(
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    out _);
                return currentOperation == StoreStatus.Success
                    ? CorruptHere()
                    : currentOperation == StoreStatus.NotFound
                        ? StoreStatus.Success
                        : currentOperation;
            }

            if (location.Generation != operation.Generation)
            {
                // A later generation proves that this helper lost the slot
                // after its last exact-operation validation. It must never
                // erase the new lifecycle's location. An older residue can be
                // removed only while the exact operation is still current;
                // the value-tagged CAS then remains safe if reuse wins next.
                currentOperation = ClassifyCurrentOperation(
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    out _);
                if (location.Generation < operation.Generation
                    && currentOperation == StoreStatus.Success)
                {
                    StoreStatus locationCleanup = TryClearLocationReference(
                        ref slot.DirectoryLocation,
                        locationRaw,
                        out _);
                    if (locationCleanup != StoreStatus.Success)
                    {
                        return locationCleanup;
                    }
                }

                if (currentOperation is not (StoreStatus.Success or StoreStatus.NotFound))
                {
                    return currentOperation;
                }

                return StoreStatus.Success;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterLocationValidation);
            currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentOperation != StoreStatus.Success)
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            var target = DirectoryOperation.Encode(
                IntentUnlink,
                PhaseTargetSelected,
                location.Kind,
                location.Index,
                operation.Generation);
            ulong observedTarget = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)target),
                unchecked((long)operationRaw)));
            return ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                target,
                observedTarget);
        }

        if (operation.Phase == PhaseTargetSelected)
        {
            if (!TryGetTargetCell(operation.Kind, operation.Index, out var cell))
            {
                return CorruptHere();
            }

            StoreStatus currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out int slotState);
            if (currentOperation != StoreStatus.Success
                || slotState is not (SlotAborting or SlotReclaiming))
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead);
            ulong expectedLocation = DirectoryLocation.Encode(
                operation.Kind,
                operation.Index,
                operation.Generation);
            ulong observedLocation = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
            if (observedLocation != 0 && observedLocation != expectedLocation)
            {
                if (!TryDecodeLocation(observedLocation, out var otherLocation))
                {
                    currentOperation = ClassifyCurrentOperation(
                        binding,
                        ref slot,
                        operationRaw,
                        operation,
                        out _);
                    return currentOperation == StoreStatus.Success
                        ? CorruptHere()
                        : currentOperation == StoreStatus.NotFound
                            ? StoreStatus.Success
                            : currentOperation;
                }

                if (otherLocation.Generation != operation.Generation)
                {
                    // Future-generation metadata belongs to a reused slot.
                    // Older residue is removable only under a fresh exact
                    // operation validation and an exact-value CAS.
                    currentOperation = ClassifyCurrentOperation(
                        binding,
                        ref slot,
                        operationRaw,
                        operation,
                        out _);
                    if (otherLocation.Generation < operation.Generation
                        && currentOperation == StoreStatus.Success)
                    {
                        StoreStatus staleLocationCleanup = TryClearLocationReference(
                            ref slot.DirectoryLocation,
                            observedLocation,
                            out _);
                        if (staleLocationCleanup != StoreStatus.Success)
                        {
                            return staleLocationCleanup;
                        }
                    }

                    if (currentOperation is not (StoreStatus.Success or StoreStatus.NotFound))
                    {
                        return currentOperation;
                    }

                    return StoreStatus.Success;
                }

                currentOperation = ClassifyCurrentOperation(
                    binding,
                    ref slot,
                    operationRaw,
                    operation,
                    out _);
                return currentOperation == StoreStatus.Success
                    ? CorruptHere()
                    : currentOperation == StoreStatus.NotFound
                        ? StoreStatus.Success
                        : currentOperation;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterLocationValidation);
            currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out slotState);
            if (currentOperation != StoreStatus.Success)
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            StoreStatus cellCleanup = TryClearBindingReference(
                ref cell.Value,
                binding,
                out _);
            if (cellCleanup != StoreStatus.Success)
            {
                return cellCleanup;
            }

            StoreStatus locationCleanup = TryClearLocationReference(
                ref slot.DirectoryLocation,
                expectedLocation,
                out _);
            if (locationCleanup != StoreStatus.Success)
            {
                return locationCleanup;
            }

            var changed = DirectoryOperation.Encode(
                IntentUnlink,
                PhaseBindingChanged,
                operation.Kind,
                operation.Index,
                operation.Generation);
            ulong observedChanged = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)changed),
                unchecked((long)operationRaw)));
            return ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                changed,
                observedChanged);
        }

        if (operation.Phase == PhaseBindingChanged)
        {
            StoreStatus currentOperation = ClassifyCurrentOperation(
                binding,
                ref slot,
                operationRaw,
                operation,
                out int slotState);
            if (currentOperation != StoreStatus.Success
                || slotState is not (SlotAborting or SlotReclaiming))
            {
                return currentOperation == StoreStatus.NotFound
                    ? StoreStatus.Success
                    : currentOperation;
            }

            var complete = DirectoryOperation.Encode(
                IntentUnlink,
                PhaseComplete,
                operation.Kind,
                operation.Index,
                operation.Generation);
            ulong observedComplete = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)complete),
                unchecked((long)operationRaw)));
            return ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                operationRaw,
                complete,
                observedComplete);
        }

        return CorruptHere();
    }

    private StoreStatus FinishUnlink<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (operation.Kind is TargetPrimary or TargetOverflow)
        {
            ulong expectedLocation = DirectoryLocation.Encode(
                operation.Kind,
                operation.Index,
                operation.Generation);
            StoreStatus locationCleanup = TryClearLocationReference(
                ref slot.DirectoryLocation,
                expectedLocation,
                out _);
            if (locationCleanup != StoreStatus.Success)
            {
                return locationCleanup;
            }
        }

        StoreStatus releaseStatus = CompleteMutationRelease(
            canonicalBucketIndex,
            binding,
            ref slot,
            operationRaw,
            operation,
            budget,
            ref checkpoint);
        if (releaseStatus != StoreStatus.Success)
        {
            return releaseStatus;
        }

        StoreStatus operationCleanup = TryClearOperationReference(
            ref slot.DirectoryOperation,
            operationRaw,
            out bool clearedOperation);
        if (operationCleanup != StoreStatus.Success)
        {
            return operationCleanup;
        }

        if (clearedOperation)
        {
            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance);
        }

        return StoreStatus.Success;
    }

    private StoreStatus CancelInsert<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        if (operation.Kind is TargetPrimary or TargetOverflow
            && TryGetTargetCell(operation.Kind, operation.Index, out var cell))
        {
            StoreStatus cellCleanup = TryClearBindingReference(
                ref cell.Value,
                binding,
                out _);
            if (cellCleanup != StoreStatus.Success)
            {
                return cellCleanup;
            }

            ulong expectedLocation = DirectoryLocation.Encode(
                operation.Kind,
                operation.Index,
                operation.Generation);
            StoreStatus locationCleanup = TryClearLocationReference(
                ref slot.DirectoryLocation,
                expectedLocation,
                out _);
            if (locationCleanup != StoreStatus.Success)
            {
                return locationCleanup;
            }
        }

        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.DirectoryAfterCancelLocationClearBeforeDescriptorRejection);

        StoreStatus summaryStatus = PrepareSpillSummaryForMutationRelease(
            canonicalBucketIndex,
            binding,
            ref slot,
            operationRaw,
            operation,
            budget,
            ref checkpoint);
        if (summaryStatus != StoreStatus.Success)
        {
            return summaryStatus;
        }

        StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            binding,
            ref slot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation != StoreStatus.Success)
        {
            return canonicalOperation == StoreStatus.NotFound
                ? StoreStatus.Success
                : canonicalOperation;
        }

        ulong rejected = DirectoryOperation.Encode(
            IntentInsert,
            PhaseRejected,
            targetKind: 0,
            targetIndex: 0,
            generation: operation.Generation);
        ulong exchanged = unchecked((ulong)AtomicControlWord.CompareExchange(
            ref slot.DirectoryOperation,
            unchecked((long)rejected),
            unchecked((long)operationRaw)));
        StoreStatus operationCas = ValidateOperationCasObservation(
            ref slot.DirectoryOperation,
            operationRaw,
            rejected,
            exchanged);
        if (operationCas != StoreStatus.Success)
        {
            return operationCas;
        }

        if (exchanged == operationRaw)
        {
            return TryClearMutationWord(canonicalBucketIndex, binding);
        }

        return StoreStatus.Success;
    }

    private StoreStatus CompleteMutationRelease<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        StoreStatus summaryStatus = PrepareSpillSummaryForMutationRelease(
            canonicalBucketIndex,
            binding,
            ref slot,
            operationRaw,
            operation,
            budget,
            ref checkpoint);
        if (summaryStatus != StoreStatus.Success)
        {
            return summaryStatus;
        }

        StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            binding,
            ref slot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation == StoreStatus.Success)
        {
            return TryClearMutationWord(canonicalBucketIndex, binding);
        }

        if (canonicalOperation is not (StoreStatus.Success or StoreStatus.NotFound))
        {
            return canonicalOperation;
        }

        return StoreStatus.Success;
    }

    private StoreStatus PrepareSpillSummaryForMutationRelease<TCheckpoint>(
        int canonicalBucketIndex,
        ulong binding,
        ref ValueSlotMetadataV2 operationSlot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            binding,
            ref operationSlot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation != StoreStatus.Success)
        {
            return canonicalOperation == StoreStatus.NotFound
                ? StoreStatus.Success
                : canonicalOperation;
        }

        ref long summaryWord = ref BucketSpillSummary(canonicalBucketIndex);
        ulong capturedRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref summaryWord));
        if (!TryDecodeSpillSummary(capturedRaw, out SpillSummary captured))
        {
            return CorruptHere();
        }

        if (!captured.IsPresent)
        {
            return StoreStatus.Success;
        }

        StoreStatus witnessStatus = TryRetainPresentForExactOverflowWitness(
            canonicalBucketIndex,
            captured,
            binding,
            ref operationSlot,
            operationRaw,
            operation,
            budget,
            ref checkpoint,
            out bool retainedForExactWitness);
        if (witnessStatus != StoreStatus.Success || retainedForExactWitness)
        {
            return witnessStatus;
        }

        var scannedCellCount = 0;
        for (var index = 0; index < _layout.SlotCount; index++)
        {
            StoreStatus bound = budget.CheckPeriodic(index);
            if (bound != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return bound;
            }

            ref long cell = ref OverflowCell(index);
            ulong cellBinding = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell));
            scannedCellCount++;
            if (cellBinding == 0)
            {
                continue;
            }

            StoreStatus validationStatus = ValidateBinding(
                cellBinding,
                expectedHash: null,
                expectedKey: default,
                budget,
                out BindingValidation validation);
            if (validationStatus != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return validationStatus;
            }

            if (validation == BindingValidation.Invalid)
            {
                StoreStatus revalidationStatus = RevalidateInvalidBindingReference(
                    ref cell,
                    cellBinding,
                    cellBinding,
                    expectedHash: null,
                    expectedKey: default,
                    budget,
                    ref checkpoint,
                    out validation,
                    out bool referenceRemainsExact);
                if (revalidationStatus != StoreStatus.Success)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    return revalidationStatus;
                }

                if (!referenceRemainsExact)
                {
                    continue;
                }
            }

            if (validation == BindingValidation.Invalid)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (validation == BindingValidation.Stale)
            {
                StoreStatus cleanup = TryClearBindingReference(
                    ref cell,
                    cellBinding,
                    out _);
                if (cleanup != StoreStatus.Success)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    return cleanup;
                }

                continue;
            }

            if (!TryDecodeBinding(cellBinding, out IndexBinding decoded)
                || decoded.SlotIndex >= _layout.SlotCount)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return CorruptHere();
            }

            ref ValueSlotMetadataV2 candidateSlot = ref Slot(decoded.SlotIndex);
            CurrentSlotStatus currentSlot = TryReadCurrentSlotStatus(
                cellBinding,
                ref candidateSlot,
                out _,
                out ulong keyHash);
            if (currentSlot != CurrentSlotStatus.Current)
            {
                StoreStatus revalidationStatus = ValidateBinding(
                    cellBinding,
                    expectedHash: null,
                    expectedKey: default,
                    budget,
                    out BindingValidation revalidation);
                if (revalidationStatus != StoreStatus.Success)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    return revalidationStatus;
                }

                if (revalidation == BindingValidation.Invalid)
                {
                    revalidationStatus = RevalidateInvalidBindingReference(
                        ref cell,
                        cellBinding,
                        cellBinding,
                        expectedHash: null,
                        expectedKey: default,
                        budget,
                        ref checkpoint,
                        out revalidation,
                        out bool referenceRemainsExact);
                    if (revalidationStatus != StoreStatus.Success)
                    {
                        _telemetry.RecordOverflowScan(scannedCellCount);
                        return revalidationStatus;
                    }

                    if (!referenceRemainsExact)
                    {
                        continue;
                    }
                }

                if (revalidation == BindingValidation.Stale)
                {
                    StoreStatus cleanup = TryClearBindingReference(
                        ref cell,
                        cellBinding,
                        out _);
                    if (cleanup != StoreStatus.Success)
                    {
                        _telemetry.RecordOverflowScan(scannedCellCount);
                        return cleanup;
                    }

                    continue;
                }

                _telemetry.RecordOverflowScan(scannedCellCount);
                return revalidation == BindingValidation.Invalid
                    ? CorruptHere()
                    : StoreStatus.StoreBusy;
            }

            GetBuckets(keyHash, out int candidateCanonicalBucket, out _);
            if (candidateCanonicalBucket == canonicalBucketIndex)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return TryRepointPresentSpillSummary(
                    canonicalBucketIndex,
                    capturedRaw,
                    captured,
                    cellBinding,
                    binding,
                    ref operationSlot,
                    operationRaw,
                    operation);
            }
        }

        _telemetry.RecordOverflowScan(scannedCellCount);
        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan);
        canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            binding,
            ref operationSlot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation != StoreStatus.Success)
        {
            return canonicalOperation == StoreStatus.NotFound
                ? StoreStatus.Success
                : canonicalOperation;
        }

        ulong empty = captured.EmptyValue;
        ulong exchanged = unchecked((ulong)AtomicControlWord.CompareExchange(
            ref summaryWord,
            unchecked((long)empty),
            unchecked((long)capturedRaw)));
        StoreStatus summaryCas = ValidateSpillSummaryCasObservation(
            ref summaryWord,
            capturedRaw,
            empty,
            exchanged);
        if (summaryCas != StoreStatus.Success)
        {
            return summaryCas;
        }

        if (exchanged != capturedRaw && exchanged != empty)
        {
            return StoreStatus.StoreBusy;
        }

        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.DirectoryAfterSpillSummaryClear);
        return StoreStatus.Success;
    }

    private StoreStatus TryRetainPresentForExactOverflowWitness<TCheckpoint>(
        int canonicalBucketIndex,
        SpillSummary captured,
        ulong operationBinding,
        ref ValueSlotMetadataV2 operationSlot,
        ulong operationRaw,
        DirectoryOperation operation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out bool retained)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        retained = false;
        ulong witnessBinding = captured.Binding;
        StoreStatus validationStatus = ValidateBinding(
            witnessBinding,
            expectedHash: null,
            expectedKey: default,
            budget,
            out BindingValidation validation);
        if (validationStatus != StoreStatus.Success)
        {
            return validationStatus;
        }

        if (validation == BindingValidation.Invalid)
        {
            ref long summaryWord = ref BucketSpillSummary(canonicalBucketIndex);
            StoreStatus revalidationStatus = RevalidateInvalidBindingReference(
                ref summaryWord,
                captured.Value,
                witnessBinding,
                expectedHash: null,
                expectedKey: default,
                budget,
                ref checkpoint,
                out validation,
                out bool referenceRemainsExact);
            if (revalidationStatus != StoreStatus.Success)
            {
                return revalidationStatus;
            }

            if (!referenceRemainsExact)
            {
                return StoreStatus.Success;
            }
        }

        if (validation == BindingValidation.Invalid)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        if (validation == BindingValidation.Stale
            || !TryDecodeBinding(witnessBinding, out IndexBinding witness)
            || witness.SlotIndex >= _layout.SlotCount)
        {
            return StoreStatus.Success;
        }

        ref ValueSlotMetadataV2 witnessSlot = ref Slot(witness.SlotIndex);
        CurrentSlotStatus witnessStatus = TryReadCurrentSlotStatus(
            witnessBinding,
            ref witnessSlot,
            out _,
            out ulong witnessHash);
        if (witnessStatus != CurrentSlotStatus.Current)
        {
            return witnessStatus == CurrentSlotStatus.Invalid
                ? CorruptFrom(nameof(LockFreeKeyDirectory))
                : witnessStatus == CurrentSlotStatus.Retry
                    ? StoreStatus.StoreBusy
                    : StoreStatus.Success;
        }

        GetBuckets(witnessHash, out int witnessCanonicalBucket, out _);
        ulong locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
            ref witnessSlot.DirectoryLocation));
        if (witnessCanonicalBucket != canonicalBucketIndex
            || !TryDecodeLocation(locationRaw, out DirectoryLocation location)
            || location.Kind != TargetOverflow
            || location.Generation != witness.Generation
            || !TryGetTargetCell(location.Kind, location.Index, out CellReference cell)
            || unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell.Value)) != witnessBinding)
        {
            return StoreStatus.Success;
        }

        // If another helper already released this operation, conservative
        // Present is still safe and this stale helper must not do further work.
        StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            operationBinding,
            ref operationSlot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation != StoreStatus.Success)
        {
            retained = true;
            return canonicalOperation == StoreStatus.NotFound
                ? StoreStatus.Success
                : canonicalOperation;
        }

        // While the exact canonical mutation remains ours, no unlink for the
        // witness can pass its own canonical-operation validation. Re-read the
        // complete witness tuple before retaining Present without a table scan.
        witnessStatus = TryReadCurrentSlotStatus(
            witnessBinding,
            ref witnessSlot,
            out _,
            out ulong revalidatedHash);
        if (witnessStatus == CurrentSlotStatus.Invalid)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            operationBinding,
            ref operationSlot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation is not (StoreStatus.Success or StoreStatus.NotFound))
        {
            return canonicalOperation;
        }

        if (witnessStatus == CurrentSlotStatus.Current
            && revalidatedHash == witnessHash
            && unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref witnessSlot.DirectoryLocation)) == locationRaw
            && unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell.Value)) == witnessBinding
            && canonicalOperation == StoreStatus.Success)
        {
            retained = true;
        }

        return StoreStatus.Success;
    }

    private StoreStatus TryRepointPresentSpillSummary(
        int canonicalBucketIndex,
        ulong capturedRaw,
        SpillSummary captured,
        ulong witnessBinding,
        ulong operationBinding,
        ref ValueSlotMetadataV2 operationSlot,
        ulong operationRaw,
        DirectoryOperation operation)
    {
        if (witnessBinding == captured.Binding)
        {
            return StoreStatus.Success;
        }

        StoreStatus canonicalOperation = ClassifyCurrentCanonicalOperation(
            canonicalBucketIndex,
            operationBinding,
            ref operationSlot,
            operationRaw,
            operation,
            out _);
        if (canonicalOperation != StoreStatus.Success)
        {
            return canonicalOperation == StoreStatus.NotFound
                ? StoreStatus.Success
                : canonicalOperation;
        }

        ulong desired;
        try
        {
            desired = SpillSummary.EncodePresent(witnessBinding);
        }
        catch (ArgumentOutOfRangeException)
        {
            return CorruptHere();
        }
        catch (OverflowException)
        {
            return CorruptHere();
        }

        ref long summaryWord = ref BucketSpillSummary(canonicalBucketIndex);
        ulong exchanged = unchecked((ulong)AtomicControlWord.CompareExchange(
            ref summaryWord,
            unchecked((long)desired),
            unchecked((long)capturedRaw)));
        StoreStatus summaryCas = ValidateSpillSummaryCasObservation(
            ref summaryWord,
            capturedRaw,
            desired,
            exchanged);
        if (summaryCas != StoreStatus.Success)
        {
            return summaryCas;
        }

        if (exchanged != capturedRaw && exchanged != desired)
        {
            _telemetry.RecordCasLoss();
            return StoreStatus.Success;
        }

        return StoreStatus.Success;
    }

    private StoreStatus ValidateSpillSummaryCasObservation(
        ref long summaryWord,
        ulong expected,
        ulong desired,
        ulong observed)
    {
        if (observed == expected
            || observed == desired
            || TryDecodeSpillSummary(observed, out _))
        {
            return StoreStatus.Success;
        }

        // A losing CAS returns a moment-in-time value. Confirm the exact raw
        // word before turning a malformed sample into durable corruption.
        if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref summaryWord)) != observed)
        {
            return StoreStatus.Success;
        }

        return CorruptHere();
    }

    private StoreStatus TryClearMutationWord(int canonicalBucketIndex, ulong binding)
    {
        if ((uint)canonicalBucketIndex >= (uint)_layout.PrimaryBucketCount)
        {
            return CorruptHere();
        }

        ref long mutation = ref BucketMutation(canonicalBucketIndex);
        return TryClearBindingReference(ref mutation, binding, out _);
    }

    /// <summary>
    /// Clears one exact directory binding without treating a valid replacement
    /// as cleanup authority or corruption. A malformed CAS winner is corrupt
    /// only when the exact same word remains installed on confirmation.
    /// </summary>
    private StoreStatus TryClearBindingReference(
        ref long reference,
        ulong expected,
        out bool clearedExact) =>
        TryClearExactReference(
            ref reference,
            expected,
            CleanupReferenceKind.Binding,
            out clearedExact);

    private StoreStatus TryClearLocationReference(
        ref long reference,
        ulong expected,
        out bool clearedExact) =>
        TryClearExactReference(
            ref reference,
            expected,
            CleanupReferenceKind.Location,
            out clearedExact);

    private StoreStatus TryClearOperationReference(
        ref long reference,
        ulong expected,
        out bool clearedExact) =>
        TryClearExactReference(
            ref reference,
            expected,
            CleanupReferenceKind.Operation,
            out clearedExact);

    private StoreStatus TryClearExactReference(
        ref long reference,
        ulong expected,
        CleanupReferenceKind kind,
        out bool clearedExact)
    {
        clearedExact = false;
        ulong observed = unchecked((ulong)AtomicControlWord.CompareExchange(
            ref reference,
            value: 0,
            comparand: unchecked((long)expected)));
        if (observed == expected)
        {
            clearedExact = true;
            return StoreStatus.Success;
        }

        if (observed == 0 || IsStructurallyValidCleanupWinner(observed, kind))
        {
            _telemetry.RecordCasLoss();
            return StoreStatus.Success;
        }

        // The CAS observation may already have been replaced. Only a stable
        // exact malformed winner is durable corruption; transient samples are
        // an ordinary legal race and are never rewritten by this helper.
        if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference)) != observed)
        {
            _telemetry.RecordCasLoss();
            return StoreStatus.Success;
        }

        return CorruptHere();
    }

    private StoreStatus ValidateOperationCasObservation(
        ref long reference,
        ulong expected,
        ulong desired,
        ulong observed)
    {
        if (observed == expected || observed == desired || observed == 0
            || IsStructurallyValidCleanupWinner(observed, CleanupReferenceKind.Operation))
        {
            if (observed != expected)
            {
                _telemetry.RecordCasLoss();
            }

            return StoreStatus.Success;
        }

        if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference)) != observed)
        {
            _telemetry.RecordCasLoss();
            return StoreStatus.Success;
        }

        return CorruptHere();
    }

    private bool IsStructurallyValidCleanupWinner(
        ulong raw,
        CleanupReferenceKind kind)
    {
        switch (kind)
        {
            case CleanupReferenceKind.Binding:
                return TryDecodeBinding(raw, out IndexBinding binding)
                    && (uint)binding.SlotIndex < (uint)_layout.SlotCount;
            case CleanupReferenceKind.Location:
                return TryDecodeLocation(raw, out DirectoryLocation location)
                    && TryGetTargetCell(location.Kind, location.Index, out _);
            case CleanupReferenceKind.Operation:
                return TryDecodeOperation(raw, out DirectoryOperation operation)
                    && IsOperationTargetInBounds(operation);
            default:
                return false;
        }
    }

    private StoreStatus TryPublishExactLocation<TCheckpoint>(
        ref ValueSlotMetadataV2 slot,
        ulong binding,
        ulong exactLocation,
        ref TCheckpoint checkpoint,
        out bool published)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        published = false;
        if (!TryDecodeBinding(binding, out var decodedBinding)
            || !TryDecodeLocation(exactLocation, out var decodedLocation)
            || decodedBinding.Generation != decodedLocation.Generation)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        for (var attempt = 0; attempt < DefaultRetryBudget; attempt++)
        {
            CurrentSlotStatus bindingStatus = TryReadCurrentSlotStatus(
                binding,
                ref slot,
                out _,
                out _);
            if (bindingStatus != CurrentSlotStatus.Current)
            {
                return bindingStatus == CurrentSlotStatus.Invalid
                    ? CorruptFrom(nameof(LockFreeKeyDirectory))
                    : bindingStatus == CurrentSlotStatus.Retry
                        ? StoreStatus.StoreBusy
                        : StoreStatus.Success;
            }

            LockFreeCheckpoint.Reach(
                ref checkpoint,
                LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation);
            ulong observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation));
            if (observed == exactLocation)
            {
                bindingStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                if (bindingStatus == CurrentSlotStatus.Current)
                {
                    published = true;
                    return StoreStatus.Success;
                }

                return bindingStatus == CurrentSlotStatus.Invalid
                    ? CorruptFrom(nameof(LockFreeKeyDirectory))
                    : bindingStatus == CurrentSlotStatus.Retry
                        ? StoreStatus.StoreBusy
                        : StoreStatus.Success;
            }

            if (observed == 0)
            {
                ulong exchanged = unchecked((ulong)AtomicControlWord.CompareExchange(
                    ref slot.DirectoryLocation,
                    unchecked((long)exactLocation),
                    comparand: 0));
                if (exchanged == 0)
                {
                    bindingStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                    if (bindingStatus == CurrentSlotStatus.Current)
                    {
                        published = true;
                        return StoreStatus.Success;
                    }

                    if (bindingStatus == CurrentSlotStatus.Invalid)
                    {
                        return CorruptFrom(nameof(LockFreeKeyDirectory));
                    }

                    StoreStatus locationCleanup = TryClearLocationReference(
                        ref slot.DirectoryLocation,
                        exactLocation,
                        out _);
                    if (locationCleanup != StoreStatus.Success)
                    {
                        return locationCleanup;
                    }

                    return bindingStatus == CurrentSlotStatus.Retry
                        ? StoreStatus.StoreBusy
                        : StoreStatus.Success;
                }

                continue;
            }

            if (!TryDecodeLocation(observed, out var existing))
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (existing.Generation == decodedBinding.Generation)
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            bindingStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
            if (bindingStatus == CurrentSlotStatus.Invalid)
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (existing.Generation > decodedBinding.Generation
                || bindingStatus != CurrentSlotStatus.Current)
            {
                // A future location can only belong to a lifecycle that reused
                // this slot after our validation. Never clear it. Older
                // residue is cleaned only while the exact binding remains
                // current; the exact-value CAS is safe if ownership changes
                // immediately after this check.
                return bindingStatus == CurrentSlotStatus.Retry
                    ? StoreStatus.StoreBusy
                    : StoreStatus.Success;
            }

            StoreStatus staleCleanup = TryClearLocationReference(
                ref slot.DirectoryLocation,
                observed,
                out _);
            if (staleCleanup != StoreStatus.Success)
            {
                return staleCleanup;
            }
        }

        return StoreStatus.StoreBusy;
    }

    private StoreStatus TryFindExactBindingLocation(
        ulong binding,
        ulong keyHash,
        long generation,
        in LockFreeOperationBudget budget,
        out DirectoryLocation location)
    {
        if (!TryDecodeBinding(binding, out IndexBinding decodedBinding)
            || decodedBinding.SlotIndex >= _layout.SlotCount)
        {
            location = default;
            return CorruptHere();
        }

        GetBuckets(keyHash, out int first, out int second);
        StoreStatus primaryStatus = TryFindExactPrimaryBinding(
            first,
            binding,
            generation,
            out location);
        if (primaryStatus == StoreStatus.Success)
        {
            return StoreStatus.Success;
        }

        if (primaryStatus != StoreStatus.NotFound)
        {
            return primaryStatus;
        }

        primaryStatus = TryFindExactPrimaryBinding(
            second,
            binding,
            generation,
            out location);
        if (primaryStatus == StoreStatus.Success)
        {
            return StoreStatus.Success;
        }

        if (primaryStatus != StoreStatus.NotFound)
        {
            return primaryStatus;
        }

        int start = OverflowStart(keyHash);
        var scannedCellCount = 0;
        for (var offset = 0; offset < _layout.SlotCount; offset++)
        {
            StoreStatus bound = budget.CheckPeriodic(offset);
            if (bound != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                location = default;
                return bound;
            }

            int index = (start + offset) % _layout.SlotCount;
            scannedCellCount++;
            ref long cell = ref OverflowCell(index);
            StoreStatus referenceStatus = TryReadStructurallyValidBindingReference(
                ref cell,
                out ulong observed);
            if (referenceStatus != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                location = default;
                return referenceStatus;
            }

            if (observed == binding)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                location = DirectoryLocation.Decode(
                    DirectoryLocation.Encode(TargetOverflow, index, generation));
                return StoreStatus.Success;
            }
        }

        _telemetry.RecordOverflowScan(scannedCellCount);
        location = default;
        return StoreStatus.NotFound;
    }

    private StoreStatus TryFindExactPrimaryBinding(
        int bucketIndex,
        ulong binding,
        long generation,
        out DirectoryLocation location)
    {
        int firstLane = bucketIndex * LayoutV2Constants.PrimaryLanesPerBucket;
        for (var lane = 0; lane < LayoutV2Constants.PrimaryLanesPerBucket; lane++)
        {
            int index = firstLane + lane;
            ref long cell = ref PrimaryCell(index);
            StoreStatus referenceStatus = TryReadStructurallyValidBindingReference(
                ref cell,
                out ulong observed);
            if (referenceStatus != StoreStatus.Success)
            {
                location = default;
                return referenceStatus;
            }

            if (observed == binding)
            {
                location = DirectoryLocation.Decode(
                    DirectoryLocation.Encode(TargetPrimary, index, generation));
                return StoreStatus.Success;
            }
        }

        location = default;
        return StoreStatus.NotFound;
    }

    private bool IsSameOrLaterInsertPhase(
        ulong observedRaw,
        DirectoryOperation expected)
    {
        return TryDecodeOperation(observedRaw, out var observed)
            && observed.Intent == IntentInsert
            && observed.Generation == expected.Generation
            && observed.Kind == expected.Kind
            && observed.Index == expected.Index
            && observed.Phase is PhaseBindingChanged or PhaseComplete;
    }

    private StoreStatus FindExact(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong excludedBinding,
        in LockFreeOperationBudget budget,
        out ulong binding,
        out DirectoryLocation location)
    {
        NoOpLockFreeCheckpoint checkpoint = default;
        return FindExact(
            key,
            keyHash,
            excludedBinding,
            budget,
            ref checkpoint,
            out binding,
            out location);
    }

    private StoreStatus FindExact<TCheckpoint>(
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong excludedBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ulong binding,
        out DirectoryLocation location)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        binding = 0;
        location = default;
        GetBuckets(keyHash, out var first, out var second);
        var status = ScanPrimaryBucket(
            first,
            key,
            keyHash,
            excludedBinding,
            budget,
            ref checkpoint,
            out binding,
            out location);
        if (status != StoreStatus.NotFound)
        {
            return status;
        }

        status = ScanPrimaryBucket(
            second,
            key,
            keyHash,
            excludedBinding,
            budget,
            ref checkpoint,
            out binding,
            out location);
        if (status != StoreStatus.NotFound)
        {
            return status;
        }

        ulong summaryRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref BucketSpillSummary(first)));
        if (!TryDecodeSpillSummary(summaryRaw, out SpillSummary summary))
        {
            return CorruptHere();
        }

        if (!summary.IsPresent)
        {
            return StoreStatus.NotFound;
        }

        var start = OverflowStart(keyHash);
        var scannedCellCount = 0;
        for (var offset = 0; offset < _layout.SlotCount; offset++)
        {
            StoreStatus bound = budget.CheckPeriodic(offset);
            if (bound != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return bound;
            }

            var index = (start + offset) % _layout.SlotCount;
            scannedCellCount++;
            status = InspectCell(
                ref OverflowCell(index),
                TargetOverflow,
                index,
                key,
                keyHash,
                excludedBinding,
                budget,
                ref checkpoint,
                out binding,
                out location);
            if (status != StoreStatus.NotFound)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                return status;
            }
        }

        _telemetry.RecordOverflowScan(scannedCellCount);
        return StoreStatus.NotFound;
    }

    private StoreStatus ScanPrimaryBucket<TCheckpoint>(
        int bucketIndex,
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong excludedBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ulong binding,
        out DirectoryLocation location)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        binding = 0;
        location = default;
        var firstLane = bucketIndex * LayoutV2Constants.PrimaryLanesPerBucket;
        for (var lane = 0; lane < LayoutV2Constants.PrimaryLanesPerBucket; lane++)
        {
            var cellIndex = firstLane + lane;
            var status = InspectCell(
                ref PrimaryCell(cellIndex),
                TargetPrimary,
                cellIndex,
                key,
                keyHash,
                excludedBinding,
                budget,
                ref checkpoint,
                out binding,
                out location);
            if (status != StoreStatus.NotFound)
            {
                return status;
            }
        }

        return StoreStatus.NotFound;
    }

    private StoreStatus InspectCell<TCheckpoint>(
        ref long cell,
        int kind,
        long index,
        ReadOnlySpan<byte> key,
        ulong keyHash,
        ulong excludedBinding,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out ulong binding,
        out DirectoryLocation location)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        binding = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell));
        location = default;
        if (binding == 0 || binding == excludedBinding)
        {
            binding = 0;
            return StoreStatus.NotFound;
        }

        StoreStatus validationStatus = ValidateBinding(
            binding,
            keyHash,
            key,
            budget,
            out BindingValidation validation);
        if (validationStatus != StoreStatus.Success)
        {
            return validationStatus;
        }

        if (validation == BindingValidation.Invalid)
        {
            StoreStatus revalidationStatus = RevalidateInvalidBindingReference(
                ref cell,
                binding,
                binding,
                keyHash,
                key,
                budget,
                ref checkpoint,
                out validation,
                out bool referenceRemainsExact);
            if (revalidationStatus != StoreStatus.Success)
            {
                return revalidationStatus;
            }

            if (!referenceRemainsExact)
            {
                binding = 0;
                return StoreStatus.NotFound;
            }
        }

        if (validation == BindingValidation.Stale)
        {
            StoreStatus cleanup = TryClearBindingReference(
                ref cell,
                binding,
                out _);
            if (cleanup != StoreStatus.Success)
            {
                return cleanup;
            }

            binding = 0;
            return StoreStatus.NotFound;
        }

        if (validation == BindingValidation.Invalid)
        {
            return CorruptHere();
        }

        if (validation != BindingValidation.Exact
            || unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell)) != binding)
        {
            binding = 0;
            return StoreStatus.NotFound;
        }

        if (!TryDecodeBinding(binding, out var decoded))
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        location = DirectoryLocation.Decode(DirectoryLocation.Encode(kind, index, decoded.Generation));
        return StoreStatus.Success;
    }

    private StoreStatus SelectInsertTarget<TCheckpoint>(
        ulong keyHash,
        long generation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out DirectoryLocation target)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        GetBuckets(keyHash, out var first, out var second);
        StoreStatus primaryStatus = TrySelectPrimary(
            first,
            generation,
            budget,
            ref checkpoint,
            out target);
        if (primaryStatus == StoreStatus.Success)
        {
            return StoreStatus.Success;
        }

        if (primaryStatus != StoreStatus.NotFound)
        {
            return primaryStatus;
        }

        primaryStatus = TrySelectPrimary(
            second,
            generation,
            budget,
            ref checkpoint,
            out target);
        if (primaryStatus == StoreStatus.Success)
        {
            return StoreStatus.Success;
        }

        if (primaryStatus != StoreStatus.NotFound)
        {
            return primaryStatus;
        }

        var start = OverflowStart(keyHash);
        var scannedCellCount = 0;
        for (var offset = 0; offset < _layout.SlotCount; offset++)
        {
            StoreStatus bound = budget.CheckPeriodic(offset);
            if (bound != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                target = default;
                return bound;
            }

            var index = (start + offset) % _layout.SlotCount;
            scannedCellCount++;
            ref var cell = ref OverflowCell(index);
            var raw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell));
            if (raw == 0)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                target = DirectoryLocation.Decode(
                    DirectoryLocation.Encode(TargetOverflow, index, generation));
                return StoreStatus.Success;
            }

            StoreStatus validationStatus = ValidateBinding(
                raw,
                expectedHash: null,
                expectedKey: default,
                budget,
                out BindingValidation validation);
            if (validationStatus != StoreStatus.Success)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                target = default;
                return validationStatus;
            }

            if (validation == BindingValidation.Invalid)
            {
                StoreStatus revalidationStatus = RevalidateInvalidBindingReference(
                    ref cell,
                    raw,
                    raw,
                    expectedHash: null,
                    expectedKey: default,
                    budget,
                    ref checkpoint,
                    out validation,
                    out bool referenceRemainsExact);
                if (revalidationStatus != StoreStatus.Success)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    target = default;
                    return revalidationStatus;
                }

                if (!referenceRemainsExact)
                {
                    continue;
                }
            }

            if (validation == BindingValidation.Invalid)
            {
                _telemetry.RecordOverflowScan(scannedCellCount);
                target = default;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (validation == BindingValidation.Stale)
            {
                StoreStatus cleanup = TryClearBindingReference(
                    ref cell,
                    raw,
                    out _);
                if (cleanup != StoreStatus.Success)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    target = default;
                    return cleanup;
                }

                if (AtomicControlWord.LoadAcquire(ref cell) == 0)
                {
                    _telemetry.RecordOverflowScan(scannedCellCount);
                    target = DirectoryLocation.Decode(
                        DirectoryLocation.Encode(TargetOverflow, index, generation));
                    return StoreStatus.Success;
                }
            }
        }

        _telemetry.RecordOverflowScan(scannedCellCount);
        target = default;
        return CorruptFrom(nameof(LockFreeKeyDirectory));
    }

    private StoreStatus TrySelectPrimary<TCheckpoint>(
        int bucketIndex,
        long generation,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out DirectoryLocation target)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        var firstLane = bucketIndex * LayoutV2Constants.PrimaryLanesPerBucket;
        for (var lane = 0; lane < LayoutV2Constants.PrimaryLanesPerBucket; lane++)
        {
            var index = firstLane + lane;
            ref var cell = ref PrimaryCell(index);
            var raw = unchecked((ulong)AtomicControlWord.LoadAcquire(ref cell));
            if (raw == 0)
            {
                target = DirectoryLocation.Decode(
                    DirectoryLocation.Encode(TargetPrimary, index, generation));
                return StoreStatus.Success;
            }

            StoreStatus validationStatus = ValidateBinding(
                raw,
                expectedHash: null,
                expectedKey: default,
                budget,
                out BindingValidation validation);
            if (validationStatus != StoreStatus.Success)
            {
                target = default;
                return validationStatus;
            }

            if (validation == BindingValidation.Invalid)
            {
                StoreStatus revalidationStatus = RevalidateInvalidBindingReference(
                    ref cell,
                    raw,
                    raw,
                    expectedHash: null,
                    expectedKey: default,
                    budget,
                    ref checkpoint,
                    out validation,
                    out bool referenceRemainsExact);
                if (revalidationStatus != StoreStatus.Success)
                {
                    target = default;
                    return revalidationStatus;
                }

                if (!referenceRemainsExact)
                {
                    continue;
                }
            }

            if (validation == BindingValidation.Invalid)
            {
                target = default;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (validation == BindingValidation.Stale)
            {
                StoreStatus cleanup = TryClearBindingReference(
                    ref cell,
                    raw,
                    out _);
                if (cleanup != StoreStatus.Success)
                {
                    target = default;
                    return cleanup;
                }

                if (AtomicControlWord.LoadAcquire(ref cell) == 0)
                {
                    target = DirectoryLocation.Decode(
                        DirectoryLocation.Encode(TargetPrimary, index, generation));
                    return StoreStatus.Success;
                }
            }
        }

        target = default;
        return StoreStatus.NotFound;
    }

    private StoreStatus ValidateBinding(
        ulong raw,
        ulong? expectedHash,
        ReadOnlySpan<byte> expectedKey,
        in LockFreeOperationBudget budget,
        out BindingValidation validation)
    {
        validation = BindingValidation.Invalid;
        if (!TryDecodeBinding(raw, out var binding) || binding.SlotIndex >= _layout.SlotCount)
        {
            return StoreStatus.Success;
        }

        ref var slot = ref Slot(binding.SlotIndex);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            var control1 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            ControlBindingStatus controlStatus1 = ClassifyControlBinding(
                control1,
                binding.Generation);
            if (controlStatus1 != ControlBindingStatus.Current)
            {
                validation = controlStatus1 == ControlBindingStatus.Stale
                    ? BindingValidation.Stale
                    : BindingValidation.Invalid;
                return StoreStatus.Success;
            }

            if (slot.DirectoryBinding != raw)
            {
                ulong controlAfterBindingChange = unchecked((ulong)AtomicControlWord.LoadAcquire(
                    ref slot.Control));
                if (control1 != controlAfterBindingChange)
                {
                    continue;
                }

                ControlBindingStatus controlStatus2 = ClassifyControlBinding(
                    controlAfterBindingChange,
                    binding.Generation);
                validation = controlStatus2 == ControlBindingStatus.Stale
                    ? BindingValidation.Stale
                    : BindingValidation.Invalid;
                return StoreStatus.Success;
            }

            ulong observedHash = slot.KeyHash;
            ReadOnlySpan<byte> storedKey = default;
            bool validKey = expectedHash is null || TryGetKey(ref slot, out storedKey);
            bool equal = false;
            if (expectedHash is not null
                && observedHash == expectedHash.Value
                && validKey)
            {
                StoreStatus equalityStatus = KeysEqual(
                    storedKey,
                    expectedKey,
                    budget,
                    out equal);
                if (equalityStatus != StoreStatus.Success)
                {
                    return equalityStatus;
                }
            }

            var control2 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            if (control1 == control2 && slot.DirectoryBinding == raw)
            {
                if (expectedHash is null)
                {
                    validation = BindingValidation.CurrentOther;
                    return StoreStatus.Success;
                }

                if (observedHash == expectedHash.Value && !validKey)
                {
                    validation = BindingValidation.Invalid;
                    return StoreStatus.Success;
                }

                validation = equal ? BindingValidation.Exact : BindingValidation.CurrentOther;
                return StoreStatus.Success;
            }

            // State changes such as Initializing -> Reserved -> Published do
            // not make an exact-generation directory cell stale. Retry the
            // snapshot; only a generation/binding change permits cell cleanup.
            ControlBindingStatus revalidation = ClassifyControlBinding(
                control2,
                binding.Generation);
            if (revalidation != ControlBindingStatus.Current
                || slot.DirectoryBinding != raw)
            {
                validation = revalidation == ControlBindingStatus.Stale
                    ? BindingValidation.Stale
                    : BindingValidation.Invalid;
                return StoreStatus.Success;
            }
        }

        // Persistent same-generation movement is current contention, never
        // evidence that a helper may erase the cell.
        validation = BindingValidation.CurrentOther;
        return StoreStatus.Success;
    }

    /// <summary>
    /// Confirms that a would-be corrupt binding is still named by the exact
    /// directory/summary word that supplied it. A sampled word may be removed
    /// or replaced while its old slot is concurrently reclaimed; that obsolete
    /// sample is contention, not evidence that the current store is corrupt.
    /// The second exact-word read makes fail-closed classification depend on a
    /// freshly validated reference rather than the original scan observation.
    /// </summary>
    private StoreStatus RevalidateInvalidBindingReference<TCheckpoint>(
        ref long reference,
        ulong expectedReference,
        ulong binding,
        ulong? expectedHash,
        ReadOnlySpan<byte> expectedKey,
        in LockFreeOperationBudget budget,
        ref TCheckpoint checkpoint,
        out BindingValidation validation,
        out bool referenceRemainsExact)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        validation = BindingValidation.Invalid;
        referenceRemainsExact = false;
        if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference)) != expectedReference)
        {
            return StoreStatus.Success;
        }

        LockFreeCheckpoint.Reach(
            ref checkpoint,
            LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation);

        StoreStatus status = ValidateBinding(
            binding,
            expectedHash,
            expectedKey,
            budget,
            out validation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        referenceRemainsExact = unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference))
            == expectedReference;
        return StoreStatus.Success;
    }

    private static StoreStatus KeysEqual(
        ReadOnlySpan<byte> storedKey,
        ReadOnlySpan<byte> expectedKey,
        in LockFreeOperationBudget budget,
        out bool equal)
    {
        equal = false;
        if (storedKey.Length != expectedKey.Length)
        {
            return StoreStatus.Success;
        }

        const int ComparisonChunkBytes = 64;
        var chunkCount = 0;
        for (var offset = 0; offset < storedKey.Length; offset += ComparisonChunkBytes)
        {
            StoreStatus bound = budget.CheckPeriodic(chunkCount);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            int length = Math.Min(ComparisonChunkBytes, storedKey.Length - offset);
            if (!storedKey.Slice(offset, length).SequenceEqual(expectedKey.Slice(offset, length)))
            {
                return StoreStatus.Success;
            }

            chunkCount++;
        }

        StoreStatus completionStatus = budget.CheckPeriodic(chunkCount);
        if (completionStatus != StoreStatus.Success)
        {
            return completionStatus;
        }

        equal = true;
        return StoreStatus.Success;
    }

    private bool ControlMatchesBinding(ulong control, long generation) =>
        ClassifyControlBinding(control, generation) == ControlBindingStatus.Current;

    private ControlBindingStatus ClassifyControlBinding(
        ulong control,
        long expectedGeneration)
    {
        long observedGeneration = (long)((control >> 3) & SlotGenerationMask);
        ulong participant = (control >> 36) & SlotParticipantMask;
        int state = (int)(control & 0x7UL);
        bool structurallyValid = state switch
        {
            LayoutV2Constants.SlotFree => participant == 0,
            SlotInitializing or SlotReserved => ParticipantToken.IsStructurallyValid(
                participant,
                _layout.ParticipantRecordCount),
            SlotPublished or SlotRemoveRequested or SlotAborting or SlotReclaiming =>
                participant == 0,
            SlotRetired => participant == 0
                && observedGeneration == LockFreeSlotTable.TerminalGeneration,
            _ => false,
        };
        if (!structurallyValid || observedGeneration is < 1 or > LockFreeSlotTable.TerminalGeneration)
        {
            return ControlBindingStatus.Invalid;
        }

        if (observedGeneration > expectedGeneration)
        {
            return ControlBindingStatus.Stale;
        }

        if (observedGeneration < expectedGeneration)
        {
            return ControlBindingStatus.Invalid;
        }

        return state switch
        {
            LayoutV2Constants.SlotFree => ControlBindingStatus.Invalid,
            SlotRetired => ControlBindingStatus.Stale,
            _ => ControlBindingStatus.Current,
        };
    }

    private static bool IsInsertCancellationState(int state) =>
        state is SlotAborting or SlotReclaiming;

    private StoreStatus ObserveInsertOutcomeBeforeBudget<TCheckpoint>(
        ulong bindingRaw,
        long expectedGeneration,
        ref ValueSlotMetadataV2 slot,
        ref TCheckpoint checkpoint,
        out bool hasOutcome,
        out DirectoryLocation location)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        hasOutcome = false;
        location = default;

        // This observation deliberately precedes the operation-wide budget
        // check. A helper may have crossed the insert's ordering point while
        // this caller was descheduled, and an expired caller must not rewrite
        // that already-ordered outcome as StoreBusy/OperationCanceled.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ulong control1 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            ControlBindingStatus controlStatus = ClassifyControlBinding(
                control1,
                expectedGeneration);
            if (controlStatus != ControlBindingStatus.Current)
            {
                hasOutcome = true;
                return controlStatus == ControlBindingStatus.Stale
                    ? StoreStatus.InvalidReservation
                    : CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            ulong observedBinding = slot.DirectoryBinding;
            ulong operationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            ulong locationRaw = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryLocation));
            ulong control2 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            ulong operationRaw2 = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            ulong locationRaw2 = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryLocation));

            if (control1 != control2
                || operationRaw != operationRaw2
                || locationRaw != locationRaw2)
            {
                continue;
            }

            if (slot.DirectoryBinding != observedBinding)
            {
                continue;
            }

            if (observedBinding != bindingRaw)
            {
                hasOutcome = true;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            int state = (int)(control2 & 0x7UL);
            bool operationDecoded = TryDecodeOperation(operationRaw, out DirectoryOperation operation);
            if (IsInsertCancellationState(state))
            {
                // Complete plus its exact location proves that Reserved was
                // published before cancellation. Every other valid canceling
                // insert/unlink observation is still pre-order and therefore
                // an invalid reservation, not corruption.
                if (operationDecoded
                    && operation.Intent == IntentInsert
                    && operation.Generation == expectedGeneration
                    && (operation.Phase is PhaseBindingChanged or PhaseComplete))
                {
                    hasOutcome = true;
                    if (locationRaw == 0)
                    {
                        // CancelInsert clears the exact location before it can
                        // replace BindingChanged/Complete with Rejected. This
                        // stable zero is a legal pre-order cleanup window.
                        return StoreStatus.InvalidReservation;
                    }

                    bool exactLocation = TryValidateInsertLocation(
                        operation,
                        locationRaw,
                        out location);
                    if (!exactLocation)
                    {
                        return CorruptFrom(nameof(LockFreeKeyDirectory));
                    }

                    return operation.Phase == PhaseComplete
                        ? StoreStatus.Success
                        : StoreStatus.InvalidReservation;
                }

                if (operationRaw == 0
                    || (operationDecoded
                        && operation.Generation == expectedGeneration
                        && operation.Intent is IntentInsert or IntentUnlink
                        && IsOperationTargetInBounds(operation)))
                {
                    hasOutcome = true;
                    return StoreStatus.InvalidReservation;
                }

                hasOutcome = true;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (!operationDecoded
                || operation.Intent != IntentInsert
                || operation.Generation != expectedGeneration)
            {
                hasOutcome = true;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (state == SlotInitializing)
            {
                if (operation.Phase == PhaseRejected)
                {
                    hasOutcome = true;
                    return StoreStatus.DuplicateKey;
                }

                if (operation.Phase is PhasePrepared or PhaseTargetSelected or PhaseBindingChanged)
                {
                    return StoreStatus.Success;
                }

                hasOutcome = true;
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            if (state == SlotReserved
                && operation.Phase is PhaseBindingChanged or PhaseComplete)
            {
                if (operation.Phase == PhaseComplete)
                {
                    LockFreeCheckpoint.Reach(
                        ref checkpoint,
                        LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead);
                }

                hasOutcome = true;
                return TryValidateInsertLocation(
                    operation,
                    locationRaw,
                    out location)
                    ? StoreStatus.Success
                    : CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            hasOutcome = true;
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        // A rapidly changing, still pre-order snapshot remains ordinary
        // contention. The caller's normal budget check decides that outcome.
        return StoreStatus.Success;
    }

    private bool TryValidateInsertLocation(
        DirectoryOperation operation,
        ulong locationRaw,
        out DirectoryLocation location)
    {
        location = default;
        if (!TryDecodeLocation(locationRaw, out DirectoryLocation decodedLocation)
            || decodedLocation.Kind != operation.Kind
            || decodedLocation.Index != operation.Index
            || decodedLocation.Generation != operation.Generation
            || !TryGetTargetCell(operation.Kind, operation.Index, out _))
        {
            return false;
        }

        location = decodedLocation;
        return true;
    }

    private bool IsCanceledInsertObservation(
        ulong bindingRaw,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        bool operationDecoded,
        DirectoryOperation operation)
    {
        CurrentSlotStatus slotStatus = TryReadCurrentSlotStatus(
            bindingRaw,
            ref slot,
            out int state,
            out _);
        if (!TryDecodeBinding(bindingRaw, out IndexBinding binding)
            || slotStatus != CurrentSlotStatus.Current
            || !IsInsertCancellationState(state))
        {
            return false;
        }

        if (operationRaw == 0)
        {
            return true;
        }

        if (!operationDecoded
            || operation.Generation != binding.Generation
            || operation.Intent is not (IntentInsert or IntentUnlink)
            || !IsOperationTargetInBounds(operation))
        {
            return false;
        }

        // Exact Insert/Complete is an ordered outcome, not a generic canceled
        // descriptor. Its location must be validated by the stable tuple path;
        // excluding it here prevents malformed/zero location metadata from
        // being normalized to InvalidReservation.
        return operation.Intent != IntentInsert || operation.Phase != PhaseComplete;
    }

    private bool IsOperationTargetInBounds(DirectoryOperation operation) =>
        operation.Phase is PhasePrepared or PhaseRejected
        || (operation.Intent == IntentUnlink
            && operation.Phase == PhaseComplete
            && operation.Kind == 0
            && operation.Index == 0)
        || TryGetTargetCell(operation.Kind, operation.Index, out _);

    private CurrentSlotStatus TryReadCurrentSlotStatus(
        ulong raw,
        ref ValueSlotMetadataV2 slot,
        out int state,
        out ulong keyHash)
    {
        state = SlotRetired;
        keyHash = 0;
        if (!TryDecodeBinding(raw, out var binding))
        {
            return CurrentSlotStatus.Invalid;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ulong control1 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            ControlBindingStatus controlStatus1 = ClassifyControlBinding(
                control1,
                binding.Generation);
            if (controlStatus1 != ControlBindingStatus.Current)
            {
                return controlStatus1 == ControlBindingStatus.Invalid
                    ? CurrentSlotStatus.Invalid
                    : CurrentSlotStatus.Stale;
            }

            ulong directoryBinding = slot.DirectoryBinding;
            ulong observedKeyHash = slot.KeyHash;
            ulong control2 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            if (control1 == control2 && directoryBinding == raw)
            {
                state = (int)(control2 & 0x7UL);
                keyHash = observedKeyHash;
                return CurrentSlotStatus.Current;
            }

            ControlBindingStatus controlStatus2 = ClassifyControlBinding(
                control2,
                binding.Generation);
            if (controlStatus2 != ControlBindingStatus.Current)
            {
                return controlStatus2 == ControlBindingStatus.Invalid
                    ? CurrentSlotStatus.Invalid
                    : CurrentSlotStatus.Stale;
            }

            if (slot.DirectoryBinding != raw)
            {
                return CurrentSlotStatus.Invalid;
            }
        }

        ulong control = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
        ControlBindingStatus controlStatus = ClassifyControlBinding(control, binding.Generation);
        if (controlStatus != ControlBindingStatus.Current)
        {
            return controlStatus == ControlBindingStatus.Invalid
                ? CurrentSlotStatus.Invalid
                : CurrentSlotStatus.Stale;
        }

        ulong finalBinding = slot.DirectoryBinding;
        ulong finalHash = slot.KeyHash;
        ulong confirmedControl = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
        ControlBindingStatus confirmedStatus = ClassifyControlBinding(
            confirmedControl,
            binding.Generation);
        if (confirmedStatus != ControlBindingStatus.Current)
        {
            return confirmedStatus == ControlBindingStatus.Invalid
                ? CurrentSlotStatus.Invalid
                : CurrentSlotStatus.Stale;
        }

        if (confirmedControl != control)
        {
            return CurrentSlotStatus.Retry;
        }

        if (finalBinding != raw)
        {
            return CurrentSlotStatus.Invalid;
        }

        state = (int)(control & 0x7UL);
        keyHash = finalHash;
        return CurrentSlotStatus.Current;
    }

    private MutationSnapshotStatus TryReadMutationSnapshot(
        ulong bindingRaw,
        ref ValueSlotMetadataV2 slot,
        out ulong keyHash,
        out ulong operationRaw,
        out DirectoryOperation operation,
        out int state,
        out SlotPublicationIntent publicationIntent)
    {
        keyHash = 0;
        operationRaw = 0;
        operation = default;
        state = SlotRetired;
        publicationIntent = SlotPublicationIntent.None;
        if (!TryDecodeBinding(bindingRaw, out var binding))
        {
            return MutationSnapshotStatus.Invalid;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            ulong control1 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            ControlBindingStatus controlStatus1 = ClassifyControlBinding(
                control1,
                binding.Generation);
            if (controlStatus1 != ControlBindingStatus.Current)
            {
                return controlStatus1 == ControlBindingStatus.Invalid
                    ? MutationSnapshotStatus.Invalid
                    : MutationSnapshotStatus.Stale;
            }

            ulong directoryBinding = slot.DirectoryBinding;
            ulong observedKeyHash = slot.KeyHash;
            int rawPublicationIntent = Volatile.Read(ref slot.PublicationIntent);
            ulong observedOperation = unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation));
            ulong control2 = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.Control));
            if (control1 != control2 || directoryBinding != bindingRaw)
            {
                ControlBindingStatus controlStatus2 = ClassifyControlBinding(
                    control2,
                    binding.Generation);
                if (controlStatus2 != ControlBindingStatus.Current
                    || slot.DirectoryBinding != bindingRaw)
                {
                    return controlStatus2 == ControlBindingStatus.Invalid
                        ? MutationSnapshotStatus.Invalid
                        : MutationSnapshotStatus.Stale;
                }

                continue;
            }

            state = (int)(control2 & 0x7UL);
            keyHash = observedKeyHash;
            operationRaw = observedOperation;
            publicationIntent = (SlotPublicationIntent)rawPublicationIntent;
            if (publicationIntent is not (
                    SlotPublicationIntent.ExplicitReservation
                    or SlotPublicationIntent.AtomicPublication))
            {
                publicationIntent = SlotPublicationIntent.None;
                return MutationSnapshotStatus.Invalid;
            }

            if (operationRaw == 0)
            {
                return MutationSnapshotStatus.Current;
            }

            return TryDecodeOperation(operationRaw, out operation)
                ? MutationSnapshotStatus.Current
                : MutationSnapshotStatus.Invalid;
        }

        return MutationSnapshotStatus.Retry;
    }

    private StoreStatus ClassifyCurrentOperation(
        ulong bindingRaw,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        out int state)
    {
        state = SlotRetired;
        if (!TryDecodeBinding(bindingRaw, out var binding)
            || operation.Generation != binding.Generation)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        if (unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref slot.DirectoryOperation)) != operationRaw)
        {
            return StoreStatus.NotFound;
        }

        CurrentSlotStatus slotStatus = TryReadCurrentSlotStatus(
            bindingRaw,
            ref slot,
            out state,
            out _);
        if (slotStatus != CurrentSlotStatus.Current)
        {
            if (slotStatus == CurrentSlotStatus.Invalid
                && unchecked((ulong)AtomicControlWord.LoadAcquire(
                    ref slot.DirectoryOperation)) == operationRaw)
            {
                return CorruptFrom(nameof(LockFreeKeyDirectory));
            }

            return slotStatus == CurrentSlotStatus.Retry
                ? StoreStatus.StoreBusy
                : StoreStatus.NotFound;
        }

        return unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation)) == operationRaw
            ? StoreStatus.Success
            : StoreStatus.NotFound;
    }

    private StoreStatus ClassifyCurrentCanonicalOperation(
        int canonicalBucketIndex,
        ulong bindingRaw,
        ref ValueSlotMetadataV2 slot,
        ulong operationRaw,
        DirectoryOperation operation,
        out int state)
    {
        state = SlotRetired;
        if (unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref BucketMutation(canonicalBucketIndex))) != bindingRaw)
        {
            return StoreStatus.NotFound;
        }

        StoreStatus operationStatus = ClassifyCurrentOperation(
            bindingRaw,
            ref slot,
            operationRaw,
            operation,
            out state);
        if (operationStatus != StoreStatus.Success)
        {
            return operationStatus;
        }

        return unchecked((ulong)AtomicControlWord.LoadAcquire(
                ref BucketMutation(canonicalBucketIndex))) == bindingRaw
            ? StoreStatus.Success
            : StoreStatus.NotFound;
    }

    private StoreStatus PublishReserved(ref ValueSlotMetadataV2 slot, ulong bindingRaw)
    {
        if (!TryDecodeBinding(bindingRaw, out var binding))
        {
            return StoreStatus.CorruptStore;
        }

        for (var attempt = 0; attempt < DefaultRetryBudget; attempt++)
        {
            long observedControl = AtomicControlWord.LoadAcquire(ref slot.Control);
            if (!LockFreeSlotTable.TryClassifyStructuralControl(
                    observedControl,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return StoreStatus.CorruptStore;
            }

            var observed = unchecked((ulong)observedControl);
            if (((observed >> 3) & SlotGenerationMask) != (ulong)binding.Generation)
            {
                return StoreStatus.CorruptStore;
            }

            var state = (int)(observed & 0x7);
            if (state == SlotReserved)
            {
                return StoreStatus.Success;
            }

            if (state != SlotInitializing)
            {
                return StoreStatus.CorruptStore;
            }

            var desired = (observed & ~0x7UL) | SlotReserved;
            long publicationObservation = AtomicControlWord.CompareExchange(
                ref slot.Control,
                unchecked((long)desired),
                unchecked((long)observed));
            if (!LockFreeSlotTable.TryClassifyStructuralControl(
                    publicationObservation,
                    _layout.ParticipantRecordCount,
                    out _))
            {
                return StoreStatus.CorruptStore;
            }

            if (unchecked((ulong)publicationObservation) == observed)
            {
                return StoreStatus.Success;
            }
        }

        return StoreStatus.StoreBusy;
    }

    private StoreStatus PrepareOperation(
        ref ValueSlotMetadataV2 slot,
        ulong binding,
        ulong prepared,
        int intent,
        in LockFreeOperationBudget budget)
    {
        if (!TryDecodeBinding(binding, out var decodedBinding)
            || !TryDecodeOperation(prepared, out var preparedOperation)
            || preparedOperation.Intent != intent
            || preparedOperation.Phase != PhasePrepared
            || preparedOperation.Generation != decodedBinding.Generation)
        {
            return CorruptFrom(nameof(LockFreeKeyDirectory));
        }

        for (var attempt = 0; ; attempt++)
        {
            StoreStatus bound = budget.CheckPeriodic(attempt);
            if (bound != StoreStatus.Success)
            {
                return bound;
            }

            CurrentSlotStatus slotStatus = TryReadCurrentSlotStatus(
                binding,
                ref slot,
                out int slotState,
                out _);
            if (slotStatus != CurrentSlotStatus.Current)
            {
                return CurrentSlotFailure(slotStatus, intent);
            }

            var observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation));
            if (observed == prepared)
            {
                slotStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                return slotStatus == CurrentSlotStatus.Current
                    ? StoreStatus.Success
                    : CurrentSlotFailure(slotStatus, intent);
            }

            if (observed == 0)
            {
                slotStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                if (slotStatus != CurrentSlotStatus.Current)
                {
                    return CurrentSlotFailure(slotStatus, intent);
                }

                ulong publicationObservation = unchecked((ulong)AtomicControlWord.CompareExchange(
                    ref slot.DirectoryOperation,
                    unchecked((long)prepared),
                    comparand: 0));
                StoreStatus publicationCas = ValidateOperationCasObservation(
                    ref slot.DirectoryOperation,
                    expected: 0,
                    desired: prepared,
                    publicationObservation);
                if (publicationCas != StoreStatus.Success)
                {
                    return publicationCas;
                }

                if (publicationObservation == 0)
                {
                    slotStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                    if (slotStatus == CurrentSlotStatus.Current)
                    {
                        return StoreStatus.Success;
                    }

                    if (slotStatus == CurrentSlotStatus.Invalid)
                    {
                        return CorruptFrom(nameof(LockFreeKeyDirectory));
                    }

                    // Ownership changed after publication. Withdraw only this
                    // exact generation-tagged descriptor; a newer operation
                    // cannot compare equal.
                    StoreStatus operationCleanup = TryClearOperationReference(
                        ref slot.DirectoryOperation,
                        prepared,
                        out _);
                    if (operationCleanup != StoreStatus.Success)
                    {
                        return operationCleanup;
                    }

                    return CurrentSlotFailure(slotStatus, intent);
                }

                continue;
            }

            if (!TryDecodeOperation(observed, out var operation))
            {
                slotStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                return slotStatus is CurrentSlotStatus.Current or CurrentSlotStatus.Invalid
                    ? CorruptFrom(nameof(LockFreeKeyDirectory))
                    : CurrentSlotFailure(slotStatus, intent);
            }

            if (operation.Generation != decodedBinding.Generation)
            {
                slotStatus = TryReadCurrentSlotStatus(binding, ref slot, out _, out _);
                bool stillCurrent = slotStatus == CurrentSlotStatus.Current;
                if (slotStatus == CurrentSlotStatus.Invalid)
                {
                    return CorruptFrom(nameof(LockFreeKeyDirectory));
                }

                if (operation.Generation > decodedBinding.Generation)
                {
                    // A future descriptor belongs to a reused slot. If the old
                    // binding somehow still appears current this is structural
                    // corruption; otherwise this helper simply lost ownership.
                    return stillCurrent
                        ? CorruptFrom(nameof(LockFreeKeyDirectory))
                        : intent == IntentInsert
                            ? StoreStatus.InvalidReservation
                            : StoreStatus.NotFound;
                }

                if (!stillCurrent)
                {
                    return intent == IntentInsert
                        ? StoreStatus.InvalidReservation
                        : StoreStatus.NotFound;
                }

                StoreStatus operationCleanup = TryClearOperationReference(
                    ref slot.DirectoryOperation,
                    observed,
                    out _);
                if (operationCleanup != StoreStatus.Success)
                {
                    return operationCleanup;
                }

                continue;
            }

            if (intent == IntentInsert)
            {
                if (operation.Intent == IntentInsert)
                {
                    return StoreStatus.Success;
                }

                return StoreStatus.InvalidReservation;
            }

            if (operation.Intent == IntentUnlink)
            {
                return StoreStatus.Success;
            }

            if (operation.Intent != IntentInsert
                || slotState is not (SlotAborting or SlotReclaiming))
            {
                return StoreStatus.StoreBusy;
            }

            ulong replacementObservation = unchecked((ulong)AtomicControlWord.CompareExchange(
                ref slot.DirectoryOperation,
                unchecked((long)prepared),
                unchecked((long)observed)));
            StoreStatus replacementCas = ValidateOperationCasObservation(
                ref slot.DirectoryOperation,
                observed,
                prepared,
                replacementObservation);
            if (replacementCas != StoreStatus.Success)
            {
                return replacementCas;
            }

            if (replacementObservation == observed)
            {
                return StoreStatus.Success;
            }

            if (attempt + 1 >= DefaultRetryBudget
                && !budget.TryContinueAfterContention(attempt, out StoreStatus terminal))
            {
                return terminal;
            }
        }
    }

    private StoreStatus CurrentSlotFailure(CurrentSlotStatus status, int intent) =>
        status switch
        {
            CurrentSlotStatus.Invalid => CorruptFrom(nameof(LockFreeKeyDirectory)),
            CurrentSlotStatus.Retry => StoreStatus.StoreBusy,
            _ => intent == IntentInsert
                ? StoreStatus.InvalidReservation
                : StoreStatus.NotFound,
        };

    private bool TryGetKey(ref ValueSlotMetadataV2 slot, out ReadOnlySpan<byte> key)
    {
        key = default;
        if (slot.KeyLength <= 0 || slot.KeyLength > _layout.MaxKeyBytes)
        {
            return false;
        }

        if (!TryDecodeBinding(slot.DirectoryBinding, out var binding) || binding.SlotIndex >= _layout.SlotCount)
        {
            return false;
        }

        var expectedOffset = _layout.KeyStorageOffset + ((long)binding.SlotIndex * _layout.KeyStride);
        if (slot.KeyOffset != expectedOffset
            || expectedOffset < _layout.KeyStorageOffset
            || expectedOffset + slot.KeyLength > _layout.KeyStorageOffset + _layout.KeyStorageLength)
        {
            return false;
        }

        key = new ReadOnlySpan<byte>(_region.Pointer + expectedOffset, slot.KeyLength);
        return true;
    }

    private bool TryGetTargetCell(int kind, long index, out CellReference cell)
    {
        if (kind == TargetPrimary && index >= 0 && index < _layout.PrimaryLaneCount)
        {
            cell = new CellReference(_region.Pointer + PrimaryCellOffset((int)index));
            return true;
        }

        if (kind == TargetOverflow && index >= 0 && index < _layout.SlotCount)
        {
            cell = new CellReference(_region.Pointer + OverflowCellOffset((int)index));
            return true;
        }

        cell = default;
        return false;
    }

    private void GetBuckets(ulong hash, out int first, out int second)
    {
        first = (int)(Mix(hash) & (uint)_bucketMask);
        second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & (uint)_bucketMask);
        if (second == first)
        {
            second = (first + 1) & _bucketMask;
        }
    }

    private int OverflowStart(ulong hash) => (int)(Mix(hash ^ 0xd6e8_feb8_6659_fd93UL) % (uint)_layout.SlotCount);

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private ref ValueSlotMetadataV2 Slot(int slotIndex) =>
        ref *(ValueSlotMetadataV2*)(_region.Pointer + _layout.SlotMetadataOffset + ((long)slotIndex * _layout.SlotMetadataStride));

    private ref long BucketSpillSummary(int bucketIndex) =>
        ref *(long*)(_region.Pointer + _layout.PrimaryDirectoryOffset + ((long)bucketIndex * _layout.PrimaryBucketStride));

    private ref long BucketMutation(int bucketIndex) =>
        ref *(long*)(_region.Pointer + _layout.PrimaryDirectoryOffset + ((long)bucketIndex * _layout.PrimaryBucketStride) + 8);

    private ref long PrimaryCell(int absoluteCellIndex) =>
        ref *(long*)(_region.Pointer + PrimaryCellOffset(absoluteCellIndex));

    private long PrimaryCellOffset(int absoluteCellIndex)
    {
        var bucket = absoluteCellIndex / LayoutV2Constants.PrimaryLanesPerBucket;
        var lane = absoluteCellIndex % LayoutV2Constants.PrimaryLanesPerBucket;
        return _layout.PrimaryDirectoryOffset
            + ((long)bucket * _layout.PrimaryBucketStride)
            + 16
            + (lane * sizeof(long));
    }

    private ref long OverflowCell(int index) =>
        ref *(long*)(_region.Pointer + OverflowCellOffset(index));

    private long OverflowCellOffset(int index) =>
        _layout.OverflowDirectoryOffset + ((long)index * _layout.OverflowStride);

    /// <summary>
    /// Reads a directory binding word and rejects only a malformed value that
    /// remains in the exact same location on confirmation. A concurrently
    /// replaced malformed sample is contention; a valid binding for an older
    /// slot generation remains ordinary stale directory state.
    /// </summary>
    private StoreStatus TryReadStructurallyValidBindingReference(
        ref long reference,
        out ulong observed)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            observed = unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference));
            if (observed == 0
                || (TryDecodeBinding(observed, out IndexBinding binding)
                    && binding.SlotIndex < _layout.SlotCount))
            {
                return StoreStatus.Success;
            }

            if (unchecked((ulong)AtomicControlWord.LoadAcquire(ref reference)) == observed)
            {
                return CorruptHere();
            }
        }

        observed = 0;
        return StoreStatus.StoreBusy;
    }

    private static bool TryDecodeBinding(ulong raw, out IndexBinding binding)
    {
        try
        {
            binding = IndexBinding.Decode(raw);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            binding = default;
            return false;
        }
        catch (OverflowException)
        {
            binding = default;
            return false;
        }
    }

    private bool TryDecodeSpillSummary(ulong raw, out SpillSummary summary)
    {
        try
        {
            summary = SpillSummary.Decode(raw);
            return summary.IsInitial || (uint)summary.SlotIndex < (uint)_layout.SlotCount;
        }
        catch (ArgumentOutOfRangeException)
        {
            summary = default;
            return false;
        }
        catch (OverflowException)
        {
            summary = default;
            return false;
        }
    }

    private static bool TryDecodeOperation(ulong raw, out DirectoryOperation operation)
    {
        try
        {
            operation = DirectoryOperation.Decode(raw);
            if (raw == 0
                || operation.Intent is not (IntentInsert or IntentUnlink)
                || operation.Phase is < PhasePrepared or > PhaseComplete)
            {
                return false;
            }

            if (operation.Phase == PhasePrepared)
            {
                return operation.Kind == 0 && operation.Index == 0;
            }

            if (operation.Phase == PhaseRejected)
            {
                return operation.Intent == IntentInsert
                    && operation.Kind == 0
                    && operation.Index == 0;
            }

            if (operation.Phase == PhaseComplete
                && operation.Intent == IntentUnlink
                && operation.Kind == 0)
            {
                return operation.Index == 0;
            }

            return operation.Kind is TargetPrimary or TargetOverflow;
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
    }

    private static bool TryDecodeLocation(ulong raw, out DirectoryLocation location)
    {
        try
        {
            location = DirectoryLocation.Decode(raw);
            return raw != 0 && location.Kind is TargetPrimary or TargetOverflow;
        }
        catch (ArgumentOutOfRangeException)
        {
            location = default;
            return false;
        }
        catch (OverflowException)
        {
            location = default;
            return false;
        }
    }

    private StoreStatus CorruptHere(
        [CallerMemberName] string member = "",
        [CallerLineNumber] int line = 0) =>
        LockFreeStoreControl.ReportCorruption(
            _storeControl,
            nameof(LockFreeKeyDirectory),
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

    private enum BindingValidation
    {
        Stale,
        CurrentOther,
        Exact,
        Invalid
    }

    private enum ControlBindingStatus
    {
        Current,
        Stale,
        Invalid,
    }

    private enum CurrentSlotStatus
    {
        Current,
        Stale,
        Retry,
        Invalid,
    }

    private enum MutationSnapshotStatus
    {
        Stale,
        Current,
        Retry,
        Invalid
    }

    private enum CleanupReferenceKind
    {
        Binding,
        Location,
        Operation,
    }

    private readonly unsafe struct CellReference
    {
        private readonly long* _pointer;

        internal CellReference(byte* pointer)
        {
            _pointer = (long*)pointer;
        }

        internal ref long Value => ref *_pointer;
    }
}
