using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SharedMemoryStore.UnitTests;

/// <summary>
/// Configured SC-017 evidence over the real mapped directory protocol. The
/// ordinary test default executes every transition once; release qualification
/// raises the total through SMS_DIRECTORY_GENERATION_STRESS_REPETITIONS.
/// </summary>
public sealed class LockFreeDirectoryGenerationStressTests
{
    private const string RepetitionsVariable = "SMS_DIRECTORY_GENERATION_STRESS_REPETITIONS";
    private const string SeedVariable = "SMS_DIRECTORY_GENERATION_STRESS_SEED";
    private const ulong DefaultSeed = 0x5C01_700D_D1CE_7001UL;
    private const int QualificationTransitionCount = 50;
    private const int CanonicalBucket = 0;
    private const int PrimarySlotCount = 1;
    private const int OverflowSlotCount = 17;
    private const int OverflowAnchorCount = 16;

    private static readonly TransitionCase[] TransitionCases =
    [
        Operation("insert-primary-prepared", DelayedOperation.Insert, DirectoryTarget.Primary, 1, phase: 1),
        Operation("insert-primary-target-selected", DelayedOperation.Insert, DirectoryTarget.Primary, 2, phase: 2),
        Operation("insert-primary-binding-changed", DelayedOperation.Insert, DirectoryTarget.Primary, 3, phase: 3),
        Operation("insert-primary-complete", DelayedOperation.Insert, DirectoryTarget.Primary, 4, phase: 5),
        Operation("insert-overflow-prepared", DelayedOperation.Insert, DirectoryTarget.Overflow, 1, phase: 1),
        Operation("insert-overflow-target-selected", DelayedOperation.Insert, DirectoryTarget.Overflow, 2, phase: 2),
        Operation("insert-overflow-binding-changed", DelayedOperation.Insert, DirectoryTarget.Overflow, 3, phase: 3),
        Operation("insert-overflow-complete", DelayedOperation.Insert, DirectoryTarget.Overflow, 4, phase: 5),

        Operation("unlink-primary-prepared", DelayedOperation.Unlink, DirectoryTarget.Primary, 1, phase: 1),
        Operation("unlink-primary-target-selected", DelayedOperation.Unlink, DirectoryTarget.Primary, 2, phase: 2),
        Operation("unlink-primary-binding-changed", DelayedOperation.Unlink, DirectoryTarget.Primary, 3, phase: 3),
        Operation("unlink-primary-complete", DelayedOperation.Unlink, DirectoryTarget.Primary, 4, phase: 5),
        Operation("unlink-overflow-prepared", DelayedOperation.Unlink, DirectoryTarget.Overflow, 1, phase: 1),
        Operation("unlink-overflow-target-selected", DelayedOperation.Unlink, DirectoryTarget.Overflow, 2, phase: 2),
        Operation("unlink-overflow-binding-changed", DelayedOperation.Unlink, DirectoryTarget.Overflow, 3, phase: 3),
        Operation("unlink-overflow-complete", DelayedOperation.Unlink, DirectoryTarget.Overflow, 4, phase: 5),

        OperationRevalidation(
            "insert-primary-revalidated-prepared-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Primary,
            1,
            phase: 1),
        OperationRevalidation(
            "insert-primary-revalidated-target-selected-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Primary,
            2,
            phase: 2),
        OperationRevalidation(
            "insert-primary-revalidated-binding-changed-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Primary,
            3,
            phase: 3),
        OperationRevalidation(
            "insert-primary-revalidated-complete-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Primary,
            4,
            phase: 5),
        OperationRevalidation(
            "insert-overflow-revalidated-prepared-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Overflow,
            1,
            phase: 1),
        OperationRevalidation(
            "insert-overflow-revalidated-target-selected-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Overflow,
            2,
            phase: 2),
        OperationRevalidation(
            "insert-overflow-revalidated-binding-changed-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Overflow,
            3,
            phase: 3),
        OperationRevalidation(
            "insert-overflow-revalidated-complete-before-dispatch",
            DelayedOperation.Insert,
            DirectoryTarget.Overflow,
            4,
            phase: 5),

        OperationRevalidation(
            "unlink-primary-revalidated-prepared-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Primary,
            1,
            phase: 1),
        OperationRevalidation(
            "unlink-primary-revalidated-target-selected-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Primary,
            2,
            phase: 2),
        OperationRevalidation(
            "unlink-primary-revalidated-binding-changed-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Primary,
            3,
            phase: 3),
        OperationRevalidation(
            "unlink-primary-revalidated-complete-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Primary,
            4,
            phase: 5),
        OperationRevalidation(
            "unlink-overflow-revalidated-prepared-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Overflow,
            1,
            phase: 1),
        OperationRevalidation(
            "unlink-overflow-revalidated-target-selected-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Overflow,
            2,
            phase: 2),
        OperationRevalidation(
            "unlink-overflow-revalidated-binding-changed-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Overflow,
            3,
            phase: 3),
        OperationRevalidation(
            "unlink-overflow-revalidated-complete-before-dispatch",
            DelayedOperation.Unlink,
            DirectoryTarget.Overflow,
            4,
            phase: 5),

        InsertBindingChangedStateValidation(
            "insert-primary-binding-changed-state-validated-before-reserved",
            DirectoryTarget.Primary),
        InsertBindingChangedStateValidation(
            "insert-overflow-binding-changed-state-validated-before-reserved",
            DirectoryTarget.Overflow),

        Location("unlink-primary-prepared-location", DirectoryTarget.Primary, 1, phase: 1),
        Location("unlink-primary-target-location", DirectoryTarget.Primary, 2, phase: 2),
        Location("unlink-overflow-prepared-location", DirectoryTarget.Overflow, 1, phase: 1),
        Location("unlink-overflow-target-location", DirectoryTarget.Overflow, 2, phase: 2),

        UnlinkLocationRead(
            "unlink-primary-after-operation-validation-before-location-read",
            DirectoryTarget.Primary),
        UnlinkLocationRead(
            "unlink-overflow-after-operation-validation-before-location-read",
            DirectoryTarget.Overflow),
        LocationPublication(
            "insert-primary-after-binding-validation-before-location-publication",
            DirectoryTarget.Primary),
        LocationPublication(
            "insert-overflow-after-binding-validation-before-location-publication",
            DirectoryTarget.Overflow),
        LocationPublication(
            "insert-primary-after-empty-location-source-revalidation-before-publication-cas",
            DirectoryTarget.Primary,
            LockFreeCheckpointId.DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas),
        LocationPublication(
            "insert-overflow-after-empty-location-source-revalidation-before-publication-cas",
            DirectoryTarget.Overflow,
            LockFreeCheckpointId.DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas),
        LocationPublication(
            "insert-primary-after-location-publication-before-source-revalidation",
            DirectoryTarget.Primary,
            LockFreeCheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation),
        LocationPublication(
            "insert-overflow-after-location-publication-before-source-revalidation",
            DirectoryTarget.Overflow,
            LockFreeCheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation),

        Spill(
            "insert-overflow-before-present-cas",
            DelayedOperation.Insert,
            LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas,
            phase: 2),
        Spill(
            "insert-overflow-after-present-publication",
            DelayedOperation.Insert,
            LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication,
            phase: 2),
        Spill(
            "unlink-overflow-after-empty-scan",
            DelayedOperation.Unlink,
            LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan,
            phase: 5),
        Spill(
            "unlink-overflow-after-versioned-clear",
            DelayedOperation.Unlink,
            LockFreeCheckpointId.DirectoryAfterSpillSummaryClear,
            phase: 5)
    ];

