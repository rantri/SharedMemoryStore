namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeAcquireAllocationTests
{
    private const int MeasuredAcquireCycles = 1_000_000;
    private const int TieredPgoWarmupCycles = 50_000;

    [Fact]
    public void OneMillionWarmedAcquireProjectReleaseCyclesAllocateZeroBytes()
    {
        using var store = CreateLockFreeStore(leaseRecordCount: 2);
        var key = new byte[] { 1, 0, 2 };
        var value = new byte[] { 3, 0, 4, 5 };
        var descriptor = new byte[] { 6, 0, 7 };
        Assert.Equal(StoreStatus.Success, store.TryPublish(key, value, descriptor));

        // Fifty thousand cycles are intentionally outside the measured million.
        // This lets tiered PGO promote the facade, directory, lease-registry, and
        // projection paths before allocation accounting begins.
        RunSuccessfulCycles(store, key, TieredPgoWarmupCycles);
        CollectGarbage();

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunSuccessfulCycles(store, key, MeasuredAcquireCycles);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WarmExpectedMissAndLeaseTableFullPathsAllocateZeroBytes()
    {
        const int MeasuredFailureCycles = 100_000;
        using var store = CreateLockFreeStore(leaseRecordCount: 1);
        var publishedKey = new byte[] { 1 };
        var missingKey = new byte[] { 2 };
        Assert.Equal(StoreStatus.Success, store.TryPublish(publishedKey, [9], [8]));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(publishedKey, out var held));

        RunExpectedFailures(
            store,
            publishedKey,
            missingKey,
            TieredPgoWarmupCycles);
        CollectGarbage();

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunExpectedFailures(store, publishedKey, missingKey, MeasuredFailureCycles);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(StoreStatus.Success, held.Release());
    }

    private static void RunSuccessfulCycles(MemoryStore store, byte[] key, int cycles)
    {
        for (var index = 0; index < cycles; index++)
        {
            StoreStatus acquire = store.TryAcquire(key, out var lease);
            if (acquire != StoreStatus.Success
                || !lease.IsValid
                || lease.ValueLength != 4
                || lease.DescriptorLength != 3)
            {
                throw new InvalidOperationException(
                    $"Acquire/projection metadata failed at cycle {index}: {acquire}.");
            }

            ReadOnlySpan<byte> value = lease.ValueSpan;
            ReadOnlySpan<byte> descriptor = lease.DescriptorSpan;
            if (value.Length != 4
                || value[0] + value[1] + value[2] + value[3] != 12
                || descriptor.Length != 3
                || descriptor[0] + descriptor[1] + descriptor[2] != 13)
            {
                throw new InvalidOperationException($"Projected bytes failed at cycle {index}.");
            }

            StoreStatus release = lease.Release();
            if (release != StoreStatus.Success || lease.IsValid)
            {
                throw new InvalidOperationException(
                    $"Lease release failed at cycle {index}: {release}.");
            }
        }
    }

    private static void RunExpectedFailures(
        MemoryStore store,
        byte[] publishedKey,
        byte[] missingKey,
        int cycles)
    {
        for (var index = 0; index < cycles; index++)
        {
            StoreStatus missing = store.TryAcquire(missingKey, out var missingLease);
            StoreStatus full = store.TryAcquire(publishedKey, out var fullLease);
            if (missing != StoreStatus.NotFound
                || missingLease.IsValid
                || full != StoreStatus.LeaseTableFull
                || fullLease.IsValid)
            {
                throw new InvalidOperationException(
                    $"Expected miss/full failed at cycle {index}: miss={missing}, full={full}.");
            }
        }
    }

    private static MemoryStore CreateLockFreeStore(int leaseRecordCount)
    {
        var options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-acquire-allocation-{Guid.NewGuid():N}",
            slotCount: 2,
            maxValueBytes: 16,
            maxDescriptorBytes: 8,
            maxKeyBytes: 8,
            leaseRecordCount,
            participantRecordCount: 1,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<MemoryStore>(store);
        Assert.Equal(StoreProfile.LockFree, result.Profile);
        return result;
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
