using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.LinearizabilityTests;

public sealed class CheckerSelfTests
{
    [Fact]
    public void RecorderKeepsInvocationEntryReturnAndResponseDistinctAndMonotonic()
    {
        var recorder = new MonotonicHistoryRecorder();
        var pending = recorder.Invoke(7, 3, ReferenceCommand.Publish(1, "key", "value"));

        pending.Enter();
        var operation = pending.Complete(ReferenceResultCode.Success);

        Assert.True(operation.HasValidCallEnvelope);
        Assert.Equal(7, operation.Id);
        Assert.Equal(3, operation.ActorId);
        Assert.Equal([operation], recorder.Snapshot());
    }

    [Fact]
    public void RecorderDistinguishesRealTimePrecedenceFromOverlap()
    {
        var recorder = new MonotonicHistoryRecorder();
        var first = recorder.Invoke(1, 1, ReferenceCommand.Publish(1, "a", "1"));
        first.Enter();
        var second = recorder.Invoke(2, 2, ReferenceCommand.Publish(1, "b", "2"));
        second.Enter();
        var secondCompleted = second.Complete(ReferenceResultCode.Success);
        var firstCompleted = first.Complete(ReferenceResultCode.Success);
        var third = recorder.Invoke(3, 3, ReferenceCommand.Publish(1, "c", "3"));
        third.Enter();
        var thirdCompleted = third.Complete(ReferenceResultCode.Success);

        Assert.True(firstCompleted.Overlaps(secondCompleted));
        Assert.True(secondCompleted.Overlaps(firstCompleted));
        Assert.True(firstCompleted.HappensBefore(thirdCompleted));
        Assert.True(secondCompleted.HappensBefore(thirdCompleted));
        Assert.False(thirdCompleted.Overlaps(firstCompleted));
    }

