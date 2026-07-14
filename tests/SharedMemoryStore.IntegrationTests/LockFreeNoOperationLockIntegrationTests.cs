using System.Buffers;
using System.Runtime.InteropServices;
using SharedMemoryStore.IntegrationTests.TestSupport;
using SharedMemoryStore.Engines;
using SharedMemoryStore.Interop;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;

namespace SharedMemoryStore.IntegrationTests;

public sealed class LockFreeNoOperationLockIntegrationTests
{
    private const int SlotCount = 12;

    [Fact]
    [Trait("Category", "Integration")]
    public void CompleteSteadyStateSurfaceNeverReentersTheColdSynchronization()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        SharedMemoryStoreOptions options = Options(
            $"sms-v2-counting-sync-{Guid.NewGuid():N}",
            OpenMode.CreateNew);
        Assert.Equal(
            StoreOpenStatus.Success,
            SharedStorePlatform.TryOpenRegion(
                options,
                StoreWaitOptions.Default,
                out MemoryMappedStoreRegion? region));
        Assert.NotNull(region);

        var synchronization = new CountingThrowingSynchronization();
        Assert.Equal(
            StoreStatus.Success,
            synchronization.TryEnter(StoreWaitOptions.Default));
        IStoreEngine? engine;
        StoreOpenStatus open;
        using (var openScope = new SharedStoreOpenScope(
                   region!,
                   synchronization,
                   outerLifecycleGate: null,
                   RegionOpenDisposition.CreatedNew))
        {
            open = LockFreeStoreEngine.TryCreateOrOpenUnderColdGate(
                options,
                StoreWaitOptions.Default,
                System.Diagnostics.Stopwatch.GetTimestamp(),
                region!,
                synchronization,
                RegionOpenDisposition.CreatedNew,
                out engine);
            if (open == StoreOpenStatus.Success && engine is not null)
            {
                openScope.TransferResourceOwnership();
            }
        }

        Assert.Equal(StoreOpenStatus.Success, open);
        Assert.NotNull(engine);
        var store = new MemoryStore(engine!);

        Assert.Equal(1, synchronization.EnterCount);
        Assert.Equal(1, synchronization.ExitCount);
        synchronization.ThrowOnEnter = true;

        try
        {
            ExerciseCompleteSteadyStateSurface(store, new StoreWaitOptions(TimeSpan.FromMilliseconds(250)));

            Assert.Equal(1, synchronization.EnterCount);
            Assert.Equal(1, synchronization.ExitCount);
        }
        finally
        {
            // Participant unregistration is an exact record-local CAS/help
            // lifecycle and must not re-enter the mapping-initialization lock.
            synchronization.ThrowOnEnter = false;
            store.Dispose();
        }

