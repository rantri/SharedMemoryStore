using System.Diagnostics;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;

namespace SharedMemoryStore.ContractTests;

public sealed class LockFreeRemoveContractTests
{
    [Fact]
    public void InfiniteRemoveWithoutLeasesReturnsSuccessAndMakesCapacityReusable()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 1, leaseRecordCount: 4);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x11], [1, 2, 3]));

        Assert.Equal(
            StoreStatus.Success,
            owned.Store.TryRemove([0x11], StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.NotFound, owned.Store.TryAcquire([0x11], out var absent));
        Assert.False(absent.IsValid);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x11], [4, 5, 6]));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x11], out var replacement));
        Assert.True(replacement.ValueSpan.SequenceEqual(new byte[] { 4, 5, 6 }));
        Assert.Equal(StoreStatus.Success, replacement.Release());
        Assert.Equal(StoreStatus.DuplicateKey, owned.Store.TryPublish([0x11], [7]));
    }

    [Fact]
    public void InfiniteRemoveWithActiveLeaseReturnsPendingAndPreservesBytesUntilReusable()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 1, leaseRecordCount: 4);
        byte[] original = [2, 4, 6, 8];
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x21], original, [9]));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x21], out var lease));

        Assert.Equal(
            StoreStatus.RemovePending,
            owned.Store.TryRemove([0x21], StoreWaitOptions.Infinite));
        Assert.Equal(StoreStatus.NotFound, owned.Store.TryAcquire([0x21], out var rejected));
        Assert.False(rejected.IsValid);
        Assert.Equal(StoreStatus.DuplicateKey, owned.Store.TryPublish([0x21], [1]));
        Assert.True(lease.IsValid);
        Assert.True(lease.ValueSpan.SequenceEqual(original));
        Assert.True(lease.DescriptorSpan.SequenceEqual(new byte[] { 9 }));

        Assert.Equal(StoreStatus.Success, lease.Release());
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x21], [1, 3, 5]));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x21], out var replacement));
        Assert.True(replacement.ValueSpan.SequenceEqual(new byte[] { 1, 3, 5 }));
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public void NoWaitAfterLogicalRemovalReturnsConservativePendingAndCooperativeWorkRestoresCapacity()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 1, leaseRecordCount: 8_192);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x31], [3, 1]));

        Assert.Equal(StoreStatus.RemovePending, owned.Store.TryRemove([0x31], StoreWaitOptions.NoWait));
        Assert.Equal(StoreStatus.NotFound, owned.Store.TryAcquire([0x31], out _));

        // The key is already logically absent. A later allocator/helper must be
        // able to finish physical unlink/reclaim without a global maintenance owner.
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x31], [3, 2]));
    }

    [Fact]
    public void PreCanceledRemoveDoesNotCrossLogicalOrderingPointOrLoseTheValue()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 2, leaseRecordCount: 4);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x41], [4, 1]));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(
            StoreStatus.OperationCanceled,
            owned.Store.TryRemove(
                [0x41],
                new StoreWaitOptions(TimeSpan.FromSeconds(1), cancellation.Token)));
        Assert.Equal(StoreStatus.Success, owned.Store.TryAcquire([0x41], out var preserved));
        Assert.True(preserved.ValueSpan.SequenceEqual(new byte[] { 4, 1 }));
        Assert.Equal(StoreStatus.Success, preserved.Release());
    }

    [Fact]
    public void RemoveValidatesKeysAndReturnsStableMissingStatus()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 2, leaseRecordCount: 4, maxKeyBytes: 2);
        Assert.Equal(StoreStatus.InvalidKey, owned.Store.TryRemove([]));
        Assert.Equal(StoreStatus.KeyTooLarge, owned.Store.TryRemove([1, 2, 3]));
        Assert.Equal(StoreStatus.NotFound, owned.Store.TryRemove([1]));
    }

    [Fact]
    public void RemoveNeverEntersTheNamedOperationSynchronizer()
    {
        if (!IsSupportedLockFreeHost())
        {
            return;
        }

        using var owned = CreateStore(slotCount: 2, leaseRecordCount: 4);
        Assert.Equal(StoreStatus.Success, owned.Store.TryPublish([0x51], [5, 1]));
        using var held = new HeldOperationSynchronizer(owned.Name);
        var stopwatch = Stopwatch.StartNew();

        StoreStatus status = owned.Store.TryRemove([0x51], StoreWaitOptions.NoWait);

        stopwatch.Stop();
        Assert.Contains(status, new[] { StoreStatus.Success, StoreStatus.RemovePending });
        Assert.Equal(StoreStatus.NotFound, owned.Store.TryAcquire([0x51], StoreWaitOptions.NoWait, out _));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    private static OwnedStore CreateStore(
        int slotCount,
        int leaseRecordCount,
        int maxKeyBytes = 8)
    {
        string name = $"sms-v2-remove-contract-{Guid.NewGuid():N}";
        var options = SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            maxValueBytes: 64,
            maxDescriptorBytes: 8,
            maxKeyBytes,
            leaseRecordCount,
            participantRecordCount: 4,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus openStatus = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, openStatus);
        return new OwnedStore(name, Assert.IsType<MemoryStore>(store));
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
