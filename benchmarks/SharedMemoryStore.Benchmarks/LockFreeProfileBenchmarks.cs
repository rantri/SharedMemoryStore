using BenchmarkDotNet.Attributes;

using Store = SharedMemoryStore.MemoryStore;

namespace SharedMemoryStore.Benchmarks;

/// <summary>
/// Side-by-side profile benchmarks for the v1.2 synchronized engine and the
/// v2 lock-free engine. Keys and buffers are prepared during setup so the
/// MemoryDiagnoser reports library-path allocations rather than harness noise.
/// </summary>
[MemoryDiagnoser]
public class LockFreeProfileBenchmarks
{
    private const int SlotCount = 64;
    private const int PayloadBytes = 256;
    private const int PrimaryBucketCount = 64;

    private Store _store = null!;
    private byte[][] _keys = null!;
    private byte[][] _collidingKeys = null!;
    private byte[] _payload = null!;
    private int _cursor;

    [Params(StoreProfile.Legacy, StoreProfile.LockFree)]
    public StoreProfile Profile { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string name = $"sms-profile-benchmark-{Profile}-{Guid.NewGuid():N}";
        SharedMemoryStoreOptions options = Profile == StoreProfile.LockFree
            ? SharedMemoryStoreOptions.CreateLockFree(
                name,
                SlotCount,
                PayloadBytes,
                maxDescriptorBytes: 8,
                maxKeyBytes: 8,
                leaseRecordCount: 128,
                participantRecordCount: 16,
                openMode: OpenMode.CreateNew,
                enableLeaseRecovery: true)
            : SharedMemoryStoreOptions.Create(
                name,
                SlotCount,
                PayloadBytes,
                maxDescriptorBytes: 8,
                maxKeyBytes: 8,
                leaseRecordCount: 128,
                openMode: OpenMode.CreateNew,
                enableLeaseRecovery: true);

        StoreOpenStatus open = Store.TryCreateOrOpen(options, out Store? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            throw new InvalidOperationException($"Cannot open {Profile} benchmark store: {open}.");
        }

        _store = store;
        _keys = Enumerable.Range(0, SlotCount).Select(BitConverter.GetBytes).ToArray();
        _payload = new byte[PayloadBytes];
        for (var index = 0; index < _payload.Length; index++)
        {
            _payload[index] = unchecked((byte)(index * 31));
        }

        _collidingKeys = FindCollidingKeys(9);
        for (var index = 0; index < 8; index++)
        {
            Ensure(_store.TryPublish(_collidingKeys[index], _payload));
        }

        Ensure(_store.TryPublish(_keys[0], _payload));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
    }

    [Benchmark(Baseline = true)]
    public StoreStatus AcquireProjectRelease()
    {
        StoreStatus acquire = _store.TryAcquire(_keys[0], out ValueLease lease);
        if (acquire != StoreStatus.Success)
        {
            return acquire;
        }

        _ = lease.ValueSpan[PayloadBytes - 1];
        return lease.Release();
    }

    [Benchmark]
    public StoreStatus PublishRemove()
    {
        int index = 1 + (Interlocked.Increment(ref _cursor) & 31);
        byte[] key = _keys[index];
        StoreStatus publish = _store.TryPublish(key, _payload);
        return publish == StoreStatus.Success ? _store.TryRemove(key) : publish;
    }

    [Benchmark]
    public StoreStatus ZeroCopyReserveCommitRemove()
    {
        int index = 33 + (Interlocked.Increment(ref _cursor) & 15);
        byte[] key = _keys[index];
        StoreStatus reserve = _store.TryReserve(key, PayloadBytes, [], out ValueReservation reservation);
        if (reserve != StoreStatus.Success)
        {
            return reserve;
        }

        _payload.CopyTo(reservation.GetSpan());
        StoreStatus advance = reservation.Advance(PayloadBytes);
        if (advance != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return advance;
        }

        StoreStatus commit = reservation.Commit();
        return commit == StoreStatus.Success ? _store.TryRemove(key) : commit;
    }

    [Benchmark]
    public StoreStatus CollisionOverflowPublishRemove()
    {
        byte[] key = _collidingKeys[8];
        StoreStatus publish = _store.TryPublish(key, _payload);
        return publish == StoreStatus.Success ? _store.TryRemove(key) : publish;
    }

    [Benchmark]
    public StoreStatus CurrentOwnerLeaseRecovery()
    {
        int index = 49 + (Interlocked.Increment(ref _cursor) & 7);
        byte[] key = _keys[index];
        StoreStatus publish = _store.TryPublish(key, _payload);
        if (publish != StoreStatus.Success)
        {
            return publish;
        }

        StoreStatus acquire = _store.TryAcquire(key, out _);
        if (acquire != StoreStatus.Success)
        {
            _ = _store.TryRemove(key);
            return acquire;
        }

        StoreStatus recovery = _store.TryRecoverLeases(
            new LeaseRecoveryOptions(RecoverCurrentProcessLeases: true),
            out _);
        StoreStatus remove = _store.TryRemove(key);
        return recovery == StoreStatus.Success ? remove : recovery;
    }

    private static byte[][] FindCollidingKeys(int count)
    {
        var keys = new List<byte[]>(count);
        for (long candidate = 0; keys.Count < count; candidate++)
        {
            byte[] key = BitConverter.GetBytes(candidate);
            if ((Hash(key) & (PrimaryBucketCount - 1)) == 0)
            {
                keys.Add(key);
            }
        }

        return keys.ToArray();
    }

    private static ulong Hash(ReadOnlySpan<byte> key)
    {
        ulong hash = 14_695_981_039_346_656_037UL;
        foreach (byte value in key)
        {
            hash ^= value;
            hash *= 1_099_511_628_211UL;
        }

        return hash;
    }

    private static void Ensure(StoreStatus status)
    {
        if (status != StoreStatus.Success)
        {
            throw new InvalidOperationException("Benchmark setup failed: " + status);
        }
    }
}
