using BenchmarkDotNet.Attributes;
using System.Threading;
using System.Threading.Tasks;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class LifecycleStressBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _value = [1];
    private readonly ValueLease[] _leases = new ValueLease[4];

    [GlobalSetup]
    public void Setup() => _store = BenchmarkStoreFactory.Create(slotCount: 2, leaseRecordCount: 8);

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public LifecycleStressValidationResult ProducerFourReaderLifecycle()
    {
        var firstFailure = (int)StoreStatus.Success;
        var useAfterReleaseDetected = 0;

        for (var i = 0; i < BenchmarkEnvironment.LifecycleCycleCount; i++)
        {
            _value[0] = (byte)i;
            var status = _store.TryPublish(_key, _value);
            if (status != StoreStatus.Success)
            {
                firstFailure = (int)status;
                break;
            }

            var observedFailures = 0;
            Parallel.For(0, _leases.Length, reader =>
            {
                var acquireStatus = _store.TryAcquire(_key, out _leases[reader]);
                if (acquireStatus != StoreStatus.Success)
                {
                    Interlocked.CompareExchange(ref observedFailures, 1, 0);
                    Interlocked.CompareExchange(ref firstFailure, (int)acquireStatus, (int)StoreStatus.Success);
                    return;
                }

                if (_leases[reader].ValueSpan.IsEmpty || _leases[reader].ValueSpan[0] != _value[0])
                {
                    Interlocked.CompareExchange(ref observedFailures, 1, 0);
                    Interlocked.CompareExchange(ref firstFailure, (int)StoreStatus.UnknownFailure, (int)StoreStatus.Success);
                }
            });

            if (observedFailures != 0)
            {
                break;
            }

            status = _store.TryRemove(_key);
            if (status != StoreStatus.RemovePending)
            {
                firstFailure = (int)status;
                break;
            }

            Parallel.For(0, _leases.Length, reader =>
            {
                var releaseStatus = _leases[reader].Release();
                if (releaseStatus != StoreStatus.Success)
                {
                    Interlocked.CompareExchange(ref observedFailures, 1, 0);
                    Interlocked.CompareExchange(ref firstFailure, (int)releaseStatus, (int)StoreStatus.Success);
                    return;
                }

                if (!_leases[reader].ValueSpan.IsEmpty)
                {
                    Interlocked.Exchange(ref useAfterReleaseDetected, 1);
                    Interlocked.CompareExchange(ref observedFailures, 1, 0);
                    Interlocked.CompareExchange(ref firstFailure, (int)StoreStatus.UnknownFailure, (int)StoreStatus.Success);
                }
            });

            if (observedFailures != 0)
            {
                break;
            }
        }

        var diagnostics = _store.GetDiagnostics();
        var leakedActiveLeases = diagnostics.ActiveLeaseCount;
        var underflowDetected = diagnostics.CorruptStoreFailures > 0 || firstFailure == (int)StoreStatus.CorruptStore;
        var useAfterReleaseWasDetected = Volatile.Read(ref useAfterReleaseDetected) != 0;

        return new LifecycleStressValidationResult(
            BenchmarkEnvironment.LifecycleCycleCount,
            _leases.Length,
            (StoreStatus)firstFailure,
            leakedActiveLeases,
            underflowDetected,
            useAfterReleaseWasDetected,
            firstFailure == (int)StoreStatus.Success && leakedActiveLeases == 0 && !underflowDetected && !useAfterReleaseWasDetected);
    }
}

public readonly record struct LifecycleStressValidationResult(
    int CycleCount,
    int ReaderCount,
    StoreStatus FirstFailure,
    int LeakedActiveLeaseCount,
    bool UsageCountUnderflowDetected,
    bool UseAfterReleaseDetected,
    bool Passed);
