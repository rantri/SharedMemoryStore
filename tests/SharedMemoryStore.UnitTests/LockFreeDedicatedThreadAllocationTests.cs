using System.Buffers;

namespace SharedMemoryStore.UnitTests;

public sealed class LockFreeDedicatedThreadAllocationTests
{
    private const int WarmupCycles = 50_000;
    private const int MeasuredCycles = 1_000_000;

    [Fact]
    public void MillionSegmentedPublishRemoveCyclesAllocateZeroOnWarmedDedicatedThread()
    {
        using MemoryStore store = CreateStore(slotCount: 1, leaseCount: 1);
        byte[] key = [0x31];
        ReadOnlySequence<byte> payload = TwoSegmentSequence([1, 2], [3, 4]);

        AllocationRun result = RunDedicated(() =>
        {
            StoreStatus publish = store.TryPublishSegments(key, payload, [], out long copied);
            if (publish != StoreStatus.Success || copied != 4)
            {
                return publish == StoreStatus.Success ? StoreStatus.UnknownFailure : publish;
            }

            return store.TryRemove(key);
        });

        Assert.Null(result.Exception);
        Assert.Equal(StoreStatus.Success, result.LastStatus);
        Assert.Equal(MeasuredCycles, result.CompletedCycles);
        Assert.Equal(0, result.AllocatedBytes);
    }

    [Fact]
    public void MillionExpectedFailureSetsAllocateZeroOnWarmedDedicatedThread()
    {
        using MemoryStore store = CreateStore(slotCount: 1, leaseCount: 1);
        byte[] publishedKey = [0x41];
        byte[] capacityKey = [0x42];
        byte[] missingKey = [0x43];
        byte[] value = [0x51];
        Assert.Equal(StoreStatus.Success, store.TryPublish(publishedKey, value));
        Assert.Equal(StoreStatus.Success, store.TryAcquire(publishedKey, out ValueLease held));

        AllocationRun result;
        try
        {
            result = RunDedicated(() =>
            {
                if (store.TryPublish(publishedKey, value) != StoreStatus.DuplicateKey
                    || store.TryReserve(publishedKey, 1, [], out _) != StoreStatus.DuplicateKey
                    || store.TryPublish(capacityKey, value) != StoreStatus.StoreFull
                    || store.TryAcquire(publishedKey, out _) != StoreStatus.LeaseTableFull
                    || store.TryAcquire(missingKey, out _) != StoreStatus.NotFound)
                {
                    return StoreStatus.UnknownFailure;
                }

                return StoreStatus.Success;
            });
        }
        finally
        {
            Assert.Equal(StoreStatus.Success, held.Release());
        }

        Assert.Null(result.Exception);
        Assert.Equal(StoreStatus.Success, result.LastStatus);
        Assert.Equal(MeasuredCycles, result.CompletedCycles);
        Assert.Equal(0, result.AllocatedBytes);
    }

    private static AllocationRun RunDedicated(Func<StoreStatus> cycle)
    {
        AllocationRun result = default;
        var thread = new Thread(() =>
        {
            try
            {
                for (var index = 0; index < WarmupCycles; index++)
                {
                    StoreStatus status = cycle();
                    if (status != StoreStatus.Success)
                    {
                        result = new AllocationRun(-1, index, status, null);
                        return;
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                long before = GC.GetAllocatedBytesForCurrentThread();
                StoreStatus last = StoreStatus.Success;
                var completed = 0;
                for (; completed < MeasuredCycles; completed++)
                {
                    last = cycle();
                    if (last != StoreStatus.Success)
                    {
                        break;
                    }
                }

                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                result = new AllocationRun(allocated, completed, last, null);
            }
            catch (Exception exception)
            {
                result = new AllocationRun(-1, 0, StoreStatus.UnknownFailure, exception);
            }
        })
        {
            IsBackground = true,
            Name = "SharedMemoryStore allocation qualification"
        };

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(5)), "Dedicated allocation run timed out.");
        return result;
    }

    private static ReadOnlySequence<byte> TwoSegmentSequence(byte[] first, byte[] second)
    {
        var start = new Segment(first);
        Segment end = start.Append(second);
        return new ReadOnlySequence<byte>(start, 0, end, end.Memory.Length);
    }

    private static MemoryStore CreateStore(int slotCount, int leaseCount)
    {
        SharedMemoryStoreOptions options = SharedMemoryStoreOptions.CreateLockFree(
            $"sms-v2-dedicated-allocation-{Guid.NewGuid():N}",
            slotCount,
            maxValueBytes: 8,
            maxDescriptorBytes: 0,
            maxKeyBytes: 4,
            leaseRecordCount: leaseCount,
            participantRecordCount: 1,
            openMode: OpenMode.CreateNew,
            enableLeaseRecovery: true);
        Assert.Equal(StoreOpenStatus.Success, MemoryStore.TryCreateOrOpen(options, out MemoryStore? store));
        return Assert.IsType<MemoryStore>(store);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory)
        {
            Memory = memory;
        }

        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new Segment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }

    private readonly record struct AllocationRun(
        long AllocatedBytes,
        int CompletedCycles,
        StoreStatus LastStatus,
        Exception? Exception);
}
