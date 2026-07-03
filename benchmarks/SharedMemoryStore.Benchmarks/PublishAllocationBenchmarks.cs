using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class PublishAllocationBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _value = [1, 2, 3, 4];
    private readonly byte[] _descriptor = [9];

    [GlobalSetup]
    public void Setup() => _store = BenchmarkStoreFactory.Create(maxValueBytes: _value.Length);

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus PublishRemove()
    {
        var publish = _store.TryPublish(_key, _value, _descriptor);
        var remove = _store.TryRemove(_key);
        return publish == StoreStatus.Success ? remove : publish;
    }
}
