using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using Store = SharedMemoryStore.SharedMemoryStore;

namespace SharedMemoryStore.Benchmarks;

[MemoryDiagnoser]
public class TombstonePressureBenchmarks
{
    private const int SlotCount = 64;
    private const int ChurnOperations = 512;
    private const int BaselineSamples = 32;

    [Benchmark]
    public TombstonePressureBenchmarkResult ManagedPressureChurn()
    {
        using var clean = BenchmarkStoreFactory.Create(slotCount: SlotCount, maxKeyBytes: 8, leaseRecordCount: SlotCount);
        var cleanMissingTicks = MeasureMissingLookupTicks(clean, firstKey: 50_000, count: BaselineSamples);
        var cleanInsertTicks = MeasureCleanInsertTicks(clean, firstKey: 60_000, count: BaselineSamples);

        using var managed = BenchmarkStoreFactory.Create(slotCount: SlotCount, maxKeyBytes: 8, leaseRecordCount: SlotCount);
        var firstFailure = StoreStatus.Success;
        var pressureDetectedBeforeWorstCase = false;

        firstFailure = PreparePreservationState(managed, out var protectedLease, out var pendingReservation);
        for (var i = 0; i < ChurnOperations && firstFailure == StoreStatus.Success; i++)
        {
            var key = Key(i);
            firstFailure = managed.TryPublish(key, [(byte)i]);
            if (firstFailure != StoreStatus.Success)
            {
                break;
            }

            firstFailure = managed.TryRemove(key);
            if (firstFailure != StoreStatus.Success)
            {
                break;
            }

            var diagnostics = managed.GetDiagnostics();
            if (diagnostics.IndexCompactionCount > 0
                && i + 1 < (diagnostics.IndexEntryCount * 3) / 4)
            {
                pressureDetectedBeforeWorstCase = true;
            }
        }

        var managedMissingTicks = MeasureMissingLookupTicks(managed, firstKey: 70_000, count: BaselineSamples);
        var managedInsertTicks = MeasureManagedInsertTicks(managed, firstKey: 80_000, count: BaselineSamples);
        var finalDiagnostics = managed.GetDiagnostics();
        var preservationPassed = VerifyPreservation(managed, protectedLease, pendingReservation);
        _ = protectedLease.Release();
        _ = pendingReservation.Abort();

        var missingWithinTwoTimesClean = managedMissingTicks <= Math.Max(1, cleanMissingTicks) * 2;
        var insertWithinTwoTimesClean = managedInsertTicks <= Math.Max(1, cleanInsertTicks) * 2;

        return new TombstonePressureBenchmarkResult(
            ChurnOperations,
            finalDiagnostics.IndexEntryCount,
            finalDiagnostics.TombstoneIndexEntryCount,
            cleanMissingTicks,
            managedMissingTicks,
            cleanInsertTicks,
            managedInsertTicks,
            finalDiagnostics.MaxObservedProbeLength,
            finalDiagnostics.IndexCompactionCount,
            pressureDetectedBeforeWorstCase,
            missingWithinTwoTimesClean,
            insertWithinTwoTimesClean,
            preservationPassed,
            firstFailure == StoreStatus.Success
                && finalDiagnostics.IndexCompactionCount > 0
                && pressureDetectedBeforeWorstCase
                && missingWithinTwoTimesClean
                && insertWithinTwoTimesClean
                && preservationPassed);
    }

    private static StoreStatus PreparePreservationState(Store store, out ValueLease protectedLease, out ValueReservation pendingReservation)
    {
        protectedLease = default;
        pendingReservation = default;

        var status = store.TryPublish(Key(90_000), [90]);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        status = store.TryAcquire(Key(90_000), out protectedLease);
        if (status != StoreStatus.Success)
        {
            return status;
        }

        status = store.TryRemove(Key(90_000));
        if (status != StoreStatus.RemovePending)
        {
            return status;
        }

        return store.TryReserve(Key(90_001), 1, default, out pendingReservation);
    }

    private static bool VerifyPreservation(Store store, ValueLease protectedLease, ValueReservation pendingReservation)
    {
        return protectedLease.IsValid
            && protectedLease.ValueLength == 1
            && protectedLease.ValueSpan[0] == 90
            && pendingReservation.IsValid
            && store.TryAcquire(Key(90_001), out _) == StoreStatus.NotFound
            && store.TryPublish(Key(90_001), [1]) == StoreStatus.DuplicateKey
            && store.TryPublish(Key(90_000), [1]) == StoreStatus.DuplicateKey;
    }

    private static long MeasureMissingLookupTicks(Store store, int firstKey, int count)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            _ = store.TryAcquire(Key(firstKey + i), out _);
        }

        return Math.Max(1, Stopwatch.GetTimestamp() - start);
    }

    private static long MeasureCleanInsertTicks(Store store, int firstKey, int count)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            var status = store.TryPublish(Key(firstKey + i), [(byte)i]);
            if (status != StoreStatus.Success)
            {
                throw new InvalidOperationException("Clean insert baseline failed with " + status);
            }
        }

        return Math.Max(1, Stopwatch.GetTimestamp() - start);
    }

    private static long MeasureManagedInsertTicks(Store store, int firstKey, int count)
    {
        var start = Stopwatch.GetTimestamp();
        for (var i = 0; i < count; i++)
        {
            var key = Key(firstKey + i);
            var status = store.TryPublish(key, [(byte)i]);
            if (status != StoreStatus.Success)
            {
                throw new InvalidOperationException("Managed insert measurement failed with " + status);
            }

            _ = store.TryRemove(key);
        }

        return Math.Max(1, Stopwatch.GetTimestamp() - start);
    }

    private static byte[] Key(int value) => BitConverter.GetBytes(value);
}
