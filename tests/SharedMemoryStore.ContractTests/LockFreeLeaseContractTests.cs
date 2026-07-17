using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreeLeaseContractTests
{
    [Fact]
    public void SharedLeasesProjectExactImmutableDescriptorAndPayloadBytes()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(leaseRecordCount: 4);
        byte[] payload = Enumerable.Range(0, 64).Select(static value => (byte)value).ToArray();
        byte[] descriptor = [0xa1, 0xb2, 0xc3];
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x11], payload, descriptor));

        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x11], out var first));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x11], out var second));
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(payload.Length, first.ValueLength);
        Assert.Equal(descriptor.Length, first.DescriptorLength);
        Assert.True(first.ValueSpan.SequenceEqual(payload));
        Assert.True(first.DescriptorSpan.SequenceEqual(descriptor));
        Assert.True(second.ValueSpan.SequenceEqual(payload));
        Assert.True(second.DescriptorSpan.SequenceEqual(descriptor));

        var copiedFirst = first;
        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.False(first.IsValid);
        Assert.True(first.ValueSpan.IsEmpty);
        Assert.True(first.DescriptorSpan.IsEmpty);
        Assert.Contains(
            copiedFirst.Release(),
            new[] { StoreStatus.LeaseAlreadyReleased, StoreStatus.InvalidLease });

        // Releasing one shared lease cannot invalidate another lease over the
        // same immutable generation.
        Assert.True(second.IsValid);
        Assert.True(second.ValueSpan.SequenceEqual(payload));
        Assert.Equal(StoreStatus.Success, second.Release());
    }

    [Fact]
    public void NormalRecoveryPreservesLiveCurrentProcessLeasesAcrossHandles()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(leaseRecordCount: 4);
        using MemoryStore attached = OpenExisting(owned.Name, leaseRecordCount: 4);
        byte[] payload = [0x61, 0x62, 0x63];
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x61], payload));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x61], out var first));
        Assert.Equal(StoreStatus.Success, attached.TryAcquire([0x61], out var second));

        // False is the normal concurrently safe policy. It must preserve live
        // current-process records regardless of which local handle created them.
        Assert.True(first.ValueSpan.SequenceEqual(payload));
        Assert.True(second.ValueSpan.SequenceEqual(payload));
        Assert.Equal(
            StoreStatus.Success,
            owned.Store.TryRecoverLeases(
                new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
                out LeaseRecoveryReport report));
        Assert.Equal(0, report.RecoveredLeaseCount);
        Assert.Equal(2, report.ActiveLeaseCount);
        Assert.True(first.IsValid);
        Assert.True(second.IsValid);
        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.Equal(StoreStatus.Success, second.Release());
    }

    [Fact]
    public void MissingAndExhaustedLeaseOutcomesReturnInvalidTokensAndReleasedCapacityIsReusable()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(leaseRecordCount: 2);
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), owned.Store.ProtocolInfo);
        Assert.Equal(2, owned.Store.ProtocolInfo.LayoutMajorVersion);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x21], [1, 2, 3]));

        Assert.Equal(StoreStatus.NotFound, owned.Store.TryAcquire([0xff], out var missing));
        Assert.False(missing.IsValid);
        Assert.Equal(StoreStatus.InvalidLease, missing.Release());

        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x21], out var first));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x21], out var second));
        Assert.Equal(StoreStatus.LeaseTableFull, owned.Store.TryAcquire([0x21], out var exhausted));
        Assert.False(exhausted.IsValid);

        Assert.Equal(StoreStatus.Success, first.Release());
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x21], out var replacement));
        Assert.True(replacement.IsValid);
        Assert.Equal(StoreStatus.Success, second.Release());
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public void PreCanceledAcquireLeavesNoLeaseClaimAndLaterAcquireSucceeds()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(leaseRecordCount: 1);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x31], [3, 1]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(
            StoreStatus.OperationCanceled,
            owned.Store.TryAcquire(
                [0x31],
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token),
                out var canceled));
        Assert.False(canceled.IsValid);
        Assert.Equal(StoreStatus.InvalidLease, canceled.Release());

        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x31], out var lease));
        Assert.True(lease.ValueSpan.SequenceEqual(new byte[] { 3, 1 }));
        Assert.Equal(StoreStatus.Success, lease.Release());
    }

    [Fact]
    public void DisposingOneHandleInvalidatesItsBorrowedLeaseWithoutExposingMappedMemory()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        var owned = CreateStore(leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x41], [4, 1]));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x41], out var lease));
        Assert.True(lease.IsValid);

        owned.Dispose();

        Assert.False(lease.IsValid);
        Assert.Equal(0, lease.ValueLength);
        Assert.Equal(0, lease.DescriptorLength);
        Assert.True(lease.ValueSpan.IsEmpty);
        Assert.True(lease.DescriptorSpan.IsEmpty);
        Assert.Contains(lease.Release(), new[] { StoreStatus.StoreDisposed, StoreStatus.InvalidLease });
    }

    [Fact]
    public void AcquireProjectionAndReleaseNeverEnterTheNamedOperationSynchronizer()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(leaseRecordCount: 2);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x51], [5, 1], [9]));
        using var held = new HeldOperationSynchronizer(owned.Name);
        var stopwatch = Stopwatch.StartNew();

        Assert.Equal(
            StoreStatus.Success,
            owned.Store.TryAcquire([0x51], StoreWaitOptions.NoWait, out var lease));
        Assert.True(lease.ValueSpan.SequenceEqual(new byte[] { 5, 1 }));
        Assert.True(lease.DescriptorSpan.SequenceEqual(new byte[] { 9 }));
        Assert.Equal(StoreStatus.Success, lease.Release(StoreWaitOptions.NoWait));

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static OwnedStore CreateStore(int leaseRecordCount)
    {
        string name = $"sms-v2-lease-contract-{Guid.NewGuid():N}";
        var options = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 4,
            maxValueBytes: 128,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return new OwnedStore(name, Assert.IsType<MemoryStore>(store));
    }

    private static MemoryStore OpenExisting(string name, int leaseRecordCount)
    {
        var options = SharedMemoryStoreOptions.Create(
            name,
            slotCount: 4,
            maxValueBytes: 128,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 4,
            openMode: OpenMode.OpenExisting,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static bool IsSupportedLockFreeHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private sealed class OwnedStore(string name, MemoryStore store) : IDisposable
    {
        public string Name { get; } = name;

        public MemoryStore Store { get; } = store;

        public void Dispose() => Store.Dispose();
    }

    private sealed class HeldOperationSynchronizer : IDisposable
    {
        private readonly ManualResetEventSlim _ready = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;
        private Exception? _failure;

        public HeldOperationSynchronizer(string storeName)
        {
            _thread = new Thread(() => Hold(storeName)) { IsBackground = true };
            _thread.Start();
            Assert.True(_ready.Wait(TimeSpan.FromSeconds(5)));
            if (_failure is not null)
            {
                throw new InvalidOperationException("Unable to hold the operation synchronizer.", _failure);
            }
        }

        public void Dispose()
        {
            _release.Set();
            Assert.True(_thread.Join(TimeSpan.FromSeconds(5)));
            _ready.Dispose();
            _release.Dispose();
            if (_failure is not null)
            {
                throw new InvalidOperationException("The synchronization holder failed.", _failure);
            }
        }

        private void Hold(string storeName)
        {
            try
            {
                using var synchronization = SharedStorePlatform.CreateSynchronization(
                    PlatformResourceName.Create(storeName));
                StoreStatus status = synchronization.TryEnter(StoreWaitOptions.Infinite);
                if (status != StoreStatus.Success)
                {
                    throw new InvalidOperationException($"Unable to enter operation synchronizer: {status}.");
                }

                _ready.Set();
                _release.Wait();
                synchronization.Exit();
            }
            catch (Exception error)
            {
                _failure = error;
                _ready.Set();
            }
        }
    }
}
