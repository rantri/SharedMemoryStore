using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class LeaseAllocationBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];

    [GlobalSetup]
    public void Setup()
    {
        _store = BenchmarkStoreFactory.Create();
        _store.TryPublish(_key, [1, 2, 3, 4]);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus AcquireRelease()
    {
        var status = _store.TryAcquire(_key, out var lease);
        return status == StoreStatus.Success ? lease.Release() : status;
    }
}
