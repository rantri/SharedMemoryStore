using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeDiagnosticsIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BoundedSnapshotsRunAlongsideUnrelatedDataProgress()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        string name = $"sms-v2-live-diagnostics-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions create = Options(name, OpenMode.CreateNew);
        SharedMemoryStoreOptions open = Options(name, OpenMode.OpenExisting);
        using MemoryStore writer = Open(create);
        using MemoryStore observer = Open(open);
        var failures = new ConcurrentQueue<string>();

        Task data = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 2_000; iteration++)
            {
                byte[] key = BitConverter.GetBytes(iteration);
                StoreStatus publish = writer.TryPublish(key, key);
                if (publish != StoreStatus.Success)
                {
                    failures.Enqueue($"publish[{iteration}]={publish}");
                    return;
                }

                StoreStatus acquire = observer.TryAcquire(key, out ValueLease lease);
                if (acquire != StoreStatus.Success)
                {
                    failures.Enqueue($"acquire[{iteration}]={acquire}");
                    return;
                }

                if (!lease.ValueSpan.SequenceEqual(key))
                {
                    failures.Enqueue($"payload[{iteration}]");
                    return;
                }

                if (lease.Release() != StoreStatus.Success)
                {
                    failures.Enqueue($"release[{iteration}]");
                    return;
                }

                StoreStatus remove = writer.TryRemove(key);
                if (remove != StoreStatus.Success)
                {
                    failures.Enqueue($"remove[{iteration}]={remove}");
                    return;
                }
            }
        });

        Task scans = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 500; iteration++)
            {
                Assert.Equal(StoreStatus.Success, observer.TryGetDiagnostics(out DiagnosticsSnapshot snapshot));
                Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), snapshot.ProtocolInfo);
                Assert.Equal(observer.ProtocolInfo, snapshot.ProtocolInfo);
                Assert.Equal(32, snapshot.SlotCount);
                Assert.Equal(4, snapshot.ParticipantRecordCount);
                Assert.InRange(snapshot.ActiveParticipantCount, 1, 4);
                Assert.Equal(
                    snapshot.SlotCount,
                    snapshot.FreeSlotCount
                        + snapshot.InitializingSlotCount
                        + snapshot.ReservedSlotCount
                        + snapshot.PublishedSlotCount
                        + snapshot.PendingRemovalCount
                        + snapshot.ReclaimingSlotCount
                        + snapshot.RetiredSlotCount);
            }
        });

        await Task.WhenAll(data, scans).WaitAsync(TestTimeout);
        Assert.Empty(failures);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PublicOperationsFeedLocalFailureTokenAbortAndRecoveryCounters()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using MemoryStore store = Open(Options(
            $"sms-v2-diagnostics-counters-{Guid.NewGuid():N}",
            OpenMode.CreateNew));

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.DuplicateKey, store.TryPublish([1], [8]));
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire([2], out _));

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out ValueLease lease));
        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.LeaseAlreadyReleased, lease.Release());

        Assert.Equal(StoreStatus.Success, store.TryReserve([3], 1, [], out ValueReservation reservation));
        Assert.Equal(StoreStatus.Success, reservation.Abort());
        Assert.Equal(StoreStatus.InvalidReservation, reservation.Abort());

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), out LeaseRecoveryReport leaseRecovery));
        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                out ReservationRecoveryReport reservationRecovery));

        DiagnosticsSnapshot snapshot = store.GetDiagnostics();
        Assert.True(snapshot.GetFailureCount(StoreStatus.DuplicateKey) >= 1);
        Assert.True(snapshot.GetFailureCount(StoreStatus.NotFound) >= 1);
        Assert.True(snapshot.GetFailureCount(StoreStatus.LeaseAlreadyReleased) >= 1);
        Assert.True(snapshot.GetFailureCount(StoreStatus.InvalidReservation) >= 1);
        Assert.True(snapshot.StaleTokenCount >= 1);
        Assert.True(snapshot.InvalidTokenCount >= 1);
        Assert.True(snapshot.AbortedReservationCount >= 1);
        Assert.True(snapshot.RecoveryAttemptCount >= leaseRecovery.ScannedRecordCount);
        Assert.True(snapshot.RecoveryAttemptCount >= reservationRecovery.ScannedReservationCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExactContentionHelpingAndOwnerClassificationFeedSharedTelemetrySink()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var paused = new ManualResetEventSlim(initialState: false);
        using var resume = new ManualResetEventSlim(initialState: false);
        InstrumentedLockFreeCheckpoint checkpoint = LockFreeCheckpointFactory.CreateInstrumented(entry =>
        {
            if (entry.Id != LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas)
            {
                return;
            }

            paused.Set();
            resume.Wait();
        });
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.Create(
            $"sms-v2-diagnostics-telemetry-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus open = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            checkpoint,
            out MemoryStore? candidate);
        Assert.Equal(StoreOpenStatus.Success, open);
        using MemoryStore store = Assert.IsType<MemoryStore>(candidate);

        Assert.Equal(StoreStatus.Success, store.TryPublish([1], [7]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out ValueLease first));
        Task<StoreStatus> delayedRelease = Task.Run(() => first.Release(StoreWaitOptions.Infinite));
        Assert.True(paused.Wait(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryAcquire([1], StoreWaitOptions.Infinite, out ValueLease replacement));
        resume.Set();
        Assert.Equal(StoreStatus.Success, await delayedRelease.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(false), out LeaseRecoveryReport report));
        Assert.Equal(1, report.ActiveLeaseCount);

        DiagnosticsSnapshot snapshot = store.GetDiagnostics();
        Assert.True(snapshot.CasRetryCount > 0);
        Assert.True(snapshot.HelpedTransitionCount > 0);
        Assert.True(snapshot.CurrentOwnerClassificationCount > 0);
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode mode) =>
        SharedMemoryStoreOptions.Create(
            name,
            slotCount: 32,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 64,
            participantRecordCount: 4,
            openMode: mode,
            enableLeaseRecovery: true);

    private static MemoryStore Open(in SharedMemoryStoreOptions options)
    {
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static bool IsSupportedLockFreeHost() =>
        Environment.Is64BitProcess
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
