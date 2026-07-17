using System.Reflection;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreePublishAllocationTests
{
    [Fact]
    public void RetainedMemoryManagersAreSparseAndSpanOnlyHandlesCreateNone()
    {
        using var store = CreateStore(slotCount: 257);
        object engine = typeof(MemoryStore)
            .GetField("_engine", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(store)!;
        object reservationMemory = engine.GetType()
            .GetField("_reservationMemory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(engine)!;
        FieldInfo pagesField = reservationMemory.GetType()
            .GetField("_managerPages", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.Null(pagesField.GetValue(reservationMemory));
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 1, [], out var spanOnly));
        spanOnly.GetSpan()[0] = 1;
        Assert.Null(pagesField.GetValue(reservationMemory));
        Assert.Equal(StoreStatus.Success, spanOnly.Abort());

        Assert.Equal(StoreStatus.Success, store.TryReserve([2], 1, [], out var retained));
        Assert.False(retained.DangerousGetMemory(1).IsEmpty);
        var pages = Assert.IsAssignableFrom<Array>(pagesField.GetValue(reservationMemory));
        Assert.Equal(2, pages.Length);
        Assert.Single(pages.Cast<object?>(), static page => page is not null);
        var populatedPage = Assert.IsAssignableFrom<Array>(pages.Cast<object?>().Single(static page => page is not null));
        Assert.Single(populatedPage.Cast<object?>(), static manager => manager is not null);
        Assert.Equal(StoreStatus.Success, retained.Abort());
    }

    [Fact]
    public void WarmReservationAbortAndReuseAllocateZeroBytes()
    {
        using var store = CreateStore(slotCount: 1);
        var key = new byte[] { 1 };

        // Cross the tiered-PGO promotion threshold before measuring; otherwise
        // the runtime's one-time 24-byte call-counting transition is charged to
        // the library hot path even though subsequent operations allocate zero.
        for (var index = 0; index < 10_000; index++)
        {
            ReserveThenAbort(store, key);
        }

        CollectGarbage();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            ReserveThenAbort(store, key);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void WarmDuplicateReservationAndPublicationFailuresAllocateZeroBytes()
    {
        using var store = CreateStore(slotCount: 2);
        var key = new byte[] { 1 };
        var value = new byte[] { 2 };
        Assert.Equal(StoreStatus.Success, store.TryReserve(key, 1, default, out var owner));

        for (var index = 0; index < 1_000; index++)
        {
            RequireStatus(StoreStatus.DuplicateKey, store.TryReserve(key, 1, default, out _));
            RequireStatus(StoreStatus.DuplicateKey, store.TryPublish(key, value));
        }

        CollectGarbage();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            RequireStatus(StoreStatus.DuplicateKey, store.TryReserve(key, 1, default, out _));
            RequireStatus(StoreStatus.DuplicateKey, store.TryPublish(key, value));
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(StoreStatus.Success, owner.Abort());
    }

    [Fact]
    public void WarmInvalidIncompleteAndStaleReservationFailuresAllocateZeroBytes()
    {
        using var store = CreateStore(slotCount: 1);
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var pending));

        for (var index = 0; index < 1_000; index++)
        {
            RequireStatus(StoreStatus.ReservationWriteOutOfRange, pending.Advance(3));
            RequireStatus(StoreStatus.ReservationIncomplete, pending.Commit());
        }

        CollectGarbage();
        var beforePending = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            RequireStatus(StoreStatus.ReservationWriteOutOfRange, pending.Advance(3));
            RequireStatus(StoreStatus.ReservationIncomplete, pending.Commit());
        }
        var pendingAllocated = GC.GetAllocatedBytesForCurrentThread() - beforePending;
        Assert.Equal(0, pendingAllocated);

        Assert.Equal(StoreStatus.Success, pending.Abort());
        for (var index = 0; index < 1_000; index++)
        {
            RequireStatus(StoreStatus.InvalidReservation, pending.Advance(1));
            RequireStatus(StoreStatus.InvalidReservation, pending.Commit());
            RequireStatus(StoreStatus.InvalidReservation, pending.Abort());
        }

        CollectGarbage();
        var beforeStale = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            RequireStatus(StoreStatus.InvalidReservation, pending.Advance(1));
            RequireStatus(StoreStatus.InvalidReservation, pending.Commit());
            RequireStatus(StoreStatus.InvalidReservation, pending.Abort());
        }
        var staleAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeStale;

        Assert.Equal(0, staleAllocated);
    }

    [Fact]
    public void SuccessfulDirectCommitsAllocateZeroBytesThroughConfiguredCapacity()
    {
        const int SlotCount = 256;

        // Warm every direct-ingest method in a disposable store. Span-only
        // direct ingest does not require retained-memory manager creation.
        using (var warmup = CreateStore(slotCount: 1))
        {
            CommitOne(warmup, 1);
        }

        using var store = CreateStore(slotCount: SlotCount);
        CollectGarbage();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < SlotCount; index++)
        {
            CommitOne(store, index);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        Span<byte> overflowKey = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(overflowKey, SlotCount);
        _ = store.TryReserve(overflowKey, 1, default, out _); // warm the capacity failure
        CollectGarbage();
        var beforeFull = GC.GetAllocatedBytesForCurrentThread();
        var fullStatus = store.TryReserve(overflowKey, 1, default, out _);
        var fullAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeFull;
        Assert.Equal(StoreStatus.StoreFull, fullStatus);
        Assert.Equal(0, fullAllocated);
    }

    private static void ReserveThenAbort(MemoryStore store, byte[] key)
    {
        RequireStatus(StoreStatus.Success, store.TryReserve(key, 1, default, out var reservation));
        RequireStatus(StoreStatus.Success, reservation.Abort());
    }

    private static void CommitOne(MemoryStore store, int id)
    {
        Span<byte> key = stackalloc byte[sizeof(int)];
        BitConverter.TryWriteBytes(key, id);
        RequireStatus(StoreStatus.Success, store.TryReserve(key, 1, default, out var reservation));
        reservation.GetSpan()[0] = unchecked((byte)id);
        RequireStatus(StoreStatus.Success, reservation.Advance(1));
        RequireStatus(StoreStatus.Success, reservation.Commit());
    }

    private static MemoryStore CreateStore(int slotCount)
    {
        var options = SharedMemoryStoreOptions.Create(
            $"sms-v2-publish-allocation-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 8,
            maxDescriptorBytes: 2,
            maxKeyBytes: 8,
            leaseRecordCount: 8,
            participantRecordCount: 1,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        var status = MemoryStore.TryCreateOrOpen(options, out var store);
        Assert.Equal(StoreOpenStatus.Success, status);
        var result = Assert.IsType<MemoryStore>(store);
        Assert.Equal(new StoreProtocolInfo(2, 0, 2, 7, 0), result.ProtocolInfo);
        return result;
    }

    private static void RequireStatus(StoreStatus expected, StoreStatus actual)
    {
        if (actual != expected)
        {
            throw new InvalidOperationException($"Expected {expected}, received {actual}.");
        }
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
