namespace SharedMemoryStore.Benchmarks;

public static class BenchmarkEnvironment
{
    public const int FramePayloadBytes = 1_300_000;
    public const int FrameThroughputDurationSeconds = 60;
    public const int TargetFramePublishesPerSecond = 500;
    public const int LifecycleCycleCount = 100_000;
    public const int ReuseCycleCount = 1_000_000;
    public const double CommittedMemoryToleranceRatio = 0.01;
    public const long DocumentedFixedOverheadBytes = 1_048_576;

    public static string Summary =>
        $".NET {Environment.Version}; OS {Environment.OSVersion}; CPU {Environment.ProcessorCount}; " +
        $"default slots 8; frame payload {FramePayloadBytes:N0} bytes; readers 4; " +
        $"frame target {TargetFramePublishesPerSecond}/s for {FrameThroughputDurationSeconds}s; " +
        $"reuse cycles {ReuseCycleCount:N0}; committed-memory tolerance {CommittedMemoryToleranceRatio:P0} " +
        $"plus fixed overhead {DocumentedFixedOverheadBytes:N0} bytes";
}
