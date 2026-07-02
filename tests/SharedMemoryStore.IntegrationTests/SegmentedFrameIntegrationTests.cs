using System.Buffers;

namespace SharedMemoryStore.IntegrationTests;

public sealed class SegmentedFrameIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void PublishesSixteenSegmentFrameWithoutFlattening()
    {
        using var store = TestSupport.IntegrationStoreFactory.Create(TestSupport.IntegrationStoreFactory.Options(maxValueBytes: 128));
        var segments = Enumerable.Range(0, 16)
            .Select(i => new[] { (byte)i, (byte)(i + 1) })
            .ToArray();
        var expected = segments.SelectMany(s => s).ToArray();
        var sequence = SequenceFactory.Create(segments);

        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([1], sequence, [7, 8], out var copied));
        Assert.Equal(expected.Length, copied);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([1], out var lease));
        Assert.Equal(expected, lease.ValueSpan.ToArray());
        Assert.Equal(new byte[] { 7, 8 }, lease.DescriptorSpan.ToArray());
        lease.Dispose();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void PublishesOneSegmentFrameThroughSameApi()
    {
        using var store = TestSupport.IntegrationStoreFactory.Create(TestSupport.IntegrationStoreFactory.Options());
        var sequence = new ReadOnlySequence<byte>(new byte[] { 9, 10, 11 });

        Assert.Equal(StoreStatus.Success, store.TryPublishSegments([2], sequence, default, out var copied));
        Assert.Equal(3, copied);

        Assert.Equal(StoreStatus.Success, store.TryAcquire([2], out var lease));
        Assert.Equal(new byte[] { 9, 10, 11 }, lease.ValueSpan.ToArray());
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
