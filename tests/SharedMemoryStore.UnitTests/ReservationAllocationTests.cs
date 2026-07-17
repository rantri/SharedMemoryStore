using System.Buffers;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class ReservationAllocationTests
{
    [Fact]
    public void DirectReservationDoesNotAllocateAfterWarmup()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2, maxValueBytes: 8));
        var key = new byte[] { 1 };
        var value = new byte[] { 1, 2, 3, 4 };
        var descriptor = new byte[] { 9 };

        AllocationAssert.NoAllocAfterWarmup(() =>
        {
            var status = store.TryReserve(key, value.Length, descriptor, out var reservation);
            if (status != StoreStatus.Success)
            {
                return status;
            }

            value.CopyTo(reservation.GetSpan());
            status = reservation.Advance(value.Length);
            if (status != StoreStatus.Success)
            {
                return status;
            }

            status = reservation.Commit();
            return status == StoreStatus.Success ? store.TryRemove(key) : status;
        });
    }

    [Fact]
    public void DirectStreamReservationDoesNotAllocatePayloadBufferAfterWarmup()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2, maxValueBytes: 8));
        var key = new byte[] { 2 };
        var value = new byte[] { 1, 2, 3, 4 };
        using var source = new MemoryStream(value, writable: false);

        AllocationAssert.NoAllocAfterWarmup(() =>
        {
            source.Position = 0;
            var status = store.TryReserve(key, value.Length, default, out var reservation);
            if (status != StoreStatus.Success)
            {
                return status;
            }

            while (reservation.RemainingBytes > 0)
            {
                var readLength = Math.Min(2, reservation.RemainingBytes);
                var target = reservation.DangerousGetMemory(readLength);
                if (target.IsEmpty)
                {
                    _ = reservation.Abort();
                    return StoreStatus.InvalidReservation;
                }

                var received = source.Read(target.Span[..readLength]);
                if (received == 0)
                {
                    _ = reservation.Abort();
                    return StoreStatus.ReservationIncomplete;
                }

                status = reservation.Advance(received);
                if (status != StoreStatus.Success)
                {
                    _ = reservation.Abort();
                    return status;
                }
            }

            status = reservation.Commit();
            return status == StoreStatus.Success ? store.TryRemove(key) : status;
        });
    }

    [Fact]
    public void SegmentedPublishDoesNotAllocateTemporaryPayloadAfterWarmup()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 2, maxValueBytes: 16));
        var key = new byte[] { 1 };
        var first = new byte[] { 1, 2 };
        var second = new byte[] { 3, 4 };
        var sequence = SequenceFactory.Create(first, second);

        AllocationAssert.NoAllocAfterWarmup(() =>
        {
            var status = store.TryPublishSegments(key, sequence, default, out _);
            return status == StoreStatus.Success ? store.TryRemove(key) : status;
        });
    }

    [Fact]
    public void FailurePathsAvoidManagedAllocationAndRecoveryRemainsFunctional()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(slotCount: 1));
        Assert.Equal(StoreStatus.Success, store.TryReserve([1], 2, default, out var reservation));

        NoAllocStatus(() => reservation.Commit(), StoreStatus.ReservationIncomplete);
        NoAllocStatus(() => reservation.Advance(3), StoreStatus.ReservationWriteOutOfRange);
        Assert.Equal(
            StoreStatus.Success,
            store.TryRecoverReservations(new ReservationRecoveryOptions(false), out _));

        Assert.Equal(StoreStatus.Success, reservation.Abort());
    }

    private static void NoAllocStatus(Func<StoreStatus> operation, StoreStatus expected)
    {
        operation();
        operation();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = operation();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected, status);
        Assert.Equal(0, allocated);
    }

    private static class SequenceFactory
    {
        public static ReadOnlySequence<byte> Create(params byte[][] segments)
        {
            BufferSegment? first = null;
            BufferSegment? last = null;
            foreach (var segment in segments)
            {
                last = last is null ? first = new BufferSegment(segment) : last.Append(segment);
            }

            return new ReadOnlySequence<byte>(first!, 0, last!, last!.Memory.Length);
        }
    }

    private sealed class BufferSegment : ReadOnlySequenceSegment<byte>
    {
        public BufferSegment(byte[] memory)
        {
            Memory = memory;
        }

        public BufferSegment Append(byte[] memory)
        {
            var segment = new BufferSegment(memory)
            {
                RunningIndex = RunningIndex + Memory.Length
            };
            Next = segment;
            return segment;
        }
    }
}
