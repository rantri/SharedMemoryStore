namespace SharedMemoryStore.LinearizabilityTests;

public sealed class LockFreeHistoryTests
{
    private const int DefaultSeed = 0x5eed_0200;
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public LockFreeHistoryTests(Xunit.Abstractions.ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> RaceFamilies => new()
    {
        "publish-publish",
        "publish-reserve",
        "reserve-reserve",
        "commit-acquire",
        "acquire-remove",
        "release-reclaim",
        "recovery-live-action",
        "disposal-operation",
        "participant-capacity",
        "value-capacity",
        "lease-capacity",
        "cancellation",
        "stale-token"
    };

    [Theory]
    [MemberData(nameof(RaceFamilies))]
    [Trait("Category", "Linearizability")]
    public void SeededRaceFamilyHasAReferenceLinearization(string family)
    {
        HistoryCase testCase = Create(family);
        LinearizabilityCheckResult result = testCase.Checker.Check(testCase.History);

        Assert.True(result.IsLinearizable, $"{family}: {result.Failure}");
        Assert.Equal(testCase.History.Count, result.Linearization.Count);
    }

    [Fact]
    [Trait("Category", "LinearizabilityRandomized")]
    public void ConfiguredSeedTierChecksEveryRaceFamilyAndRecordsReproducibleSeed()
    {
        int repetitions = ConfiguredRepetitions();
        int seed = ConfiguredSeed();
        var random = new Random(seed);
        string[] families =
        [
            "publish-publish",
            "publish-reserve",
            "reserve-reserve",
            "commit-acquire",
            "acquire-remove",
            "release-reclaim",
            "recovery-live-action",
            "disposal-operation",
            "participant-capacity",
            "value-capacity",
            "lease-capacity",
            "cancellation",
            "stale-token"
        ];

        for (var familyIndex = 0; familyIndex < families.Length; familyIndex++)
        {
            HistoryCase testCase = Create(families[familyIndex]);
            int[] order = new int[testCase.History.Count];
            var checkedHistories = 0;
            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                // Input order is deliberately randomized. Real-time edges live
                // in the envelopes, so the checker must not depend on array order.
                for (var index = 0; index < order.Length; index++)
                {
                    order[index] = index;
                }

                for (var index = order.Length - 1; index > 0; index--)
                {
                    int swap = random.Next(index + 1);
                    (order[index], order[swap]) = (order[swap], order[index]);
                }

                var shuffled = new RecordedOperation[order.Length];
                for (var index = 0; index < order.Length; index++)
                {
                    shuffled[index] = testCase.History[order[index]];
                }

                LinearizabilityCheckResult result = testCase.Checker.Check(shuffled);
                Assert.True(
                    result.IsLinearizable,
                    $"family={families[familyIndex]}, repetition={repetition}, seed={seed}: {result.Failure}");
                checkedHistories++;
            }

            Assert.Equal(repetitions, checkedHistories);
            _output.WriteLine(
                $"family={families[familyIndex]} seed={seed} " +
                $"completedCheckerInvocations={checkedHistories} source=reference-model");
        }
    }

