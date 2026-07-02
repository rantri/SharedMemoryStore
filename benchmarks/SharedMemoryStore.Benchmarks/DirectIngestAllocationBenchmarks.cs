using BenchmarkDotNet.Attributes;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class DirectIngestAllocationBenchmarks
{
    private Store _store = null!;
    private readonly byte[] _key = [1];
    private readonly byte[] _descriptor = [9, 8, 7, 6];
    private int _payloadLength;

    [GlobalSetup]
    public void Setup()
    {
        _payloadLength = BenchmarkEnvironment.FramePayloadBytes;

        _store = BenchmarkStoreFactory.Create(slotCount: 2, maxValueBytes: _payloadLength, maxDescriptorBytes: _descriptor.Length);
    }

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    [Benchmark]
    public StoreStatus ReserveFillCommitRemove()
    {
        var status = _store.TryReserve(_key, _payloadLength, _descriptor, out var reservation);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        reservation.GetSpan(_payloadLength).Fill(0x5A);
        status = reservation.Advance(_payloadLength);
        if (status != StoreStatus.Success)
        {
            _ = reservation.Abort();
            return status;
        }

        status = reservation.Commit();
        return status == StoreStatus.Success ? _store.TryRemove(_key) : status;
    }

    [Benchmark]
    public DirectIngestAllocationValidationResult ValidateOneHundredThousandFramesAllocation()
    {
        _ = ReserveFillCommitRemove();
        _ = ReserveFillCommitRemove();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var status = StoreStatus.Success;
        var completedFrames = 0;
        for (; completedFrames < BenchmarkEnvironment.DirectIngestAllocationFrames; completedFrames++)
        {
            status = ReserveFillCommitRemove();
            if (status != StoreStatus.Success)
            {
                break;
            }
        }

        var totalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        var allocatedBytesPerFrame = completedFrames == 0
            ? totalAllocatedBytes
            : totalAllocatedBytes / (double)completedFrames;

        return new DirectIngestAllocationValidationResult(
            completedFrames,
            totalAllocatedBytes,
            allocatedBytesPerFrame,
            status,
            completedFrames == BenchmarkEnvironment.DirectIngestAllocationFrames
                && totalAllocatedBytes == 0
                && status == StoreStatus.Success);
    }
}

public readonly record struct DirectIngestAllocationValidationResult(
    int FrameCount,
    long TotalAllocatedBytes,
    double AllocatedBytesPerFrame,
    StoreStatus FinalStatus,
    bool Passed);
