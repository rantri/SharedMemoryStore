using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LinearizabilityTests;

internal sealed record LinearizabilityCheckResult(
    bool IsLinearizable,
    IReadOnlyList<int> Linearization,
    string? Failure);

internal sealed class LinearizabilityChecker
{
    private readonly int _participantCapacity;
    private readonly int _valueCapacity;
    private readonly int _leaseCapacity;
    private readonly int[] _initialParticipants;

    public LinearizabilityChecker(
        int participantCapacity,
        int valueCapacity,
        IEnumerable<int>? initialParticipants = null,
        int? leaseCapacity = null)
    {
        _participantCapacity = participantCapacity;
        _valueCapacity = valueCapacity;
        _leaseCapacity = leaseCapacity ?? Math.Max(1, valueCapacity);
        _initialParticipants = initialParticipants?.ToArray() ?? [];
    }

    public LinearizabilityCheckResult Check(IReadOnlyList<RecordedOperation> history)
    {
        return CheckCore(
            history,
            physicalStoreFullOperations: null,
            physicalLeaseTableFullOperations: null);
    }

    public LinearizabilityCheckResult Check(
        IReadOnlyList<RecordedOperation> history,
        IReadOnlyList<RecordedSlotResourceWitness> resourceWitnesses)
    {
        ArgumentNullException.ThrowIfNull(resourceWitnesses);
        if (!TryValidateResourceWitnesses(
                history,
                resourceWitnesses,
                out HashSet<int>? physicalStoreFullOperations,
                out HashSet<int>? physicalLeaseTableFullOperations,
                out string? failure))
        {
            return Failure(failure!);
        }

        return CheckCore(
            history,
            physicalStoreFullOperations,
            physicalLeaseTableFullOperations);
    }

    private LinearizabilityCheckResult CheckCore(
        IReadOnlyList<RecordedOperation> history,
        IReadOnlySet<int>? physicalStoreFullOperations,
        IReadOnlySet<int>? physicalLeaseTableFullOperations)
    {
        if (history.Count > 63)
        {
            return Failure("The bounded checker supports at most 63 operations per history.");
        }

        if (history.Select(static operation => operation.Id).Distinct().Count() != history.Count)
        {
            return Failure("Every operation ID must be unique.");
        }

        if (history.Any(static operation => !operation.HasValidCallEnvelope))
        {
            return Failure("Every operation requires invocation < entry < return < response.");
        }

        if (!TryValidateOperationMetadata(history, out string? metadataFailure))
        {
            return Failure(metadataFailure!);
        }

        var predecessorMasks = BuildPredecessorMasks(history);
        var initial = new ReferenceStoreModel(
            _participantCapacity,
            _valueCapacity,
            _initialParticipants,
            _leaseCapacity);
        var allMask = history.Count == 0 ? 0UL : (1UL << history.Count) - 1;
        var order = new List<int>(history.Count);
        var rejectedStates = new HashSet<string>(StringComparer.Ordinal);
        if (Search(
                history,
                predecessorMasks,
                allMask,
                completedMask: 0,
                initial,
                order,
                rejectedStates,
                physicalStoreFullOperations,
                physicalLeaseTableFullOperations))
        {
            return new LinearizabilityCheckResult(true, order.ToArray(), null);
        }

        return Failure("No sequential execution both preserves real-time precedence and explains every result.");
    }

    /// <summary>
    /// Deterministically removes irrelevant operations from a rejected history
    /// while preserving at least one non-linearizable witness.
    /// </summary>
    public IReadOnlyList<RecordedOperation> MinimizeFailingHistory(
        IReadOnlyList<RecordedOperation> history)
    {
        if (Check(history).IsLinearizable)
        {
            throw new ArgumentException("Only a failing history can be minimized.", nameof(history));
        }

        var minimized = history.ToList();
        var index = 0;
        while (index < minimized.Count)
        {
            var candidate = minimized.Where((_, current) => current != index).ToArray();
            if (candidate.Length != 0 && !Check(candidate).IsLinearizable)
            {
                minimized = candidate.ToList();
                index = 0;
                continue;
            }

            index++;
        }

        return minimized;
    }

