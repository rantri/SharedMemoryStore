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

if (args.Length == 2
    && string.Equals(args[0], "--validation", StringComparison.Ordinal)
    && string.Equals(args[1], "tombstone-pressure", StringComparison.Ordinal))
{
    var result = RunTombstonePressureValidation();
    Console.WriteLine(BenchmarkEnvironment.Summary);
    Console.WriteLine(
        $"Tombstone pressure: operations={result.OperationCount:N0}; indexEntries={result.IndexEntryCount:N0}; tombstones={result.TombstoneCount:N0}; " +
        $"cleanMissingTicks={result.CleanMissingLookupTicks:N0}; managedMissingTicks={result.ManagedMissingLookupTicks:N0}; " +
        $"cleanInsertTicks={result.CleanInsertTicks:N0}; managedInsertTicks={result.ManagedInsertTicks:N0}; " +
        $"maxProbe={result.MaxProbeLength:N0}; compactions={result.CompactionCount:N0}; earlyPressure={result.PressureDetectedBeforeSeventyFivePercentWorstCase}; " +
        $"missingWithin2x={result.MissingLookupWithinTwoTimesClean}; insertWithin2x={result.InsertWithinTwoTimesClean}; preservation={result.PreservationPassed}; passed={result.Passed}");
    if (!result.Passed)
    {
        Environment.ExitCode = 1;
    }

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

static TombstonePressureBenchmarkResult RunTombstonePressureValidation()
{
    var benchmark = new TombstonePressureBenchmarks();
    return benchmark.ManagedPressureChurn();
}
