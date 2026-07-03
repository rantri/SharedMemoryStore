using BenchmarkDotNet.Attributes;
using SharedMemoryStore.Layout;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class LifecycleRolloverBenchmarks
{
    private Store _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _store = BenchmarkStoreFactory.Create(slotCount: 1, maxKeyBytes: 8, leaseRecordCount: 1);
        _store.SetSlotSearchCursorForTesting(int.MaxValue - 2);
        _store.SetLeaseSearchCursorForTesting(int.MaxValue - 2);
        _store.SetSlotLifecycleForTesting(0, new SlotLifecycleId(int.MaxValue, 0));
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public LifecycleRolloverBenchmarkResult BoundaryOperations()
    {
        var staleAccepted = false;
        for (var i = 0; i < 1_000; i++)
        {
            var key = BitConverter.GetBytes(i);
            if (_store.TryPublish(key, [1]) != StoreStatus.Success)
            {
                break;
            }

            if (_store.TryAcquire(key, out var lease) != StoreStatus.Success)
            {
                break;
            }

            _ = _store.TryRemove(key);
            _ = lease.Release();
            staleAccepted |= new ValueLease(_store, lease.SlotIndexForTesting, lease.LifecycleIdForTesting, lease.LeaseRecordIdForTesting).IsValid;
        }

        var diagnostics = _store.GetDiagnostics();
        return new LifecycleRolloverBenchmarkResult(1_000, diagnostics.MaxObservedProbeLength, staleAccepted, !staleAccepted);
    }
}