        Assert.Equal(1, synchronization.EnterCount);
        Assert.Equal(1, synchronization.ExitCount);
        Assert.Equal(1, synchronization.DisposeCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void FailedColdOpenScopeReleasesGatesBeforeMappedOwnerCleanupExactlyOnce()
    {
        var events = new List<string>();
        var synchronization = new RecordingSynchronization(events);
        var outerLifecycle = new RecordingDisposable(events, "lifecycle-exit");
        Assert.Equal(
            StoreStatus.Success,
            synchronization.TryEnter(StoreWaitOptions.NoWait));

        var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateNew(
            mapName: null,
            capacity: 4096,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
        var accessor = mapping.CreateViewAccessor(
            0,
            4096,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.ReadWrite);
        MemoryMappedStoreRegion region = MemoryMappedStoreRegion.Create(
            mapping,
            accessor,
            () => events.Add("region-owner-cleanup"));
        var scope = new SharedStoreOpenScope(
            region,
            synchronization,
            outerLifecycle,
            RegionOpenDisposition.CreatedNew);

        scope.Dispose();
        scope.Dispose();

        Assert.Equal(
            ["enter", "exit", "lifecycle-exit", "region-owner-cleanup", "sync-dispose"],
            events);
        Assert.Equal(1, synchronization.EnterCount);
        Assert.Equal(1, synchronization.ExitCount);
        Assert.Equal(1, synchronization.DisposeCount);
        Assert.Equal(1, outerLifecycle.DisposeCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task HeldLegacySynchronizationDoesNotDelayAnySteadyStateOperation()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        SharedMemoryStoreOptions options = Options(
            $"sms-v2-held-legacy-sync-{Guid.NewGuid():N}",
            OpenMode.CreateNew);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out MemoryStore? candidate));
        using MemoryStore store = Assert.IsType<MemoryStore>(candidate);
        using var blocker = new NamedSynchronizationBlocker(options.Name);

        Task operation = Task.Run(() => ExerciseCompleteSteadyStateSurface(store, StoreWaitOptions.NoWait));
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
    }

    internal static void ExerciseCompleteSteadyStateSurface(MemoryStore store, StoreWaitOptions wait)
    {
        byte[] simpleKey = Key(1);
        byte[] segmentedKey = Key(2);
        byte[] committedReservationKey = Key(3);
        byte[] abortedReservationKey = Key(4);
        byte[] disposedReservationKey = Key(5);
        byte[] disposedLeaseKey = Key(6);

        Assert.Equal(StoreStatus.Success, store.TryPublish(simpleKey, [11, 12], [13], wait));

        var segmentedPayload = new ReadOnlySequence<byte>(new byte[] { 21, 22, 23 });
        Assert.Equal(
            StoreStatus.Success,
            store.TryPublishSegments(segmentedKey, segmentedPayload, [24], wait, out long copiedBytes));
        Assert.Equal(3, copiedBytes);

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(committedReservationKey, 2, [31], wait, out ValueReservation committed));
        Assert.True(committed.IsValid);
        Assert.Equal(2, committed.PayloadLength);
        Assert.Equal(0, committed.BytesWritten);
        Span<byte> writable = committed.GetSpan(2);
        Assert.Equal(2, writable.Length);
        writable[0] = 32;
        committed.DangerousGetMemory(1).Span[1] = 33;
        Assert.Equal(StoreStatus.Success, committed.Advance(2, wait));
        Assert.Equal(2, committed.BytesWritten);
        Assert.Equal(StoreStatus.Success, committed.Commit(wait));

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(abortedReservationKey, 1, default, wait, out ValueReservation aborted));
        aborted.GetSpan()[0] = 41;
        Assert.Equal(StoreStatus.Success, aborted.Abort(wait));

        Assert.Equal(
            StoreStatus.Success,
            store.TryReserve(disposedReservationKey, 1, default, wait, out ValueReservation disposedReservation));
        disposedReservation.Dispose();
        Assert.False(disposedReservation.IsValid);

