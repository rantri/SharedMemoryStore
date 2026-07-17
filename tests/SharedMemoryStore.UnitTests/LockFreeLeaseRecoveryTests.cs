using System.Diagnostics;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeLeaseRecoveryTests
{
    [Fact]
    public void CurrentProcessOverrideDoesNotRevokeLiveClaimInitialization()
    {
        Assert.Equal(
            LockFreeLeaseRegistry.RecoveryDisposition.Active,
            LockFreeLeaseRegistry.RecoveryDispositionFor(
                LockFreeLeaseRegistry.ClaimingState,
                ParticipantClassificationKind.CurrentProcess,
                LayoutV2Constants.ParticipantActive,
                participantHandoffPublished: false,
                recoverCurrentProcessLeases: true));

        Assert.Equal(
            LockFreeLeaseRegistry.RecoveryDisposition.Recover,
            LockFreeLeaseRegistry.RecoveryDispositionFor(
                LockFreeLeaseRegistry.ActiveState,
                ParticipantClassificationKind.CurrentProcess,
                LayoutV2Constants.ParticipantActive,
                participantHandoffPublished: false,
                recoverCurrentProcessLeases: true));

        Assert.Equal(
            LockFreeLeaseRegistry.RecoveryDisposition.Recover,
            LockFreeLeaseRegistry.RecoveryDispositionFor(
                LockFreeLeaseRegistry.ClaimingState,
                ParticipantClassificationKind.CurrentProcess,
                LayoutV2Constants.ParticipantClosing,
                participantHandoffPublished: true,
                recoverCurrentProcessLeases: false));

        Assert.Equal(
            LockFreeLeaseRegistry.RecoveryDisposition.Recover,
            LockFreeLeaseRegistry.RecoveryDispositionFor(
                LockFreeLeaseRegistry.ActiveState,
                ParticipantClassificationKind.Live,
                LayoutV2Constants.ParticipantClosing,
                participantHandoffPublished: true,
                recoverCurrentProcessLeases: false));

        Assert.Equal(
            LockFreeLeaseRegistry.RecoveryDisposition.Recover,
            LockFreeLeaseRegistry.RecoveryDispositionFor(
                LockFreeLeaseRegistry.ClaimingState,
                ParticipantClassificationKind.Unsupported,
                LayoutV2Constants.ParticipantRecovering,
                participantHandoffPublished: true,
                recoverCurrentProcessLeases: false));
    }

    [Fact]
    public async Task LiveReleaseAndRecordReuseWinningBeforeRecoveryCasPreservesLaterIncarnation()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 2, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [11]));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [22]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        ulong firstLeaseToken = first.HandleForEngine.LeaseToken;
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        LeaseRecoveryReport report = default;
        var recovery = Task.Run(() => store.TryRecoverLeases(
            new LeaseRecoveryOptions(true),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var replacement));
        Assert.NotEqual(firstLeaseToken, replacement.HandleForEngine.LeaseToken);
        scheduler.Continue();

        Assert.Equal(StoreStatus.Success, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new LeaseRecoveryReport(1, 0, 0, 0, 0), report);
        Assert.True(replacement.IsValid);
        Assert.Equal(22, replacement.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task RecoveryExactCasWinningBeforeLiveReleaseFencesOldHandleAndRestoresCapacity()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        scheduler.PauseAt(LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas);

        StoreStatus releaseStatus = default;
        var release = Task.Run(() => releaseStatus = lease.Release());
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report));
        Assert.Equal(new LeaseRecoveryReport(1, 1, 0, 0, 0), report);
        Assert.False(lease.IsValid);
        scheduler.Continue();
        await release.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.LeaseAlreadyReleased, releaseStatus);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task OrdinaryAcquireHelpsPausedUnownedReleaseAndReusesCapacityOneLeaseRecord()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        ulong firstToken = first.HandleForEngine.LeaseToken;
        scheduler.PauseAt(LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas);

        StoreStatus releaseStatus = default;
        var release = Task.Run(() => releaseStatus = first.Release(StoreWaitOptions.Infinite));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        // The lease record is unowned Releasing. Ordinary acquisition, without
        // invoking explicit recovery, must finish that exact incarnation and
        // claim the next one instead of leaking LeaseTableFull.
        Assert.Equal(
            StoreStatus.Success,
            store.TryAcquire([1], StoreWaitOptions.Infinite, out var replacement));
        Assert.NotEqual(firstToken, replacement.HandleForEngine.LeaseToken);
        Assert.True(replacement.IsValid);

        scheduler.Continue();
        await release.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(StoreStatus.Success, releaseStatus);
        Assert.False(first.IsValid);
        Assert.True(replacement.IsValid);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task CancellationBeforeRecoveryCasPreservesLeaseAndReturnsPartialReport()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        LeaseRecoveryReport report = default;
        var recovery = Task.Run(() => store.TryRecoverLeases(
            new LeaseRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();

        Assert.Equal(StoreStatus.OperationCanceled, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new LeaseRecoveryReport(1, 0, 0, 0, 0), report);
        Assert.True(lease.IsValid);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task DeadlineBeforeRecoveryCasPreservesLeaseAndReturnsPartialReport()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryBeforeOwnerClassification);

        LeaseRecoveryReport report = default;
        var recovery = Task.Run(() => store.TryRecoverLeases(
            new LeaseRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromMilliseconds(50)),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        scheduler.Continue();

        Assert.Equal(StoreStatus.StoreBusy, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new LeaseRecoveryReport(1, 0, 0, 0, 0), report);
        Assert.True(lease.IsValid);
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public async Task CancellationAfterExactRecoveryCasFinishesHelpableTransition()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);

        LeaseRecoveryReport report = default;
        var recovery = Task.Run(() => store.TryRecoverLeases(
            new LeaseRecoveryOptions(true),
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token),
            out report));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();

        Assert.Equal(StoreStatus.Success, await recovery.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new LeaseRecoveryReport(1, 1, 0, 0, 0), report);
        Assert.False(lease.IsValid);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement));
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task SecondRecoveryHelpsPublishedRecoveringAndDelayedWinnerCannotDamageReuse()
    {
        using var scheduler = new ControlledLockFreeScheduler();
        using var store = CreateInstrumentedStore(scheduler, slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        scheduler.PauseAt(LockFreeCheckpointId.RecoveryAfterExactRecoveryCas);

        LeaseRecoveryReport winnerReport = default;
        var winner = Task.Run(() => store.TryRecoverLeases(
            new LeaseRecoveryOptions(true),
            out winnerReport));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var helperReport));
        Assert.Equal(new LeaseRecoveryReport(1, 0, 0, 0, 0), helperReport);
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement));

        scheduler.Continue();
        Assert.Equal(StoreStatus.Success, await winner.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(new LeaseRecoveryReport(1, 1, 0, 0, 0), winnerReport);
        Assert.False(lease.IsValid);
        Assert.True(replacement.IsValid);
        Assert.Equal(7, replacement.ValueSpan[0]);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public void ReportsLiveAndRecoveredRecordsAndRestoresEveryLeaseRecord()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var first));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var second));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var active));
        Assert.Equal(new LeaseRecoveryReport(2, 0, 2, 0, 0), active);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var recovered));
        Assert.Equal(new LeaseRecoveryReport(2, 2, 0, 0, 0), recovered);
        Assert.False(first.IsValid);
        Assert.False(second.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement1));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var replacement2));
        Assert.Equal(StoreStatus.Success, replacement1.Release());
        Assert.Equal(StoreStatus.Success, replacement2.Release());
    }

    [Fact]
    public void QuiescentCurrentProcessOverrideRecoversLeasesFromEveryLocalHandle()
    {
        const int slotCount = 1;
        const int leaseRecordCount = 2;
        string name = $"sms-v2-quiescent-lease-recovery-{Guid.NewGuid():N}";
        using var firstStore = CreateStore(name, slotCount, leaseRecordCount, OpenMode.CreateNew);
        Assert.Equal(
            StoreOpenStatus.Success,
            MemoryStore.TryCreateOrOpen(
                Options(name, slotCount, leaseRecordCount, OpenMode.OpenExisting),
                out MemoryStore? opened));
        using MemoryStore secondStore = Assert.IsType<MemoryStore>(opened);

        Assert.Equal(StoreStatus.Success, firstStore.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, firstStore.TryAcquire([1], out var first));
        Assert.Equal(StoreStatus.Success, secondStore.TryAcquire([1], out var second));
        Assert.Equal(7, first.ValueSpan[0]);
        Assert.Equal(7, second.ValueSpan[0]);

        // From this boundary until recovery returns, acquisition, projection,
        // borrowed-span use, and release are quiescent on both local handles.
        Assert.Equal(
            StoreStatus.Success,
            firstStore.TryRecoverLeases(
                new LeaseRecoveryOptions(RecoverCurrentProcessLeases: true),
                StoreWaitOptions.Infinite,
                out LeaseRecoveryReport report));
        Assert.Equal(new LeaseRecoveryReport(2, 2, 0, 0, 0), report);
        Assert.False(first.IsValid);
        Assert.False(second.IsValid);
    }

    [Fact]
    public void RecoveryTriggersFreshCooperativeReclaimForRemoveRequestedBinding()
    {
        using var store = CreateStore(slotCount: 1, leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(StoreStatus.RemovePending, store.TryRemove([1]));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report));
        Assert.Equal(1, report.RecoveredLeaseCount);
        Assert.False(lease.IsValid);

        // If recovery merely recycled the lease, NoWait would observe the still
        // published removal and return RemovePending. NotFound proves recovery's
        // reclaimer completed the exact removed binding.
        Assert.Equal(StoreStatus.NotFound, store.TryRemove([1], StoreWaitOptions.NoWait));
        Assert.Equal(StoreStatus.Success, store.TryPublish([2], [9]));
    }

    [Fact]
    public void StaleOwnerRecoveryRetiresParticipantAndRestoresParticipantCapacity()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return;
        }

        const int slotCount = 1;
        const int leaseRecordCount = 1;
        var name = $"sms-v2-lease-retirement-{Guid.NewGuid():N}";
        using var store = CreateStore(name, slotCount, leaseRecordCount, OpenMode.CreateNew);
        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7], [3]));

        var readyFile = Path.Combine(Path.GetTempPath(), $"sms-ready-{Guid.NewGuid():N}");
        var continueFile = Path.Combine(Path.GetTempPath(), $"sms-continue-{Guid.NewGuid():N}");
        using var owner = StartLeaseOwner(
            name,
            slotCount,
            leaseRecordCount,
            readyFile,
            continueFile);
        try
        {
            Assert.True(WaitForFile(readyFile, TimeSpan.FromSeconds(10)));

            Assert.Equal(
                StoreStatus.Success,
                store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var live));
            Assert.Equal(new LeaseRecoveryReport(1, 0, 1, 0, 0), live);

            var existing = Options(name, slotCount, leaseRecordCount, OpenMode.OpenExisting);
            Assert.Equal(
                StoreOpenStatus.ParticipantTableFull,
                MemoryStore.TryCreateOrOpen(existing, out var rejected));
            Assert.Null(rejected);

            owner.Kill(entireProcessTree: true);
            Assert.True(owner.WaitForExit(10_000));

            Assert.Equal(
                StoreStatus.Success,
                store.TryRecoverLeases(new LeaseRecoveryOptions(false), out var report));
            Assert.Equal(new LeaseRecoveryReport(1, 1, 0, 0, 0), report);

            Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(existing, out var replacement));
            using var opened = Assert.IsType<MemoryStore>(replacement);
        }
        finally
        {
            TryStop(owner);
            File.Delete(readyFile);
            File.Delete(continueFile);
        }
    }

    private static MemoryStore CreateInstrumentedStore(
        ControlledLockFreeScheduler scheduler,
        int slotCount,
        int leaseRecordCount)
    {
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            Options($"sms-v2-lease-recovery-{Guid.NewGuid():N}", slotCount, leaseRecordCount),
            scheduler.CreateInstrumentedCheckpoint(),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static MemoryStore CreateStore(int slotCount, int leaseRecordCount)
    {
        return CreateStore(
            $"sms-v2-lease-recovery-{Guid.NewGuid():N}",
            slotCount,
            leaseRecordCount,
            OpenMode.CreateNew);
    }

    private static MemoryStore CreateStore(
        string name,
        int slotCount,
        int leaseRecordCount,
        OpenMode openMode)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(
            Options(name, slotCount, leaseRecordCount, openMode),
            out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static SharedMemoryStoreOptions Options(
        string name,
        int slotCount,
        int leaseRecordCount,
        OpenMode openMode = OpenMode.CreateNew) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 2,
            openMode,
            enableLeaseRecovery: true);

    private static Process StartLeaseOwner(
        string name,
        int slotCount,
        int leaseRecordCount,
        string readyFile,
        string continueFile)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
        {
            "exec",
            LocateAgentAssembly(),
            "lease-hold",
            name,
            slotCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "16",
            "4",
            "8",
            leaseRecordCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "2",
            "01",
            "07",
            "03",
            readyFile,
            continueFile
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the lock-free lease owner agent.");
    }

    private static string LocateAgentAssembly()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        var path = Path.Combine(
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

    private static bool WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var spin = new SpinWait();
        while (!File.Exists(path))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return false;
            }

            spin.SpinOnce();
        }

        return true;
    }

    private static void TryStop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch
        {
        }
    }
}