    public IReadOnlyList<RecordedOperation> MinimizeFailingHistory(
        IReadOnlyList<RecordedOperation> history,
        IReadOnlyList<RecordedSlotResourceWitness> resourceWitnesses)
    {
        if (Check(history, resourceWitnesses).IsLinearizable)
        {
            throw new ArgumentException("Only a failing history can be minimized.", nameof(history));
        }

        var minimized = history.ToList();
        var index = 0;
        while (index < minimized.Count)
        {
            var candidate = minimized.Where((_, current) => current != index).ToArray();
            if (candidate.Length != 0 && !Check(candidate, resourceWitnesses).IsLinearizable)
            {
                minimized = candidate.ToList();
                index = 0;
                continue;
            }

            index++;
        }

        return minimized;
    }

    private static bool Search(
        IReadOnlyList<RecordedOperation> history,
        ulong[] predecessorMasks,
        ulong allMask,
        ulong completedMask,
        ReferenceStoreModel model,
        List<int> order,
        HashSet<string> rejectedStates,
        IReadOnlySet<int>? physicalStoreFullOperations,
        IReadOnlySet<int>? physicalLeaseTableFullOperations)
    {
        if (completedMask == allMask)
        {
            return true;
        }

        var stateKey = completedMask.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ':'
            + model.Fingerprint();
        if (!rejectedStates.Add(stateKey))
        {
            return false;
        }

        for (var index = 0; index < history.Count; index++)
        {
            var bit = 1UL << index;
            if ((completedMask & bit) != 0
                || (predecessorMasks[index] & ~completedMask) != 0
                || !model.TryApply(
                    history[index].Command,
                    history[index].Result,
                    out var next,
                    physicalStoreFullOperations?.Contains(history[index].Id) == true,
                    physicalLeaseTableFullOperations?.Contains(history[index].Id) == true,
                    history[index].ObservedValue,
                    history[index].ObservedGeneration,
                    history[index].RequiresAcquireObservation))
            {
                continue;
            }

            order.Add(history[index].Id);
            if (Search(
                    history,
                    predecessorMasks,
                    allMask,
                    completedMask | bit,
                    next!,
                    order,
                    rejectedStates,
                    physicalStoreFullOperations,
                    physicalLeaseTableFullOperations))
            {
                return true;
            }

            order.RemoveAt(order.Count - 1);
        }

        return false;
    }

    private static bool TryValidateOperationMetadata(
        IReadOnlyList<RecordedOperation> history,
        out string? failure)
    {
        foreach (RecordedOperation operation in history)
        {
            if (operation.Result == ReferenceResultCode.Unexpected)
            {
                failure = $"Operation {operation.Id} returned an unexpected production result.";
                return false;
            }

            if (operation.UsesInfiniteWait
                && operation.Result == ReferenceResultCode.StoreBusy)
            {
                failure = $"Operation {operation.Id} returned StoreBusy from an infinite-wait production call.";
                return false;
            }

            bool isAcquire = operation.Command.Kind is ReferenceOperationKind.Acquire
                or ReferenceOperationKind.AcquireLease;
            bool carriesObservation = operation.ObservedValue is not null
                || operation.ObservedGeneration != 0;
            if (operation.RequiresAcquireObservation && !isAcquire)
            {
                failure = $"Operation {operation.Id} requires acquire output metadata but is not an acquire.";
                return false;
            }

            if (isAcquire && operation.Result == ReferenceResultCode.Success)
            {
                if (operation.RequiresAcquireObservation
                    && (operation.ObservedValue is null
                        || operation.ObservedGeneration is < 1
                            or > LockFreeSlotTable.TerminalGeneration))
                {
                    failure = $"Operation {operation.Id} is a successful acquire without exact returned bytes and generation.";
                    return false;
                }

                if (carriesObservation
                    && (operation.ObservedValue is null
                        || operation.ObservedGeneration is < 1
                            or > LockFreeSlotTable.TerminalGeneration))
                {
                    failure = $"Operation {operation.Id} carries a malformed acquire observation.";
                    return false;
                }
            }
            else if (carriesObservation)
            {
                failure = $"Operation {operation.Id} carries acquire output metadata for a non-successful acquire.";
                return false;
            }
        }

        failure = null;
        return true;
    }