        Assert.Equal(StoreStatus.Success, store.TryAcquire(simpleKey, wait, out ValueLease lease));
        Assert.True(lease.IsValid);
        Assert.Equal(2, lease.ValueLength);
        Assert.Equal(1, lease.DescriptorLength);
        Assert.Equal(new byte[] { 11, 12 }, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 13 }, lease.DescriptorSpan.ToArray());
        Assert.Equal(StoreStatus.Success, lease.Release(wait));

        Assert.Equal(StoreStatus.Success, store.TryPublish(disposedLeaseKey, [61], default, wait));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(disposedLeaseKey, wait, out ValueLease disposedLease));
        disposedLease.Dispose();
        Assert.False(disposedLease.IsValid);
        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(Key(99), wait, out _));

        AssertRemovalAndReclaim(store, simpleKey, wait);
        AssertRemovalAndReclaim(store, segmentedKey, wait);
        AssertRemovalAndReclaim(store, committedReservationKey, wait);
        AssertRemovalAndReclaim(store, disposedLeaseKey, wait);

        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverLeases(new LeaseRecoveryOptions(true), wait, out LeaseRecoveryReport leaseReport));
        Assert.Equal(0, leaseReport.RecoveredLeaseCount);
        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(
                new ReservationRecoveryOptions(true),
                wait,
                out ReservationRecoveryReport reservationReport));
        Assert.Equal(0, reservationReport.RecoveredReservationCount);

        Assert.Equal(StoreStatus.Success, store.TryGetDiagnostics(wait, out var diagnostics));
        Assert.Equal(StoreProfile.LockFree, diagnostics.Profile);
        Assert.Equal(0, diagnostics.ActiveLeaseCount);
        Assert.Equal(0, diagnostics.ActiveReservationCount);
        Assert.Equal(0, diagnostics.PublishedSlotCount);
        Assert.Equal(0, diagnostics.PendingRemovalCount);
        Assert.Equal(SlotCount, diagnostics.FreeSlotCount);
        Assert.Equal(StoreProfile.LockFree, store.GetDiagnostics().Profile);
    }

    private static void AssertRemovalAndReclaim(
        MemoryStore store,
        byte[] key,
        StoreWaitOptions selectedWait)
    {
        StoreStatus first = store.TryRemove(key, selectedWait);
        Assert.Contains(first, new[] { StoreStatus.Success, StoreStatus.RemovePending });
        if (first == StoreStatus.RemovePending)
        {
            StoreStatus helped = store.TryRemove(key, new StoreWaitOptions(TimeSpan.FromMilliseconds(250)));
            Assert.Contains(helped, new[] { StoreStatus.Success, StoreStatus.NotFound });
        }

        Assert.Equal(StoreStatus.NotFound, store.TryAcquire(key, out _));
    }

    private static SharedMemoryStoreOptions Options(string name, OpenMode openMode) =>
        SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount: SlotCount,
            maxValueBytes: 64,
            maxDescriptorBytes: 16,
            maxKeyBytes: 8,
            leaseRecordCount: 16,
            participantRecordCount: 4,
            openMode,
            enableLeaseRecovery: true);

    private static byte[] Key(int value) => BitConverter.GetBytes(value);

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && LayoutV2Constants.IsSupportedArchitecture(RuntimeInformation.ProcessArchitecture);

    private sealed class CountingThrowingSynchronization : ISharedStoreSynchronization
    {
        private int _enterCount;
        private int _exitCount;
        private int _disposeCount;

        internal bool ThrowOnEnter { get; set; }

        internal int EnterCount => Volatile.Read(ref _enterCount);

        internal int ExitCount => Volatile.Read(ref _exitCount);

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public StoreStatus TryEnter(StoreWaitOptions waitOptions)
        {
            Interlocked.Increment(ref _enterCount);
            if (ThrowOnEnter)
            {
                throw new InvalidOperationException(
                    "A layout-v2 steady-state operation attempted to enter cold synchronization.");
            }

            return StoreStatus.Success;
        }

        public void Exit() => Interlocked.Increment(ref _exitCount);

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class RecordingSynchronization : ISharedStoreSynchronization
    {
        private readonly List<string> _events;
        private int _enterCount;
        private int _exitCount;
        private int _disposeCount;

        internal RecordingSynchronization(List<string> events)
        {
            _events = events;
        }

        internal int EnterCount => _enterCount;

        internal int ExitCount => _exitCount;

        internal int DisposeCount => _disposeCount;

        public StoreStatus TryEnter(StoreWaitOptions waitOptions)
        {
            _enterCount++;
            _events.Add("enter");
            return StoreStatus.Success;
        }

        public void Exit()
        {
            _exitCount++;
            _events.Add("exit");
        }

        public void Dispose()
        {
            _disposeCount++;
            _events.Add("sync-dispose");
        }
    }

    private sealed class RecordingDisposable : IDisposable
    {
        private readonly List<string> _events;
        private readonly string _event;

        internal RecordingDisposable(List<string> events, string @event)
        {
            _events = events;
            _event = @event;
        }

        internal int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            _events.Add(_event);
        }
    }

    private sealed class NamedSynchronizationBlocker : IDisposable
    {
        private readonly string _storeName;
        private readonly ManualResetEventSlim _ready = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Thread _thread;
        private Exception? _failure;

        internal NamedSynchronizationBlocker(string storeName)
        {
            _storeName = storeName;
            _thread = new Thread(Hold)
            {
                IsBackground = true,
                Name = "SharedMemoryStore legacy synchronization blocker"
            };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)), "The legacy synchronization was not acquired.");
            if (_failure is not null)
            {
                throw new Xunit.Sdk.XunitException(
                    $"The legacy synchronization blocker failed: {_failure}");
            }
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)), "The legacy synchronization blocker did not stop.");
            _ready.Dispose();
            _release.Dispose();
            if (_failure is not null)
            {
                throw new Xunit.Sdk.XunitException(
                    $"The legacy synchronization blocker failed: {_failure}");
            }
        }

        private void Hold()
        {
            try
            {
                using IDisposable synchronization = PlatformCapabilityProbe.HoldStoreSynchronization(_storeName);
                _ready.Set();
                _release.Wait();
            }
            catch (Exception exception)
            {
                _failure = exception;
                _ready.Set();
            }
        }
    }
}
