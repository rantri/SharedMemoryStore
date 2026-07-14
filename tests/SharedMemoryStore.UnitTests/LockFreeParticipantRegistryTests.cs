using System.Reflection;
using System.Diagnostics;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.Leasing;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeParticipantRegistryTests
{
    [Fact]
    public void ParticipantStatesAndCrashClassificationsCoverEveryLifecyclePhase()
    {
        Assert.Equal(0, LayoutV2Constants.ParticipantFree);
        Assert.Equal(1, LayoutV2Constants.ParticipantRegistering);
        Assert.Equal(2, LayoutV2Constants.ParticipantActive);
        Assert.Equal(3, LayoutV2Constants.ParticipantClosing);
        Assert.Equal(4, LayoutV2Constants.ParticipantRecovering);
        Assert.Equal(5, LayoutV2Constants.ParticipantReclaiming);
        Assert.Equal(6, LayoutV2Constants.ParticipantRetired);

        Assert.Equal(ParticipantOwnerClass.Live, ClassifyIdentity(42, 1, 100, 42, 1, 100));
        Assert.Equal(ParticipantOwnerClass.Stale, ClassifyIdentity(42, 1, 100, 42, 1, 101));
        Assert.Equal(ParticipantOwnerClass.Unsupported, ClassifyIdentity(42, 0, 0, 42, 0, 0));
        Assert.Equal(ParticipantOwnerClass.Inconsistent, ClassifyIdentity(0, 1, 100, 42, 1, 100));
    }

    [Fact]
    public void RegistryExposesRecordLocalRecoveryHelpingRetirementAndDiagnostics()
    {
        Type registry = RequireType("SharedMemoryStore.LockFree.LockFreeParticipantRegistry");
        MethodInfo[] methods = registry.GetMethods(
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.Contains(methods, method => method.Name.Contains("Register", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Close", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Unregister", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Recover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Reclaim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Help", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Diagnostic", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
            || method.Name.Contains("Count", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(methods, method => method.Name.Contains("Advance", StringComparison.OrdinalIgnoreCase)
            && method.Name.Contains("Retire", StringComparison.OrdinalIgnoreCase));

        Type[] forbidden = [typeof(Mutex), typeof(Semaphore), typeof(SemaphoreSlim), typeof(ReaderWriterLockSlim)];
        Assert.DoesNotContain(
            registry.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            field => forbidden.Contains(field.FieldType));
    }

    [Fact]
    public void ParticipantIdentitySurfaceIncludesPidKindStartValueAndConservativeClassification()
    {
        Type incarnation = RequireType("SharedMemoryStore.LockFree.ParticipantIncarnation");
        var members = incarnation.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.Contains(members, member => member.Name.Contains("Pid", StringComparison.OrdinalIgnoreCase)
            || member.Name.Contains("ProcessId", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(members, member => member.Name.Contains("Identity", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(members, member => member.Name.Contains("Start", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(members, member => member.Name.Contains("Namespace", StringComparison.OrdinalIgnoreCase));

        Type classifier = RequireType("SharedMemoryStore.Leasing.LeaseOwnerClassifier");
        Assert.Contains(
            classifier.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic),
            method => method.Name.Contains("Classif", StringComparison.OrdinalIgnoreCase)
                && method.GetParameters().Any(parameter => parameter.ParameterType == incarnation));
    }

    [Fact]
    public void CompleteParticipantTokenRoundTripsEveryRecordAndChangesOnReuse()
    {
        const int participantCount = 7;
        for (var recordIndex = 0; recordIndex < participantCount; recordIndex++)
        {
            ulong first = ParticipantToken.Encode(recordIndex, generation: 1, participantCount);
            ParticipantToken decoded = ParticipantToken.Decode(first, participantCount);
            Assert.Equal(recordIndex, decoded.RecordIndex);
            Assert.Equal(1, decoded.Generation);
            Assert.NotEqual(first, ParticipantToken.Encode(recordIndex, generation: 2, participantCount));
        }
    }

    [Fact]
    public void FirstRegisteringClaimAtomicallyCarriesPidAndIncarnation()
    {
        const int incarnation = 17;
        int pid = Environment.ProcessId;
        ulong claim = AtomicControlWord.EncodeParticipant(
            LayoutV2Constants.ParticipantRegistering,
            incarnation,
            pid);

        Assert.Equal(LayoutV2Constants.ParticipantRegistering, (int)(claim & 0x7UL));
        Assert.Equal(incarnation, (int)((claim >> 3) & 0x0fff_ffffUL));
        Assert.Equal(pid, (int)(claim >> 31));
        Assert.Equal(0UL, claim >> 63);
    }

    [Fact]
    public unsafe void HeaderAndActiveParticipantPublishTheAdmittedPidNamespaceIdentity()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        string name = $"sms-v2-participant-pidns-{Guid.NewGuid():N}";
        using MemoryStore store = Open(name, OpenMode.CreateNew, participantCount: 2);
        (_, object engine) = ReadEngineComponents(store);
        var region = Assert.IsAssignableFrom<SharedMemoryStore.Interop.MemoryMappedStoreRegion>(engine.GetType()
            .GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        ref StoreHeaderV2 header = ref *(StoreHeaderV2*)region.Pointer;
        (LockFreeParticipantRegistry registry, LockFreeParticipantRegistry.Registration registration) =
            ReadParticipantComponents(store);
        ParticipantClassification active = registry.ClassifyParticipant(registration.Token);

        Assert.Equal(ParticipantClassificationKind.CurrentProcess, active.Kind);
        Assert.Equal(header.PidNamespaceId, active.Incarnation.PidNamespaceId);
        if (OperatingSystem.IsLinux())
        {
            Assert.NotEqual(0UL, header.PidNamespaceId);
        }
        else
        {
            Assert.Equal(0UL, header.PidNamespaceId);
        }
        Assert.Equal(
            LayoutV2Constants.PidNamespaceRecoveryEnabled,
            AtomicControlWord.LoadAcquire(ref header.PidNamespaceMode));
    }

    [Fact]
    public async Task LinuxPidNamespaceMismatchPublishesMixedBeforeRegisteringAndPreservesTheClaim()
    {
        if (!OperatingSystem.IsLinux()
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        string name = $"sms-v2-participant-pidns-mismatch-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        (_, object engine) = ReadEngineComponents(controller);
        var region = Assert.IsAssignableFrom<SharedMemoryStore.Interop.MemoryMappedStoreRegion>(engine.GetType()
            .GetField("_region", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        ulong admittedNamespace = ReadHeaderPidNamespaceId(region);
        Assert.NotEqual(0UL, admittedNamespace);
        ulong otherNamespace = admittedNamespace == ulong.MaxValue
            ? admittedNamespace - 1
            : admittedNamespace + 1;
        long sequenceBefore = ReadHeaderSequence(region);
        (LockFreeParticipantRegistry registry, _) = ReadParticipantComponents(controller);
        ulong registeringToken = ParticipantToken.Encode(recordIndex: 1, generation: 1, participantCount: 2);
        using var scheduler = new ControlledLockFreeScheduler();
        scheduler.PauseAt(LockFreeCheckpointId.ParticipantAfterIdentityKindWrite);

        WriteHeaderPidNamespaceId(region, otherNamespace);
        try
        {
            StoreOpenStatus status = default;
            MemoryStore? candidate = null;
            Task opening = Task.Run(() => status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                Options(name, OpenMode.OpenExisting, participantCount: 2),
                scheduler.CreateInstrumentedCheckpoint(),
                out candidate));
            Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

            Assert.Equal(otherNamespace, ReadHeaderPidNamespaceId(region));
            Assert.Equal(sequenceBefore, ReadHeaderSequence(region));
            Assert.Equal(
                LayoutV2Constants.PidNamespaceRecoveryMixed,
                ReadHeaderPidNamespaceMode(region));
            ParticipantClassification partial = registry.ClassifyParticipant(registeringToken);
            Assert.Equal(ParticipantClassificationKind.Unsupported, partial.Kind);
            Assert.Equal(LayoutV2Constants.ParticipantRegistering, partial.Incarnation.State);
            Assert.Equal(
                ParticipantTransitionResult.Unsupported,
                registry.TryRecoverParticipant(registeringToken));
            ParticipantClassification preserved = registry.ClassifyParticipant(registeringToken);
            Assert.Equal(LayoutV2Constants.ParticipantRegistering, preserved.Incarnation.State);
            Assert.Equal(partial.Incarnation.Control, preserved.Incarnation.Control);

            scheduler.Continue();
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            using MemoryStore opened = Assert.IsType<MemoryStore>(candidate);
            Assert.Equal(StoreOpenStatus.Success, status);
            ParticipantClassification active = registry.ClassifyParticipant(registeringToken);
            Assert.Equal(ParticipantClassificationKind.CurrentProcess, active.Kind);
            Assert.Equal(LayoutV2Constants.ParticipantActive, active.Incarnation.State);
            Assert.Equal(StoreStatus.Success, opened.TryPublish([0x7A], [0x7B]));
        }
        finally
        {
            scheduler.Continue();
            WriteHeaderPidNamespaceId(region, admittedNamespace);
        }
    }

    [Fact]
    public void RegisteringWithNewIdentityKindAndStalePriorStartUsesPresenceOnlyClassification()
    {
        Assert.True(LeaseOwnerClassifier.TryCaptureCurrentProcessIdentity(
            out int identityKind,
            out long currentStart,
            out ulong pidNamespaceId));
        long stalePriorStart = currentStart == long.MaxValue ? currentStart - 1 : currentStart + 1;
        var mixed = new ParticipantIncarnation(
            RecordIndex: 0,
            Generation: 2,
            Token: 2,
            State: LayoutV2Constants.ParticipantRegistering,
            ProcessId: Environment.ProcessId,
            IdentityKind: identityKind,
            ProcessStartValue: stalePriorStart,
            OpenSequence: 1,
            PidNamespaceId: pidNamespaceId,
            ReservedValue: 0,
            Control: 0);

        Assert.Equal(LeaseOwnerKind.StaleProcess, LeaseOwnerClassifier.Classify(mixed).Kind);
        Assert.Equal(
            LeaseOwnerKind.CurrentProcess,
            LockFreeParticipantRegistry.ClassifySnapshotOwner(mixed, pidNamespaceId).Kind);
    }

    [Theory]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterIdentityKindWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterReservedWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterProcessStartWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterPidNamespaceWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterOpenSequenceWrite)]
    public async Task RecoveryCannotRetireLiveRegisteringParticipantAtAnyOrdinaryFieldWrite(
        int checkpointValue)
    {
        string name = $"sms-v2-registering-live-{checkpointValue}-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        using var scheduler = new ControlledLockFreeScheduler();
        scheduler.PauseAt((LockFreeCheckpointId)checkpointValue);

        StoreOpenStatus openStatus = default;
        MemoryStore? second = null;
        var opening = Task.Run(() => openStatus = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, OpenMode.OpenExisting, participantCount: 2),
            scheduler.CreateInstrumentedCheckpoint(),
            out second));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, controller.TryGetDiagnostics(out DiagnosticsSnapshot paused));
        Assert.Equal(1, paused.ActiveParticipantCount);
        Assert.Equal(1, paused.RegisteringParticipantCount);
        Assert.Equal(
            StoreStatus.Success,
            controller.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                out ReservationRecoveryReport report));
        Assert.Equal(default(ReservationRecoveryReport), report);
        Assert.Equal(StoreStatus.Success, controller.TryGetDiagnostics(out DiagnosticsSnapshot preserved));
        Assert.Equal(1, preserved.ActiveParticipantCount);
        Assert.Equal(1, preserved.RegisteringParticipantCount);

        scheduler.Continue();
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        using MemoryStore opened = Assert.IsType<MemoryStore>(second);
        Assert.Equal(StoreOpenStatus.Success, openStatus);
        Assert.Equal(StoreStatus.Success, controller.TryGetDiagnostics(out DiagnosticsSnapshot active));
        Assert.Equal(2, active.ActiveParticipantCount);
        Assert.Equal(0, active.RegisteringParticipantCount);
    }

    [Theory]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterIdentityKindWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterReservedWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterProcessStartWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterPidNamespaceWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterOpenSequenceWrite)]
    [InlineData((int)LockFreeCheckpointId.ParticipantAfterActivePublication)]
    public void RegistrationObserverExceptionRetiresTheUnescapedExactClaim(
        int checkpointValue)
    {
        string name = $"sms-v2-registering-exception-{checkpointValue}-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        var target = (LockFreeCheckpointId)checkpointValue;
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id == target)
            {
                throw new InjectedRegistrationException();
            }
        });

        Assert.Equal(
            StoreOpenStatus.MappingFailed,
            LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                Options(name, OpenMode.OpenExisting, participantCount: 2),
                checkpoint,
                out MemoryStore? failed));
        Assert.Null(failed);

        using MemoryStore replacement = Open(name, OpenMode.OpenExisting, participantCount: 2);
        Assert.Equal(StoreStatus.Success, replacement.TryPublish([0x41], [0x42]));
        Assert.Equal(StoreStatus.Success, replacement.TryRemove([0x41]));
    }

    [Fact]
    public void PostRegistrationConstructionExceptionRetiresTheExactActiveIncarnation()
    {
        string name = $"sms-v2-construction-exception-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id == LockFreeCheckpointId.ParticipantAfterRegistrationBeforeEngineConstruction)
            {
                throw new InjectedRegistrationException();
            }
        });

        Assert.Equal(
            StoreOpenStatus.MappingFailed,
            LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
                Options(name, OpenMode.OpenExisting, participantCount: 2),
                checkpoint,
                out MemoryStore? failed));
        Assert.Null(failed);

        using MemoryStore replacement = Open(name, OpenMode.OpenExisting, participantCount: 2);
        Assert.Equal(StoreStatus.Success, replacement.TryPublish([0x51], [0x52]));
        Assert.Equal(StoreStatus.Success, replacement.TryRemove([0x51]));
    }

    [Fact]
    public void FullParticipantTableRejectsOnlyNewHandleAndReusesAdvancedIncarnation()
    {
        string name = $"sms-v2-participant-table-{Guid.NewGuid():N}";
        using var first = Open(name, OpenMode.CreateNew, participantCount: 2);
        var second = Open(name, OpenMode.OpenExisting, participantCount: 2);

        Assert.Equal(StoreStatus.Success, second.TryReserve([2], 1, default, out var secondReservation));
        ulong secondToken = secondReservation.HandleForEngine.ParticipantToken;
        Assert.Equal(StoreStatus.Success, secondReservation.Abort());

        Assert.Equal(
            StoreOpenStatus.ParticipantTableFull,
            MemoryStore.TryCreateOrOpen(Options(name, OpenMode.OpenExisting, participantCount: 2), out var rejected));
        Assert.Null(rejected);
        Assert.Equal(StoreStatus.Success, first.TryPublish([1], [1]));

        second.Dispose();
        using var reused = Open(name, OpenMode.OpenExisting, participantCount: 2);
        Assert.Equal(StoreStatus.Success, reused.TryReserve([3], 1, default, out var replacement));
        ulong replacementToken = replacement.HandleForEngine.ParticipantToken;
        Assert.NotEqual(secondToken, replacementToken);
        Assert.Equal(
            ParticipantToken.Decode(secondToken, 2).RecordIndex,
            ParticipantToken.Decode(replacementToken, 2).RecordIndex);
        Assert.Equal(
            ParticipantToken.Decode(secondToken, 2).Generation + 1,
            ParticipantToken.Decode(replacementToken, 2).Generation);
        Assert.Equal(StoreStatus.Success, replacement.Abort());
    }

    [Theory]
    [InlineData(LayoutV2Constants.ParticipantClosing)]
    [InlineData(LayoutV2Constants.ParticipantRecovering)]
    public void ClaimClosedLiveParticipantIsRetiredWithoutOwnerLivenessClassification(
        int handedOffState)
    {
        string name = $"sms-v2-participant-handoff-{handedOffState}-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        using MemoryStore handedOff = Open(name, OpenMode.OpenExisting, participantCount: 2);
        (LockFreeParticipantRegistry registry, LockFreeParticipantRegistry.Registration registration) =
            ReadParticipantComponents(handedOff);

        ParticipantTransitionResult transition;
        if (handedOffState == LayoutV2Constants.ParticipantClosing)
        {
            transition = registry.TryBeginClose(registration);
        }
        else
        {
            ParticipantClassification active = registry.ClassifyParticipant(registration.Token);
            Assert.Equal(ParticipantClassificationKind.CurrentProcess, active.Kind);
            Assert.Equal(LayoutV2Constants.ParticipantActive, active.Incarnation.State);
            transition = registry.TryBeginRecovery(active.Incarnation);
        }

        Assert.Equal(ParticipantTransitionResult.Succeeded, transition);
        Assert.Equal(
            StoreStatus.Success,
            controller.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                StoreWaitOptions.Infinite,
                out ReservationRecoveryReport report));
        Assert.Equal(default, report);
        Assert.Equal(StoreStatus.Success, controller.TryGetDiagnostics(out DiagnosticsSnapshot snapshot));
        Assert.Equal(1, snapshot.ActiveParticipantCount);
        Assert.Equal(0, snapshot.ClosingParticipantCount);
        Assert.Equal(0, snapshot.RecoveringParticipantCount);
        Assert.Equal(1, snapshot.FreeParticipantCount);

        using MemoryStore replacement = Open(name, OpenMode.OpenExisting, participantCount: 2);
        Assert.Equal(StoreStatus.Success, replacement.TryPublish([0x61], [0x62]));
        Assert.Equal(StoreStatus.Success, replacement.TryRemove([0x61]));
    }

    [Fact]
    public void UnescapedPostRegistrationOwnerCanCloseAndRetireWithoutAReferenceScan()
    {
        string name = $"sms-v2-participant-construction-cleanup-{Guid.NewGuid():N}";
        using MemoryStore controller = Open(name, OpenMode.CreateNew, participantCount: 2);
        using MemoryStore unescaped = Open(name, OpenMode.OpenExisting, participantCount: 2);
        (LockFreeParticipantRegistry registry, LockFreeParticipantRegistry.Registration registration) =
            ReadParticipantComponents(unescaped);

        registry.RetireUnreferencedRegistration(registration);

        Assert.Equal(StoreStatus.Success, controller.TryGetDiagnostics(out DiagnosticsSnapshot cleaned));
        Assert.Equal(1, cleaned.ActiveParticipantCount);
        Assert.Equal(1, cleaned.FreeParticipantCount);
        using MemoryStore replacement = Open(name, OpenMode.OpenExisting, participantCount: 2);
        Assert.Equal(StoreStatus.Success, replacement.TryPublish([0x71], [0x72]));
        Assert.Equal(StoreStatus.Success, replacement.TryRemove([0x71]));
    }

    [Fact]
    public void InstrumentedOpenBracketsFirstParticipantClaimAndActivePublication()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        var status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-participant-checkpoints-{Guid.NewGuid():N}", OpenMode.CreateNew, participantCount: 1),
            scheduler.CreateInstrumentedCheckpoint(),
            out var store);
        using var opened = Assert.IsType<MemoryStore>(store);
        Assert.Equal(StoreOpenStatus.Success, status);

        LockFreeCheckpointId[] observed = scheduler.Snapshot()
            .Select(observation => observation.Entry.Id)
            .ToArray();
        Assert.Contains(LockFreeCheckpointId.ParticipantBeforeRegisteringCas, observed);
        Assert.Contains(LockFreeCheckpointId.ParticipantAfterActivePublication, observed);
        Assert.True(
            Array.IndexOf(observed, LockFreeCheckpointId.ParticipantBeforeRegisteringCas)
            < Array.IndexOf(observed, LockFreeCheckpointId.ParticipantAfterActivePublication));
    }

    [Fact]
    public async Task FreshReferenceScanAfterRecoveryFencePreventsParticipantReuse()
    {
        if ((!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                != System.Runtime.InteropServices.Architecture.X64)
        {
            return;
        }

        const int slotCount = 4;
        const int participantCount = 2;
        string name = $"sms-v2-participant-fence-{Guid.NewGuid():N}";
        using var scheduler = new ControlledLockFreeScheduler();
        StoreOpenStatus opened = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options(name, OpenMode.CreateNew, participantCount),
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, opened);
        using MemoryStore store = Assert.IsType<MemoryStore>(candidate);

        await CreateOrphanParticipant(name, slotCount, participantCount);
        const int orphanRecordIndex = 1;
        const int orphanGeneration = 1;
        ulong orphanToken = ParticipantToken.Encode(
            orphanRecordIndex,
            orphanGeneration,
            participantCount);
        scheduler.PauseAt(LockFreeCheckpointId.ParticipantAfterRecoveryFenceBeforeReferenceScan);

        ReservationRecoveryReport report = default;
        Task<StoreStatus> recovery = Task.Run(() => store.TryRecoverReservations(
            new ReservationRecoveryOptions(false),
            StoreWaitOptions.Infinite,
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot fenced));
        Assert.Equal(1, fenced.RecoveringParticipantCount);

        PublishSyntheticPreFenceReference(store, orphanToken);
        scheduler.Continue();
        Assert.Equal(StoreStatus.Success, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(default, report);

        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot retained));
        Assert.Equal(1, retained.RecoveringParticipantCount);
        Assert.Equal(0, retained.FreeParticipantCount);

        ClearSyntheticReference(store, orphanToken);
        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(false),
                StoreWaitOptions.Infinite,
                out _));
        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(out DiagnosticsSnapshot reclaimed));
        Assert.Equal(0, reclaimed.RecoveringParticipantCount);
        Assert.Equal(1, reclaimed.FreeParticipantCount);
    }

    private static async Task CreateOrphanParticipant(
        string name,
        int slotCount,
        int participantCount)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "exec",
            LocateAgentAssembly(),
            "participant-orphan",
            name,
            slotCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "16",
            "4",
            "8",
            slotCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            participantCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the orphan participant agent.");
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        Assert.True(
            process.ExitCode == 0,
            $"Orphan participant failed with exit {process.ExitCode}. stdout={output} stderr={error}");
    }

    private static unsafe void PublishSyntheticPreFenceReference(MemoryStore store, ulong participantToken)
    {
        (LockFreeSlotTable slots, _) = ReadEngineComponents(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        long free = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.FreeState,
            generation: 1,
            participantToken: 0));
        long initializing = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.InitializingState,
            generation: 1,
            checked((int)participantToken)));
        Assert.Equal(
            free,
            AtomicControlWord.CompareExchange(ref slot.Control, initializing, free));
    }

    private static unsafe void ClearSyntheticReference(MemoryStore store, ulong participantToken)
    {
        (LockFreeSlotTable slots, _) = ReadEngineComponents(store);
        ref ValueSlotMetadataV2 slot = ref slots.Slot(0);
        long initializing = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.InitializingState,
            generation: 1,
            checked((int)participantToken)));
        long free = unchecked((long)AtomicControlWord.EncodeSlot(
            LockFreeSlotTable.FreeState,
            generation: 1,
            participantToken: 0));
        Assert.Equal(
            initializing,
            AtomicControlWord.CompareExchange(ref slot.Control, free, initializing));
    }

    private static (LockFreeSlotTable Slots, object Engine) ReadEngineComponents(MemoryStore store)
    {
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        var slots = Assert.IsType<LockFreeSlotTable>(engine.GetType()
            .GetField("_slots", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        return (slots, engine);
    }

    private static unsafe ulong ReadHeaderPidNamespaceId(
        SharedMemoryStore.Interop.MemoryMappedStoreRegion region) =>
        ((StoreHeaderV2*)region.Pointer)->PidNamespaceId;

    private static unsafe long ReadHeaderPidNamespaceMode(
        SharedMemoryStore.Interop.MemoryMappedStoreRegion region) =>
        AtomicControlWord.LoadAcquire(ref ((StoreHeaderV2*)region.Pointer)->PidNamespaceMode);

    private static unsafe long ReadHeaderSequence(
        SharedMemoryStore.Interop.MemoryMappedStoreRegion region) =>
        ((StoreHeaderV2*)region.Pointer)->Sequence;

    private static unsafe void WriteHeaderPidNamespaceId(
        SharedMemoryStore.Interop.MemoryMappedStoreRegion region,
        ulong pidNamespaceId) =>
        ((StoreHeaderV2*)region.Pointer)->PidNamespaceId = pidNamespaceId;

    private static (
        LockFreeParticipantRegistry Registry,
        LockFreeParticipantRegistry.Registration Registration) ReadParticipantComponents(
        MemoryStore store)
    {
        (_, object engine) = ReadEngineComponents(store);
        var registry = Assert.IsType<LockFreeParticipantRegistry>(engine.GetType()
            .GetField("_participants", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        var registration = Assert.IsType<LockFreeParticipantRegistry.Registration>(engine.GetType()
            .GetField("_registration", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine));
        return (registry, registration);
    }

    private static string LocateAgentAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        string root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        string path = Path.Combine(
            root,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("Lock-free agent assembly was not built.", path);
    }

    private static Type RequireType(string name)
    {
        Type? type = typeof(MemoryStore).Assembly.GetType(name, throwOnError: false, ignoreCase: false);
        Assert.True(type is not null, $"Required participant recovery surface {name} is missing.");
        return type!;
    }

    private static ParticipantOwnerClass ClassifyIdentity(
        int storedPid,
        int storedKind,
        long storedStart,
        int observedPid,
        int observedKind,
        long observedStart)
    {
        if (storedPid <= 0 || storedKind is < 0 or > 2 || storedStart < 0)
        {
            return ParticipantOwnerClass.Inconsistent;
        }

        if (storedKind == LayoutV2Constants.IdentityUnknown || observedKind == LayoutV2Constants.IdentityUnknown)
        {
            return ParticipantOwnerClass.Unsupported;
        }

        return storedPid == observedPid && storedKind == observedKind && storedStart == observedStart
            ? ParticipantOwnerClass.Live
            : ParticipantOwnerClass.Stale;
    }

    private static MemoryStore Open(string name, OpenMode mode, int participantCount)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(Options(name, mode, participantCount), out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode mode, int participantCount) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: 4,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 4,
            participantRecordCount: participantCount,
            openMode: mode,
            enableLeaseRecovery: true);

    private enum ParticipantOwnerClass
    {
        Live,
        Stale,
        Unsupported,
        Inconsistent
    }

    private sealed class InjectedRegistrationException : Exception;
}
