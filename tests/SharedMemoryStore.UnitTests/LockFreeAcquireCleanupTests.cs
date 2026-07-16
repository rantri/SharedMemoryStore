using System.Reflection;
using System.Runtime.InteropServices;
using SharedMemoryStore.Interop;
using SharedMemoryStore.Layout;
using SharedMemoryStore.LayoutV2;
using SharedMemoryStore.LockFree;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeAcquireCleanupTests
{
    [Fact]
    public async Task DeadlineAfterLeaseActivationReturnsBusyAndRecyclesProvisionalLease()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateStore(scheduler);
        byte[] key = [1];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [7]));
        scheduler.PauseAt(LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup);

        TimeSpan operationTimeout = TimeSpan.FromSeconds(2);
        ValueLease observed = default;
        Task<StoreStatus> acquire = StartLongRunning(() => store.TryAcquire(
            key,
            new StoreWaitOptions(operationTimeout),
            out observed));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        await Task.Delay(operationTimeout + TimeSpan.FromMilliseconds(200));
        scheduler.Continue();

        Assert.Equal(StoreStatus.StoreBusy, await acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(observed.IsValid);
        AssertLeaseRecordWasRecycled(store);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease replacement));
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task CancellationAfterLeaseActivationReturnsCanceledAndRecyclesProvisionalLease()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateStore(scheduler);
        byte[] key = [2];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [8]));
        using var cancellation = new CancellationTokenSource();
        scheduler.PauseAt(LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup);

        ValueLease observed = default;
        Task<StoreStatus> acquire = StartLongRunning(() => store.TryAcquire(
            key,
            new StoreWaitOptions(TimeSpan.FromSeconds(10), cancellation.Token),
            out observed));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        cancellation.Cancel();
        scheduler.Continue();

        Assert.Equal(StoreStatus.OperationCanceled, await acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(observed.IsValid);
        AssertLeaseRecordWasRecycled(store);
        Assert.Equal(StoreStatus.Success, store.TryAcquire(key, out ValueLease replacement));
        Assert.Equal(StoreStatus.Success, replacement.Release());
    }

    [Fact]
    public async Task CorruptExactDirectoryCellAfterLeaseActivationPropagatesCorruptionAndRecyclesLease()
    {
        if (!IsSupportedHost())
        {
            return;
        }

        using var scheduler = new ControlledLockFreeScheduler();
        using MemoryStore store = CreateStore(scheduler);
        byte[] key = [3];
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, [9]));
        DirectoryLocation location = LocateExactDirectoryCell(store, key);
        scheduler.PauseAt(LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup);

        ValueLease observed = default;
        Task<StoreStatus> acquire = StartLongRunning(() => store.TryAcquire(
            key,
            StoreWaitOptions.Infinite,
            out observed));
        Assert.True(scheduler.WaitUntilPaused(TimeSpan.FromSeconds(5)));
        CorruptDirectoryCell(store, location);
        scheduler.Continue();

        Assert.Equal(StoreStatus.CorruptStore, await acquire.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(observed.IsValid);
        AssertLeaseRecordWasRecycled(store);
    }

    private static void AssertLeaseRecordWasRecycled(MemoryStore store)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        LockFreeLeaseRegistry leases = ReadPrivate<LockFreeLeaseRegistry>(engine, "_leases");
        long control = AtomicControlWord.LoadAcquire(ref leases.Record(0).Control);
        Assert.Equal(LockFreeLeaseRegistry.FreeState, (int)((ulong)control & 0x7UL));
    }

    private static Task<StoreStatus> StartLongRunning(Func<StoreStatus> operation) =>
        Task.Factory.StartNew(
            operation,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    private static DirectoryLocation LocateExactDirectoryCell(MemoryStore store, byte[] key)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        LockFreeKeyDirectory directory = ReadPrivate<LockFreeKeyDirectory>(engine, "_directory");
        Assert.Equal(
            StoreStatus.Success,
            directory.TryLookup(key, StoreKey.Hash(key), out _, out DirectoryLocation location));
        return location;
    }

    private static unsafe void CorruptDirectoryCell(MemoryStore store, DirectoryLocation location)
    {
        object engine = ReadPrivate<object>(store, "_engine");
        MemoryMappedStoreRegion region = ReadPrivate<MemoryMappedStoreRegion>(engine, "_region");
        StoreLayoutV2 layout = ReadPrivate<StoreLayoutV2>(engine, "_layout");
        long offset = location.Kind switch
        {
            1 => layout.PrimaryDirectoryOffset
                + ((location.Index / LayoutV2Constants.PrimaryLanesPerBucket) * layout.PrimaryBucketStride)
                + 16
                + ((location.Index % LayoutV2Constants.PrimaryLanesPerBucket) * sizeof(long)),
            2 => layout.OverflowDirectoryOffset + (location.Index * layout.OverflowStride),
            _ => throw new InvalidOperationException("The exact binding has an invalid directory kind.")
        };

        ref long cell = ref *(long*)(region.Pointer + offset);
        // Generation one with a zero encoded index is structurally impossible;
        // it must be reported as corruption, not treated as a stale binding.
        AtomicControlWord.StoreRelease(ref cell, unchecked((long)(1UL << 31)));
    }

    private static T ReadPrivate<T>(object owner, string fieldName)
    {
        FieldInfo field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {owner.GetType().FullName}.{fieldName}.");
        return Assert.IsAssignableFrom<T>(field.GetValue(owner));
    }

    private static MemoryStore CreateStore(ControlledLockFreeScheduler scheduler)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-acquire-cleanup-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 16,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 2,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = LockFreeInstrumentedStoreFactory.TryCreateOrOpen(
            options,
            scheduler.CreateInstrumentedCheckpoint(),
            out MemoryStore? store);
        Assert.Equal(StoreOpenStatus.Success, status);
        return Assert.IsType<MemoryStore>(store);
    }

    private static bool IsSupportedHost() =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        && RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