    // SC-017 is specifically a stale directory-helper-after-validation test.
    // These are every Directory-family checkpoint at which such a helper can
    // still perform a generation-fenced side effect.
    private static readonly LockFreeCheckpointId[] Sc017MutationCheckpoints =
    [
        LockFreeCheckpointId.DirectoryAfterOperationValidation,
        LockFreeCheckpointId.DirectoryAfterLocationValidation,
        LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead,
        LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation,
        LockFreeCheckpointId.DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas,
        LockFreeCheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation,
        LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch,
        LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication,
        LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas,
        LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication,
        LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan,
        LockFreeCheckpointId.DirectoryAfterSpillSummaryClear
    ];

    // These catalog entries bracket the directory protocol but are not
    // directory-helper mutation windows. BeforeDescriptorPublication runs
    // before a canonical mutation/operation is visible, so another participant
    // has no exact descriptor to validate or complete. AfterDescriptorClear
    // runs only after TryInsert has observed completion and the canonical
    // mutation is zero, so no delayed directory side effect remains.
    // AfterInsertCompletionStateValidationBeforeLocationRead is likewise a
    // caller-side terminal classification window after helping is complete.
    // BeforeInsertOuterLoopBudgetCheck is a caller retry boundary before any
    // helper dispatch in that iteration.
    // AfterInvalidReferenceConfirmationBeforeBindingRevalidation is a
    // read-only correction window: the helper has not acquired mutation
    // ownership and can only revalidate or restart the lookup.
    // AfterUnlinkDescriptorClearBeforeGenerationAdvance follows the unlink
    // helper's final exact directory write; only slot generation advance
    // remains, so no delayed directory side effect can cross reuse.
    // AfterCancelLocationClearBeforeDescriptorRejection is a cancellation-only
    // observability seam. Its remaining exact descriptor/mutation release is
    // covered by the deterministic canceled-outcome and recovery tests rather
    // than the ordinary insert/unlink reuse matrix.
    // Their owner/recovery schedules are covered by reservation tests, not
    // SC-017.
    private static readonly LockFreeCheckpointId[] NonMutationDirectoryBrackets =
    [
        LockFreeCheckpointId.DirectoryBeforeDescriptorPublication,
        LockFreeCheckpointId.DirectoryAfterDescriptorClear,
        LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead,
        LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck,
        LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation,
        LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance,
        LockFreeCheckpointId.DirectoryAfterCancelLocationClearBeforeDescriptorRejection
    ];

    private static readonly byte[][] PrimaryKeys = GenerateBucketPairCollisions(count: 64, PrimarySlotCount);
    private static readonly byte[][] OverflowKeys = GenerateBucketPairCollisions(count: 64, OverflowSlotCount);

    private readonly ITestOutputHelper _output;

    public LockFreeDirectoryGenerationStressTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ConfiguredProductionStressFencesEveryDirectoryMutationTransitionAcrossSlotReuse()
    {
        ValidateTransitionMatrix();
        if (!IsSupportedLockFreeHost())
        {
            _output.WriteLine(
                $"SC017 skipped: OS={RuntimeInformation.OSDescription}; architecture={RuntimeInformation.ProcessArchitecture}.");
            return;
        }

        int configuredRepetitions = ReadRepetitions();
        ulong seed = ReadSeed();
        var evidence = new EvidenceCounters();
        _output.WriteLine(
            $"SC017 start: seed=0x{seed:X16}; configuredRepetitions={configuredRepetitions}; " +
            $"transitionCount={TransitionCases.Length}; distribution=quotient-plus-remainder.");
        _output.WriteLine(
            "SC017 catalog mapping: covered helper-mutation checkpoints=" +
            string.Join(",", Sc017MutationCheckpoints) +
            "; excluded non-mutation brackets=DirectoryBeforeDescriptorPublication" +
            " (no published canonical mutation),DirectoryAfterDescriptorClear (mutation already zero)," +
            "DirectoryAfterInsertCompletionStateValidationBeforeLocationRead" +
            " (terminal caller classification after helping)," +
            "DirectoryBeforeInsertOuterLoopBudgetCheck" +
            " (caller retry boundary before helper dispatch)," +
            "DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation" +
            " (read-only exact-reference correction window).");

        int quotient = Math.DivRem(configuredRepetitions, TransitionCases.Length, out int remainder);
        for (var transitionIndex = 0; transitionIndex < TransitionCases.Length; transitionIndex++)
        {
            int repetitions = quotient + (transitionIndex < remainder ? 1 : 0);
            ulong transitionSeed = seed ^ (0x9E37_79B9_7F4A_7C15UL * checked((ulong)(transitionIndex + 1)));
            RunTransition(
                TransitionCases[transitionIndex],
                transitionIndex,
                repetitions,
                transitionSeed,
                evidence);
            _output.WriteLine(
                $"SC017 transition={TransitionCases[transitionIndex].Name}; " +
                $"seed=0x{transitionSeed:X16}; repetitions={repetitions}; result=pass.");
        }

        Assert.Equal(configuredRepetitions, evidence.ExecutedRepetitions);
        Assert.Equal(0, evidence.WrongGenerationMutationCount);
        Assert.Equal(0, evidence.CorruptionCount);
        Assert.Equal(0, evidence.FalseMissCount);
        Assert.Equal(0, evidence.LeakedCapacityCount);
        _output.WriteLine(
            $"SC017 complete: seed=0x{seed:X16}; executedRepetitions={evidence.ExecutedRepetitions}; " +
            $"wrongGenerationMutations={evidence.WrongGenerationMutationCount}; " +
            $"corruption={evidence.CorruptionCount}; falseMisses={evidence.FalseMissCount}; " +
            $"leakedCapacity={evidence.LeakedCapacityCount}.");
    }

    [Fact]
    public void TransitionMatrixMapsEveryDirectoryCatalogEntryAndEveryMutationPhase()
    {
        ValidateTransitionMatrix();
    }

    private static void ValidateTransitionMatrix()
    {
        Assert.Equal(QualificationTransitionCount, TransitionCases.Length);

        LockFreeCheckpointId[] catalog = LockFreeCheckpointCatalog.Entries
            .Where(static entry => entry.Family == LockFreeCheckpointFamily.Directory)
            .Select(static entry => entry.Id)
            .Order()
            .ToArray();
        LockFreeCheckpointId[] classified = Sc017MutationCheckpoints
            .Concat(NonMutationDirectoryBrackets)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(catalog, classified);

        LockFreeCheckpointId[] covered = TransitionCases
            .Select(static transition => transition.Checkpoint)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(Sc017MutationCheckpoints.Order().ToArray(), covered);

        int[] operationPhases = [1, 2, 3, 5];
        foreach (LockFreeCheckpointId checkpoint in new[]
        {
            LockFreeCheckpointId.DirectoryAfterOperationValidation,
            LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch
        })
        {
            foreach (DelayedOperation operation in Enum.GetValues<DelayedOperation>())
            {
                foreach (DirectoryTarget target in Enum.GetValues<DirectoryTarget>())
                {
                    foreach (int phase in operationPhases)
                    {
                        Assert.Contains(
                            TransitionCases,
                            transition => transition.Checkpoint == checkpoint
                                && transition.Operation == operation
                                && transition.Target == target
                                && transition.ExpectedPhase == phase);
                    }
                }
            }
        }

        foreach (DirectoryTarget target in Enum.GetValues<DirectoryTarget>())
        {
            foreach (int phase in new[] { 1, 2 })
            {
                Assert.Contains(
                    TransitionCases,
                    transition => transition.Checkpoint == LockFreeCheckpointId.DirectoryAfterLocationValidation
                        && transition.Operation == DelayedOperation.Unlink
                        && transition.Target == target
                        && transition.ExpectedPhase == phase);
            }

            Assert.Contains(
                TransitionCases,
                transition => transition.Checkpoint
                        == LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead
                    && transition.Operation == DelayedOperation.Unlink
                    && transition.Target == target
                    && transition.ExpectedPhase == 1);
            Assert.Contains(
                TransitionCases,
                transition => transition.Checkpoint
                        == LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation
                    && transition.Operation == DelayedOperation.Insert
                    && transition.Target == target
                    && transition.ExpectedPhase == 2);
            Assert.Contains(
                TransitionCases,
                transition => transition.Checkpoint
                        == LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication
                    && transition.Operation == DelayedOperation.Insert
                    && transition.Target == target
                    && transition.ExpectedPhase == 3);
        }

        foreach (LockFreeCheckpointId spillCheckpoint in new[]
        {
            LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas,
            LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication,
            LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan,
            LockFreeCheckpointId.DirectoryAfterSpillSummaryClear
        })
        {
            Assert.Contains(
                TransitionCases,
                transition => transition.Checkpoint == spillCheckpoint
                    && transition.Target == DirectoryTarget.Overflow);
        }
    }

