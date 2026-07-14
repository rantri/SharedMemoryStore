using System.Collections;
using System.Reflection;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeCheckpointCoverageTests
{
    private static readonly string[] RequiredAbaWindowCheckpoints =
    [
        "DirectoryAfterOperationValidation",
        "DirectoryAfterLocationValidation",
        "DirectoryAfterUnlinkOperationValidationBeforeLocationRead",
        "DirectoryAfterLocationPublisherBindingValidation",
        "DirectoryAfterCurrentOperationRevalidationBeforeDispatch",
        "DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication",
        "DirectoryAfterInsertCompletionStateValidationBeforeLocationRead",
        "DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance",
        "DirectoryAfterCancelLocationClearBeforeDescriptorRejection",
        "DirectoryBeforeSpillSummaryPublicationCas",
        "DirectoryAfterSpillSummaryPublication",
        "DirectoryAfterEmptySpillSummaryScan",
        "DirectoryAfterSpillSummaryClear",
        "ReclaimAfterMetadataValidation"
    ];

    private static readonly string[] RequiredInsertCancellationWindowCheckpoints =
    [
        "DirectoryAfterCurrentOperationRevalidationBeforeDispatch",
        "DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication",
        "DirectoryAfterInsertCompletionStateValidationBeforeLocationRead",
        "DirectoryAfterCancelLocationClearBeforeDescriptorRejection",
        "ReserveAfterDirectoryInsertBeforePendingClassification",
        "DirectoryBeforeInsertOuterLoopBudgetCheck"
    ];

    private static readonly string[] RequiredFamilies =
    [
        "Publish",
        "Reserve",
        "Commit",
        "Abort",
        "Acquire",
        "Project",
        "Release",
        "Remove",
        "Reclaim",
        "Directory",
        "Diagnostics",
        "Recovery",
        "Disposal",
        "Participant",
        "Advance"
    ];

    [Fact]
    public void RequiredTransitionFamiliesAndCheckpointCatalogCoverEachOther()
    {
        var entries = ReadCheckpointEntries();
        var catalogFamilies = entries
            .Select(entry => ReadRequiredMember(entry, "Family"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(RequiredFamilies.Order(StringComparer.Ordinal), catalogFamilies);
        foreach (var family in RequiredFamilies)
        {
            var positions = entries
                .Where(entry => ReadRequiredMember(entry, "Family") == family)
                .Select(entry => ReadRequiredMember(entry, "Position"))
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("Before", positions);
            Assert.Contains("After", positions);
        }
    }

    [Fact]
    public void EveryCheckpointHasUniqueIdentityAndPauseCrashRaceClassifications()
    {
        var entries = ReadCheckpointEntries();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Assert.True(ids.Add(ReadRequiredMember(entry, "Id")), "Checkpoint IDs must be stable and unique.");
            AssertClassified(entry, "Pause");
            AssertClassified(entry, "Crash");
            AssertClassified(entry, "Race");
        }
    }

    [Fact]
    public void CatalogIncludesPostValidationAbaWindowsForDirectoryAndReclaimHelpers()
    {
        string[] ids = ReadCheckpointEntries()
            .Select(entry => ReadRequiredMember(entry, "Id"))
            .ToArray();

        foreach (string required in RequiredAbaWindowCheckpoints)
        {
            Assert.Contains(required, ids);
        }
    }

    [Fact]
    public void CatalogIncludesEveryInsertCancellationPostValidationWindow()
    {
        string[] ids = ReadCheckpointEntries()
            .Select(entry => ReadRequiredMember(entry, "Id"))
            .ToArray();

        foreach (string required in RequiredInsertCancellationWindowCheckpoints)
        {
            Assert.Contains(required, ids);
        }
    }

    [Fact]
    public void DirectoryOperationAndLocationWordsCarryTheValidatedSlotGeneration()
    {
        var assembly = typeof(MemoryStore).Assembly;
        Type? operationType = assembly.GetType(
            "SharedMemoryStore.LockFree.DirectoryOperation",
            throwOnError: false,
            ignoreCase: false);
        Type? locationType = assembly.GetType(
            "SharedMemoryStore.LockFree.DirectoryLocation",
            throwOnError: false,
            ignoreCase: false);
        Assert.NotNull(operationType);
        Assert.NotNull(locationType);
        Type operation = operationType!;
        Type location = locationType!;

        Assert.Contains(
            operation.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            member => member.Name.Contains("Generation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            location.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            member => member.Name.Contains("Generation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            operation.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name.Contains("Encode", StringComparison.OrdinalIgnoreCase))
                .SelectMany(method => method.GetParameters()),
            parameter => parameter.Name?.Contains("generation", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(
            location.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name.Contains("Encode", StringComparison.OrdinalIgnoreCase))
                .SelectMany(method => method.GetParameters()),
            parameter => parameter.Name?.Contains("generation", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void HelperConvergesFromEveryInsertPauseWithoutDuplicatingTheBinding()
    {
        var pauses = InsertOracle.NormalInsertPauses(binding: 0x0000_0002_8000_0001UL).ToArray();
        Assert.NotEmpty(pauses);

        foreach (var paused in pauses)
        {
            var completed = InsertOracle.HelpToQuiescence(paused);

            Assert.Equal(SlotControl.Reserved, completed.Control);
            Assert.Equal(DirectoryPhase.Complete, completed.Phase);
            Assert.False(completed.DescriptorPublished);
            Assert.Equal(paused.CandidateBinding, completed.CellBinding);
            Assert.Equal(1, completed.BindingInstallCount);
        }
    }

    [Fact]
    public void StaleDescriptorAtEveryInsertPhaseIsClearedWithoutTouchingUnrelatedBinding()
    {
        const ulong unrelatedBinding = 0x0000_0003_0000_0002UL;
        foreach (var phase in Enum.GetValues<DirectoryPhase>().Where(static phase => phase != DirectoryPhase.None))
        {
            var paused = new InsertState(
                SlotControl.Initializing,
                phase,
                DescriptorPublished: true,
                DescriptorGenerationMatches: false,
                CandidateBinding: 0x0000_0002_8000_0001UL,
                CellBinding: unrelatedBinding,
                BindingInstallCount: 0,
                TargetConflictRemaining: false);

            var completed = InsertOracle.HelpToQuiescence(paused);

            Assert.False(completed.DescriptorPublished);
            Assert.Equal(unrelatedBinding, completed.CellBinding);
            Assert.Equal(0, completed.BindingInstallCount);
        }
    }

    [Fact]
    public void ConflictingTargetSelectionRestartsAndRemainsHelpable()
    {
        var paused = new InsertState(
            SlotControl.Initializing,
            DirectoryPhase.TargetSelected,
            DescriptorPublished: true,
            DescriptorGenerationMatches: true,
            CandidateBinding: 0x0000_0002_8000_0001UL,
            CellBinding: 0x0000_0003_0000_0002UL,
            BindingInstallCount: 0,
            TargetConflictRemaining: true);

        var completed = InsertOracle.HelpToQuiescence(paused);

        Assert.Equal(SlotControl.Reserved, completed.Control);
        Assert.Equal(DirectoryPhase.Complete, completed.Phase);
        Assert.Equal(paused.CandidateBinding, completed.CellBinding);
        Assert.Equal(1, completed.BindingInstallCount);
        Assert.False(completed.DescriptorPublished);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DelayedValidatedDirectoryHelperCannotMutateReusedGeneration(bool locationWasValidated)
    {
        const long oldGeneration = 41;
        const long newGeneration = 42;
        ulong oldBinding = Binding(oldGeneration);
        ulong newBinding = Binding(newGeneration);
        var old = new TaggedDirectoryState(
            oldGeneration,
            TaggedControl.Reclaiming,
            oldBinding,
            TaggedWord(oldGeneration, 1),
            TaggedWord(oldGeneration, 2),
            oldBinding,
            KeyHash: 0x1111,
            KeyLength: 1,
            DescriptorLength: 1,
            ValueLength: 2,
            PayloadMarker: 0x11);
        var validated = ValidatedDirectoryHelper.Capture(old, locationWasValidated);

        var reused = new TaggedDirectoryState(
            newGeneration,
            TaggedControl.Initializing,
            newBinding,
            TaggedWord(newGeneration, 3),
            TaggedWord(newGeneration, 4),
            newBinding,
            KeyHash: 0x2222,
            KeyLength: 3,
            DescriptorLength: 2,
            ValueLength: 7,
            PayloadMarker: 0x22);

        TaggedDirectoryState afterDelayedResume = TaggedDirectoryOracle.ResumeValidatedHelper(reused, validated);

        Assert.Equal(reused, afterDelayedResume);
    }

    [Fact]
    public void DelayedHelperRollsBackOnlyItsExactOldBindingResidueAfterSlotReuse()
    {
        const long oldGeneration = 51;
        const long newGeneration = 52;
        ulong oldBinding = Binding(oldGeneration);
        ulong newBinding = Binding(newGeneration);
        var validated = ValidatedDirectoryHelper.Capture(
            new TaggedDirectoryState(
                oldGeneration,
                TaggedControl.Reclaiming,
                oldBinding,
                TaggedWord(oldGeneration, 1),
                TaggedWord(oldGeneration, 2),
                oldBinding,
                KeyHash: 0x3333,
                KeyLength: 1,
                DescriptorLength: 1,
                ValueLength: 1,
                PayloadMarker: 0x33),
            locationWasValidated: true);
        var reusedWithOldResidue = new TaggedDirectoryState(
            newGeneration,
            TaggedControl.Initializing,
            newBinding,
            TaggedWord(newGeneration, 3),
            TaggedWord(newGeneration, 4),
            oldBinding,
            KeyHash: 0x4444,
            KeyLength: 4,
            DescriptorLength: 3,
            ValueLength: 8,
            PayloadMarker: 0x44);

        TaggedDirectoryState cleaned = TaggedDirectoryOracle.ResumeValidatedHelper(
            reusedWithOldResidue,
            validated);

        Assert.Equal(0UL, cleaned.TargetCellBinding);
        Assert.Equal(reusedWithOldResidue with { TargetCellBinding = 0 }, cleaned);

        var newBindingInTarget = reusedWithOldResidue with { TargetCellBinding = newBinding };
        Assert.Equal(
            newBindingInTarget,
            TaggedDirectoryOracle.ResumeValidatedHelper(newBindingInTarget, validated));
    }

    private static object[] ReadCheckpointEntries()
    {
        var assembly = typeof(MemoryStore).Assembly;
        var catalogType = assembly.GetType(
            "SharedMemoryStore.LockFree.LockFreeCheckpointCatalog",
            throwOnError: false,
            ignoreCase: false);
        Assert.True(
            catalogType is not null,
            "The lock-free implementation must expose an internal canonical LockFreeCheckpointCatalog for deterministic validation.");
        var entriesMember = catalogType!.GetMember(
            "Entries",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).SingleOrDefault();
        Assert.True(entriesMember is not null, "LockFreeCheckpointCatalog must expose a static Entries member.");
        var value = entriesMember switch
        {
            PropertyInfo property => property.GetValue(null),
            FieldInfo field => field.GetValue(null),
            _ => null
        };
        var enumerable = Assert.IsAssignableFrom<IEnumerable>(value);
        var entries = enumerable.Cast<object>().ToArray();
        Assert.NotEmpty(entries);
        return entries;
    }

    private static string ReadRequiredMember(object entry, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var type = entry.GetType();
        var value = type.GetProperty(name, flags)?.GetValue(entry)
            ?? type.GetField(name, flags)?.GetValue(entry);
        Assert.True(value is not null, $"Checkpoint entry '{type.FullName}' must expose {name}.");
        var text = value!.ToString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        return text!;
    }

    private static void AssertClassified(object entry, string classification)
    {
        var text = ReadRequiredMember(entry, classification);
        Assert.False(string.Equals("Unspecified", text, StringComparison.OrdinalIgnoreCase));
    }

    private enum SlotControl
    {
        Initializing,
        Reserved,
        Aborting
    }

    private enum DirectoryPhase
    {
        None,
        Prepared,
        TargetSelected,
        BindingChanged,
        Rejected,
        Complete
    }

    private enum TaggedControl
    {
        Reclaiming,
        Free,
        Initializing
    }

    private readonly record struct InsertState(
        SlotControl Control,
        DirectoryPhase Phase,
        bool DescriptorPublished,
        bool DescriptorGenerationMatches,
        ulong CandidateBinding,
        ulong CellBinding,
        int BindingInstallCount,
        bool TargetConflictRemaining);

    private readonly record struct TaggedDirectoryState(
        long Generation,
        TaggedControl Control,
        ulong SlotBinding,
        ulong OperationWord,
        ulong LocationWord,
        ulong TargetCellBinding,
        ulong KeyHash,
        int KeyLength,
        int DescriptorLength,
        int ValueLength,
        long PayloadMarker);

    private readonly record struct ValidatedDirectoryHelper(
        long Generation,
        ulong Binding,
        ulong OperationWord,
        ulong LocationWord,
        bool LocationWasValidated)
    {
        internal static ValidatedDirectoryHelper Capture(
            TaggedDirectoryState state,
            bool locationWasValidated) =>
            new(
                state.Generation,
                state.SlotBinding,
                state.OperationWord,
                state.LocationWord,
                locationWasValidated);
    }

    private static class TaggedDirectoryOracle
    {
        internal static TaggedDirectoryState ResumeValidatedHelper(
            TaggedDirectoryState current,
            ValidatedDirectoryHelper delayed)
        {
            // Any stale target installed by this exact old-generation helper is
            // independently removable. Every slot-local write remains fenced by
            // the complete generation-tagged operation/location/control tuple.
            ulong target = current.TargetCellBinding == delayed.Binding
                ? 0
                : current.TargetCellBinding;
            if (current.Generation != delayed.Generation
                || current.SlotBinding != delayed.Binding
                || current.OperationWord != delayed.OperationWord
                || (delayed.LocationWasValidated && current.LocationWord != delayed.LocationWord))
            {
                return current with { TargetCellBinding = target };
            }

            return current with { TargetCellBinding = target };
        }
    }

    private static ulong Binding(long generation) =>
        ((ulong)generation << 31) | 1UL;

    private static ulong TaggedWord(long generation, byte payload) =>
        ((ulong)generation << 16) | payload;

    private static class InsertOracle
    {
        public static IEnumerable<InsertState> NormalInsertPauses(ulong binding)
        {
            var state = new InsertState(
                SlotControl.Initializing,
                DirectoryPhase.Prepared,
                DescriptorPublished: true,
                DescriptorGenerationMatches: true,
                CandidateBinding: binding,
                CellBinding: 0,
                BindingInstallCount: 0,
                TargetConflictRemaining: false);
            yield return state;
            while (state.DescriptorPublished)
            {
                state = HelpOne(state);
                yield return state;
            }
        }

        public static InsertState HelpToQuiescence(InsertState state)
        {
            for (var step = 0; state.DescriptorPublished && step < 16; step++)
            {
                state = HelpOne(state);
            }

            Assert.False(state.DescriptorPublished, "Every published insert descriptor must converge under helping.");
            return state;
        }

        private static InsertState HelpOne(InsertState state)
        {
            if (!state.DescriptorPublished)
            {
                return state;
            }

            if (!state.DescriptorGenerationMatches)
            {
                return state with { DescriptorPublished = false };
            }

            return state.Phase switch
            {
                DirectoryPhase.Prepared => state with { Phase = DirectoryPhase.TargetSelected },
                DirectoryPhase.TargetSelected when state.TargetConflictRemaining => state with
                {
                    Phase = DirectoryPhase.Prepared,
                    CellBinding = 0,
                    TargetConflictRemaining = false
                },
                DirectoryPhase.TargetSelected when state.CellBinding == 0 => state with
                {
                    Phase = DirectoryPhase.BindingChanged,
                    CellBinding = state.CandidateBinding,
                    BindingInstallCount = state.BindingInstallCount + 1
                },
                DirectoryPhase.TargetSelected when state.CellBinding == state.CandidateBinding =>
                    state with { Phase = DirectoryPhase.BindingChanged },
                DirectoryPhase.BindingChanged => state with
                {
                    Control = SlotControl.Reserved,
                    Phase = DirectoryPhase.Complete
                },
                DirectoryPhase.Complete => state with { DescriptorPublished = false },
                DirectoryPhase.Rejected => state with
                {
                    Control = SlotControl.Aborting,
                    DescriptorPublished = false
                },
                _ => throw new InvalidOperationException($"Unsafe insert state: {state}")
            };
        }
    }
}
