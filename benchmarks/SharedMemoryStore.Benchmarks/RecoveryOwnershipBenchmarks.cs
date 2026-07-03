using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class RecoveryOwnershipBenchmarks
{
    private Store _store = null!;

    [GlobalSetup]
    public void Setup() => _store = BenchmarkStoreFactory.Create(slotCount: 1, leaseRecordCount: 1);

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public RecoveryOwnershipBenchmarkResult CurrentProcessRecoveryCycles()
    {
        var recovered = 0;
        var active = 0;
        var failed = 0;

        for (var i = 0; i < 1_000; i++)
        {
            var key = BitConverter.GetBytes(i);
            if (_store.TryPublish(key, [1]) != StoreStatus.Success)
            {
                failed++;
                break;
            }

            if (_store.TryAcquire(key, out _) != StoreStatus.Success)
            {
                failed++;
                break;
            }

            if (_store.TryRecoverLeases(new LeaseRecoveryOptions(true), out var report) != StoreStatus.Success)
            {
                failed++;
                break;
            }

            recovered += report.RecoveredLeaseCount;
            active += report.ActiveLeaseCount;
            _ = _store.TryRemove(key);
        }

        return new RecoveryOwnershipBenchmarkResult(1_000, recovered, active, failed, failed == 0 && recovered > 0);
    }
}