    [Fact]
    public void CheckerAcceptsOneWinnerForOverlappingSameKeyPublications()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "same", "left"), ReferenceResultCode.DuplicateKey, 1, 3, 8, 9),
            Operation(2, ReferenceCommand.Publish(1, "same", "right"), ReferenceResultCode.Success, 2, 4, 5, 6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([2, 1], result.Linearization);
    }

    [Fact]
    public void CheckerRejectsDuplicateWhenOverlappingExplicitReserveNeverOrders()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "owner"),
                ReferenceResultCode.StoreBusy,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.Reserve(1, 20, "same", "contender"),
                ReferenceResultCode.DuplicateKey,
                3,
                4,
                5,
                6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.False(result.IsLinearizable);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void CheckerAcceptsDuplicateOnlyWhenOverlappingExplicitReserveOrders()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "owner"),
                ReferenceResultCode.Success,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.Reserve(1, 20, "same", "contender"),
                ReferenceResultCode.DuplicateKey,
                3,
                4,
                5,
                6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2], result.Linearization);
    }

    [Fact]
    public void CheckerAcceptsRetryWhenOverlappingExplicitReserveNeverOrders()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "owner"),
                ReferenceResultCode.StoreBusy,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.Reserve(1, 20, "same", "contender"),
                ReferenceResultCode.StoreBusy,
                3,
                4,
                5,
                6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    public void CheckerRejectsDuplicateWhenOverlappingConveniencePublishNeverPublishes()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Publish(1, "same", "owner"),
                ReferenceResultCode.StoreBusy,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.Reserve(1, 20, "same", "contender"),
                ReferenceResultCode.DuplicateKey,
                3,
                4,
                5,
                6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.False(result.IsLinearizable);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void CheckerAcceptsDuplicateAgainstExplicitReservedKey()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "owner"),
                ReferenceResultCode.Success,
                1,
                2,
                3,
                4),
            Operation(
                2,
                ReferenceCommand.Publish(1, "same", "contender"),
                ReferenceResultCode.DuplicateKey,
                5,
                6,
                7,
                8)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2], result.Linearization);
    }

    [Fact]
    public void CheckerRejectsTwoSuccessfulCurrentGenerationsForOneKey()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "same", "left"), ReferenceResultCode.Success, 1, 3, 8, 9),
            Operation(2, ReferenceCommand.Publish(1, "same", "right"), ReferenceResultCode.Success, 2, 4, 5, 6)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.False(result.IsLinearizable);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void CheckerPreservesCompletedBeforeInvokedRealTimeOrder()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "same", "first"), ReferenceResultCode.DuplicateKey, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Publish(1, "same", "second"), ReferenceResultCode.Success, 5, 6, 7, 8)
        };

        var result = Checker(valueCapacity: 2).Check(history);

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    public void ModelTracksValueCapacityAndRestoresItAfterRemoval()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "a", "1"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Publish(1, "b", "2"), ReferenceResultCode.StoreFull, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.Remove(1, "a"), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(4, ReferenceCommand.Publish(1, "b", "2"), ReferenceResultCode.Success, 13, 14, 15, 16)
        };

        var result = Checker(valueCapacity: 1).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2, 3, 4], result.Linearization);
    }

    [Fact]
    public void ModelTracksParticipantCapacityCloseAndReuse()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.OpenParticipant(1), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.OpenParticipant(2), ReferenceResultCode.ParticipantTableFull, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.CloseParticipant(1), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(4, ReferenceCommand.OpenParticipant(2), ReferenceResultCode.Success, 13, 14, 15, 16),
            Operation(5, ReferenceCommand.Publish(1, "stale", "x"), ReferenceResultCode.ParticipantNotActive, 17, 18, 19, 20)
        };
        var checker = new LinearizabilityChecker(participantCapacity: 1, valueCapacity: 1);

        var result = checker.Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    public void InvalidReservationFromReserveWithoutRecoveryEvidenceIsRejected()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "tentative"),
                ReferenceResultCode.InvalidReservation,
                1,
                3,
                6,
                8),
            Operation(
                2,
                ReferenceCommand.Publish(1, "same", "winner"),
                ReferenceResultCode.Success,
                2,
                4,
                5,
                7)
        };

        var result = Checker(valueCapacity: 1).Check(history);

        Assert.False(result.IsLinearizable);
        Assert.NotNull(result.Failure);
    }

    [Fact]
    public void TentativeReservationInvalidatedBeforeOrderingCannotExplainDuplicateKey()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "same", "tentative"),
                ReferenceResultCode.InvalidReservation,
                1,
                3,
                6,
                8),
            Operation(
                2,
                ReferenceCommand.Publish(1, "same", "candidate"),
                ReferenceResultCode.DuplicateKey,
                2,
                4,
                5,
                7)
        };

        var result = Checker(valueCapacity: 1).Check(history);

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    public void RecoveryAfterReserveOrderingDoesNotRewriteSuccessfulReserve()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Reserve(1, 10, "key", "value"),
                ReferenceResultCode.Success,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.RecoverReservation(2, 10),
                ReferenceResultCode.Success,
                3,
                4,
                5,
                6),
            Operation(
                3,
                ReferenceCommand.CommitReservation(1, 10),
                ReferenceResultCode.InvalidReservation,
                9,
                10,
                11,
                12)
        };

        var result = new LinearizabilityChecker(
            participantCapacity: 2,
            valueCapacity: 1,
            initialParticipants: [1, 2]).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2, 3], result.Linearization);
    }

    [Fact]
    public void CheckerRejectsSuccessfulAcquireWithWrongReturnedBytes()
    {
        RecordedOperation[] history =
        [
            Operation(
                1,
                ReferenceCommand.Publish(1, "key", "61"),
                ReferenceResultCode.Success,
                1,
                2,
                3,
                4),
            Operation(
                2,
                ReferenceCommand.AcquireLease(1, 20, "key"),
                ReferenceResultCode.Success,
                5,
                6,
                7,
                8,
                observedValue: "62",
                observedGeneration: 7,
                requiresAcquireObservation: true)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history);

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    public void CheckerRejectsTwoGenerationsForTheSamePublishedValue()
    {
        RecordedOperation[] history =
        [
            Operation(1, ReferenceCommand.Publish(1, "key", "61"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(
                2,
                ReferenceCommand.AcquireLease(1, 20, "key"),
                ReferenceResultCode.Success,
                5,
                6,
                7,
                8,
                observedValue: "61",
                observedGeneration: 7,
                requiresAcquireObservation: true),
            Operation(3, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(
                4,
                ReferenceCommand.AcquireLease(1, 21, "key"),
                ReferenceResultCode.Success,
                13,
                14,
                15,
                16,
                observedValue: "61",
                observedGeneration: 8,
                requiresAcquireObservation: true)
        ];
        var checker = new LinearizabilityChecker(2, 1, [1], leaseCapacity: 1);

        LinearizabilityCheckResult result = checker.Check(history);

        Assert.False(result.IsLinearizable);
    }

    [Fact]
    public void RepublishedValueMayBindANewMappedGeneration()
    {
        RecordedOperation[] history =
        [
            Operation(1, ReferenceCommand.Publish(1, "key", "61"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(
                2,
                ReferenceCommand.AcquireLease(1, 20, "key"),
                ReferenceResultCode.Success,
                5,
                6,
                7,
                8,
                observedValue: "61",
                observedGeneration: 7,
                requiresAcquireObservation: true),
            Operation(3, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(4, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.Success, 13, 14, 15, 16),
            Operation(5, ReferenceCommand.Publish(1, "key", "62"), ReferenceResultCode.Success, 17, 18, 19, 20),
            Operation(
                6,
                ReferenceCommand.AcquireLease(1, 21, "key"),
                ReferenceResultCode.Success,
                21,
                22,
                23,
                24,
                observedValue: "62",
                observedGeneration: 8,
                requiresAcquireObservation: true)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    public void ProductionRaceRejectsInvocationOnlyOverlap()
    {
        RecordedOperation first = Operation(
            1,
            ReferenceCommand.Publish(1, "key", "61"),
            ReferenceResultCode.Success,
            invocation: 1,
            entry: 3,
            returned: 4,
            response: 9);
        RecordedOperation second = Operation(
            2,
            ReferenceCommand.Publish(1, "key", "62"),
            ReferenceResultCode.DuplicateKey,
            invocation: 2,
            entry: 5,
            returned: 6,
            response: 8);

        Assert.False(first.HappensBefore(second));
        Assert.False(second.HappensBefore(first));
        Assert.True(first.Overlaps(second));
        Assert.False(first.ImplementationOverlaps(second));
        Assert.Throws<Xunit.Sdk.XunitException>(() =>
            ProductionGeneratedHistoryTests.AssertProductionRaceOverlap(first, second));
    }

    [Fact]
    public void CheckerRejectsStoreBusyFromInfiniteWaitProductionCall()
    {
        RecordedOperation operation = Operation(
            1,
            ReferenceCommand.Publish(1, "key", "61"),
            ReferenceResultCode.StoreBusy,
            1,
            2,
            3,
            4,
            usesInfiniteWait: true);

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check([operation]);

        Assert.False(result.IsLinearizable);
        Assert.Contains("infinite-wait", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckerRejectsUnexpectedProductionResult()
    {
        RecordedOperation operation = Operation(
            1,
            ReferenceCommand.Publish(1, "key", "61"),
            ReferenceResultCode.Unexpected,
            1,
            2,
            3,
            4,
            usesInfiniteWait: true);

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check([operation]);

        Assert.False(result.IsLinearizable);
        Assert.Contains("unexpected production result", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckerRejectsMalformedCallEnvelopeBeforeSearching()
    {
        var malformed = Operation(
            1,
            ReferenceCommand.Publish(1, "key", "value"),
            ReferenceResultCode.Success,
            invocation: 2,
            entry: 1,
            returned: 3,
            response: 4);

        var result = Checker(valueCapacity: 1).Check([malformed]);

        Assert.False(result.IsLinearizable);
        Assert.Contains("invocation < entry", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void PhysicalClaimCanExplainStoreFullWithoutCreatingAbstractKeyOwnership()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Publish(1, "tentative", "value"),
                ReferenceResultCode.OperationCanceled,
                1,
                2,
                14,
                15),
            Operation(
                2,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.StoreFull,
                4,
                5,
                8,
                9),
            Operation(
                3,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.Success,
                16,
                17,
                19,
                20)
        };
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.Claim, 0, 1, 3),
            new(RecordedSlotResourceKind.StoreFullProof, -1, 1, 6, 7),
            new(RecordedSlotResourceKind.Free, 0, 1, 10),
            new(RecordedSlotResourceKind.Claim, 0, 2, 18)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history, resources);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2, 3], result.Linearization);
    }

    [Fact]
    public void StoreFullWitnessOutsideImplementationIntervalIsRejected()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Publish(1, "winner", "value"),
                ReferenceResultCode.Success,
                1,
                2,
                9,
                10),
            Operation(
                2,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.StoreFull,
                3,
                4,
                6,
                7)
        };
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.StoreFullProof, -1, 1, 11, 12)
        ];
        LinearizabilityChecker checker = Checker(valueCapacity: 1);

        Assert.True(checker.Check(history).IsLinearizable);
        LinearizabilityCheckResult result = checker.Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreFullCandidateInsideCallButConfirmationAfterReturnIsRejected()
    {
        RecordedOperation[] history =
        [
            Operation(
                1,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.StoreFull,
                1,
                2,
                5,
                8)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.StoreFullProof, -1, 1, 3, 6)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void DelayedClaimWitnessAloneCannotJustifyStoreFull()
    {
        RecordedOperation[] history =
        [
            Operation(
                1,
                ReferenceCommand.Publish(1, "tentative", "value"),
                ReferenceResultCode.OperationCanceled,
                1,
                2,
                9,
                10),
            Operation(
                2,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.StoreFull,
                3,
                4,
                7,
                8)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.Claim, 0, 1, 5)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void OneStoreFullProofCannotJustifyTwoOverlappingCalls()
    {
        RecordedOperation[] history =
        [
            Operation(
                1,
                ReferenceCommand.Publish(1, "first", "value"),
                ReferenceResultCode.StoreFull,
                1,
                2,
                7,
                8),
            Operation(
                2,
                ReferenceCommand.Publish(1, "second", "value"),
                ReferenceResultCode.StoreFull,
                3,
                4,
                9,
                10)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.StoreFullProof, -1, 1, 5, 6)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalRetireRemainsAFullPhysicalSlotWitness()
    {
        var history = new[]
        {
            Operation(
                1,
                ReferenceCommand.Publish(1, "contender", "value"),
                ReferenceResultCode.StoreFull,
                3,
                4,
                7,
                8)
        };
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.Claim, 0, LockFreeSlotTable.TerminalGeneration, 1),
            new(RecordedSlotResourceKind.Retire, 0, LockFreeSlotTable.TerminalGeneration, 2),
            new(RecordedSlotResourceKind.StoreFullProof, -1, 1, 5, 6)
        ];

        LinearizabilityCheckResult result = Checker(valueCapacity: 1).Check(history, resources);

        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    public void LeaseTableFullRequiresItsOwnExactProofInStrictHistory()
    {
        RecordedOperation[] history =
        [
            Operation(
                1,
                ReferenceCommand.Publish(1, "key", "value"),
                ReferenceResultCode.Success,
                1,
                2,
                3,
                4),
            Operation(
                2,
                ReferenceCommand.AcquireLease(1, 20, "key"),
                ReferenceResultCode.LeaseTableFull,
                5,
                6,
                9,
                10)
        ];
        var checker = new LinearizabilityChecker(
            participantCapacity: 2,
            valueCapacity: 1,
            initialParticipants: [1],
            leaseCapacity: 1);

        LinearizabilityCheckResult missing = checker.Check(history, []);
        LinearizabilityCheckResult witnessed = checker.Check(
            history,
            [new(RecordedSlotResourceKind.LeaseTableFullProof, -1, 1, 7, 8)]);

        Assert.False(missing.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", missing.Failure, StringComparison.Ordinal);
        Assert.True(witnessed.IsLinearizable, witnessed.Failure);
    }

    [Fact]
    public void LeaseTableFullProofOutsideImplementationIntervalIsRejected()
    {
        RecordedOperation[] history =
        [
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.LeaseTableFull, 5, 6, 9, 10)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.LeaseTableFullProof, -1, 1, 11, 12)
        ];
        var checker = new LinearizabilityChecker(2, 1, [1], leaseCapacity: 1);

        LinearizabilityCheckResult result = checker.Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void LeaseTableFullProofWithWrongConfiguredCapacityIsRejected()
    {
        RecordedOperation[] history =
        [
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.LeaseTableFull, 5, 6, 9, 10)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.LeaseTableFullProof, -1, 2, 7, 8)
        ];
        var checker = new LinearizabilityChecker(2, 1, [1], leaseCapacity: 1);

        LinearizabilityCheckResult result = checker.Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("Invalid LeaseTableFull proof identity", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void OneLeaseTableFullProofCannotJustifyTwoOverlappingCalls()
    {
        RecordedOperation[] history =
        [
            Operation(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.LeaseTableFull, 5, 6, 11, 12),
            Operation(3, ReferenceCommand.AcquireLease(1, 21, "key"), ReferenceResultCode.LeaseTableFull, 7, 8, 13, 14)
        ];
        RecordedSlotResourceWitness[] resources =
        [
            new(RecordedSlotResourceKind.LeaseTableFullProof, -1, 1, 9, 10)
        ];
        var checker = new LinearizabilityChecker(2, 1, [1], leaseCapacity: 1);

        LinearizabilityCheckResult result = checker.Check(history, resources);

        Assert.False(result.IsLinearizable);
        Assert.Contains("without its own exact double-collect proof", result.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public void CombinedReservationLeaseRemovalAndRecoveryLifecycleRestoresCapacity()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Reserve(1, 10, "key", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.CommitReservation(1, 10), ReferenceResultCode.Success, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(4, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.RemovePending, 13, 14, 15, 16),
            Operation(5, ReferenceCommand.RecoverLease(2, 20), ReferenceResultCode.Success, 17, 18, 19, 20),
            Operation(6, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.InvalidLease, 21, 22, 23, 24),
            Operation(7, ReferenceCommand.Publish(1, "next", "value"), ReferenceResultCode.Success, 25, 26, 27, 28)
        };

        var result = new LinearizabilityChecker(
            participantCapacity: 2,
            valueCapacity: 1,
            initialParticipants: [1, 2],
            leaseCapacity: 1).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], result.Linearization);
    }

    [Fact]
    public void DisposalAtomicallyInvalidatesOwnedReservationsAndLeasesInTheModel()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Reserve(1, 10, "reserved", "value"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Publish(1, "published", "value"), ReferenceResultCode.Success, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.AcquireLease(1, 20, "published"), ReferenceResultCode.Success, 9, 10, 11, 12),
            Operation(4, ReferenceCommand.Remove(2, "published"), ReferenceResultCode.RemovePending, 13, 14, 15, 16),
            Operation(5, ReferenceCommand.DisposeParticipant(1), ReferenceResultCode.Success, 17, 18, 19, 20),
            Operation(6, ReferenceCommand.CommitReservation(1, 10), ReferenceResultCode.ParticipantNotActive, 21, 22, 23, 24),
            Operation(7, ReferenceCommand.Publish(2, "replacement", "value"), ReferenceResultCode.Success, 25, 26, 27, 28)
        };

        var result = new LinearizabilityChecker(
            participantCapacity: 2,
            valueCapacity: 2,
            initialParticipants: [1, 2],
            leaseCapacity: 1).Check(history);

        Assert.True(result.IsLinearizable, result.Failure);
    }

    [Fact]
    public void FailingHistoryMinimizerIsDeterministicAndDropsIrrelevantOperations()
    {
        var history = new[]
        {
            Operation(1, ReferenceCommand.Publish(1, "irrelevant", "x"), ReferenceResultCode.Success, 1, 2, 3, 4),
            Operation(2, ReferenceCommand.Remove(1, "irrelevant"), ReferenceResultCode.Success, 5, 6, 7, 8),
            Operation(3, ReferenceCommand.Publish(1, "same", "left"), ReferenceResultCode.Success, 9, 11, 16, 17),
            Operation(4, ReferenceCommand.Publish(1, "same", "right"), ReferenceResultCode.Success, 10, 12, 13, 14)
        };
        LinearizabilityChecker checker = Checker(valueCapacity: 2);

        IReadOnlyList<RecordedOperation> first = checker.MinimizeFailingHistory(history);
        IReadOnlyList<RecordedOperation> second = checker.MinimizeFailingHistory(history);

        Assert.Equal(first, second);
        Assert.Equal([3, 4], first.Select(static operation => operation.Id));
        Assert.False(checker.Check(first).IsLinearizable);
    }

    private static LinearizabilityChecker Checker(int valueCapacity) =>
        new(participantCapacity: 2, valueCapacity, initialParticipants: [1]);

    private static RecordedOperation Operation(
        int id,
        ReferenceCommand command,
        ReferenceResultCode result,
        long invocation,
        long entry,
        long returned,
        long response,
        string? observedValue = null,
        long observedGeneration = 0,
        bool requiresAcquireObservation = false,
        bool usesInfiniteWait = false) =>
        new(
            id,
            id,
            command,
            result,
            invocation,
            entry,
            returned,
            response,
            observedValue,
            observedGeneration,
            requiresAcquireObservation,
            usesInfiniteWait);
}
