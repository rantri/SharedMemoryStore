using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class DirectIngestFrameThroughputBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _descriptor = [1, 0, 0, 0];
    private int _payloadLength;

    [GlobalSetup]
    public void Setup()
    {
        _payloadLength = BenchmarkEnvironment.FramePayloadBytes;

        _store = BenchmarkStoreFactory.Create(slotCount: 2, maxValueBytes: _payloadLength, maxDescriptorBytes: _descriptor.Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus DirectFrameIngestRemove()
    {
        var status = _store.TryReserve(_key, _payloadLength, _descriptor, out var reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        reservation.GetSpan(_payloadLength).Fill(0x5A);
        status = reservation.Advance(_payloadLength);
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return status;
        }

        status = reservation.Commit();
        return status == StoreStatus.Success ? _store.TryRemove(_key) : status;
    }

    [Benchmark]
    public DirectIngestThroughputValidationResult SustainedDirectIngestForSixtySeconds()
    {
        var stopwatch = Stopwatch.StartNew();
        long frames = 0;
        var status = StoreStatus.Success;

        while (stopwatch.Elapsed < TimeSpan.FromSeconds(BenchmarkEnvironment.FrameThroughputDurationSeconds))
        {
            status = DirectFrameIngestRemove();
            if (status != StoreStatus.Success)
            {
                break;
            }

            frames++;
        }

        stopwatch.Stop();
        var framesPerSecond = frames / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        return new DirectIngestThroughputValidationResult(
            frames,
            framesPerSecond,
            BenchmarkEnvironment.TargetFramePublishesPerSecond,
            BenchmarkEnvironment.FrameThroughputDurationSeconds,
            status,
            framesPerSecond >= BenchmarkEnvironment.TargetFramePublishesPerSecond && status == StoreStatus.Success);
    }
}

public readonly record struct DirectIngestThroughputValidationResult(
    long FrameCount,
    double FramesPerSecond,
    int TargetFramesPerSecond,
    int DurationSeconds,
    StoreStatus FinalStatus,
    bool Passed);
