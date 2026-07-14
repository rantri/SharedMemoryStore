using System.Diagnostics;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeRemoveReuseAllocationTests
{
    private const int MeasuredLifecycleCycles = 1_000_000;
    private const int TieredPgoWarmupCycles = 50_000;

    [Fact]
    public void OneMillionWarmedCompleteLifecycleCyclesAllocateZeroBytesAndRestoreExactCapacity()
    {
        using var store = CreateLockFreeStore();
        var key = new byte[] { 1, 0, 2 };
        var otherKey = new byte[] { 3, 0, 4 };
        var publishedValue = new byte[] { 5, 0, 6, 7 };
        var publishedDescriptor = new byte[] { 8, 0, 9 };
        var reservedValue = new byte[] { 10, 0, 11, 12 };
        var reservedDescriptor = new byte[] { 13, 0, 14 };

        // Warm every successful facade, directory, slot, lease, projection,
        // reclaim, and reuse path far enough for tiered PGO promotion.
        RunCompleteLifecycleCycles(
            store,
            key,
            publishedValue,
            publishedDescriptor,
            reservedValue,
            reservedDescriptor,
            TieredPgoWarmupCycles);
        VerifyExactCapacityRestoration(store, key, otherKey, publishedValue, publishedDescriptor);
        CollectGarbage();

        long started = Stopwatch.GetTimestamp();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunCompleteLifecycleCycles(
            store,
            key,
            publishedValue,
            publishedDescriptor,
            reservedValue,
            reservedDescriptor,
            MeasuredLifecycleCycles);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TimeSpan elapsed = Stopwatch.GetElapsedTime(started);

        Assert.Equal(0, allocated);
        Assert.True(elapsed > TimeSpan.Zero);
        VerifyExactCapacityRestoration(store, key, otherKey, publishedValue, publishedDescriptor);
    }

    private static void RunCompleteLifecycleCycles(
        MemoryStore store,
        byte[] key,
        byte[] publishedValue,
        byte[] publishedDescriptor,
        byte[] reservedValue,
        byte[] reservedDescriptor,
        int cycles)
    {
        for (var index = 0; index < cycles; index++)
        {
            RequireStatus(
                StoreStatus.Success,
                store.TryPublish(key, publishedValue, publishedDescriptor),
                "publish",
                index);
            AcquireProjectRelease(
                store,
                key,
                expectedValueSum: 18,
                expectedDescriptorSum: 17,
                index);
            RequireStatus(StoreStatus.Success, store.TryRemove(key), "remove published", index);

            RequireStatus(
                StoreStatus.Success,
                store.TryReserve(
                    key,
                    reservedValue.Length,
                    reservedDescriptor,
                    out var reservation),
                "reserve",
                index);
            Span<byte> destination = reservation.GetSpan(reservedValue.Length);
            reservedValue.AsSpan().CopyTo(destination);
            RequireStatus(
                StoreStatus.Success,
                reservation.Advance(reservedValue.Length),
                "advance",
                index);
            RequireStatus(StoreStatus.Success, reservation.Commit(), "commit", index);
            AcquireProjectRelease(
                store,
                key,
                expectedValueSum: 33,
                expectedDescriptorSum: 27,
                index);
            RequireStatus(StoreStatus.Success, store.TryRemove(key), "remove reserved", index);
        }
    }

    private static void AcquireProjectRelease(
        MemoryStore store,
        byte[] key,
        int expectedValueSum,
        int expectedDescriptorSum,
        int cycle)
    {
        StoreStatus acquire = store.TryAcquire(key, out var lease);
        if (acquire != StoreStatus.Success
            || !lease.IsValid
            || lease.ValueLength != 4
            || lease.DescriptorLength != 3)
        {
            throw new InvalidOperationException(
                $"Acquire/projection metadata failed during cycle {cycle}: {acquire}.");
        }

        ReadOnlySpan<byte> value = lease.ValueSpan;
        ReadOnlySpan<byte> descriptor = lease.DescriptorSpan;
        if (value.Length != 4
            || value[0] + value[1] + value[2] + value[3] != expectedValueSum
            || descriptor.Length != 3
            || descriptor[0] + descriptor[1] + descriptor[2] != expectedDescriptorSum)
        {
            throw new InvalidOperationException($"Projected bytes failed during cycle {cycle}.");
        }

        StoreStatus release = lease.Release();
        if (release != StoreStatus.Success || lease.IsValid)
        {
            throw new InvalidOperationException(
                $"Lease release failed during cycle {cycle}: {release}.");
        }
    }

    private static void VerifyExactCapacityRestoration(
        MemoryStore store,
        byte[] firstKey,
        byte[] secondKey,
        byte[] value,
        byte[] descriptor)
    {
        // The store has exactly one slot. Filling it must reject a second key;
        // aborting or removing the owner must make exactly that slot reusable.
        RequireStatus(
            StoreStatus.Success,
            store.TryReserve(firstKey, value.Length, descriptor, out var reservation),
            "capacity reserve",
            cycle: -1);
        RequireStatus(
            StoreStatus.StoreFull,
            store.TryPublish(secondKey, value, descriptor),
            "capacity full after reserve",
            cycle: -1);
        RequireStatus(StoreStatus.Success, reservation.Abort(), "capacity abort", cycle: -1);

        RequireStatus(
            StoreStatus.Success,
            store.TryPublish(secondKey, value, descriptor),
            "capacity publish after abort",
            cycle: -1);
        RequireStatus(
            StoreStatus.StoreFull,
            store.TryReserve(firstKey, value.Length, descriptor, out _),
            "capacity full after publish",
            cycle: -1);
        AcquireProjectRelease(
            store,
            secondKey,
            expectedValueSum: 18,
            expectedDescriptorSum: 17,
            cycle: -1);
        RequireStatus(
            StoreStatus.Success,
            store.TryRemove(secondKey),
            "capacity remove",
            cycle: -1);

        RequireStatus(
            StoreStatus.Success,
            store.TryReserve(firstKey, value.Length, descriptor, out var restored),
            "capacity reserve after remove",
            cycle: -1);
        RequireStatus(StoreStatus.Success, restored.Abort(), "capacity final abort", cycle: -1);
    }

    private static MemoryStore CreateLockFreeStore()
    {
        var options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-remove-reuse-allocation-{Guid.NewGuid():N}",
            slotCount: 1,
            maxValueBytes: 8,
            maxDescriptorBytes: 4,
            maxKeyBytes: 8,
            leaseRecordCount: 1,
            participantRecordCount: 1,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        StoreOpenStatus status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<MemoryStore>(store);
        Assert.Equal(StoreProfile.LockFree, result.Profile);
        return result;
    }

    private static void RequireStatus(
        StoreStatus expected,
        StoreStatus actual,
        string operation,
        int cycle)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException(
                $"Expected {expected} from {operation} during cycle {cycle}, received {actual}.");
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