    private bool TryValidateResourceWitnesses(
        IReadOnlyList<RecordedOperation> history,
        IReadOnlyList<RecordedSlotResourceWitness> resourceWitnesses,
        out HashSet<int>? physicalStoreFullOperations,
        out HashSet<int>? physicalLeaseTableFullOperations,
        out string? failure)
    {
        physicalStoreFullOperations = null;
        physicalLeaseTableFullOperations = null;
        failure = null;

        var allSequences = new HashSet<long>();
        foreach (RecordedOperation operation in history)
        {
            long[] envelope =
            [
                operation.InvocationSequence,
                operation.EntrySequence,
                operation.ReturnSequence,
                operation.ResponseSequence
            ];
            if (envelope.Any(static sequence => sequence <= 0)
                || envelope.Any(sequence => !allSequences.Add(sequence)))
            {
                failure = "Strict histories require positive, globally unique call and resource sequences.";
                return false;
            }
        }

        RecordedSlotResourceWitness[] ordered = resourceWitnesses
            .OrderBy(static witness => witness.Sequence)
            .ToArray();
        foreach (RecordedSlotResourceWitness witness in ordered)
        {
            if (witness.Sequence <= 0 || !allSequences.Add(witness.Sequence))
            {
                failure = "Strict histories require positive, globally unique call and resource sequences.";
                return false;
            }

            if (witness.Kind is RecordedSlotResourceKind.StoreFullProof
                or RecordedSlotResourceKind.LeaseTableFullProof)
            {
                if (witness.ConfirmationSequence <= witness.Sequence
                    || !allSequences.Add(witness.ConfirmationSequence))
                {
                    failure = "Strict capacity proofs require a unique confirmation sequence after their candidate sequence.";
                    return false;
                }
            }
            else if (witness.ConfirmationSequence != 0)
            {
                failure = "Only capacity-proof witnesses may carry a confirmation sequence.";
                return false;
            }
        }

        var states = new SlotResourceState[_valueCapacity];
        var storeFullProofs = new List<RecordedSlotResourceWitness>();
        var leaseTableFullProofs = new List<RecordedSlotResourceWitness>();
        foreach (RecordedSlotResourceWitness witness in ordered)
        {
            if (witness.Kind == RecordedSlotResourceKind.StoreFullProof)
            {
                if (witness.SlotIndex != -1 || witness.Generation != _valueCapacity)
                {
                    failure = $"Invalid StoreFull proof identity at sequence {witness.Sequence}.";
                    return false;
                }

                storeFullProofs.Add(witness);
                continue;
            }

            if (witness.Kind == RecordedSlotResourceKind.LeaseTableFullProof)
            {
                if (witness.SlotIndex != -1 || witness.Generation != _leaseCapacity)
                {
                    failure = $"Invalid LeaseTableFull proof identity at sequence {witness.Sequence}.";
                    return false;
                }

                leaseTableFullProofs.Add(witness);
                continue;
            }

            if ((uint)witness.SlotIndex >= (uint)_valueCapacity
                || witness.Generation is < 1 or > LockFreeSlotTable.TerminalGeneration)
            {
                failure = $"Invalid slot resource identity at sequence {witness.Sequence}.";
                return false;
            }

            ref SlotResourceState state = ref states[witness.SlotIndex];
            switch (witness.Kind)
            {
                case RecordedSlotResourceKind.Claim:
                    if (state.Kind != SlotResourceStateKind.Free)
                    {
                        failure = $"Slot {witness.SlotIndex} was claimed while already occupied or retired.";
                        return false;
                    }

                    state = new SlotResourceState(SlotResourceStateKind.Claimed, witness.Generation);
                    break;

                case RecordedSlotResourceKind.Free:
                    if (state.Kind != SlotResourceStateKind.Claimed
                        || state.Generation != witness.Generation)
                    {
                        failure = $"Slot {witness.SlotIndex} was freed without its exact generation claim.";
                        return false;
                    }

                    state = default;
                    break;

                case RecordedSlotResourceKind.Retire:
                    if (state.Kind != SlotResourceStateKind.Claimed
                        || state.Generation != witness.Generation
                        || witness.Generation != LockFreeSlotTable.TerminalGeneration)
                    {
                        failure = $"Slot {witness.SlotIndex} was retired without its exact generation claim.";
                        return false;
                    }

                    state = new SlotResourceState(SlotResourceStateKind.Retired, witness.Generation);
                    break;

                default:
                    failure = $"Unknown slot resource event at sequence {witness.Sequence}.";
                    return false;
            }
        }

        physicalStoreFullOperations = [];
        var unmatchedProofs = new List<RecordedSlotResourceWitness>(storeFullProofs);
        foreach (RecordedOperation operation in history
                     .Where(static operation => operation.Result == ReferenceResultCode.StoreFull)
                     .OrderBy(static operation => operation.ReturnSequence))
        {
            if (operation.Command.Kind is not (
                    ReferenceOperationKind.Publish or ReferenceOperationKind.Reserve))
            {
                failure = $"Operation {operation.Id} reports StoreFull for a non-allocating command.";
                return false;
            }

            int proofIndex = unmatchedProofs.FindIndex(proof =>
                proof.Sequence > operation.EntrySequence
                    && proof.ConfirmationSequence > proof.Sequence
                    && proof.ConfirmationSequence < operation.ReturnSequence);
            if (proofIndex < 0)
            {
                failure = $"Operation {operation.Id} reports StoreFull without its own exact double-collect proof between entry and return.";
                return false;
            }

            unmatchedProofs.RemoveAt(proofIndex);
            physicalStoreFullOperations.Add(operation.Id);
        }

        physicalLeaseTableFullOperations = [];
        var unmatchedLeaseProofs = new List<RecordedSlotResourceWitness>(
            leaseTableFullProofs);
        foreach (RecordedOperation operation in history
                     .Where(static operation =>
                         operation.Result == ReferenceResultCode.LeaseTableFull)
                     .OrderBy(static operation => operation.ReturnSequence))
        {
            if (operation.Command.Kind != ReferenceOperationKind.AcquireLease)
            {
                failure = $"Operation {operation.Id} reports LeaseTableFull for a non-acquiring command.";
                return false;
            }

            int proofIndex = unmatchedLeaseProofs.FindIndex(proof =>
                proof.Sequence > operation.EntrySequence
                    && proof.ConfirmationSequence > proof.Sequence
                    && proof.ConfirmationSequence < operation.ReturnSequence);
            if (proofIndex < 0)
            {
                failure = $"Operation {operation.Id} reports LeaseTableFull without its own exact double-collect proof between entry and return.";
                return false;
            }

            unmatchedLeaseProofs.RemoveAt(proofIndex);
            physicalLeaseTableFullOperations.Add(operation.Id);
        }

        return true;
    }

    private static ulong[] BuildPredecessorMasks(IReadOnlyList<RecordedOperation> history)
    {
        var masks = new ulong[history.Count];
        for (var current = 0; current < history.Count; current++)
        {
            for (var candidate = 0; candidate < history.Count; candidate++)
            {
                if (candidate != current && history[candidate].HappensBefore(history[current]))
                {
                    masks[current] |= 1UL << candidate;
                }
            }
        }

        return masks;
    }

    private static LinearizabilityCheckResult Failure(string failure) =>
        new(false, Array.Empty<int>(), failure);

    private enum SlotResourceStateKind
    {
        Free,
        Claimed,
        Retired
    }

    private readonly record struct SlotResourceState(
        SlotResourceStateKind Kind,
        long Generation);

}