    private static void RunTransition(
        TransitionCase transition,
        int transitionIndex,
        int repetitions,
        ulong seed,
        EvidenceCounters evidence)
    {
        int slotCount = transition.Target == DirectoryTarget.Overflow
            ? OverflowSlotCount
            : PrimarySlotCount;
        byte[][] keys = transition.Target == DirectoryTarget.Overflow
            ? OverflowKeys
            : PrimaryKeys;
        string name = $"sms-sc017-{transitionIndex:D2}-{Guid.NewGuid():N}";
        using var controller = new CheckpointPauseController();
        using MemoryStore delayedStore = CreateInstrumentedStore(name, slotCount, controller);
        using MemoryStore helperStore = OpenHelperStore(name, slotCount);
        using MemoryStore reuseStore = OpenHelperStore(name, slotCount);
        using var delayedActor = new DelayedActor(delayedStore);
        LockFreeKeyDirectory directory = ReadDirectory(helperStore);
        LockFreeSlotTable slots = ReadSlots(helperStore);
        var random = new DeterministicRandom(seed);
        var helperValue = new byte[1];

        if (transition.Target == DirectoryTarget.Overflow)
        {
            for (var index = 0; index < OverflowAnchorCount; index++)
            {
                helperValue[0] = unchecked((byte)index);
                RequireExactStatus(
                    helperStore.TryPublish(keys[index], helperValue, default, StoreWaitOptions.Infinite),
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}: publish anchor {index}");
            }

            Require(
                directory.PrimaryOccupancy == OverflowAnchorCount && directory.OverflowOccupancy == 0,
                $"{transition.Name}: the collision anchors did not fill exactly the two primary buckets.");
        }

        int keyPoolStart = transition.Target == DirectoryTarget.Overflow
            ? OverflowAnchorCount
            : 0;
        int keyPoolLength = keys.Length - keyPoolStart;

        for (var repetition = 0; repetition < repetitions; repetition++)
        {
            int oldOffset = random.Next(keyPoolLength);
            int newOffset = random.Next(keyPoolLength - 1);
            if (newOffset >= oldOffset)
            {
                newOffset++;
            }

            byte[] oldKey = keys[keyPoolStart + oldOffset];
            byte[] newKey = keys[keyPoolStart + newOffset];
            byte oldMarker = unchecked((byte)random.NextUInt64());
            byte newMarker = unchecked((byte)random.NextUInt64());
            helperValue[0] = oldMarker;
            if (transition.Operation == DelayedOperation.Unlink)
            {
                RequireExactStatus(
                    helperStore.TryPublish(oldKey, helperValue, default, StoreWaitOptions.Infinite),
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}/{repetition}: publish unlink target");
            }

            controller.Arm(transition.Checkpoint, transition.CheckpointOccurrence);
            delayedActor.Start(transition.Operation, oldKey, oldMarker);
            bool resumed = false;
            try
            {
                Require(
                    controller.WaitUntilPaused(TimeSpan.FromSeconds(10)),
                    $"{transition.Name}/{repetition}: delayed actor did not reach " +
                    $"{transition.Checkpoint} occurrence {transition.CheckpointOccurrence}.");

                ulong pausedMutation = directory.ReadCanonicalMutation(CanonicalBucket);
                Require(pausedMutation != 0, $"{transition.Name}/{repetition}: canonical mutation is absent.");
                IndexBinding mutationBinding = IndexBinding.Decode(pausedMutation);
                int targetSlotIndex = mutationBinding.SlotIndex;
                SlotSnapshot paused = ReadSlotSnapshot(slots, targetSlotIndex);
                IndexBinding oldBinding = ValidatePausedTransition(transition, paused, targetSlotIndex);
                Require(
                    pausedMutation == paused.DirectoryBinding,
                    $"{transition.Name}/{repetition}: canonical mutation no longer names the validated generation.");
                ValidateSpillCheckpointState(transition, directory, paused.DirectoryBinding);

                RequireExactStatus(
                    directory.HelpMutation(
                        CanonicalBucket,
                        LockFreeOperationBudget.UnboundedScan,
                        maxSteps: 128),
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}/{repetition}: independent helper completion");

                if (transition.Operation == DelayedOperation.Insert)
                {
                    // Adversarial generation-fencing injection. The live
                    // delayed writer means this intentionally violates the
                    // administrative override's process-wide quiescence
                    // precondition. Assertions below cover only stale-helper
                    // isolation and mapped-state convergence, not a supported
                    // public result contract for the delayed call.
                    StoreStatus recoveryStatus = helperStore.TryRecoverReservations(
                        new ReservationRecoveryOptions(RecoverCurrentProcessReservations: true),
                        StoreWaitOptions.Infinite,
                        out ReservationRecoveryReport recovery);
                    RequireExactStatus(
                        recoveryStatus,
                        StoreStatus.Success,
                        evidence,
                        $"{transition.Name}/{repetition}: recover delayed reservation");
                    Require(
                        recovery.RecoveredReservationCount >= 1,
                        $"{transition.Name}/{repetition}: exact delayed reservation was not recovered.");
                }

                helperValue[0] = newMarker;
                // Keep completion/recovery and exact-slot reuse on distinct
                // participants.  This deterministically models the sustained
                // three-or-more-process helper churn that exposed the delayed
                // location-publication source-loss race.
                StoreStatus republish = reuseStore.TryPublish(
                    newKey,
                    helperValue,
                    default,
                    StoreWaitOptions.Infinite);
                if (republish == StoreStatus.StoreFull)
                {
                    evidence.LeakedCapacityCount++;
                }

                RequireExactStatus(
                    republish,
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}/{repetition}: publish later generation");

                DirectoryGenerationSnapshot beforeResume = ReadPublishedSnapshot(
                    directory,
                    slots,
                    newKey,
                    targetSlotIndex,
                    evidence,
                    $"{transition.Name}/{repetition}: before delayed resume");
                Require(
                    beforeResume.Binding.SlotIndex == oldBinding.SlotIndex
                    && beforeResume.Binding.Generation == oldBinding.Generation + 1,
                    $"{transition.Name}/{repetition}: helper did not reuse the exact slot at the immediately following generation.");

                controller.Continue();
                resumed = true;
                StoreStatus delayedStatus = delayedActor.WaitUntilComplete(TimeSpan.FromSeconds(10));
                ValidateDelayedStatus(transition, delayedStatus, evidence, repetition);

                DirectoryGenerationSnapshot afterResume = ReadPublishedSnapshot(
                    directory,
                    slots,
                    newKey,
                    targetSlotIndex,
                    evidence,
                    $"{transition.Name}/{repetition}: after delayed resume");
                if (beforeResume != afterResume)
                {
                    evidence.WrongGenerationMutationCount++;
                    throw new XunitException(
                        $"{transition.Name}/{repetition}: resumed helper changed the later generation. " +
                        $"before={beforeResume}; after={afterResume}.");
                }

                RequireMissing(
                    reuseStore,
                    oldKey,
                    evidence,
                    $"{transition.Name}/{repetition}: old generation remained visible");
                RequirePublishedValue(
                    reuseStore,
                    newKey,
                    newMarker,
                    evidence,
                    $"{transition.Name}/{repetition}: later generation false miss or byte mismatch");
                if (transition.Target == DirectoryTarget.Overflow)
                {
                    int anchorIndex = random.Next(OverflowAnchorCount);
                    RequirePublishedValue(
                        helperStore,
                        keys[anchorIndex],
                        unchecked((byte)anchorIndex),
                        evidence,
                        $"{transition.Name}/{repetition}: colliding anchor false miss");
                }

                RequireExactStatus(
                    reuseStore.TryRemove(newKey, StoreWaitOptions.Infinite),
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}/{repetition}: remove later generation");
                Require(
                    directory.ReadCanonicalMutation(CanonicalBucket) == 0,
                    $"{transition.Name}/{repetition}: canonical mutation leaked after cleanup.");
                if (transition.Target == DirectoryTarget.Overflow)
                {
                    SpillSummary summary = SpillSummary.Decode(
                        directory.ReadSpillSummary(CanonicalBucket));
                    Require(
                        !summary.IsPresent && directory.OverflowOccupancy == 0,
                        $"{transition.Name}/{repetition}: spill state leaked after cleanup.");
                }

                evidence.ExecutedRepetitions++;
            }
            finally
            {
                if (!resumed)
                {
                    controller.Continue();
                    _ = delayedActor.TryWaitUntilComplete(TimeSpan.FromSeconds(10));
                }
            }
        }

        VerifyAllCapacityRecoverable(helperStore, directory, keys, transition, helperValue, evidence);
    }

