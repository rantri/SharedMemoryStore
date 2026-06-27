using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class ReuseBenchmarks
{
    private Store _store = null!;
    private long _configuredCapacityBytes;
    private readonly byte[] _key = [1];
    private readonly byte[] _value = [1];

    [GlobalSetup]
    public void Setup()
    {
        var options = BenchmarkStoreFactory.Options(slotCount: 1);
        _configuredCapacityBytes = options.TotalBytes;
        _store = BenchmarkStoreFactory.Create(options);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public ReuseValidationResult PublishRemoveReuseCycle()
    {
        StoreStatus status = StoreStatus.Success;
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var initialCommittedBytes = process.PrivateMemorySize64;

        for (var i = 0; i < BenchmarkEnvironment.ReuseCycleCount; i++)
        {
            _value[0] = (byte)i;
            status = _store.TryPublish(_key, _value);
            if (status != StoreStatus.Success)
            {
                break;
            }

            status = _store.TryRemove(_key);
            if (status != StoreStatus.Success)
            {
                break;
            }
        }

        process.Refresh();
        var finalCommittedBytes = process.PrivateMemorySize64;
        var committedGrowthBytes = Math.Max(0, finalCommittedBytes - initialCommittedBytes);
        var allowedGrowthBytes = (long)Math.Ceiling(_configuredCapacityBytes * BenchmarkEnvironment.CommittedMemoryToleranceRatio)
            + BenchmarkEnvironment.DocumentedFixedOverheadBytes;
        var diagnostics = _store.GetDiagnostics();

        return new ReuseValidationResult(
            BenchmarkEnvironment.ReuseCycleCount,
            _configuredCapacityBytes,
            initialCommittedBytes,
            finalCommittedBytes,
            committedGrowthBytes,
            allowedGrowthBytes,
            status,
            diagnostics.FreeSlotCount,
            status == StoreStatus.Success && diagnostics.FreeSlotCount == 1 && committedGrowthBytes <= allowedGrowthBytes);
    }
}

public readonly record struct ReuseValidationResult(
    int CycleCount,
    long ConfiguredCapacityBytes,
    long InitialCommittedBytes,
    long FinalCommittedBytes,
    long CommittedGrowthBytes,
    long AllowedGrowthBytes,
    StoreStatus FinalStatus,
    int FreeSlotCount,
    bool Passed);
