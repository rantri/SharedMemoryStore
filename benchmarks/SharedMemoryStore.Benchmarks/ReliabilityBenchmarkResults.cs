namespace SharedMemoryStore.Benchmarks;

public readonly record struct TombstonePressureBenchmarkResult(
    int OperationCount,
    int IndexEntryCount,
    int TombstoneCount,
    long CleanMissingLookupTicks,
    long ManagedMissingLookupTicks,
    long CleanInsertTicks,
    long ManagedInsertTicks,
    int MaxProbeLength,
    long CompactionCount,
    bool PressureDetectedBeforeSeventyFivePercentWorstCase,
    bool MissingLookupWithinTwoTimesClean,
    bool InsertWithinTwoTimesClean,
    bool PreservationPassed,
    bool Passed);

public readonly record struct RecoveryOwnershipBenchmarkResult(
    int CycleCount,
    int RecoveredLeaseCount,
    int ActiveLeaseCount,
    int FailedRecoveryCount,
    bool Passed);

public readonly record struct LifecycleRolloverBenchmarkResult(
    int OperationCount,
    int MaxObservedProbeLength,
    bool StaleLeaseAccepted,
    bool Passed);