    private static void VerifyAllCapacityRecoverable(
        MemoryStore store,
        LockFreeKeyDirectory directory,
        byte[][] keys,
        TransitionCase transition,
        byte[] value,
        EvidenceCounters evidence)
    {
        if (transition.Target == DirectoryTarget.Overflow)
        {
            for (var index = 0; index < OverflowAnchorCount; index++)
            {
                RequireExactStatus(
                    store.TryRemove(keys[index], StoreWaitOptions.Infinite),
                    StoreStatus.Success,
                    evidence,
                    $"{transition.Name}: remove capacity anchor {index}");
            }
        }

        int slotCount = transition.Target == DirectoryTarget.Overflow
            ? OverflowSlotCount
            : PrimarySlotCount;
        for (var index = 0; index < slotCount; index++)
        {
            value[0] = unchecked((byte)(0x80 + index));
            StoreStatus publish = store.TryPublish(
                keys[index],
                value,
                default,
                StoreWaitOptions.Infinite);
            if (publish == StoreStatus.StoreFull)
            {
                evidence.LeakedCapacityCount++;
            }

            RequireExactStatus(
                publish,
                StoreStatus.Success,
                evidence,
                $"{transition.Name}: fill-to-capacity slot {index}");
        }

        RequireExactStatus(
            store.TryPublish(keys[slotCount], value, default, StoreWaitOptions.Infinite),
            StoreStatus.StoreFull,
            evidence,
            $"{transition.Name}: full-capacity sentinel");
        for (var index = 0; index < slotCount; index++)
        {
            RequirePublishedValue(
                store,
                keys[index],
                unchecked((byte)(0x80 + index)),
                evidence,
                $"{transition.Name}: fill-to-capacity visibility {index}");
            RequireExactStatus(
                store.TryRemove(keys[index], StoreWaitOptions.Infinite),
                StoreStatus.Success,
                evidence,
                $"{transition.Name}: drain capacity slot {index}");
        }

        Require(
            directory.PrimaryOccupancy == 0
            && directory.OverflowOccupancy == 0
            && directory.ReadCanonicalMutation(CanonicalBucket) == 0,
            $"{transition.Name}: directory capacity did not drain to zero.");
        if (transition.Target == DirectoryTarget.Overflow)
        {
            Require(
                !SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket)).IsPresent,
                $"{transition.Name}: spill summary remained Present after full drain.");
        }
    }

    private static IndexBinding ValidatePausedTransition(
        TransitionCase transition,
        SlotSnapshot snapshot,
        int expectedSlotIndex)
    {
        Require(snapshot.DirectoryBinding != 0, $"{transition.Name}: paused slot has no exact binding.");
        IndexBinding binding = IndexBinding.Decode(snapshot.DirectoryBinding);
        Require(binding.SlotIndex == expectedSlotIndex, $"{transition.Name}: paused the wrong value slot.");
        Require(
            binding.Generation == SlotGeneration(snapshot.Control),
            $"{transition.Name}: slot control and binding generations disagree.");
        Require(snapshot.DirectoryOperation != 0, $"{transition.Name}: paused slot has no directory operation.");
        DirectoryOperation operation = DirectoryOperation.Decode(
            unchecked((ulong)snapshot.DirectoryOperation));
        int expectedIntent = transition.Operation == DelayedOperation.Insert ? 1 : 2;
        Require(operation.Intent == expectedIntent, $"{transition.Name}: unexpected mutation intent.");
        Require(
            operation.Phase == transition.ExpectedPhase,
            $"{transition.Name}: expected mutation phase {transition.ExpectedPhase}, observed {operation.Phase}.");
        Require(operation.Generation == binding.Generation, $"{transition.Name}: operation generation mismatch.");
        if (transition.ExpectedPhase == 1)
        {
            Require(operation.Kind == 0, $"{transition.Name}: Prepared unexpectedly names a target.");
        }
        else
        {
            int expectedKind = transition.Target == DirectoryTarget.Overflow ? 2 : 1;
            Require(operation.Kind == expectedKind, $"{transition.Name}: mutation names the wrong target section.");
        }

        return binding;
    }

    private static void ValidateSpillCheckpointState(
        TransitionCase transition,
        LockFreeKeyDirectory directory,
        ulong oldBinding)
    {
        if (transition.Checkpoint == LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas)
        {
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Require(!summary.IsPresent, $"{transition.Name}: summary was already Present before its CAS.");
        }
        else if (transition.Checkpoint == LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication)
        {
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Require(
                summary.IsPresent && summary.Binding == oldBinding,
                $"{transition.Name}: Present summary does not identify the validated insertion.");
        }
        else if (transition.Checkpoint == LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan)
        {
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Require(
                summary.IsPresent && summary.Binding == oldBinding && directory.OverflowOccupancy == 0,
                $"{transition.Name}: empty-scan checkpoint did not retain the captured Present token.");
        }
        else if (transition.Checkpoint == LockFreeCheckpointId.DirectoryAfterSpillSummaryClear)
        {
            SpillSummary summary = SpillSummary.Decode(directory.ReadSpillSummary(CanonicalBucket));
            Require(
                !summary.IsPresent && summary.Binding == oldBinding && directory.OverflowOccupancy == 0,
                $"{transition.Name}: versioned clear did not preserve the exact old identity.");
        }
    }

    private static DirectoryGenerationSnapshot ReadPublishedSnapshot(
        LockFreeKeyDirectory directory,
        LockFreeSlotTable slots,
        byte[] key,
        int expectedSlotIndex,
        EvidenceCounters evidence,
        string context)
    {
        StoreStatus lookup = directory.TryLookup(
            key,
            StoreKey.Hash(key),
            LockFreeOperationBudget.UnboundedScan,
            out ulong rawBinding,
            out DirectoryLocation location);
        if (lookup == StoreStatus.CorruptStore)
        {
            evidence.CorruptionCount++;
        }
        else if (lookup == StoreStatus.NotFound)
        {
            evidence.FalseMissCount++;
        }

        RequireExactStatus(lookup, StoreStatus.Success, evidence, context);
        IndexBinding binding = IndexBinding.Decode(rawBinding);
        Require(binding.SlotIndex == expectedSlotIndex, $"{context}: lookup resolved a different slot.");
        SlotSnapshot slot = ReadSlotSnapshot(slots, expectedSlotIndex);
        Require(
            slot.DirectoryBinding == rawBinding
            && SlotGeneration(slot.Control) == binding.Generation
            && location.Generation == binding.Generation,
            $"{context}: later-generation binding/location/control are inconsistent.");
        return new DirectoryGenerationSnapshot(
            binding,
            location.Value,
            slot,
            directory.ReadSpillSummary(CanonicalBucket),
            directory.ReadCanonicalMutation(CanonicalBucket));
    }

    private static SlotSnapshot ReadSlotSnapshot(LockFreeSlotTable slots, int slotIndex)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            ref ValueSlotMetadataV2 slot = ref slots.Slot(slotIndex);
            long control1 = AtomicControlWord.LoadAcquire(ref slot.Control);
            var snapshot = new SlotSnapshot(
                control1,
                slot.DirectoryBinding,
                AtomicControlWord.LoadAcquire(ref slot.DirectoryLocation),
                AtomicControlWord.LoadAcquire(ref slot.DirectoryOperation),
                slot.KeyHash,
                slot.KeyLength,
                slot.DescriptorLength,
                slot.ValueLength,
                AtomicControlWord.LoadAcquire(ref slot.BytesAdvanced),
                slot.CommitSequence,
                slot.KeyOffset,
                slot.DescriptorOffset,
                slot.PayloadOffset);
            if (control1 == AtomicControlWord.LoadAcquire(ref slot.Control))
            {
                return snapshot;
            }
        }

        throw new XunitException($"Slot {slotIndex} did not stabilize for an exact generation snapshot.");
    }

    private static void ValidateDelayedStatus(
        TransitionCase transition,
        StoreStatus status,
        EvidenceCounters evidence,
        int repetition)
    {
        if (status == StoreStatus.CorruptStore)
        {
            evidence.CorruptionCount++;
        }

        bool allowed = transition.Operation == DelayedOperation.Insert
            ? status != StoreStatus.CorruptStore
            : status is StoreStatus.Success or StoreStatus.NotFound;
        Require(allowed, $"{transition.Name}/{repetition}: delayed actor returned {status}.");
    }

    private static void RequirePublishedValue(
        MemoryStore store,
        byte[] key,
        byte expected,
        EvidenceCounters evidence,
        string context)
    {
        StoreStatus acquire = store.TryAcquire(key, StoreWaitOptions.Infinite, out ValueLease lease);
        if (acquire == StoreStatus.NotFound)
        {
            evidence.FalseMissCount++;
        }
        else if (acquire == StoreStatus.CorruptStore)
        {
            evidence.CorruptionCount++;
        }

        RequireExactStatus(acquire, StoreStatus.Success, evidence, context);
        try
        {
            Require(lease.ValueSpan.Length == 1 && lease.ValueSpan[0] == expected, context);
        }
        finally
        {
            RequireExactStatus(lease.Release(), StoreStatus.Success, evidence, $"{context}: release");
        }
    }

    private static void RequireMissing(
        MemoryStore store,
        byte[] key,
        EvidenceCounters evidence,
        string context)
    {
        StoreStatus status = store.TryAcquire(key, StoreWaitOptions.Infinite, out ValueLease lease);
        if (status == StoreStatus.CorruptStore)
        {
            evidence.CorruptionCount++;
        }

        if (status == StoreStatus.Success)
        {
            _ = lease.Release();
        }

        RequireExactStatus(status, StoreStatus.NotFound, evidence, context);
    }

    private static void RequireExactStatus(
        StoreStatus actual,
        StoreStatus expected,
        EvidenceCounters evidence,
        string context)
    {
        if (actual == StoreStatus.CorruptStore)
        {
            evidence.CorruptionCount++;
        }

        Require(actual == expected, $"{context}: expected {expected}, observed {actual}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new XunitException(message);
        }
    }

    private static MemoryStore CreateInstrumentedStore(
        string name,
        int slotCount,
        CheckpointPauseController controller)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, slotCount, OpenMode.CreateNew),
            LockFreeCheckpointFactory.CreateInstrumented(controller.Observe),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore OpenHelperStore(string name, int slotCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, slotCount, OpenMode.OpenExisting),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name, int slotCount, OpenMode openMode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 1,
            maxDescriptorBytes: 0,
            maxKeyBytes: sizeof(long),
            leaseRecordCount: Math.Max(2, slotCount),
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static LockFreeKeyDirectory ReadDirectory(MemoryStore store)
    {
        object engine = ReadEngine(store);
        FieldInfo field = engine.GetType().GetField(
            "_directory",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new XunitException("Lock-free engine._directory is absent.");
        return Assert.IsType<LockFreeKeyDirectory>(field.GetValue(engine));
    }

    private static LockFreeSlotTable ReadSlots(MemoryStore store)
    {
        object engine = ReadEngine(store);
        FieldInfo field = engine.GetType().GetField(
            "_slots",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new XunitException("Lock-free engine._slots is absent.");
        return Assert.IsType<LockFreeSlotTable>(field.GetValue(engine));
    }

    private static object ReadEngine(MemoryStore store)
    {
        FieldInfo field = typeof(MemoryStore).GetField(
            "_engine",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new XunitException("MemoryStore._engine is absent.");
        return field.GetValue(store) ?? throw new XunitException("MemoryStore._engine is null.");
    }

    private static byte[][] GenerateBucketPairCollisions(int count, int slotCount)
    {
        var keys = new List<byte[]>(count);
        int primaryLaneCount = NextPowerOfTwo(Math.Max(32, checked(slotCount * 4)));
        uint bucketMask = checked((uint)((primaryLaneCount / LayoutV2Constants.PrimaryLanesPerBucket) - 1));
        for (long candidate = 1; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            ulong hash = StoreKey.Hash(key);
            int first = (int)(Mix(hash) & bucketMask);
            int second = (int)(Mix(hash ^ 0x9e37_79b9_7f4a_7c15UL) & bucketMask);
            if (second == first)
            {
                second = (first + 1) & (int)bucketMask;
            }

            if (first == CanonicalBucket && second == 1)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static int NextPowerOfTwo(int value)
    {
        var result = 1;
        while (result < value)
        {
            result <<= 1;
        }

        return result;
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58_476d_1ce4_e5b9UL;
        value ^= value >> 27;
        value *= 0x94d0_49bb_1331_11ebUL;
        return value ^ (value >> 31);
    }

    private static int ReadRepetitions()
    {
        string? configured = Environment.GetEnvironmentVariable(RepetitionsVariable);
        int repetitions = string.IsNullOrWhiteSpace(configured)
            ? TransitionCases.Length
            : int.Parse(configured, NumberStyles.None, CultureInfo.InvariantCulture);
        if (repetitions < TransitionCases.Length)
        {
            throw new XunitException(
                $"{RepetitionsVariable} must be at least {TransitionCases.Length} so every transition executes.");
        }

        return repetitions;
    }

    private static ulong ReadSeed()
    {
        string? configured = Environment.GetEnvironmentVariable(SeedVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultSeed;
        }

        return configured.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(configured.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : ulong.Parse(configured, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static long SlotGeneration(long control) =>
        (long)((unchecked((ulong)control) >> 3) & 0x1_ffff_ffffUL);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static TransitionCase Operation(
        string name,
        DelayedOperation operation,
        DirectoryTarget target,
        int occurrence,
        int phase) =>
        new(
            name,
            operation,
            target,
            LockFreeCheckpointId.DirectoryAfterOperationValidation,
            occurrence,
            phase);

    private static TransitionCase Location(
        string name,
        DirectoryTarget target,
        int occurrence,
        int phase) =>
        new(
            name,
            DelayedOperation.Unlink,
            target,
            LockFreeCheckpointId.DirectoryAfterLocationValidation,
            occurrence,
            phase);

    private static TransitionCase OperationRevalidation(
        string name,
        DelayedOperation operation,
        DirectoryTarget target,
        int occurrence,
        int phase) =>
        new(
            name,
            operation,
            target,
            LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch,
            occurrence,
            phase);

    private static TransitionCase InsertBindingChangedStateValidation(
        string name,
        DirectoryTarget target) =>
        new(
            name,
            DelayedOperation.Insert,
            target,
            LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication,
            CheckpointOccurrence: 1,
            ExpectedPhase: 3);

    private static TransitionCase UnlinkLocationRead(
        string name,
        DirectoryTarget target) =>
        new(
            name,
            DelayedOperation.Unlink,
            target,
            LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead,
            CheckpointOccurrence: 1,
            ExpectedPhase: 1);

    private static TransitionCase LocationPublication(
        string name,
        DirectoryTarget target,
        LockFreeCheckpointId checkpoint =
            LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation) =>
        new(
            name,
            DelayedOperation.Insert,
            target,
            checkpoint,
            CheckpointOccurrence: 1,
            ExpectedPhase: 2);

    private static TransitionCase Spill(
        string name,
        DelayedOperation operation,
        LockFreeCheckpointId checkpoint,
        int phase) =>
        new(name, operation, DirectoryTarget.Overflow, checkpoint, 1, phase);

    private sealed class EvidenceCounters
    {
        internal long ExecutedRepetitions;
        internal long WrongGenerationMutationCount;
        internal long CorruptionCount;
        internal long FalseMissCount;
        internal long LeakedCapacityCount;
    }

    private sealed class CheckpointPauseController : IDisposable
    {
        private readonly object _sync = new();
        private readonly ManualResetEventSlim _paused = new(initialState: false);
        private readonly ManualResetEventSlim _resume = new(initialState: true);
        private LockFreeCheckpointId _target;
        private int _targetOccurrence;
        private int _observedOccurrences;
        private bool _armed;
        private bool _disposed;

        internal void Arm(LockFreeCheckpointId checkpoint, int occurrence)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_armed)
                {
                    throw new InvalidOperationException("A checkpoint pause is already armed.");
                }

                _target = checkpoint;
                _targetOccurrence = occurrence;
                _observedOccurrences = 0;
                _paused.Reset();
                _resume.Reset();
                _armed = true;
            }
        }

        internal void Observe(LockFreeCheckpointEntry entry)
        {
            bool pause = false;
            lock (_sync)
            {
                if (!_disposed && _armed && entry.Id == _target)
                {
                    _observedOccurrences++;
                    if (_observedOccurrences == _targetOccurrence)
                    {
                        _armed = false;
                        pause = true;
                    }
                }
            }

            if (pause)
            {
                _paused.Set();
                _resume.Wait();
            }
        }

        internal bool WaitUntilPaused(TimeSpan timeout) => _paused.Wait(timeout);

        internal void Continue() => _resume.Set();

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _armed = false;
                _resume.Set();
                _paused.Set();
            }

            _paused.Dispose();
            _resume.Dispose();
        }
    }

    private sealed class DelayedActor : IDisposable
    {
        private readonly MemoryStore _store;
        private readonly ManualResetEventSlim _start = new(initialState: false);
        private readonly ManualResetEventSlim _complete = new(initialState: true);
        private readonly Thread _thread;
        private readonly byte[] _value = new byte[1];
        private DelayedOperation _operation;
        private byte[] _key = [];
        private byte _marker;
        private StoreStatus _status;
        private Exception? _exception;
        private bool _stop;
        private bool _disposed;

        internal DelayedActor(MemoryStore store)
        {
            _store = store;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharedMemoryStore.SC017.DelayedActor"
            };
            _thread.Start();
        }

        internal void Start(DelayedOperation operation, byte[] key, byte marker)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_complete.IsSet)
            {
                throw new InvalidOperationException("The delayed actor is already running.");
            }

            _operation = operation;
            _key = key;
            _marker = marker;
            _exception = null;
            _complete.Reset();
            _start.Set();
        }

        internal StoreStatus WaitUntilComplete(TimeSpan timeout)
        {
            if (!_complete.Wait(timeout))
            {
                throw new XunitException("The delayed SC-017 actor did not complete within the test bound.");
            }

            if (_exception is not null)
            {
                throw new XunitException($"The delayed SC-017 actor failed: {_exception}");
            }

            return _status;
        }

        internal bool TryWaitUntilComplete(TimeSpan timeout) => _complete.Wait(timeout);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop = true;
            _start.Set();
            if (!_thread.Join(TimeSpan.FromSeconds(10)))
            {
                throw new XunitException("The delayed SC-017 actor thread did not stop.");
            }

            _start.Dispose();
            _complete.Dispose();
        }

        private void Run()
        {
            while (true)
            {
                _start.Wait();
                _start.Reset();
                if (_stop)
                {
                    return;
                }

                try
                {
                    _value[0] = _marker;
                    _status = _operation == DelayedOperation.Insert
                        ? _store.TryPublish(_key, _value, default, StoreWaitOptions.Infinite)
                        : _store.TryRemove(_key, StoreWaitOptions.Infinite);
                }
                catch (Exception exception)
                {
                    _exception = exception;
                }
                finally
                {
                    _complete.Set();
                }
            }
        }
    }

    private struct DeterministicRandom
    {
        private ulong _state;

        internal DeterministicRandom(ulong seed)
        {
            _state = seed == 0 ? 0xD1B5_4A32_D192_ED03UL : seed;
        }

        internal int Next(int exclusiveMaximum) =>
            checked((int)(NextUInt64() % checked((uint)exclusiveMaximum)));

        internal ulong NextUInt64()
        {
            ulong value = _state;
            value ^= value >> 12;
            value ^= value << 25;
            value ^= value >> 27;
            _state = value;
            return value * 0x2545_F491_4F6C_DD1DUL;
        }
    }

    private readonly record struct TransitionCase(
        string Name,
        DelayedOperation Operation,
        DirectoryTarget Target,
        LockFreeCheckpointId Checkpoint,
        int CheckpointOccurrence,
        int ExpectedPhase);

    private readonly record struct SlotSnapshot(
        long Control,
        ulong DirectoryBinding,
        long DirectoryLocation,
        long DirectoryOperation,
        ulong KeyHash,
        int KeyLength,
        int DescriptorLength,
        int ValueLength,
        long BytesAdvanced,
        long CommitSequence,
        long KeyOffset,
        long DescriptorOffset,
        long PayloadOffset);

    private readonly record struct DirectoryGenerationSnapshot(
        IndexBinding Binding,
        ulong Location,
        SlotSnapshot Slot,
        ulong SpillSummary,
        ulong CanonicalMutation);

    private enum DelayedOperation
    {
        Insert,
        Unlink
    }

    private enum DirectoryTarget
    {
        Primary,
        Overflow
    }
}
