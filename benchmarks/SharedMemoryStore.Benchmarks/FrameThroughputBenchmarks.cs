using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class FrameThroughputBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _descriptor = [1, 0, 0, 0];
    private byte[] _payload = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload = new byte[BenchmarkEnvironment.FramePayloadBytes];
        for (var i = 0; i < _payload.Length; i++)
        {
            _payload[i] = (byte)(i % 251);
        }

        _store = BenchmarkStoreFactory.Create(slotCount: 2, maxValueBytes: _payload.Length, maxDescriptorBytes: _descriptor.Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus FramePublishRemove()
    {
        var publish = _store.TryPublish(_key, _payload, _descriptor);
        var remove = _store.TryRemove(_key);
        return publish == StoreStatus.Success ? remove : publish;
    }

    [Benchmark]
    public FrameThroughputValidationResult SustainedFramePublishRemoveForSixtySeconds()
    {
        return RunSustainedValidation(TimeSpan.FromSeconds(BenchmarkEnvironment.FrameThroughputDurationSeconds));
    }

    private FrameThroughputValidationResult RunSustainedValidation(TimeSpan duration)
    {
        var stopwatch = Stopwatch.StartNew();
        long publishes = 0;
        var status = StoreStatus.Success;

        while (stopwatch.Elapsed < duration)
        {
            status = _store.TryPublish(_key, _payload, _descriptor);
            if (status != StoreStatus.Success)
            {
                break;
            }

            publishes++;
            status = _store.TryRemove(_key);
            if (status != StoreStatus.Success)
            {
                break;
            }
        }

        stopwatch.Stop();
        var publishesPerSecond = publishes / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        return new FrameThroughputValidationResult(
            publishes,
            publishesPerSecond,
            BenchmarkEnvironment.TargetFramePublishesPerSecond,
            BenchmarkEnvironment.FrameThroughputDurationSeconds,
            status,
            publishesPerSecond >= BenchmarkEnvironment.TargetFramePublishesPerSecond && status == StoreStatus.Success);
    }
}

public readonly record struct FrameThroughputValidationResult(
    long PublishCount,
    double PublishesPerSecond,
    int TargetPublishesPerSecond,
    int DurationSeconds,
    StoreStatus FinalStatus,
    bool Passed);
