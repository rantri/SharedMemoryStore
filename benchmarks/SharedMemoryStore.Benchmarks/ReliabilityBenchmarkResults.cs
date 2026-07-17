namespace SharedMemoryStore.Benchmarks;

public readonly record struct RecoveryOwnershipBenchmarkResult(
    int CycleCount,
    int RecoveredLeaseCount,
    int ActiveLeaseCount,
    int FailedRecoveryCount,
    bool Passed);
