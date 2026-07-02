using System.Buffers;
using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class SegmentedPublishBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _descriptor = [9, 8, 7, 6];
    private ReadOnlySequence<byte> _payload;

    [GlobalSetup]
    public void Setup()
    {
        var segmentLength = BenchmarkEnvironment.FramePayloadBytes / BenchmarkEnvironment.SegmentedPublishSegmentCount;
        var segments = new byte[BenchmarkEnvironment.SegmentedPublishSegmentCount][];
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = new byte[segmentLength];
            Array.Fill(segments[i], (byte)i);
        }

        _payload = SequenceFactory.Create(segments);
        _store = BenchmarkStoreFactory.Create(slotCount: 2, maxValueBytes: (int)_payload.Length, maxDescriptorBytes: _descriptor.Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus PublishSegmentsRemove()
    {
        var status = _store.TryPublishSegments(_key, _payload, _descriptor, out _);
        return status == StoreStatus.Success ? _store.TryRemove(_key) : status;
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
