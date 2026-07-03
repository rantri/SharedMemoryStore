using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class RemoveReuseAllocationBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _first = [1, 2, 3];
    private readonly byte[] _second = [4, 5, 6];

    [GlobalSetup]
    public void Setup() => _store = BenchmarkStoreFactory.Create(slotCount: 1);

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus RemoveAndReuse()
    {
        var publish = _store.TryPublish(_key, _first);
        if (publish != StoreStatus.Success)
        {
            return publish;
        }

        var remove = _store.TryRemove(_key);
        if (remove != StoreStatus.Success)
        {
            return remove;
        }

        var republish = _store.TryPublish(_key, _second);
        var cleanup = _store.TryRemove(_key);
        return republish == StoreStatus.Success ? cleanup : republish;
    }
}