    private static HistoryCase Create(string family) => family switch
    {
        "publish-publish" => Case(
            valueCapacity: 2,
            operations:
            [
                Op(1, ReferenceCommand.Publish(1, "key", "left"), ReferenceResultCode.Success, 1, 3, 8, 9),
                Op(2, ReferenceCommand.Publish(1, "key", "right"), ReferenceResultCode.DuplicateKey, 2, 4, 5, 6)
            ]),
        "publish-reserve" => Case(
            valueCapacity: 2,
            operations:
            [
                Op(1, ReferenceCommand.Publish(1, "key", "left"), ReferenceResultCode.Success, 1, 3, 8, 9),
                Op(2, ReferenceCommand.Reserve(1, 10, "key", "right"), ReferenceResultCode.DuplicateKey, 2, 4, 5, 6)
            ]),
        "reserve-reserve" => Case(
            valueCapacity: 2,
            operations:
            [
                Op(1, ReferenceCommand.Reserve(1, 10, "key", "left"), ReferenceResultCode.Success, 1, 3, 8, 9),
                Op(2, ReferenceCommand.Reserve(1, 11, "key", "right"), ReferenceResultCode.DuplicateKey, 2, 4, 5, 6)
            ]),
        "commit-acquire" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(60, ReferenceCommand.Reserve(1, 10, "key", "value"), ReferenceResultCode.Success, -8),
                Op(1, ReferenceCommand.CommitReservation(1, 10), ReferenceResultCode.Success, 1, 3, 5, 8),
                Op(2, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "acquire-remove" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(60, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, -4),
                Op(1, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, 1, 3, 5, 8),
                Op(2, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.RemovePending, 2, 4, 6, 7)
            ]),
        "release-reclaim" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(60, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, -12),
                Setup(61, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, -8),
                Setup(62, ReferenceCommand.Remove(1, "key"), ReferenceResultCode.RemovePending, -4),
                Op(1, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.Success, 1, 3, 5, 8),
                Op(2, ReferenceCommand.Publish(1, "next", "value"), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "recovery-live-action" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(59, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, -8),
                Setup(60, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, -4),
                Op(1, ReferenceCommand.RecoverLease(1, 20), ReferenceResultCode.InvalidLease, 1, 3, 5, 8),
                Op(2, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "disposal-operation" => Case(
            valueCapacity: 1,
            operations:
            [
                Op(1, ReferenceCommand.DisposeParticipant(1), ReferenceResultCode.Success, 1, 3, 5, 8),
                Op(2, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.ParticipantNotActive, 2, 4, 6, 7)
            ]),
        "participant-capacity" => Case(
            participantCapacity: 1,
            valueCapacity: 1,
            operations:
            [
                Op(1, ReferenceCommand.OpenParticipant(2), ReferenceResultCode.ParticipantTableFull, 1, 3, 5, 8),
                Op(2, ReferenceCommand.CloseParticipant(1), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "value-capacity" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(60, ReferenceCommand.Publish(1, "first", "value"), ReferenceResultCode.Success, -4),
                Op(1, ReferenceCommand.Publish(1, "second", "value"), ReferenceResultCode.StoreFull, 1, 3, 5, 8),
                Op(2, ReferenceCommand.Remove(1, "first"), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "lease-capacity" => Case(
            valueCapacity: 2,
            leaseCapacity: 1,
            operations:
            [
                Setup(58, ReferenceCommand.Publish(1, "first", "value"), ReferenceResultCode.Success, -12),
                Setup(59, ReferenceCommand.Publish(1, "second", "value"), ReferenceResultCode.Success, -8),
                Setup(60, ReferenceCommand.AcquireLease(1, 20, "first"), ReferenceResultCode.Success, -4),
                Op(1, ReferenceCommand.AcquireLease(1, 21, "second"), ReferenceResultCode.LeaseTableFull, 1, 3, 5, 8),
                Op(2, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "cancellation" => Case(
            valueCapacity: 1,
            operations:
            [
                Op(1, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.OperationCanceled, 1, 3, 5, 8),
                Op(2, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, 2, 4, 6, 7)
            ]),
        "stale-token" => Case(
            valueCapacity: 1,
            operations:
            [
                Setup(59, ReferenceCommand.Publish(1, "key", "value"), ReferenceResultCode.Success, -8),
                Setup(60, ReferenceCommand.AcquireLease(1, 20, "key"), ReferenceResultCode.Success, -4),
                Op(1, ReferenceCommand.RecoverLease(2, 20), ReferenceResultCode.Success, 1, 3, 5, 8),
                Op(2, ReferenceCommand.ReleaseLease(1, 20), ReferenceResultCode.InvalidLease, 2, 4, 6, 7)
            ]),
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, null)
    };

    private static HistoryCase Case(
        int valueCapacity,
        IReadOnlyList<RecordedOperation> operations,
        int participantCapacity = 2,
        int? leaseCapacity = null) =>
        new(
            new LinearizabilityChecker(
                participantCapacity,
                valueCapacity,
                initialParticipants: participantCapacity == 1 ? [1] : [1, 2],
                leaseCapacity),
            operations);

    private static RecordedOperation Setup(
        int id,
        ReferenceCommand command,
        ReferenceResultCode result,
        long invocation) =>
        Op(id, command, result, invocation, invocation + 1, invocation + 2, invocation + 3);

    private static RecordedOperation Op(
        int id,
        ReferenceCommand command,
        ReferenceResultCode result,
        long invocation,
        long entry,
        long returned,
        long response) =>
        new(id, id, command, result, invocation, entry, returned, response);

    private static int ConfiguredRepetitions()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_CHECKER_HISTORY_REPETITIONS");
        return int.TryParse(configured, out int value) && value > 0 ? value : 256;
    }

    private static int ConfiguredSeed()
    {
        string? configured = Environment.GetEnvironmentVariable("SMS_LINEARIZABILITY_SEED");
        return int.TryParse(configured, out int value) ? value : DefaultSeed;
    }

    private sealed record HistoryCase(
        LinearizabilityChecker Checker,
        IReadOnlyList<RecordedOperation> History);
}
