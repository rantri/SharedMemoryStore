using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class FailureLatencyBenchmarks
{
    private const int LatencySampleCount = 10_000;

    private Store _store = null!;
    private Store _unsupportedRecoveryStore = null!;
    private readonly byte[] _existingKey = [1];
    private readonly byte[] _missingKey = [2];
    private readonly byte[] _oversizedValue = [1, 2, 3, 4, 5];
    private readonly byte[] _oversizedDescriptor = [1, 2];

    [GlobalSetup]
    public void Setup()
    {
        _store = BenchmarkStoreFactory.Create(slotCount: 1, maxValueBytes: 4);
        _store.TryPublish(_existingKey, [1]);
        _unsupportedRecoveryStore = BenchmarkStoreFactory.Create(slotCount: 1, maxValueBytes: 4, enableRecovery: false);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        _unsupportedRecoveryStore.Dispose();
    }

    [Benchmark]
    public StoreStatus DuplicateKey() => _store.TryPublish(_existingKey, [2]);

    [Benchmark]
    public StoreStatus MissingKey() => _store.TryAcquire(_missingKey, out _);

    [Benchmark]
    public StoreStatus OversizedValue() => _store.TryPublish(_missingKey, _oversizedValue);

    [Benchmark]
    public StoreStatus FullStore() => _store.TryPublish(_missingKey, [2]);

    [Benchmark]
    public StoreStatus OversizedDescriptor() => _store.TryPublish(_missingKey, [2], _oversizedDescriptor);

    [Benchmark]
    public StoreStatus InvalidRelease() => default(ValueLease).Release();

    [Benchmark]
    public StoreStatus UnsupportedPlatform() => _unsupportedRecoveryStore.TryRecoverLeases(new LeaseRecoveryOptions(true), out _);

    [Benchmark]
    public FailureLatencyReport ExpectedFailureLatencyReport()
    {
        var duplicate = MeasureStatus(() => _store.TryPublish(_existingKey, [2]), StoreStatus.DuplicateKey);
        var missing = MeasureStatus(() => _store.TryAcquire(_missingKey, out _), StoreStatus.NotFound);
        var oversizedValue = MeasureStatus(() => _store.TryPublish(_missingKey, _oversizedValue), StoreStatus.ValueTooLarge);
        var oversizedDescriptor = MeasureStatus(() => _store.TryPublish(_missingKey, [2], _oversizedDescriptor), StoreStatus.DescriptorTooLarge);
        var fullStore = MeasureStatus(() => _store.TryPublish(_missingKey, [2]), StoreStatus.StoreFull);
        var invalidRelease = MeasureStatus(() => default(ValueLease).Release(), StoreStatus.InvalidLease);
        var unsupportedPlatform = MeasureStatus(() => _unsupportedRecoveryStore.TryRecoverLeases(new LeaseRecoveryOptions(true), out _), StoreStatus.UnsupportedPlatform);

        var worstP95 = new[]
        {
            duplicate.P95Microseconds,
            missing.P95Microseconds,
            oversizedValue.P95Microseconds,
            oversizedDescriptor.P95Microseconds,
            fullStore.P95Microseconds,
            invalidRelease.P95Microseconds,
            unsupportedPlatform.P95Microseconds
        }.Max();
        var worstMax = new[]
        {
            duplicate.MaxMicroseconds,
            missing.MaxMicroseconds,
            oversizedValue.MaxMicroseconds,
            oversizedDescriptor.MaxMicroseconds,
            fullStore.MaxMicroseconds,
            invalidRelease.MaxMicroseconds,
            unsupportedPlatform.MaxMicroseconds
        }.Max();

        return new FailureLatencyReport(
            LatencySampleCount,
            duplicate.P95Microseconds,
            duplicate.MaxMicroseconds,
            missing.P95Microseconds,
            missing.MaxMicroseconds,
            oversizedValue.P95Microseconds,
            oversizedValue.MaxMicroseconds,
            oversizedDescriptor.P95Microseconds,
            oversizedDescriptor.MaxMicroseconds,
            fullStore.P95Microseconds,
            fullStore.MaxMicroseconds,
            invalidRelease.P95Microseconds,
            invalidRelease.MaxMicroseconds,
            unsupportedPlatform.P95Microseconds,
            unsupportedPlatform.MaxMicroseconds,
            worstP95,
            worstMax,
            worstP95 <= 1_000
                && duplicate.Passed
                && missing.Passed
                && oversizedValue.Passed
                && oversizedDescriptor.Passed
                && fullStore.Passed
                && invalidRelease.Passed
                && unsupportedPlatform.Passed);
    }

    private static LatencyStats MeasureStatus(Func<StoreStatus> operation, StoreStatus expectedStatus)
    {
        var samples = new long[LatencySampleCount];
        var passed = true;

        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            var status = operation();
            samples[i] = Stopwatch.GetTimestamp() - started;
            passed &= status == expectedStatus;
        }

        Array.Sort(samples);
        var p95Index = Math.Clamp((int)Math.Ceiling(samples.Length * 0.95) - 1, 0, samples.Length - 1);
        return new LatencyStats(
            TimestampTicksToMicroseconds(samples[p95Index]),
            TimestampTicksToMicroseconds(samples[^1]),
            passed);
    }

    private static double TimestampTicksToMicroseconds(long timestampTicks)
    {
        return timestampTicks * 1_000_000.0 / Stopwatch.Frequency;
    }

    private readonly record struct LatencyStats(double P95Microseconds, double MaxMicroseconds, bool Passed);
}

public readonly record struct FailureLatencyReport(
    int SampleCount,
    double DuplicateKeyP95Microseconds,
    double DuplicateKeyMaxMicroseconds,
    double MissingKeyP95Microseconds,
    double MissingKeyMaxMicroseconds,
    double OversizedValueP95Microseconds,
    double OversizedValueMaxMicroseconds,
    double OversizedDescriptorP95Microseconds,
    double OversizedDescriptorMaxMicroseconds,
    double FullStoreP95Microseconds,
    double FullStoreMaxMicroseconds,
    double InvalidReleaseP95Microseconds,
    double InvalidReleaseMaxMicroseconds,
    double UnsupportedPlatformP95Microseconds,
    double UnsupportedPlatformMaxMicroseconds,
    double WorstP95Microseconds,
    double WorstMaxMicroseconds,
    bool Passed);
