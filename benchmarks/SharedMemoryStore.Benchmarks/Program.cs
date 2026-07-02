using BenchmarkDotNet.Running;
using SharedMemoryStore.Benchmarks;

if (args.Length == 2
    && string.Equals(args[0], "--validation", StringComparison.Ordinal)
    && string.Equals(args[1], "sustained-throughput", StringComparison.Ordinal))
{
    var simple = RunSimplePublishSustained();
    var direct = RunDirectIngestSustained();
    var relativeRatio = direct.FramesPerSecond / Math.Max(simple.PublishesPerSecond, 0.001);
    var relativePercent = (relativeRatio - 1) * 100;

    Console.WriteLine(BenchmarkEnvironment.Summary);
    Console.WriteLine($"Simple publish: {simple.PublishesPerSecond:N2} publishes/s; frames={simple.PublishCount:N0}; status={simple.FinalStatus}; passed={simple.Passed}");
    Console.WriteLine($"Direct ingest: {direct.FramesPerSecond:N2} frames/s; frames={direct.FrameCount:N0}; status={direct.FinalStatus}; passed={direct.Passed}");
    Console.WriteLine($"Direct ingest relative to simple publish: {relativeRatio:N3}x ({relativePercent:N1}%).");
    return;
}

if (args.Length == 2
    && string.Equals(args[0], "--validation", StringComparison.Ordinal)
    && string.Equals(args[1], "direct-allocation", StringComparison.Ordinal))
{
    var result = RunDirectIngestAllocationValidation();
    Console.WriteLine(BenchmarkEnvironment.Summary);
    Console.WriteLine($"Direct allocation: frames={result.FrameCount:N0}; totalAllocatedBytes={result.TotalAllocatedBytes:N0}; allocatedBytesPerFrame={result.AllocatedBytesPerFrame:N3}; status={result.FinalStatus}; passed={result.Passed}");
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

static FrameThroughputValidationResult RunSimplePublishSustained()
{
    var benchmark = new FrameThroughputBenchmarks();
    benchmark.Setup();
    try
    {
        return benchmark.SustainedFramePublishRemoveForSixtySeconds();
    }
    finally
    {
        benchmark.Cleanup();
    }
}

static DirectIngestThroughputValidationResult RunDirectIngestSustained()
{
    var benchmark = new DirectIngestFrameThroughputBenchmarks();
    benchmark.Setup();
    try
    {
        return benchmark.SustainedDirectIngestForSixtySeconds();
    }
    finally
    {
        benchmark.Cleanup();
    }
}

static DirectIngestAllocationValidationResult RunDirectIngestAllocationValidation()
{
    var benchmark = new DirectIngestAllocationBenchmarks();
    benchmark.Setup();
    try
    {
        return benchmark.ValidateOneHundredThousandFramesAllocation();
    }
    finally
    {
        benchmark.Cleanup();
    }
}
