using System.Buffers;
using SharedMemoryStore.UnitTests.TestSupport;

namespace SharedMemoryStore.UnitTests;

public sealed class SegmentedPublishTests
{
    [Fact]
    public void PublishesOneSegmentAndManySegments()
    {
        using var store = StoreTestNames.CreateStore(StoreTestNames.Options(maxValueBytes: 64));

        var one = new ReadOnlySequence<byte>(new byte[] { 1, 2, 3 });
        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([1], one, default, out var oneCopied));
        Assert.Equal(3, oneCopied);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var oneLease));
        Assert.Equal(new byte[] { 1, 2, 3 }, oneLease.ValueSpan.ToArray());
        oneLease.Dispose();

        var sequence = SequenceFactory.Create(
            [0], [1], [2], [3], [4], [5], [6], [7],
            [8], [9], [10], [11], [12], [13], [14], [15]);
        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([2], sequence, [9], out var copied));
        Assert.Equal(16, copied);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var lease));
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 9 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
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
