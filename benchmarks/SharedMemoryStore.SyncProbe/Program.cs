using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharedMemoryStore;
using SharedMemoryStore.LockFree;

using Store = SharedMemoryStore.MemoryStore;

const int SyncSlotCount = 32;
const int ReaderSlotCount = 256;
const int MixedSlotCount = 768;
const int BrokerSlotCount = 256;
const int DefaultStickyOverflowSlotCount = 4_096;
const int SyncValueBytes = 8;
const int ReaderPayloadBytes = 256;
const int MixedPayloadBytes = 256;
const int DefaultLargeFrameBytes = 1_363_148;
const int BrokerRotatingKeyCount = 256;
const int MixedCollisionKeyCount = 512;
const int StickyOverflowPublishedCollisionKeyCount = 17;
const int StickyOverflowProbeKeyCount = StickyOverflowPublishedCollisionKeyCount + 1;
const int DefaultStickyOverflowChurnCycles = 10_000;
const int DefaultStickyOverflowMissingSamplesPerWindow = 16_384;
const double StickyOverflowLateToEarlyP99Gate = 2.0;
const int BenchmarkDescriptorBytes = 16;
const int MaxKeyBytes = 8;
const int DefaultLeaseRecordCount = 64;
const int MixedLeaseRecordCount = 128;
const int ParticipantRecordCount = 64;
const int ReaderKeyCount = 256;
const int SyncKeysPerWorker = 2;
const int SyncMaximumWorkerCount = 12;
const int DefaultShortWarmupSeconds = 2;
const int ReleaseWarmupSeconds = 10;
const int BrokerObserverSamplingInterval = 16;
const int SamplingInterval = 64;
const int MaxLatencySamplesPerWorker = 65_536;
const int LatencyReservoirCapacityPerWindow = MaxLatencySamplesPerWorker / 2;
const int DefaultDurationSeconds = 3;
const int DefaultDurationBoundGraceSeconds = 60;
const int DefaultTrials = 3;
const int TrialHeartbeatSeconds = 30;
const int WatchdogChildKillBudgetMilliseconds = 100;

if (args.Length > 0 && string.Equals(args[0], "worker", StringComparison.Ordinal))
{
    return RunAutonomousWorker(args);
}

if (args.Length > 0 && string.Equals(args[0], "broker-worker", StringComparison.Ordinal))
{
    return await RunBrokerWorker(args);
}

if (args.Length > 0 && string.Equals(args[0], "suspension-worker", StringComparison.Ordinal))
{
    return SuspensionQualification.RunWorker(args);
}

return await RunController(args);

static async Task<int> RunController(string[] args)
{
    AssertLatencyReservoirMaximumSemantics();
    int durationSeconds = ReadPositiveIntOption(args, "--duration", DefaultDurationSeconds);
    int durationBoundGraceSeconds = ReadPositiveIntOption(
        args,
        "--duration-bound-grace",
        DefaultDurationBoundGraceSeconds);
    int trials = ReadPositiveIntOption(args, "--trials", DefaultTrials);
    string? outputPath = ReadStringOption(args, "--output");
    string mode = (ReadStringOption(args, "--mode") ?? "sync").ToLowerInvariant();
    if (mode == "suspension")
    {
        return await SuspensionQualification.RunControllerAsync(args);
    }
    int warmupSeconds = ReadNonNegativeIntOption(
        args,
        "--warmup",
        mode == "full" ? ReleaseWarmupSeconds : DefaultShortWarmupSeconds);
    StoreProfile[] profiles = ParseProfiles(
        ReadStringOption(args, "--profile") ?? (mode == "overflow" ? "v2" : "legacy"),
        "--profile");
    StoreProfile[] countBoundProfiles = ParseProfiles(
        ReadStringOption(args, "--count-bound-profiles") ?? "both",
        "--count-bound-profiles");
    bool affinityRequested = !args.Contains("--no-affinity", StringComparer.Ordinal);
    ScenarioPlan[] plans = ProbeScenarioCatalog.Select(mode);
    string? scenarioFilter = ReadStringOption(args, "--scenario");
    if (!string.IsNullOrWhiteSpace(scenarioFilter))
    {
        string[] requestedScenarios = scenarioFilter
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        plans = plans
            .Where(plan => requestedScenarios.Contains(plan.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (plans.Length == 0)
        {
            throw new ArgumentException("--scenario did not match a scenario in the selected mode.");
        }
    }

    int[]? requestedProcessCounts = ParsePositiveIntListOption(args, "--process-counts");
    if (requestedProcessCounts is not null)
    {
        plans = plans
            .Select(plan => plan with
            {
                ProcessCounts = plan.ProcessCounts
                    .Where(requestedProcessCounts.Contains)
                    .ToArray()
            })
            .Where(static plan => plan.ProcessCounts.Length != 0)
            .ToArray();
        if (plans.Length == 0)
        {
            throw new ArgumentException("--process-counts did not match the selected workload matrix.");
        }
    }
    int largeFrameBytes = ReadPositiveIntOption(
        args,
        "--large-frame-bytes",
        ReadPositiveIntOption(args, "--frame-bytes", DefaultLargeFrameBytes));
    long defaultLargeFrames = mode == "full" ? 100_000L : 256L;
    long largeFrames = ReadPositiveLongOption(
        args,
        "--large-frames",
        ReadPositiveLongEnvironment("SMS_LOCK_FREE_LARGE_FRAMES", defaultLargeFrames));
    long defaultMixedTarget = mode == "full" ? 100_000_000L : 0L;
    long mixedOperationTarget = ReadNonNegativeLongOption(
        args,
        "--mixed-operations",
        ReadNonNegativeLongOption(
            args,
            "--churn-operations",
            ReadNonNegativeLongEnvironment("SMS_LOCK_FREE_CHURN_OPERATIONS", defaultMixedTarget)));
    int stickyOverflowSlotCount = ReadPositivePowerOfTwoOption(
        args,
        "--overflow-slot-count",
        DefaultStickyOverflowSlotCount);
    int stickyOverflowChurnCycles = ReadPositiveIntOption(
        args,
        "--overflow-churn-cycles",
        DefaultStickyOverflowChurnCycles);
    int stickyOverflowMissingSamples = ReadPositiveIntOption(
        args,
        "--overflow-missing-samples",
        DefaultStickyOverflowMissingSamplesPerWindow);
    bool includesStickyOverflow = plans.Any(
        static plan => plan.Kind == ProbeScenarioKind.StickyOverflow);
    if (includesStickyOverflow && !profiles.Contains(StoreProfile.LockFree))
    {
        throw new ArgumentException("The sticky-overflow qualification requires --profile v2 or both.");
    }

    BucketPairCollisionSet stickyOverflowKeys = includesStickyOverflow
        ? BenchmarkProtocol.CreateBucketPairCollisionKeys(
            StickyOverflowProbeKeyCount,
            BenchmarkProtocol.CalculatePrimaryBucketCount(stickyOverflowSlotCount),
            firstBucket: 0,
            secondBucket: 1)
        : default;

    RepositoryProvenanceSnapshot repositoryProvenance = RepositoryEnvironmentProbe.Capture(args);
    ProbeEnvironment probeEnvironment = CaptureEnvironment(repositoryProvenance);

    var runs = new List<RunResult>();
    foreach (StoreProfile profile in profiles)
    {
        foreach (ScenarioPlan plan in plans)
        {
            if (plan.Kind == ProbeScenarioKind.StickyOverflow && profile != StoreProfile.LockFree)
            {
                continue;
            }

            foreach (int processCount in plan.ProcessCounts)
            {
                for (var trial = 1; trial <= trials; trial++)
                {
                    ProbeRunTargets targets = ProbeCompletionTargetPolicy.Resolve(
                        profile,
                        plan.Kind,
                        countBoundProfiles,
                        mixedOperationTarget,
                        largeFrames);
                    long runOperationTarget = targets.OperationTarget;
                    long runFrameTarget = targets.FrameTarget;
                    Console.Error.WriteLine(
                        $"trial-start profile={profile} scenario={plan.Name} processes={processCount} "
                        + $"trial={trial}/{trials} limit="
                        + (plan.Kind == ProbeScenarioKind.StickyOverflow
                            ? $"fixed-work:churn-cycles:{stickyOverflowChurnCycles}"
                            : runOperationTarget > 0
                            ? $"operations:{runOperationTarget}"
                            : runFrameTarget > 0
                                ? $"frames:{runFrameTarget}"
                                : $"duration-seconds:{durationSeconds}"));
                    using var heartbeatCancellation = new CancellationTokenSource();
                    Task heartbeat = ReportTrialHeartbeats(
                        profile,
                        plan.Name,
                        processCount,
                        trial,
                        trials,
                        plan.Kind,
                        targets,
                        heartbeatCancellation.Token);
                    RunResult result;
                    try
                    {
                        result = plan.Kind switch
                        {
                            ProbeScenarioKind.Autonomous => await RunAutonomousTrial(
                                profile,
                                plan,
                                processCount,
                                trial,
                                durationSeconds,
                                warmupSeconds,
                                durationBoundGraceSeconds,
                                affinityRequested,
                                operationTarget: 0),
                            ProbeScenarioKind.MixedChurn => await RunMixedTrial(
                                profile,
                                plan,
                                processCount,
                                trial,
                                durationSeconds,
                                warmupSeconds,
                                durationBoundGraceSeconds,
                                affinityRequested,
                                runOperationTarget),
                            ProbeScenarioKind.BrokerDirected => await RunBrokerTrial(
                                profile,
                                plan,
                                processCount,
                                trial,
                                durationSeconds,
                                warmupSeconds,
                                durationBoundGraceSeconds,
                                affinityRequested,
                                largeFrameBytes,
                                frameTarget: 0),
                            ProbeScenarioKind.LargeIngest => await RunBrokerTrial(
                                profile,
                                plan,
                                processCount,
                                trial,
                                durationSeconds,
                                warmupSeconds,
                                durationBoundGraceSeconds,
                                affinityRequested,
                                largeFrameBytes,
                                runFrameTarget),
                            ProbeScenarioKind.StickyOverflow => RunStickyOverflowTrial(
                                profile,
                                plan,
                                trial,
                                affinityRequested,
                                stickyOverflowSlotCount,
                                stickyOverflowChurnCycles,
                                stickyOverflowMissingSamples,
                                stickyOverflowKeys),
                            _ => throw new ArgumentOutOfRangeException(nameof(plan.Kind))
                        };
                    }
                    finally
                    {
                        await heartbeatCancellation.CancelAsync();
                        await heartbeat;
                    }

                    runs.Add(result);
                    Console.Error.WriteLine(
                        $"{profile,-8} {plan.Name,-20} processes={processCount,2} trial={trial} "
                        + $"calls/s={result.ApiCallsPerSecond:N0} frames/s={result.FramesPerSecond:N2} "
                        + $"p50={result.P50Microseconds:N3}us p95={result.P95Microseconds:N3}us "
                        + $"p99={result.P99Microseconds:N3}us late/early={result.LateToEarlyP99Ratio:N3} "
                        + $"failures={result.Failures} affinity={result.AffinityAppliedCount}/"
                        + $"{result.ReaderProcessCount + result.PublisherProcessCount + result.ObserverProcessCount} "
                        + $"qualification={result.Qualification}");
                }
            }
        }
    }

    var scenarioCounts = new SortedDictionary<string, int[]>(StringComparer.Ordinal);
    foreach (ScenarioPlan plan in plans)
    {
        scenarioCounts[plan.Name] = plan.ProcessCounts;
    }

    int syncCanonicalBucketCount = BenchmarkProtocol.CalculatePrimaryBucketCount(SyncSlotCount);
    BenchmarkKeyCatalog syncKeyCatalog = BenchmarkProtocol.CreateCanonicalBucketKeyCatalog(
        SyncKeysPerWorker,
        SyncMaximumWorkerCount,
        syncCanonicalBucketCount);
    int[] syncKeyCanonicalBucketAssignments = Enumerable.Range(0, syncKeyCatalog.Count)
        .Select(index => BenchmarkProtocol.GetCanonicalBucket(
            syncKeyCatalog[index].Span,
            syncCanonicalBucketCount))
        .ToArray();

    var report = new ProbeReport(
        SchemaVersion: ProbeReportSchema.CurrentVersion,
        TimestampUtc: DateTimeOffset.UtcNow,
        Environment: probeEnvironment,
        Configuration: new ProbeConfiguration(
            mode,
            durationSeconds,
            durationBoundGraceSeconds,
            trials,
            profiles.Select(static profile => profile.ToString()).ToArray(),
            countBoundProfiles.Select(static profile => profile.ToString()).ToArray(),
            plans.Select(static plan => plan.Name).ToArray(),
            scenarioCounts,
            CreateScenarioStoreDimensions(plans, largeFrameBytes, stickyOverflowSlotCount),
            ReaderKeyCount,
            ReaderPayloadBytes,
            BrokerRotatingKeyCount,
            largeFrameBytes,
            largeFrames,
            mixedOperationTarget,
            MixedCollisionKeyCount,
            BenchmarkProtocol.CalculatePrimaryBucketCount(MixedSlotCount),
            WarmupCycles: 0,
            warmupSeconds,
            BrokerObserverSamplingInterval,
            SamplingInterval,
            MaxLatencySamplesPerWorker,
            affinityRequested,
            "physical-core-first-then-siblings",
            stickyOverflowSlotCount,
            stickyOverflowChurnCycles,
            stickyOverflowMissingSamples,
            ProbeReportSchema.LegacyFullPayloadCopiesFieldSemantics,
            SyncKeysPerWorker,
            SyncMaximumWorkerCount,
            syncCanonicalBucketCount,
            syncKeyCatalog.CalculateSha256(),
            syncKeyCanonicalBucketAssignments),
        Runs: runs,
        Summary: Summarize(runs),
        MinimumCompatibleSchemaVersion: ProbeReportSchema.MinimumCompatibleVersion,
        SchemaCompatibility: ProbeReportSchema.Compatibility);

    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (!string.IsNullOrWhiteSpace(outputPath))
    {
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(fullPath, json);
        Console.Error.WriteLine($"report={fullPath}");
    }
    else
    {
        Console.WriteLine(json);
    }

    if (runs.Any(static run => run.Failures != 0))
    {
        return 2;
    }

    if (runs.Any(static run => run.StickyOverflow is { DiagnosticsGatePassed: false }))
    {
        return 3;
    }

    return runs.Any(static run => run.StickyOverflow is { LatencyGatePassed: false }) ? 4 : 0;
}

static async Task<RunResult> RunAutonomousTrial(
    StoreProfile profile,
    ScenarioPlan plan,
    int processCount,
    int trial,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds,
    bool affinityRequested,
    long operationTarget)
{
    string name = $"sms-sync-{Guid.NewGuid():N}";
    var workers = new List<Process>(processCount);
    var processRegistry = new ProbeProcessRegistry();
    ProbeTrialWatchdog? trialWatchdog = CreateTrialWatchdog(
        plan.Name,
        profile,
        durationSeconds,
        warmupSeconds,
        durationBoundGraceSeconds,
        operationTarget,
        frameTarget: 0,
        processRegistry);
    try
    {
        StoreOpenStatus openStatus = Store.TryCreateOrOpen(
            Options(name, OpenMode.CreateNew, profile, plan.Name, payloadBytes: 0),
            out Store? owner);
        if (openStatus != StoreOpenStatus.Success || owner is null)
        {
            throw new InvalidOperationException($"Owner open failed for {profile}: {openStatus}");
        }

        using (owner)
        {
            Seed(owner, plan.Name, processCount);
            try
            {
                for (var workerId = 0; workerId < processCount; workerId++)
                {
                    workers.Add(processRegistry.Start(() => StartAutonomousWorker(
                            plan.Name,
                            profile,
                            name,
                            workerId,
                            durationSeconds,
                            warmupSeconds,
                            affinityRequested ? workerId : -1,
                            operationTarget,
                            payloadBytes: 0)));
                }

                await AwaitReady(workers, CancellationToken.None);
                var wall = Stopwatch.StartNew();
                await SignalGo(workers, CancellationToken.None);
                List<WorkerResult> results = await CollectWorkerResults(workers, CancellationToken.None);
                wall.Stop();
                return AggregateAutonomousRun(
                    profile,
                    plan,
                    processCount,
                    trial,
                    warmupSeconds,
                    wall.Elapsed,
                    results,
                    operationTarget);
            }
            finally
            {
                DisposeProcesses(workers);
            }
        }
    }
    finally
    {
        if (trialWatchdog is not null)
        {
            await trialWatchdog.CompleteAsync();
        }
    }
}

static async Task<RunResult> RunMixedTrial(
    StoreProfile profile,
    ScenarioPlan plan,
    int readerCount,
    int trial,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds,
    bool affinityRequested,
    long totalOperationTarget)
{
    string name = $"sms-churn-{Guid.NewGuid():N}";
    int participantCount = readerCount + plan.PublisherCount;
    var workers = new List<Process>(participantCount);
    var processRegistry = new ProbeProcessRegistry();
    ProbeTrialWatchdog? trialWatchdog = CreateTrialWatchdog(
        plan.Name,
        profile,
        durationSeconds,
        warmupSeconds,
        durationBoundGraceSeconds,
        totalOperationTarget,
        frameTarget: 0,
        processRegistry);
    try
    {
        StoreOpenStatus openStatus = Store.TryCreateOrOpen(
            Options(name, OpenMode.CreateNew, profile, plan.Name, MixedPayloadBytes),
            out Store? owner);
        if (openStatus != StoreOpenStatus.Success || owner is null)
        {
            throw new InvalidOperationException($"Mixed-churn owner open failed for {profile}: {openStatus}");
        }

        using (owner)
        {
            SeedMixed(owner);
            long perWorkerOperationTarget = totalOperationTarget <= 0
                ? 0
                : checked((totalOperationTarget + participantCount - 1) / participantCount);
            try
            {
                for (var readerId = 0; readerId < readerCount; readerId++)
                {
                    workers.Add(processRegistry.Start(() => StartAutonomousWorker(
                            "mixed-churn-reader",
                            profile,
                            name,
                            readerId,
                            durationSeconds,
                            warmupSeconds,
                            affinityRequested ? readerId : -1,
                            perWorkerOperationTarget,
                            MixedPayloadBytes)));
                }

                for (var publisherId = 0; publisherId < plan.PublisherCount; publisherId++)
                {
                    workers.Add(processRegistry.Start(() => StartAutonomousWorker(
                            "mixed-churn-writer",
                            profile,
                            name,
                            publisherId,
                            durationSeconds,
                            warmupSeconds,
                            affinityRequested ? readerCount + publisherId : -1,
                            perWorkerOperationTarget,
                            MixedPayloadBytes)));
                }

                await AwaitReady(workers, CancellationToken.None);
                var wall = Stopwatch.StartNew();
                await SignalGo(workers, CancellationToken.None);
                List<WorkerResult> results = await CollectWorkerResults(workers, CancellationToken.None);
                wall.Stop();
                RunResult aggregate = AggregateAutonomousRun(
                    profile,
                    plan,
                    readerCount,
                    trial,
                    warmupSeconds,
                    wall.Elapsed,
                    results,
                    totalOperationTarget);
                if (totalOperationTarget > 0 && aggregate.Operations < totalOperationTarget)
                {
                    throw new InvalidOperationException(
                        $"Mixed-churn operation target was not met: {aggregate.Operations:N0} < {totalOperationTarget:N0}.");
                }

                return aggregate;
            }
            finally
            {
                DisposeProcesses(workers);
            }
        }
    }
    finally
    {
        if (trialWatchdog is not null)
        {
            await trialWatchdog.CompleteAsync();
        }
    }
}

static RunResult RunStickyOverflowTrial(
    StoreProfile profile,
    ScenarioPlan plan,
    int trial,
    bool affinityRequested,
    int slotCount,
    int churnCycles,
    int missingSamplesPerWindow,
    BucketPairCollisionSet collisionSet)
{
    if (profile != StoreProfile.LockFree
        || collisionSet.Keys.Length != StickyOverflowProbeKeyCount)
    {
        throw new InvalidOperationException("Sticky-overflow qualification requires the lock-free profile and exact collision keys.");
    }

    string name = $"sms-sticky-overflow-{Guid.NewGuid():N}";
    StoreOpenStatus openStatus = Store.TryCreateOrOpen(
        StickyOverflowOptions(name, slotCount),
        out Store? store);
    if (openStatus != StoreOpenStatus.Success || store is null)
    {
        throw new InvalidOperationException($"Sticky-overflow store open failed: {openStatus}");
    }

    using TemporaryProcessAffinity affinity = TemporaryProcessAffinity.Apply(
        affinityRequested ? 0 : -1);
    using (store)
    {
        byte[][] publishedKeys = collisionSet.Keys[..StickyOverflowPublishedCollisionKeyCount];
        byte[] missingKey = collisionSet.Keys[^1];
        var counters = new StatusCounters();
        var earlySamples = new double[missingSamplesPerWindow];
        var lateSamples = new double[missingSamplesPerWindow];
        Span<byte> payload = stackalloc byte[1];
        long failures = 0;

        // JIT and warm the exact normal-miss path before either latency window.
        for (var index = 0; index < Math.Min(1_024, missingSamplesPerWindow); index++)
        {
            StoreStatus warmStatus = store.TryAcquire(
                missingKey,
                StoreWaitOptions.Infinite,
                out ValueLease warmLease);
            if (warmStatus == StoreStatus.Success)
            {
                failures++;
                _ = warmLease.Release(StoreWaitOptions.Infinite);
            }
            else if (warmStatus != StoreStatus.NotFound)
            {
                failures++;
            }
        }

        StoreStatus beforeChurnStatus = store.TryGetDiagnostics(
            StoreWaitOptions.Infinite,
            out DiagnosticsSnapshot beforeChurn);
        if (beforeChurnStatus != StoreStatus.Success)
        {
            failures++;
        }
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var measured = Stopwatch.StartNew();
        failures += MeasureMissingWindow(store, missingKey, earlySamples, counters);

        DiagnosticsSnapshot duringFirstSpill = default;
        DiagnosticsSnapshot afterFirstCleanup = default;
        for (var cycle = 0; cycle < churnCycles; cycle++)
        {
            for (var keyIndex = 0; keyIndex < publishedKeys.Length; keyIndex++)
            {
                payload[0] = unchecked((byte)(cycle + keyIndex));
                StoreStatus publish = PublishWithRetry(
                    store,
                    publishedKeys[keyIndex],
                    payload,
                    counters);
                if (publish != StoreStatus.Success)
                {
                    failures++;
                }
            }

            if (cycle == 0)
            {
                StoreStatus diagnosticsStatus = store.TryGetDiagnostics(
                    StoreWaitOptions.Infinite,
                    out DiagnosticsSnapshot duringChurn);
                if (diagnosticsStatus != StoreStatus.Success)
                {
                    failures++;
                }
                else
                {
                    duringFirstSpill = duringChurn;
                }
            }

            for (var keyIndex = 0; keyIndex < publishedKeys.Length; keyIndex++)
            {
                StoreStatus remove = RemoveWithRetry(store, publishedKeys[keyIndex], counters);
                if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
                {
                    failures++;
                }
            }

            if (cycle == 0)
            {
                StoreStatus diagnosticsStatus = store.TryGetDiagnostics(
                    StoreWaitOptions.Infinite,
                    out DiagnosticsSnapshot afterCleanup);
                if (diagnosticsStatus != StoreStatus.Success)
                {
                    failures++;
                }
                else
                {
                    afterFirstCleanup = afterCleanup;
                }
            }
        }

        StoreStatus beforeLateStatus = store.TryGetDiagnostics(
            StoreWaitOptions.Infinite,
            out DiagnosticsSnapshot beforeLate);
        if (beforeLateStatus != StoreStatus.Success)
        {
            failures++;
        }

        failures += MeasureMissingWindow(store, missingKey, lateSamples, counters);
        measured.Stop();
        long measuredThreadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        StoreStatus afterLateStatus = store.TryGetDiagnostics(
            StoreWaitOptions.Infinite,
            out DiagnosticsSnapshot afterLate);
        if (afterLateStatus != StoreStatus.Success)
        {
            failures++;
        }

        double[] sortedEarly = earlySamples.Order().ToArray();
        double[] sortedLate = lateSamples.Order().ToArray();
        double earlyP99 = Percentile(sortedEarly, 0.99);
        double lateP99 = Percentile(sortedLate, 0.99);
        double lateToEarlyRatio = earlyP99 == 0 ? 0 : lateP99 / earlyP99;
        bool diagnosticsGatePassed = beforeChurn.SpilledBucketCount == 0
            && duringFirstSpill.SpilledBucketCount > 0
            && duringFirstSpill.OverflowDirectoryOccupancy > 0
            && afterFirstCleanup.SpilledBucketCount == 0
            && afterFirstCleanup.OverflowDirectoryOccupancy == 0
            && afterFirstCleanup.OverflowScanCount > duringFirstSpill.OverflowScanCount
            && afterFirstCleanup.MaxObservedOverflowScanLength >= slotCount
            && beforeLate.SpilledBucketCount == 0
            && beforeLate.OverflowDirectoryOccupancy == 0
            && beforeLate.OverflowScanCount > beforeChurn.OverflowScanCount
            && afterLate.OverflowScanCount == beforeLate.OverflowScanCount;
        bool latencyGatePassed = earlyP99 > 0
            && lateP99 > 0
            && lateToEarlyRatio <= StickyOverflowLateToEarlyP99Gate;
        string qualification = !diagnosticsGatePassed
            ? "qualification-failed-overflow-diagnostics"
            : latencyGatePassed
                ? "qualification-passed-versioned-overflow-cleanup"
                : "qualification-failed-versioned-overflow-latency";
        double measuredSeconds = Math.Max(measured.Elapsed.TotalSeconds, 0.000_001);
        double[] allSamples = [.. earlySamples, .. lateSamples];
        Array.Sort(allSamples);
        var evidence = new StickyOverflowEvidence(
            SlotCount: slotCount,
            PrimaryBucketCount: BenchmarkProtocol.CalculatePrimaryBucketCount(slotCount),
            ExactBucketPairCollisionKeyCount: StickyOverflowPublishedCollisionKeyCount,
            CollisionCandidatesExamined: collisionSet.CandidatesExamined,
            ChurnCycles: churnCycles,
            MissingSamplesPerWindow: missingSamplesPerWindow,
            SpilledBucketCountBeforeChurn: beforeChurn.SpilledBucketCount,
            SpilledBucketCountDuringChurn: duringFirstSpill.SpilledBucketCount,
            OverflowDirectoryOccupancyDuringChurn: duringFirstSpill.OverflowDirectoryOccupancy,
            SpilledBucketCountAfterFirstCleanup: afterFirstCleanup.SpilledBucketCount,
            OverflowDirectoryOccupancyAfterFirstCleanup: afterFirstCleanup.OverflowDirectoryOccupancy,
            SpilledBucketCountAfterChurn: beforeLate.SpilledBucketCount,
            OverflowDirectoryOccupancyAfterChurn: beforeLate.OverflowDirectoryOccupancy,
            OverflowScanCountBeforeFirstCleanup: duringFirstSpill.OverflowScanCount,
            OverflowScanCountAfterFirstCleanup: afterFirstCleanup.OverflowScanCount,
            MaxObservedOverflowScanLengthAfterFirstCleanup: afterFirstCleanup.MaxObservedOverflowScanLength,
            OverflowScanCountBeforeLateWindow: beforeLate.OverflowScanCount,
            OverflowScanCountAfterLateWindow: afterLate.OverflowScanCount,
            MaxObservedOverflowScanLength: afterLate.MaxObservedOverflowScanLength,
            EarlyMissingSamplesMicroseconds: earlySamples,
            LateMissingSamplesMicroseconds: lateSamples,
            LateToEarlyP99Gate: StickyOverflowLateToEarlyP99Gate,
            DiagnosticsGatePassed: diagnosticsGatePassed,
            LatencyGatePassed: latencyGatePassed);

        long bytesWritten = checked((long)churnCycles * publishedKeys.Length);
        long operations = counters.TotalOperations;
        return new RunResult(
            profile.ToString(),
            plan.Name,
            ProcessCount: 1,
            ReaderProcessCount: 0,
            PublisherProcessCount: 1,
            ObserverProcessCount: 0,
            trial,
            Cycles: churnCycles,
            operations,
            operations / measuredSeconds,
            Percentile(allSamples, 0.50),
            Percentile(allSamples, 0.95),
            Percentile(allSamples, 0.99),
            allSamples.Length == 0 ? 0 : allSamples[^1],
            earlyP99,
            lateP99,
            lateToEarlyRatio,
            [new RoleLatencyResult(
                "missing-key",
                earlySamples.Length + lateSamples.Length,
                earlyP99,
                lateP99,
                lateToEarlyRatio)],
            Frames: 0,
            FramesPerSecond: 0,
            bytesWritten,
            BytesRead: 0,
            bytesWritten / measuredSeconds,
            FullPayloadCopies: 0,
            measuredThreadAllocatedBytes,
            failures,
            measuredSeconds,
            measuredSeconds,
            allSamples.Length,
            FairnessIndex: 1,
            MinWorkerApiCallsPerSecond: operations / measuredSeconds,
            MaxWorkerApiCallsPerSecond: operations / measuredSeconds,
            WorstWorkerP99Microseconds: lateP99,
            AffinityAppliedCount: affinity.Applied ? 1 : 0,
            AssignedProcessors: [affinity.AssignedProcessor],
            AffinityStrategies: [affinity.Strategy],
            Oversubscribed: false,
            qualification,
            counters.ToHistogram(),
            WorkerCycles: [churnCycles],
            FullPayloadCopyCountIsInstrumented: false,
            FullPayloadCopyEvidenceKind: "not-applicable-no-large-payload-path",
            ProducerStoreOperationAllocatedBytes: 0,
            AllocationMeasurementScope: "controller-thread-entire-overflow-measured-window",
            StickyOverflow: evidence,
            EarlySampleCount: earlySamples.Length,
            LateSampleCount: lateSamples.Length);
    }
}

static long MeasureMissingWindow(
    Store store,
    ReadOnlySpan<byte> missingKey,
    Span<double> samples,
    StatusCounters counters)
{
    long failures = 0;
    for (var index = 0; index < samples.Length; index++)
    {
        long started = Stopwatch.GetTimestamp();
        StoreStatus status = store.TryAcquire(
            missingKey,
            StoreWaitOptions.Infinite,
            out ValueLease lease);
        samples[index] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
        counters.Record(OperationKind.Acquire, status);
        if (status == StoreStatus.Success)
        {
            failures++;
            StoreStatus release = lease.Release(StoreWaitOptions.Infinite);
            counters.Record(OperationKind.Release, release);
            if (release != StoreStatus.Success)
            {
                failures++;
            }
        }
        else if (status != StoreStatus.NotFound)
        {
            failures++;
        }
    }

    return failures;
}

static async Task<RunResult> RunBrokerTrial(
    StoreProfile profile,
    ScenarioPlan plan,
    int readerCount,
    int trial,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds,
    bool affinityRequested,
    int frameBytes,
    long frameTarget)
{
    string name = $"sms-broker-{Guid.NewGuid():N}";
    var readers = new List<Process>(readerCount);
    var observers = new List<Process>(plan.ObserverCount);
    var allProcesses = new List<Process>(readerCount + plan.ObserverCount);
    var processRegistry = new ProbeProcessRegistry();
    ProbeTrialWatchdog? trialWatchdog = CreateTrialWatchdog(
        plan.Name,
        profile,
        durationSeconds,
        warmupSeconds,
        durationBoundGraceSeconds,
        operationTarget: 0,
        frameTarget,
        processRegistry);
    try
    {
        StoreOpenStatus openStatus = Store.TryCreateOrOpen(
            Options(name, OpenMode.CreateNew, profile, plan.Name, frameBytes),
            out Store? producer);
        if (openStatus != StoreOpenStatus.Success || producer is null)
        {
            throw new InvalidOperationException($"Broker producer open failed for {profile}: {openStatus}");
        }

        using TemporaryProcessAffinity producerAffinity = TemporaryProcessAffinity.Apply(
            affinityRequested ? 0 : -1);
        using (producer)
        {
            try
            {
                for (var readerId = 0; readerId < readerCount; readerId++)
                {
                    Process process = processRegistry.Start(() => StartBrokerWorker(
                            plan.Name,
                            profile,
                            name,
                            readerId,
                            "reader",
                            affinityRequested ? readerId + 1 : -1,
                            frameBytes));
                    readers.Add(process);
                    allProcesses.Add(process);
                }

                for (var observerId = 0; observerId < plan.ObserverCount; observerId++)
                {
                    Process process = processRegistry.Start(() => StartBrokerWorker(
                            plan.Name,
                            profile,
                            name,
                            observerId,
                            "observer",
                            affinityRequested ? readerCount + observerId + 1 : -1,
                            frameBytes));
                    observers.Add(process);
                    allProcesses.Add(process);
                }

            await AwaitReady(allProcesses, CancellationToken.None);
            BenchmarkKeyCatalog keys = BenchmarkProtocol.CreateKeyCatalog(BrokerRotatingKeyCount);
            await Task.Run(
                () => WarmBrokerWorkers(
                    producer,
                    readers,
                    observers,
                    keys,
                    frameBytes,
                    warmupSeconds));
            await Task.Run(() => ResetBrokerWorkers(allProcesses));
            var readerFrames = new long[readerCount];
            BrokerMeasuredResult measuredResult = await RunBrokerMeasuredOnDedicatedThread(
                    producer,
                    readers,
                    observers,
                    keys,
                    readerFrames,
                    frameBytes,
                    durationSeconds,
                    frameTarget);
            StatusCounters counters = measuredResult.Counters;
            long failures = measuredResult.Failures;
            long frames = measuredResult.Frames;
            var stop = new BrokerKeyMessage(BrokerMessageKind.Stop, string.Empty, 0, 0, 0, 0);
            string stopLine = JsonSerializer.Serialize(stop, BenchmarkProtocol.JsonOptions);
            foreach (Process process in allProcesses)
            {
                await process.StandardInput.WriteLineAsync(stopLine.AsMemory(), CancellationToken.None);
                await process.StandardInput.FlushAsync(CancellationToken.None);
            }

            var summaries = new List<BrokerWorkerSummary>(allProcesses.Count);
            foreach (Process process in allProcesses)
            {
                string? line = await process.StandardOutput.ReadLineAsync(CancellationToken.None);
                string error = await process.StandardError.ReadToEndAsync(CancellationToken.None);
                await process.WaitForExitAsync(CancellationToken.None);
                if (process.ExitCode != 0 || line is null)
                {
                    throw new InvalidOperationException($"Broker worker exited {process.ExitCode}: {error}");
                }

                summaries.Add(JsonSerializer.Deserialize<BrokerWorkerSummary>(line, BenchmarkProtocol.JsonOptions)
                    ?? throw new InvalidOperationException("Broker worker returned invalid summary JSON."));
            }

            failures += summaries.Sum(static summary => summary.Failures);
            long operations = counters.TotalOperations + summaries.Sum(static summary => summary.Operations);
            long bytesWritten = checked(frames * frameBytes);
            long bytesRead = summaries.Sum(static summary => summary.BytesProcessed);
            double measuredSeconds = Math.Max(measuredResult.Elapsed.TotalSeconds, 0.000_001);
            double[] sortedSamples = measuredResult.SamplesMicroseconds.Order().ToArray();
            double[] sortedEarly = measuredResult.EarlySamplesMicroseconds.Order().ToArray();
            double[] sortedLate = measuredResult.LateSamplesMicroseconds.Order().ToArray();
            double earlyP99 = Percentile(sortedEarly, 0.99);
            double lateP99 = Percentile(sortedLate, 0.99);
            double[] readerRates = readerFrames.Select(count => count / measuredSeconds).ToArray();
            double fairness = JainFairness(readerRates);
            int participantProcesses = readerCount + plan.ObserverCount + 1;
            bool oversubscribed = participantProcesses > Environment.ProcessorCount;
            var histograms = summaries.Select(static summary => summary.StatusHistogram).Append(counters.ToHistogram());

                return new RunResult(
                profile.ToString(),
                plan.Name,
                readerCount,
                readerCount,
                PublisherProcessCount: 1,
                plan.ObserverCount,
                trial,
                frames,
                operations,
                operations / measuredSeconds,
                Percentile(sortedSamples, 0.50),
                Percentile(sortedSamples, 0.95),
                Percentile(sortedSamples, 0.99),
                sortedSamples.Length == 0 ? 0 : sortedSamples[^1],
                earlyP99,
                lateP99,
                earlyP99 == 0 ? 0 : lateP99 / earlyP99,
                [new RoleLatencyResult(
                    "broker-end-to-end",
                    sortedSamples.Length,
                    earlyP99,
                    lateP99,
                    earlyP99 == 0 ? 0 : lateP99 / earlyP99)],
                frames,
                frames / measuredSeconds,
                bytesWritten,
                bytesRead,
                (bytesWritten + bytesRead) / measuredSeconds,
                FullPayloadCopies: 0,
                MeasuredThreadAllocatedBytes: measuredResult.MeasuredThreadAllocatedBytes,
                failures,
                measuredSeconds,
                measuredSeconds,
                sortedSamples.Length,
                fairness,
                readerRates.Length == 0 ? 0 : readerRates.Min(),
                readerRates.Length == 0 ? 0 : readerRates.Max(),
                summaries.Where(static summary => summary.Role == "reader")
                    .Select(static summary => summary.ElapsedSeconds == 0 ? 0 : summary.Frames / summary.ElapsedSeconds)
                    .DefaultIfEmpty()
                    .Max(),
                summaries.Count(static summary => summary.AffinityApplied) + (producerAffinity.Applied ? 1 : 0),
                [producerAffinity.AssignedProcessor, .. summaries.Select(static summary => summary.AssignedProcessor)],
                [producerAffinity.Strategy, .. summaries
                    .Select(static summary => summary.AffinityStrategy)
                    .Where(strategy => !string.Equals(strategy, producerAffinity.Strategy, StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)],
                oversubscribed,
                Qualification(
                    oversubscribed,
                    durationSeconds,
                    warmupSeconds,
                    operationTarget: 0,
                    frameTarget),
                MergeHistograms(histograms),
                readerFrames,
                FullPayloadCopyCountIsInstrumented: false,
                FullPayloadCopyEvidenceKind: "structural-direct-reservation-write-and-borrowed-lease-read",
                ProducerStoreOperationAllocatedBytes: measuredResult.ProducerStoreOperationAllocatedBytes,
                AllocationMeasurementScope:
                    "dedicated-producer-and-broker-coordinator-thread-entire-measured-interval",
                EarlySampleCount: sortedEarly.Length,
                LateSampleCount: sortedLate.Length,
                OperationTarget: 0,
                    FrameTarget: frameTarget);
            }
            finally
            {
                DisposeProcesses(allProcesses);
            }
        }
    }
    finally
    {
        if (trialWatchdog is not null)
        {
            await trialWatchdog.CompleteAsync();
        }
    }
}

static RunResult AggregateAutonomousRun(
    StoreProfile profile,
    ScenarioPlan plan,
    int processCount,
    int trial,
    int warmupSeconds,
    TimeSpan wall,
    IReadOnlyList<WorkerResult> workerResults,
    long operationTarget)
{
    double[] samples = workerResults.SelectMany(static result => result.SamplesMicroseconds).Order().ToArray();
    double[] early = workerResults.SelectMany(static result => result.EarlySamplesMicroseconds).Order().ToArray();
    double[] late = workerResults.SelectMany(static result => result.LateSamplesMicroseconds).Order().ToArray();
    long totalCycles = workerResults.Sum(static result => result.Cycles);
    long totalOperations = workerResults.Sum(static result => result.Operations);
    long failures = workerResults.Sum(static result => result.Failures);
    double measuredSeconds = workerResults.Max(static result => result.ElapsedSeconds);
    double[] workerRates = workerResults
        .Select(static result => result.Operations / Math.Max(result.ElapsedSeconds, 0.000_001))
        .ToArray();
    double fairness = JainFairness(workerRates);
    double worstWorkerP99 = workerResults.Max(result =>
        Percentile(result.SamplesMicroseconds.Order().ToArray(), 0.99));
    bool mixed = plan.Kind == ProbeScenarioKind.MixedChurn;
    int readerCount = mixed
        ? workerResults.Count(static result => result.Role == "reader")
        : plan.Name == "publish-remove" ? 0 : processCount;
    int publisherCount = mixed
        ? workerResults.Count(static result => result.Role == "publisher")
        : plan.Name == "publish-remove" ? processCount : 0;
    int participantProcesses = workerResults.Count;
    bool oversubscribed = participantProcesses > Environment.ProcessorCount;
    double earlyP99 = Percentile(early, 0.99);
    double lateP99 = Percentile(late, 0.99);
    long bytesRead = workerResults
        .Where(static result => result.Role == "reader")
        .Sum(static result => result.BytesProcessed);
    long bytesWritten = workerResults
        .Where(static result => result.Role == "publisher")
        .Sum(static result => result.BytesProcessed);
    RoleLatencyResult[] roleLatency = workerResults
        .GroupBy(static result => result.Role, StringComparer.Ordinal)
        .Select(group =>
        {
            double[] roleEarly = group.SelectMany(static result => result.EarlySamplesMicroseconds).Order().ToArray();
            double[] roleLate = group.SelectMany(static result => result.LateSamplesMicroseconds).Order().ToArray();
            double roleEarlyP99 = Percentile(roleEarly, 0.99);
            double roleLateP99 = Percentile(roleLate, 0.99);
            return new RoleLatencyResult(
                group.Key,
                roleEarly.Length + roleLate.Length,
                roleEarlyP99,
                roleLateP99,
                roleEarlyP99 == 0 ? 0 : roleLateP99 / roleEarlyP99);
        })
        .OrderBy(static result => result.Role, StringComparer.Ordinal)
        .ToArray();

    return new RunResult(
        profile.ToString(),
        plan.Name,
        processCount,
        readerCount,
        publisherCount,
        ObserverProcessCount: 0,
        trial,
        totalCycles,
        totalOperations,
        totalOperations / measuredSeconds,
        Percentile(samples, 0.50),
        Percentile(samples, 0.95),
        Percentile(samples, 0.99),
        workerResults.Max(static result => result.MaximumSampleMicroseconds),
        earlyP99,
        lateP99,
        earlyP99 == 0 ? 0 : lateP99 / earlyP99,
        roleLatency,
        Frames: 0,
        FramesPerSecond: 0,
        bytesWritten,
        bytesRead,
        (bytesWritten + bytesRead) / measuredSeconds,
        FullPayloadCopies: 0,
        workerResults.Sum(static result => result.MeasuredThreadAllocatedBytes),
        failures,
        measuredSeconds,
        wall.TotalSeconds,
        samples.Length,
        fairness,
        workerRates.Min(),
        workerRates.Max(),
        worstWorkerP99,
        workerResults.Count(static result => result.AffinityApplied),
        workerResults.Select(static result => result.AssignedProcessor).ToArray(),
        workerResults.Select(static result => result.AffinityStrategy).Distinct(StringComparer.Ordinal).ToArray(),
        oversubscribed,
        Qualification(
            oversubscribed,
            (int)Math.Floor(measuredSeconds),
            warmupSeconds,
            operationTarget,
            frameTarget: 0),
        MergeHistograms(workerResults.Select(static result => result.StatusHistogram)),
        workerResults.Select(static result => result.Cycles).ToArray(),
        FullPayloadCopyCountIsInstrumented: false,
        FullPayloadCopyEvidenceKind: "not-instrumented-legacy-field-do-not-interpret-as-count",
        ProducerStoreOperationAllocatedBytes: 0,
        AllocationMeasurementScope: "sum-of-dedicated-worker-thread-measured-regions",
        EarlySampleCount: early.Length,
        LateSampleCount: late.Length,
        OperationTarget: operationTarget,
        FrameTarget: 0);
}

static async Task AwaitReady(IReadOnlyList<Process> workers, CancellationToken cancellationToken)
{
    foreach (Process worker in workers)
    {
        string? ready = await worker.StandardOutput.ReadLineAsync(cancellationToken);
        if (ready != "READY")
        {
            string error = await worker.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Worker failed to become ready: {ready}; {error}");
        }
    }
}

static async Task SignalGo(IEnumerable<Process> workers, CancellationToken cancellationToken)
{
    foreach (Process worker in workers)
    {
        await worker.StandardInput.WriteLineAsync("GO".AsMemory(), cancellationToken);
        await worker.StandardInput.FlushAsync(cancellationToken);
    }
}

static async Task<List<WorkerResult>> CollectWorkerResults(
    IReadOnlyList<Process> workers,
    CancellationToken cancellationToken)
{
    var results = new List<WorkerResult>(workers.Count);
    foreach (Process worker in workers)
    {
        string? line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
        string error = await worker.StandardError.ReadToEndAsync(cancellationToken);
        await worker.WaitForExitAsync(cancellationToken);
        if (worker.ExitCode != 0 || line is null)
        {
            throw new InvalidOperationException($"Worker exited {worker.ExitCode}: {error}");
        }

        results.Add(JsonSerializer.Deserialize<WorkerResult>(line, BenchmarkProtocol.JsonOptions)
            ?? throw new InvalidOperationException("Worker returned invalid JSON."));
    }

    return results;
}

static ProbeTrialWatchdog? CreateTrialWatchdog(
    string scenario,
    StoreProfile profile,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds,
    long operationTarget,
    long frameTarget,
    ProbeProcessRegistry processRegistry)
{
    if (operationTarget > 0 || frameTarget > 0)
    {
        return null;
    }

    long timeoutSeconds = checked(
        (long)durationSeconds + warmupSeconds + durationBoundGraceSeconds);
    return new ProbeTrialWatchdog(
        TimeSpan.FromSeconds(timeoutSeconds),
        () => FailFastDurationBoundTrial(
            scenario,
            profile,
            durationSeconds,
            warmupSeconds,
            durationBoundGraceSeconds,
            processRegistry));
}

static TimeoutException DurationBoundTrialTimeout(
    string scenario,
    StoreProfile profile,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds) =>
    new(
        $"Duration-bound trial timed out: profile={profile}; scenario={scenario}; "
        + $"warmupSeconds={warmupSeconds}; durationSeconds={durationSeconds}; "
        + $"graceSeconds={durationBoundGraceSeconds}.");

static void FailFastDurationBoundTrial(
    string scenario,
    StoreProfile profile,
    int durationSeconds,
    int warmupSeconds,
    int durationBoundGraceSeconds,
    ProbeProcessRegistry processRegistry)
{
    TimeoutException timeout = DurationBoundTrialTimeout(
        scenario,
        profile,
        durationSeconds,
        warmupSeconds,
        durationBoundGraceSeconds);
    try
    {
        var killer = new Thread(
            () => KillProcesses(processRegistry.StopAcceptingAndSnapshot()))
        {
            IsBackground = true,
            Name = "SyncProbe timeout child cleanup"
        };
        killer.Start();
        _ = killer.Join(WatchdogChildKillBudgetMilliseconds);
    }
    catch
    {
        // Process termination below is unconditional; child cleanup is best effort.
    }
    finally
    {
        // Store calls can be blocked in native/shared-memory coordination and cannot be
        // safely abandoned or unwound. This executable is the runner's child-process
        // isolation boundary, so a missed duration deadline must terminate it directly.
        Environment.FailFast(timeout.Message, timeout);
    }
}

static async Task ReportTrialHeartbeats(
    StoreProfile profile,
    string scenario,
    int processCount,
    int trial,
    int trials,
    ProbeScenarioKind scenarioKind,
    ProbeRunTargets targets,
    CancellationToken cancellationToken)
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(TrialHeartbeatSeconds));
    var elapsed = Stopwatch.StartNew();
    try
    {
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            Console.Error.WriteLine(
                $"trial-progress profile={profile} scenario={scenario} processes={processCount} "
                + $"trial={trial}/{trials} elapsed-seconds={elapsed.Elapsed.TotalSeconds:F0} "
                + (scenarioKind == ProbeScenarioKind.StickyOverflow
                    ? "fixed-work=true"
                    : targets.OperationTarget > 0
                    ? $"operation-target={targets.OperationTarget}"
                    : targets.FrameTarget > 0
                        ? $"frame-target={targets.FrameTarget}"
                        : "duration-bound=true"));
        }
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
    }
}

static void Seed(Store owner, string scenario, int processCount)
{
    if (scenario == "acquire-release")
    {
        if (processCount > SyncMaximumWorkerCount)
        {
            throw new ArgumentOutOfRangeException(nameof(processCount));
        }

        BenchmarkKeyCatalog keys = CreateSyncKeyCatalog();
        for (var workerId = 0; workerId < processCount; workerId++)
        {
            for (var keyOrdinal = 0; keyOrdinal < SyncKeysPerWorker; keyOrdinal++)
            {
                int keyIndex = checked((workerId * SyncKeysPerWorker) + keyOrdinal);
                Ensure(owner.TryPublish(keys[keyIndex].Span, [(byte)workerId]), "seed publish");
            }
        }

        return;
    }

    if (scenario == "same-key-read")
    {
        Ensure(owner.TryPublish(BenchmarkProtocol.Key(0), ReaderPayload(0)), "same-key seed publish");
        return;
    }

    if (scenario == "distributed-key-read")
    {
        for (var keyIndex = 0; keyIndex < ReaderKeyCount; keyIndex++)
        {
            Ensure(owner.TryPublish(BenchmarkProtocol.Key(keyIndex), ReaderPayload(keyIndex)), "distributed seed publish");
        }
    }
}

static BenchmarkKeyCatalog CreateSyncKeyCatalog() =>
    BenchmarkProtocol.CreateCanonicalBucketKeyCatalog(
        SyncKeysPerWorker,
        SyncMaximumWorkerCount,
        BenchmarkProtocol.CalculatePrimaryBucketCount(SyncSlotCount));

static bool IsSyncScenario(string scenario) =>
    scenario is "acquire-release" or "publish-remove";

static int SyncKeyIndex(int workerId, long cycle)
{
    if ((uint)workerId >= SyncMaximumWorkerCount)
    {
        throw new ArgumentOutOfRangeException(nameof(workerId));
    }

    return checked((workerId * SyncKeysPerWorker) + (int)(cycle & 1));
}

static ulong AutonomousSamplingSeed(string scenario, int workerId)
{
    ulong scenarioSeed = scenario switch
    {
        "acquire-release" => 0x243f_6a88_85a3_08d3UL,
        "publish-remove" => 0x1319_8a2e_0370_7344UL,
        "same-key-read" => 0xa409_3822_299f_31d0UL,
        "distributed-key-read" => 0x082e_fa98_ec4e_6c89UL,
        "mixed-churn-reader" => 0x4528_21e6_38d0_1377UL,
        "mixed-churn-writer" => 0xbe54_66cf_34e9_0c6cUL,
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    return scenarioSeed ^ (unchecked((ulong)(workerId + 1)) * 0x9e37_79b9_7f4a_7c15UL);
}

static void SeedMixed(Store owner)
{
    int primaryBucketCount = BenchmarkProtocol.CalculatePrimaryBucketCount(MixedSlotCount);
    byte[][] keys = BenchmarkProtocol.CreateCollisionKeys(MixedCollisionKeyCount, primaryBucketCount);
    var counters = new StatusCounters();
    for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
    {
        if (!PublishGeneration(owner, keys[keyIndex], keyIndex, generation: 0, MixedPayloadBytes, counters))
        {
            throw new InvalidOperationException($"Mixed-churn seed failed for collision key {keyIndex}.");
        }
    }
}

static Process StartAutonomousWorker(
    string scenario,
    StoreProfile profile,
    string name,
    int workerId,
    int durationSeconds,
    int warmupSeconds,
    int affinityOrdinal,
    long operationTarget,
    int payloadBytes)
{
    ProcessStartInfo start = CreateChildStartInfo();
    start.ArgumentList.Add("worker");
    start.ArgumentList.Add(scenario);
    start.ArgumentList.Add(profile.ToString());
    start.ArgumentList.Add(name);
    start.ArgumentList.Add(workerId.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(durationSeconds.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(warmupSeconds.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(affinityOrdinal.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(operationTarget.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(payloadBytes.ToString(CultureInfo.InvariantCulture));
    return Process.Start(start) ?? throw new InvalidOperationException("Failed to start worker process.");
}

static Process StartBrokerWorker(
    string scenario,
    StoreProfile profile,
    string name,
    int workerId,
    string role,
    int affinityOrdinal,
    int payloadBytes)
{
    ProcessStartInfo start = CreateChildStartInfo();
    start.ArgumentList.Add("broker-worker");
    start.ArgumentList.Add(scenario);
    start.ArgumentList.Add(profile.ToString());
    start.ArgumentList.Add(name);
    start.ArgumentList.Add(workerId.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(role);
    start.ArgumentList.Add(affinityOrdinal.ToString(CultureInfo.InvariantCulture));
    start.ArgumentList.Add(payloadBytes.ToString(CultureInfo.InvariantCulture));
    return Process.Start(start) ?? throw new InvalidOperationException("Failed to start broker worker process.");
}

static ProcessStartInfo CreateChildStartInfo()
{
    string processPath = Environment.ProcessPath
        ?? throw new InvalidOperationException("Cannot determine executable path.");
    var start = new ProcessStartInfo(processPath)
    {
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
    {
        start.ArgumentList.Add(typeof(Program).Assembly.Location);
    }

    return start;
}

static int RunAutonomousWorker(string[] args)
{
    if (args.Length != 10)
    {
        Console.Error.WriteLine(
            "Worker requires: worker <scenario> <profile> <store-name> <worker-id> "
            + "<duration-seconds> <warmup-seconds> <affinity-ordinal> <operation-target> <payload-bytes>.");
        return 3;
    }

    string scenario = args[1];
    StoreProfile profile = Enum.Parse<StoreProfile>(args[2], ignoreCase: true);
    string name = args[3];
    int workerId = int.Parse(args[4], CultureInfo.InvariantCulture);
    int durationSeconds = int.Parse(args[5], CultureInfo.InvariantCulture);
    int warmupSeconds = int.Parse(args[6], CultureInfo.InvariantCulture);
    int affinityOrdinal = int.Parse(args[7], CultureInfo.InvariantCulture);
    long operationTarget = long.Parse(args[8], CultureInfo.InvariantCulture);
    int payloadBytes = int.Parse(args[9], CultureInfo.InvariantCulture);
    bool affinityApplied = ProcessorAffinityPlanner.TryApply(
        affinityOrdinal,
        out int assignedProcessor,
        out string affinityStrategy);
    StoreOpenStatus openStatus = Store.TryCreateOrOpen(
        Options(name, OpenMode.OpenExisting, profile, scenario, payloadBytes),
        out Store? store);
    if (openStatus != StoreOpenStatus.Success || store is null)
    {
        Console.Error.WriteLine($"Worker open failed: {openStatus}");
        return 4;
    }

    using (store)
    {
        BenchmarkKeyCatalog stableKeys = IsSyncScenario(scenario)
            ? CreateSyncKeyCatalog()
            : BenchmarkProtocol.CreateKeyCatalog(ReaderKeyCount);
        byte[][]? collisionKeys = scenario.StartsWith("mixed-churn", StringComparison.Ordinal)
            ? BenchmarkProtocol.CreateCollisionKeys(
                MixedCollisionKeyCount,
                BenchmarkProtocol.CalculatePrimaryBucketCount(MixedSlotCount))
            : null;
        var warmupCounters = new StatusCounters();
        long warmupCycle = 0;
        var warmup = Stopwatch.StartNew();
        while (warmup.Elapsed.TotalSeconds < warmupSeconds)
        {
            RunCycle(
                store,
                scenario,
                workerId,
                warmupCycle++,
                stableKeys,
                collisionKeys,
                warmupCounters,
                out int warmupFailures,
                out _);
            if (warmupFailures != 0)
            {
                Console.Error.WriteLine(
                    "Warm-up correctness failure: " + JsonSerializer.Serialize(warmupCounters.ToHistogram()));
                return 6;
            }
        }

        Console.WriteLine("READY");
        if (Console.ReadLine() != "GO")
        {
            return 5;
        }

        ulong samplingSeed = AutonomousSamplingSeed(scenario, workerId);
        var candidateSampler = new GeometricCycleSampler(samplingSeed);
        var earlySamples = new LatencyReservoir(
            LatencyReservoirCapacityPerWindow,
            samplingSeed ^ 0xa076_1d64_78bd_642fUL);
        var lateSamples = new LatencyReservoir(
            LatencyReservoirCapacityPerWindow,
            samplingSeed ^ 0xe703_7ed1_a0b4_28dbUL);
        var counters = new StatusCounters();
        long cycles = 0;
        long bytesProcessed = 0;
        long failures = 0;
        var elapsed = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        while (operationTarget > 0
            ? counters.TotalOperations < operationTarget
            : elapsed.Elapsed.TotalSeconds < durationSeconds)
        {
            bool sample = candidateSampler.ShouldSample(cycles);
            long started = sample ? Stopwatch.GetTimestamp() : 0;
            RunCycle(
                store,
                scenario,
                workerId,
                cycles,
                stableKeys,
                collisionKeys,
                counters,
                out int cycleFailures,
                out long cycleBytes);
            if (sample)
            {
                double microseconds = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                bool early = operationTarget > 0
                    ? counters.TotalOperations < operationTarget / 2
                    : elapsed.Elapsed.TotalSeconds < durationSeconds / 2.0;
                (early ? earlySamples : lateSamples).Add(microseconds);
            }

            bytesProcessed += cycleBytes;
            failures += cycleFailures;
            cycles++;
        }

        elapsed.Stop();
        long measuredThreadAllocatedBytes = Math.Max(
            0,
            GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        double[] earlySnapshot = earlySamples.ToArray();
        double[] lateSnapshot = lateSamples.ToArray();
        double[] samples = [.. earlySnapshot, .. lateSnapshot];
        Console.WriteLine(JsonSerializer.Serialize(new WorkerResult(
            workerId,
            ScenarioRole(scenario),
            cycles,
            counters.TotalOperations,
            bytesProcessed,
            failures,
            measuredThreadAllocatedBytes,
            elapsed.Elapsed.TotalSeconds,
            affinityApplied,
            assignedProcessor,
            affinityStrategy,
            counters.ToHistogram(),
            samples,
            earlySnapshot,
            lateSnapshot,
            Math.Max(earlySamples.MaximumObserved, lateSamples.MaximumObserved)), BenchmarkProtocol.JsonOptions));
    }

    return 0;
}

static void AssertLatencyReservoirMaximumSemantics()
{
    const double outlier = 12_345.0;
    var reservoir = new LatencyReservoir(capacity: 1, seed: 1);
    reservoir.Add(outlier);
    for (var index = 0; index < 100_000; index++)
    {
        reservoir.Add(1.0);
    }

    double[] retained = reservoir.ToArray();
    if (retained.Length != 1 || retained[0] == outlier || reservoir.MaximumObserved != outlier)
    {
        throw new InvalidOperationException(
            "Latency reservoir maximum self-test did not preserve an evicted sampled outlier.");
    }
}

static async Task<int> RunBrokerWorker(string[] args)
{
    if (args.Length != 8)
    {
        Console.Error.WriteLine(
            "Broker worker requires: broker-worker <scenario> <profile> <store-name> <worker-id> "
            + "<role> <affinity-ordinal> <payload-bytes>.");
        return 3;
    }

    string scenario = args[1];
    StoreProfile profile = Enum.Parse<StoreProfile>(args[2], ignoreCase: true);
    string name = args[3];
    int workerId = int.Parse(args[4], CultureInfo.InvariantCulture);
    string role = args[5];
    int affinityOrdinal = int.Parse(args[6], CultureInfo.InvariantCulture);
    int payloadBytes = int.Parse(args[7], CultureInfo.InvariantCulture);
    bool affinityApplied = ProcessorAffinityPlanner.TryApply(
        affinityOrdinal,
        out int assignedProcessor,
        out string affinityStrategy);
    StoreOpenStatus openStatus = Store.TryCreateOrOpen(
        Options(name, OpenMode.OpenExisting, profile, scenario, payloadBytes),
        out Store? store);
    if (openStatus != StoreOpenStatus.Success || store is null)
    {
        Console.Error.WriteLine($"Broker worker open failed: {openStatus}");
        return 4;
    }

    using (store)
    {
        BenchmarkKeyCatalog keys = BenchmarkProtocol.CreateKeyCatalog(BrokerRotatingKeyCount);
        Console.WriteLine("READY");
        var counters = new StatusCounters();
        long frames = 0;
        long failures = 0;
        long bytesProcessed = 0;
        var elapsed = Stopwatch.StartNew();
        while (await Console.In.ReadLineAsync() is { } line)
        {
            BrokerKeyMessage message = JsonSerializer.Deserialize<BrokerKeyMessage>(
                line,
                BenchmarkProtocol.JsonOptions);
            if (message.Kind == BrokerMessageKind.Stop)
            {
                break;
            }

            if (message.Kind == BrokerMessageKind.Reset)
            {
                counters = new StatusCounters();
                frames = 0;
                failures = 0;
                bytesProcessed = 0;
                elapsed.Restart();
                Console.WriteLine("RESET");
                await Console.Out.FlushAsync();
                continue;
            }

            bool keyMessageValid = (uint)message.KeyIndex < (uint)keys.Count
                && string.Equals(message.KeyHex, keys.Hex(message.KeyIndex), StringComparison.Ordinal);
            ReadOnlyMemory<byte> key = keyMessageValid ? keys[message.KeyIndex] : ReadOnlyMemory<byte>.Empty;
            ValueLease lease = default;
            StoreStatus acquire;
            if (keyMessageValid)
            {
                acquire = AcquireWithRetry(store, key.Span, counters, out lease);
            }
            else
            {
                acquire = StoreStatus.InvalidKey;
                counters.Record(OperationKind.Acquire, acquire);
            }
            StoreStatus release = StoreStatus.InvalidLease;
            bool descriptorValid = false;
            bool payloadValid = false;
            int bytesObserved = 0;
            if (acquire == StoreStatus.Success)
            {
                descriptorValid = BenchmarkProtocol.ValidateDescriptor(
                    lease.DescriptorSpan,
                    message.KeyIndex,
                    message.Generation,
                    message.PayloadLength);
                bytesObserved = lease.ValueSpan.Length;
                payloadValid = bytesObserved == message.PayloadLength
                    && BenchmarkProtocol.ValidateGenerationPayload(
                        lease.ValueSpan,
                        message.KeyIndex,
                        message.Generation);
                bytesProcessed += bytesObserved;
                release = ReleaseWithRetry(lease, counters);
            }

            if (acquire != StoreStatus.Success
                || release != StoreStatus.Success
                || !keyMessageValid
                || !descriptorValid
                || !payloadValid)
            {
                failures++;
                if (!descriptorValid || !payloadValid)
                {
                    counters.RecordChecksumFailure();
                }
            }

            frames++;
            var acknowledgement = new BrokerAcknowledgement(
                workerId,
                role,
                message.KeyIndex,
                message.Generation,
                acquire,
                release,
                descriptorValid,
                payloadValid,
                bytesObserved,
                Stopwatch.GetElapsedTime(message.PublishedTimestamp).TotalMicroseconds);
            Console.WriteLine(JsonSerializer.Serialize(acknowledgement, BenchmarkProtocol.JsonOptions));
            await Console.Out.FlushAsync();
        }

        elapsed.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new BrokerWorkerSummary(
            workerId,
            role,
            frames,
            counters.TotalOperations,
            bytesProcessed,
            failures,
            elapsed.Elapsed.TotalSeconds,
            affinityApplied,
            assignedProcessor,
            affinityStrategy,
            counters.ToHistogram()), BenchmarkProtocol.JsonOptions));
    }

    return 0;
}

static void RunCycle(
    Store store,
    string scenario,
    int workerId,
    long cycle,
    BenchmarkKeyCatalog stableKeys,
    byte[][]? collisionKeys,
    StatusCounters counters,
    out int failures,
    out long bytesProcessed)
{
    failures = 0;
    bytesProcessed = 0;
    if (scenario is "acquire-release" or "same-key-read" or "distributed-key-read")
    {
        int lookupKeyIndex = scenario switch
        {
            "same-key-read" => 0,
            "distributed-key-read" => (int)((cycle + workerId * 17L) % ReaderKeyCount),
            _ => SyncKeyIndex(workerId, cycle)
        };
        StoreStatus acquire = AcquireWithRetry(
            store,
            stableKeys[lookupKeyIndex].Span,
            counters,
            out ValueLease lease);
        if (acquire != StoreStatus.Success)
        {
            failures++;
            return;
        }

        bool valid = scenario == "acquire-release"
            ? lease.ValueSpan.Length == 1 && lease.ValueSpan[0] == unchecked((byte)workerId)
            : ValidateReaderPayload(lease.ValueSpan, lookupKeyIndex);
        bytesProcessed = lease.ValueSpan.Length;
        if (!valid)
        {
            counters.RecordChecksumFailure();
            failures++;
        }

        StoreStatus release = ReleaseWithRetry(lease, counters);
        if (release != StoreStatus.Success)
        {
            failures++;
        }

        return;
    }

    if (scenario == "publish-remove")
    {
        int syncKeyIndex = SyncKeyIndex(workerId, cycle);
        StoreStatus publish = PublishWithRetry(
            store,
            stableKeys[syncKeyIndex].Span,
            [unchecked((byte)cycle)],
            counters);
        if (publish != StoreStatus.Success)
        {
            failures++;
            return;
        }

        bytesProcessed = 1;
        StoreStatus remove = RemoveWithRetry(store, stableKeys[syncKeyIndex].Span, counters);
        if (remove != StoreStatus.Success)
        {
            failures++;
        }

        return;
    }

    if (scenario == "mixed-churn-reader")
    {
        byte[][] keys = collisionKeys
            ?? throw new InvalidOperationException("Mixed-churn keys were not initialized.");
        int readKeyIndex = (int)((cycle * 17 + workerId * 43L) % keys.Length);
        StoreStatus acquire = AcquireWithRetry(store, keys[readKeyIndex], counters, out ValueLease lease);
        if (acquire == StoreStatus.NotFound)
        {
            return;
        }

        if (acquire != StoreStatus.Success)
        {
            failures++;
            return;
        }

        bool descriptorValid = lease.DescriptorSpan.Length == BenchmarkDescriptorBytes;
        long generation = descriptorValid
            ? BinaryPrimitives.ReadInt64LittleEndian(lease.DescriptorSpan)
            : long.MinValue;
        descriptorValid = descriptorValid
            && BenchmarkProtocol.ValidateDescriptor(
                lease.DescriptorSpan,
                readKeyIndex,
                generation,
                MixedPayloadBytes);
        bool payloadValid = descriptorValid
            && BenchmarkProtocol.ValidateGenerationPayload(lease.ValueSpan, readKeyIndex, generation);
        bytesProcessed = lease.ValueSpan.Length;
        if (!descriptorValid || !payloadValid)
        {
            counters.RecordChecksumFailure();
            failures++;
        }

        StoreStatus release = ReleaseWithRetry(lease, counters);
        if (release != StoreStatus.Success)
        {
            failures++;
        }

        return;
    }

    if (scenario != "mixed-churn-writer")
    {
        throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown sync-probe scenario.");
    }

    byte[][] writerKeys = collisionKeys
        ?? throw new InvalidOperationException("Mixed-churn keys were not initialized.");
    const int ChurnKeyStart = MixedCollisionKeyCount / 2;
    int localKeyCount = (MixedCollisionKeyCount - ChurnKeyStart) / 2;
    int keyIndex = ChurnKeyStart + workerId + (int)((cycle % localKeyCount) * 2);
    byte[] writerKey = writerKeys[keyIndex];
    StoreStatus removeStatus = RemoveWithRetry(store, writerKey, counters);
    if (removeStatus is not (StoreStatus.Success or StoreStatus.RemovePending))
    {
        failures++;
        return;
    }

    long writerGeneration = checked(((long)(workerId + 1) << 56) | (cycle + 1));
    if (!PublishGeneration(
        store,
        writerKey,
        keyIndex,
        writerGeneration,
        MixedPayloadBytes,
        counters,
        retryDuplicate: true))
    {
        failures++;
        return;
    }

    bytesProcessed = MixedPayloadBytes;
    if ((cycle & 1023) == 1023)
    {
        StoreStatus leaseRecovery = store.TryRecoverLeases(
            new LeaseRecoveryOptions(RecoverCurrentProcessLeases: false),
            StoreWaitOptions.Infinite,
            out _);
        counters.Record(OperationKind.RecoverLeases, leaseRecovery);
        StoreStatus reservationRecovery = store.TryRecoverReservations(
            new ReservationRecoveryOptions(RecoverCurrentProcessReservations: false),
            StoreWaitOptions.Infinite,
            out _);
        counters.Record(OperationKind.RecoverReservations, reservationRecovery);
        if (leaseRecovery != StoreStatus.Success || reservationRecovery != StoreStatus.Success)
        {
            failures++;
        }
    }
}

static bool PublishGeneration(
    Store store,
    ReadOnlySpan<byte> key,
    int keyIndex,
    long generation,
    int payloadBytes,
    StatusCounters counters,
    bool retryDuplicate = false)
{
    Span<byte> descriptor = stackalloc byte[BenchmarkDescriptorBytes];
    BenchmarkProtocol.WriteDescriptor(descriptor, keyIndex, generation, payloadBytes);
    StoreStatus reserve;
    ValueReservation reservation;
    var attempt = 0;
    do
    {
        reserve = store.TryReserve(
            key,
            payloadBytes,
            descriptor,
            StoreWaitOptions.Infinite,
            out reservation);
        counters.Record(OperationKind.Reserve, reserve);
        RetryPause(attempt++);
    }
    // A pending-removal lifecycle continues to own its key until its final
    // reader releases and reclamation completes. Only mixed remove/republish
    // calls opt into retrying the contractually expected DuplicateKey result.
    while ((reserve == StoreStatus.StoreBusy || (retryDuplicate && reserve == StoreStatus.DuplicateKey))
        && attempt < 4096);
    if (reserve != StoreStatus.Success)
    {
        return false;
    }

    using (reservation)
    {
        BenchmarkProtocol.FillGenerationPayload(reservation.GetSpan(payloadBytes), keyIndex, generation);
        StoreStatus advance;
        attempt = 0;
        do
        {
            advance = reservation.Advance(payloadBytes, StoreWaitOptions.Infinite);
            counters.Record(OperationKind.Advance, advance);
            RetryPause(attempt++);
        }
        while (advance == StoreStatus.StoreBusy && attempt < 4096);
        if (advance != StoreStatus.Success)
        {
            return false;
        }

        StoreStatus commit;
        attempt = 0;
        do
        {
            commit = reservation.Commit(StoreWaitOptions.Infinite);
            counters.Record(OperationKind.Commit, commit);
            RetryPause(attempt++);
        }
        while (commit == StoreStatus.StoreBusy && attempt < 4096);
        return commit == StoreStatus.Success;
    }
}

static StoreStatus AcquireWithRetry(
    Store store,
    ReadOnlySpan<byte> key,
    StatusCounters counters,
    out ValueLease lease)
{
    StoreStatus status;
    var attempt = 0;
    do
    {
        status = store.TryAcquire(key, StoreWaitOptions.Infinite, out lease);
        counters.Record(OperationKind.Acquire, status);
        RetryPause(attempt++);
    }
    while (status == StoreStatus.StoreBusy && attempt < 4096);

    return status;
}

static StoreStatus ReleaseWithRetry(ValueLease lease, StatusCounters counters)
{
    StoreStatus status;
    var attempt = 0;
    do
    {
        status = lease.Release(StoreWaitOptions.Infinite);
        counters.Record(OperationKind.Release, status);
        RetryPause(attempt++);
    }
    while (status == StoreStatus.StoreBusy && attempt < 4096);

    return status;
}

static StoreStatus PublishWithRetry(
    Store store,
    ReadOnlySpan<byte> key,
    ReadOnlySpan<byte> value,
    StatusCounters counters)
{
    StoreStatus status;
    var attempt = 0;
    do
    {
        status = store.TryPublish(key, value, default, StoreWaitOptions.Infinite);
        counters.Record(OperationKind.Publish, status);
        if (status == StoreStatus.CorruptStore)
        {
            counters.RecordCorruptReason(
                LockFreeCorruptionTrace.Consume() ?? "untraced");
        }
        RetryPause(attempt++);
    }
    while (status == StoreStatus.StoreBusy && attempt < 4096);

    return status;
}

static StoreStatus RemoveWithRetry(Store store, ReadOnlySpan<byte> key, StatusCounters counters)
{
    StoreStatus status;
    var attempt = 0;
    var observedBusy = false;
    do
    {
        status = store.TryRemove(key, StoreWaitOptions.Infinite);
        counters.Record(OperationKind.Remove, status);
        if (status == StoreStatus.CorruptStore)
        {
            counters.RecordCorruptReason(
                LockFreeCorruptionTrace.Consume() ?? "untraced");
        }
        observedBusy |= status == StoreStatus.StoreBusy;
        RetryPause(attempt++);
    }
    while (status == StoreStatus.StoreBusy && attempt < 4096);

    // A bounded helper may report StoreBusy after another participant completes
    // this exact unlink.  A retry observing NotFound proves the requested key is
    // now logically absent and is therefore the successful final state.
    return observedBusy && status == StoreStatus.NotFound
        ? StoreStatus.Success
        : status;
}

static void RetryPause(int attempt)
{
    if (attempt > 0)
    {
        Thread.SpinWait(4 << Math.Min(attempt, 10));
    }
}

static SharedMemoryStoreOptions Options(
    string name,
    OpenMode mode,
    StoreProfile profile,
    string scenario,
    int payloadBytes)
{
    bool reader = scenario is "same-key-read" or "distributed-key-read";
    bool mixed = scenario.StartsWith("mixed-churn", StringComparison.Ordinal);
    bool broker = scenario is "broker-directed" or "large-ingest";
    int slotCount = mixed
        ? MixedSlotCount
        : broker
            ? BrokerSlotCount
            : reader ? ReaderSlotCount : SyncSlotCount;
    int valueBytes = mixed
        ? MixedPayloadBytes
        : broker
            ? payloadBytes
            : reader ? ReaderPayloadBytes : SyncValueBytes;
    int descriptorBytes = mixed || broker ? BenchmarkDescriptorBytes : 0;
    int leaseRecords = mixed ? MixedLeaseRecordCount : DefaultLeaseRecordCount;
    return profile == StoreProfile.LockFree
        ? SharedMemoryStoreOptions.CreateLockFree(
            name,
            slotCount,
            valueBytes,
            descriptorBytes,
            MaxKeyBytes,
            leaseRecords,
            ParticipantRecordCount,
            mode,
            enableLeaseRecovery: true)
        : SharedMemoryStoreOptions.Create(
            name,
            slotCount,
            valueBytes,
            descriptorBytes,
            MaxKeyBytes,
            leaseRecords,
            mode,
            enableLeaseRecovery: true);
}

static SharedMemoryStoreOptions StickyOverflowOptions(string name, int slotCount) =>
    SharedMemoryStoreOptions.CreateLockFree(
        name,
        slotCount,
        1,
        0,
        MaxKeyBytes,
        DefaultLeaseRecordCount,
        ParticipantRecordCount,
        OpenMode.CreateNew,
        enableLeaseRecovery: true);

static byte[] ReaderPayload(int keyIndex)
{
    var payload = new byte[ReaderPayloadBytes];
    for (var index = 0; index < payload.Length; index++)
    {
        payload[index] = ExpectedReaderByte(keyIndex, index);
    }

    return payload;
}

static bool ValidateReaderPayload(ReadOnlySpan<byte> payload, int keyIndex)
{
    if (payload.Length != ReaderPayloadBytes)
    {
        return false;
    }

    for (var index = 0; index < payload.Length; index++)
    {
        if (payload[index] != ExpectedReaderByte(keyIndex, index))
        {
            return false;
        }
    }

    return true;
}

static byte ExpectedReaderByte(int keyIndex, int byteIndex) =>
    unchecked((byte)(keyIndex * 31 + byteIndex * 17 + 0x5A));

static Task<BrokerMeasuredResult> RunBrokerMeasuredOnDedicatedThread(
    Store producer,
    IReadOnlyList<Process> readers,
    IReadOnlyList<Process> observers,
    BenchmarkKeyCatalog keys,
    long[] readerFrames,
    int frameBytes,
    int durationSeconds,
    long frameTarget)
{
    var completion = new TaskCompletionSource<BrokerMeasuredResult>(
        TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try
        {
            completion.SetResult(RunBrokerMeasuredRegion(
                producer,
                readers,
                observers,
                keys,
                readerFrames,
                frameBytes,
                durationSeconds,
                frameTarget));
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
    })
    {
        IsBackground = true,
        Name = "SharedMemoryStore benchmark producer"
    };
    thread.Start();
    return completion.Task;
}

static BrokerMeasuredResult RunBrokerMeasuredRegion(
    Store producer,
    IReadOnlyList<Process> readers,
    IReadOnlyList<Process> observers,
    BenchmarkKeyCatalog keys,
    long[] readerFrames,
    int frameBytes,
    int durationSeconds,
    long frameTarget)
{
    WarmBrokerProducerCoordinatorThread(producer, readers, observers, keys, frameBytes);
    ResetBrokerWorkersSync(readers, observers);

    var counters = new StatusCounters();
    var samples = new List<double>(MaxLatencySamplesPerWorker);
    var earlySamples = new List<double>(MaxLatencySamplesPerWorker);
    var lateSamples = new List<double>(MaxLatencySamplesPerWorker);
    var pending = new PendingBrokerFrame[readers.Count];
    var measured = new Stopwatch();
    long failures = 0;
    long frames = 0;
    long producerStoreOperationAllocatedBytes = 0;

    // This is one contiguous interval on an explicitly created thread. It
    // intentionally includes test-broker serialization and pipe coordination;
    // ProducerStoreOperationAllocatedBytes separately isolates the synchronous
    // store calls made by the same thread.
    long measuredAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    measured.Start();
    while ((frameTarget > 0 && frames < frameTarget)
        || (frameTarget == 0 && (frames == 0 || measured.Elapsed.TotalSeconds < durationSeconds)))
    {
        var pendingCount = 0;
        bool batchFailed = false;
        int batchSize = frameTarget > 0
            ? checked((int)Math.Min(readers.Count, frameTarget - frames))
            : readers.Count;
        for (var batchOffset = 0; batchOffset < batchSize; batchOffset++)
        {
            long frameNumber = frames;
            int keyIndex = (int)(frameNumber % BrokerRotatingKeyCount);
            ReadOnlyMemory<byte> key = keys[keyIndex];
            if (frameNumber >= BrokerRotatingKeyCount)
            {
                long operationAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                StoreStatus remove = RemoveWithRetry(producer, key.Span, counters);
                producerStoreOperationAllocatedBytes +=
                    GC.GetAllocatedBytesForCurrentThread() - operationAllocatedBefore;
                if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
                {
                    failures++;
                    batchFailed = true;
                    break;
                }
            }

            long generation = frameNumber + 1;
            long started = Stopwatch.GetTimestamp();
            long publishAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool published = PublishGeneration(
                producer,
                key.Span,
                keyIndex,
                generation,
                frameBytes,
                counters);
            producerStoreOperationAllocatedBytes +=
                GC.GetAllocatedBytesForCurrentThread() - publishAllocatedBefore;
            if (!published)
            {
                failures++;
                batchFailed = true;
                break;
            }

            var message = new BrokerKeyMessage(
                BrokerMessageKind.Key,
                keys.Hex(keyIndex),
                keyIndex,
                generation,
                frameBytes,
                started);
            string encoded = JsonSerializer.Serialize(message, BenchmarkProtocol.JsonOptions);
            int readerId = (int)(frameNumber % readers.Count);
            Process assignedReader = readers[readerId];
            assignedReader.StandardInput.WriteLine(encoded);
            assignedReader.StandardInput.Flush();

            bool observerSampled = frameNumber % BrokerObserverSamplingInterval == 0;
            if (observerSampled)
            {
                foreach (Process observer in observers)
                {
                    observer.StandardInput.WriteLine(encoded);
                    observer.StandardInput.Flush();
                }
            }

            pending[pendingCount++] = new PendingBrokerFrame(
                frameNumber,
                keyIndex,
                generation,
                started,
                readerId,
                observerSampled);
            frames++;
        }

        for (var index = 0; index < pendingCount; index++)
        {
            PendingBrokerFrame frame = pending[index];
            BrokerAcknowledgement assignedAck = ReadBrokerAcknowledgementSync(readers[frame.ReaderId]);
            if (assignedAck.WorkerId != frame.ReaderId
                || !IsValidAcknowledgement(
                    assignedAck,
                    frame.KeyIndex,
                    frame.Generation,
                    frameBytes,
                    "reader"))
            {
                failures++;
            }

            readerFrames[frame.ReaderId]++;
            if (frame.ObserverSampled)
            {
                foreach (Process observer in observers)
                {
                    BrokerAcknowledgement observerAck = ReadBrokerAcknowledgementSync(observer);
                    if (!IsValidAcknowledgement(
                        observerAck,
                        frame.KeyIndex,
                        frame.Generation,
                        frameBytes,
                        "observer"))
                    {
                        failures++;
                    }
                }
            }

            if (frame.FrameNumber % SamplingInterval == 0
                && samples.Count < MaxLatencySamplesPerWorker)
            {
                double latency = Stopwatch.GetElapsedTime(frame.PublishedTimestamp).TotalMicroseconds;
                samples.Add(latency);
                bool early = frameTarget > 0
                    ? frame.FrameNumber < frameTarget / 2
                    : measured.Elapsed.TotalSeconds < durationSeconds / 2.0;
                (early ? earlySamples : lateSamples).Add(latency);
            }
        }

        if (batchFailed)
        {
            break;
        }
    }

    measured.Stop();
    long measuredThreadAllocatedBytes =
        GC.GetAllocatedBytesForCurrentThread() - measuredAllocatedBefore;
    return new BrokerMeasuredResult(
        frames,
        failures,
        measured.Elapsed,
        measuredThreadAllocatedBytes,
        producerStoreOperationAllocatedBytes,
        counters,
        samples.ToArray(),
        earlySamples.ToArray(),
        lateSamples.ToArray());
}

static void WarmBrokerProducerCoordinatorThread(
    Store producer,
    IReadOnlyList<Process> readers,
    IReadOnlyList<Process> observers,
    BenchmarkKeyCatalog keys,
    int frameBytes)
{
    var counters = new StatusCounters();
    int warmFrames = Math.Max(readers.Count, BrokerObserverSamplingInterval);
    for (var frame = 0; frame < warmFrames; frame++)
    {
        int keyIndex = frame % keys.Count;
        ReadOnlyMemory<byte> key = keys[keyIndex];
        long generation = -frame - 1L;
        long started = Stopwatch.GetTimestamp();
        if (!PublishGeneration(producer, key.Span, keyIndex, generation, frameBytes, counters))
        {
            throw new InvalidOperationException($"Dedicated producer-thread warm-up publication {frame} failed.");
        }

        var message = new BrokerKeyMessage(
            BrokerMessageKind.Key,
            keys.Hex(keyIndex),
            keyIndex,
            generation,
            frameBytes,
            started);
        string encoded = JsonSerializer.Serialize(message, BenchmarkProtocol.JsonOptions);
        int readerId = frame % readers.Count;
        Process reader = readers[readerId];
        reader.StandardInput.WriteLine(encoded);
        reader.StandardInput.Flush();
        bool observerSampled = frame % BrokerObserverSamplingInterval == 0;
        if (observerSampled)
        {
            foreach (Process observer in observers)
            {
                observer.StandardInput.WriteLine(encoded);
                observer.StandardInput.Flush();
            }
        }

        BrokerAcknowledgement readerAck = ReadBrokerAcknowledgementSync(reader);
        if (readerAck.WorkerId != readerId
            || !IsValidAcknowledgement(readerAck, keyIndex, generation, frameBytes, "reader"))
        {
            throw new InvalidOperationException($"Dedicated producer-thread reader warm-up {frame} failed validation.");
        }

        if (observerSampled)
        {
            foreach (Process observer in observers)
            {
                BrokerAcknowledgement observerAck = ReadBrokerAcknowledgementSync(observer);
                if (!IsValidAcknowledgement(observerAck, keyIndex, generation, frameBytes, "observer"))
                {
                    throw new InvalidOperationException(
                        $"Dedicated producer-thread observer warm-up {frame} failed validation.");
                }
            }
        }

        StoreStatus remove = RemoveWithRetry(producer, key.Span, counters);
        if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
        {
            throw new InvalidOperationException(
                $"Dedicated producer-thread warm-up removal {frame} failed: {remove}.");
        }
    }
}

static void ResetBrokerWorkersSync(
    IReadOnlyList<Process> readers,
    IReadOnlyList<Process> observers)
{
    var reset = new BrokerKeyMessage(BrokerMessageKind.Reset, string.Empty, 0, 0, 0, 0);
    string encoded = JsonSerializer.Serialize(reset, BenchmarkProtocol.JsonOptions);
    foreach (Process process in readers)
    {
        process.StandardInput.WriteLine(encoded);
        process.StandardInput.Flush();
    }

    foreach (Process process in observers)
    {
        process.StandardInput.WriteLine(encoded);
        process.StandardInput.Flush();
    }

    foreach (Process process in readers)
    {
        if (process.StandardOutput.ReadLine() != "RESET")
        {
            throw new InvalidOperationException("Broker reader did not reset after dedicated-thread warm-up.");
        }
    }

    foreach (Process process in observers)
    {
        if (process.StandardOutput.ReadLine() != "RESET")
        {
            throw new InvalidOperationException("Broker observer did not reset after dedicated-thread warm-up.");
        }
    }
}

static bool IsValidAcknowledgement(
    BrokerAcknowledgement acknowledgement,
    int keyIndex,
    long generation,
    int payloadBytes,
    string role) =>
    acknowledgement.Role == role
    && acknowledgement.KeyIndex == keyIndex
    && acknowledgement.Generation == generation
    && acknowledgement.AcquireStatus == StoreStatus.Success
    && acknowledgement.ReleaseStatus == StoreStatus.Success
    && acknowledgement.DescriptorValid
    && acknowledgement.PayloadValid
    && acknowledgement.BytesObserved == payloadBytes;

static BrokerAcknowledgement ReadBrokerAcknowledgementSync(Process process)
{
    string? line = process.StandardOutput.ReadLine();
    if (line is null)
    {
        string error = process.StandardError.ReadToEnd();
        throw new InvalidOperationException($"Broker worker ended before acknowledgement: {error}");
    }

    return JsonSerializer.Deserialize<BrokerAcknowledgement>(line, BenchmarkProtocol.JsonOptions);
}

static async Task<BrokerAcknowledgement> ReadBrokerAcknowledgement(Process process)
{
    string? line = await process.StandardOutput.ReadLineAsync();
    if (line is null)
    {
        string error = await process.StandardError.ReadToEndAsync();
        throw new InvalidOperationException($"Broker worker ended before acknowledgement: {error}");
    }

    return JsonSerializer.Deserialize<BrokerAcknowledgement>(line, BenchmarkProtocol.JsonOptions);
}

static async Task WarmBrokerWorkers(
    Store producer,
    IReadOnlyList<Process> readers,
    IReadOnlyList<Process> observers,
    BenchmarkKeyCatalog keys,
    int frameBytes,
    int warmupSeconds)
{
    var counters = new StatusCounters();
    long frame = 0;
    var elapsed = Stopwatch.StartNew();
    while (frame < readers.Count || elapsed.Elapsed.TotalSeconds < warmupSeconds)
    {
        int keyIndex = (int)(frame % keys.Count);
        ReadOnlyMemory<byte> key = keys[keyIndex];
        long generation = -frame - 1L;
        long started = Stopwatch.GetTimestamp();
        if (!PublishGeneration(producer, key.Span, keyIndex, generation, frameBytes, counters))
        {
            throw new InvalidOperationException($"Broker warm-up publication {frame} failed.");
        }

        var message = new BrokerKeyMessage(
            BrokerMessageKind.Key,
            keys.Hex(keyIndex),
            keyIndex,
            generation,
            frameBytes,
            started);
        string encoded = JsonSerializer.Serialize(message, BenchmarkProtocol.JsonOptions);
        int readerId = (int)(frame % readers.Count);
        Process reader = readers[readerId];
        await reader.StandardInput.WriteLineAsync(encoded);
        await reader.StandardInput.FlushAsync();
        bool observerSampled = frame % BrokerObserverSamplingInterval == 0;
        if (observerSampled)
        {
            foreach (Process observer in observers)
            {
                await observer.StandardInput.WriteLineAsync(encoded);
                await observer.StandardInput.FlushAsync();
            }
        }

        BrokerAcknowledgement readerAck = await ReadBrokerAcknowledgement(reader);
        if (readerAck.WorkerId != readerId
            || !IsValidAcknowledgement(readerAck, keyIndex, generation, frameBytes, "reader"))
        {
            throw new InvalidOperationException($"Broker reader warm-up {frame} failed validation.");
        }

        if (observerSampled)
        {
            foreach (Process observer in observers)
            {
                BrokerAcknowledgement observerAck = await ReadBrokerAcknowledgement(observer);
                if (!IsValidAcknowledgement(observerAck, keyIndex, generation, frameBytes, "observer"))
                {
                    throw new InvalidOperationException($"Broker observer warm-up {frame} failed validation.");
                }
            }
        }

        StoreStatus remove = RemoveWithRetry(producer, key.Span, counters);
        if (remove is not (StoreStatus.Success or StoreStatus.RemovePending))
        {
            throw new InvalidOperationException($"Broker warm-up removal {frame} failed: {remove}.");
        }

        frame++;
    }
}

static async Task ResetBrokerWorkers(IReadOnlyList<Process> processes)
{
    var reset = new BrokerKeyMessage(BrokerMessageKind.Reset, string.Empty, 0, 0, 0, 0);
    string encoded = JsonSerializer.Serialize(reset, BenchmarkProtocol.JsonOptions);
    foreach (Process process in processes)
    {
        await process.StandardInput.WriteLineAsync(encoded);
        await process.StandardInput.FlushAsync();
    }

    foreach (Process process in processes)
    {
        string? acknowledgement = await process.StandardOutput.ReadLineAsync();
        if (acknowledgement != "RESET")
        {
            string error = await process.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"Broker worker did not reset its measured counters: {acknowledgement}; {error}");
        }
    }
}

static string ScenarioRole(string scenario) => scenario switch
{
    "publish-remove" or "mixed-churn-writer" => "publisher",
    _ => "reader"
};

static void Ensure(StoreStatus status, string operation)
{
    if (status != StoreStatus.Success)
    {
        throw new InvalidOperationException($"{operation} failed: {status}");
    }
}

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0)
    {
        return 0;
    }

    int index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
    return sorted[index];
}

static double JainFairness(double[] rates)
{
    if (rates.Length == 0)
    {
        return 0;
    }

    double sum = rates.Sum();
    double squareSum = rates.Sum(static rate => rate * rate);
    return squareSum == 0 ? 0 : sum * sum / (rates.Length * squareSum);
}

static IReadOnlyList<SummaryResult> Summarize(IReadOnlyList<RunResult> runs)
{
    return runs
        .GroupBy(static run => new { run.Profile, run.Scenario, run.ProcessCount })
        .Select(group => new SummaryResult(
            group.Key.Profile,
            group.Key.Scenario,
            group.Key.ProcessCount,
            Median(group.Select(static run => run.ApiCallsPerSecond)),
            Median(group.Select(static run => run.P50Microseconds)),
            Median(group.Select(static run => run.P95Microseconds)),
            Median(group.Select(static run => run.P99Microseconds)),
            Median(group.Select(static run => run.MaxMicroseconds)),
            Median(group.Select(static run => run.EarlyP99Microseconds)),
            Median(group.Select(static run => run.LateP99Microseconds)),
            Median(group.Select(static run => run.LateToEarlyP99Ratio)),
            Median(group.Select(static run => run.FramesPerSecond)),
            Median(group.Select(static run => run.BytesPerSecond)),
            Median(group.Select(static run => run.FairnessIndex)),
            Median(group.Select(static run => run.WorstWorkerP99Microseconds)),
            group.Sum(static run => run.Frames),
            group.Sum(static run => run.BytesWritten),
            group.Sum(static run => run.FullPayloadCopies),
            group.Sum(static run => run.MeasuredThreadAllocatedBytes),
            group.Sum(static run => run.Failures),
            MergeHistograms(group.Select(static run => run.StatusHistogram)),
            group.All(static run => run.FullPayloadCopyCountIsInstrumented),
            group.Select(static run => run.FullPayloadCopyEvidenceKind)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            group.Sum(static run => run.ProducerStoreOperationAllocatedBytes),
            group.Select(static run => run.AllocationMeasurementScope)
                .Distinct(StringComparer.Ordinal)
                .ToArray()))
        .OrderBy(static result => result.Profile, StringComparer.Ordinal)
        .ThenBy(static result => result.Scenario, StringComparer.Ordinal)
        .ThenBy(static result => result.ProcessCount)
        .ToArray();
}

static SortedDictionary<string, long> MergeHistograms(IEnumerable<IReadOnlyDictionary<string, long>> histograms)
{
    var merged = new SortedDictionary<string, long>(StringComparer.Ordinal);
    foreach (IReadOnlyDictionary<string, long> histogram in histograms)
    {
        foreach ((string key, long value) in histogram)
        {
            merged[key] = merged.GetValueOrDefault(key) + value;
        }
    }

    return merged;
}

static double Median(IEnumerable<double> values)
{
    double[] sorted = values.Order().ToArray();
    return sorted.Length % 2 == 0
        ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0
        : sorted[sorted.Length / 2];
}

static string Qualification(
    bool oversubscribed,
    int durationSeconds,
    int warmupSeconds,
    long operationTarget,
    long frameTarget)
{
    if (oversubscribed)
    {
        return "not-qualified-oversubscribed";
    }

    if (warmupSeconds < ReleaseWarmupSeconds)
    {
        return "smoke-only-insufficient-warmup";
    }

    return durationSeconds >= 60 || operationTarget >= 100_000_000 || frameTarget >= 100_000
        ? "qualification-measurement"
        : "smoke-only";
}

static void KillProcesses(IReadOnlyList<Process> processes)
{
    for (var index = 0; index < processes.Count; index++)
    {
        try
        {
            Process process = processes[index];
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort only. Fail-fast remains the hard isolation boundary.
        }
    }
}

static void DisposeProcesses(IReadOnlyList<Process> processes)
{
    foreach (Process process in processes)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(milliseconds: 5_000);
            }
        }
        catch
        {
            // Preserve cleanup of every remaining child and the original trial result.
        }
        finally
        {
            try
            {
                process.Dispose();
            }
            catch
            {
                // Process disposal is idempotent from the controller's perspective.
            }
        }
    }
}

static StoreProfile[] ParseProfiles(string value, string optionName) => value.ToLowerInvariant() switch
{
    "legacy" => [StoreProfile.Legacy],
    "v2" or "lockfree" or "lock-free" => [StoreProfile.LockFree],
    "both" => [StoreProfile.Legacy, StoreProfile.LockFree],
    _ => throw new ArgumentException($"{optionName} must be legacy, v2, or both.")
};

static int ReadPositiveIntOption(string[] args, string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index + 1 >= args.Length
        || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        || value <= 0)
    {
        throw new ArgumentException($"{name} requires a positive integer.");
    }

    return value;
}

static int ReadPositivePowerOfTwoOption(string[] args, string name, int fallback)
{
    int value = ReadPositiveIntOption(args, name, fallback);
    if (value < 32 || (value & (value - 1)) != 0)
    {
        throw new ArgumentException($"{name} requires a power of two greater than or equal to 32.");
    }

    return value;
}

static int ReadNonNegativeIntOption(string[] args, string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index + 1 >= args.Length
        || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        || value < 0)
    {
        throw new ArgumentException($"{name} requires a non-negative integer.");
    }

    return value;
}

static long ReadPositiveLongOption(string[] args, string name, long fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index + 1 >= args.Length
        || !long.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long value)
        || value <= 0)
    {
        throw new ArgumentException($"{name} requires a positive integer.");
    }

    return value;
}

static long ReadNonNegativeLongOption(string[] args, string name, long fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return fallback;
    }

    if (index + 1 >= args.Length
        || !long.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long value)
        || value < 0)
    {
        throw new ArgumentException($"{name} requires a non-negative integer.");
    }

    return value;
}

static string? ReadStringOption(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static int[]? ParsePositiveIntListOption(string[] args, string name)
{
    string? raw = ReadStringOption(args, name);
    if (raw is null)
    {
        return null;
    }

    string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length == 0)
    {
        throw new ArgumentException($"{name} requires one or more positive integers.");
    }

    var values = new int[parts.Length];
    for (var index = 0; index < parts.Length; index++)
    {
        if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value <= 0)
        {
            throw new ArgumentException($"{name} requires a comma-separated list of positive integers.");
        }

        values[index] = value;
    }

    return values.Distinct().ToArray();
}

static long ReadPositiveLongEnvironment(string name, long fallback) =>
    long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
        && value > 0
        ? value
        : fallback;

static long ReadNonNegativeLongEnvironment(string name, long fallback) =>
    long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
        && value >= 0
        ? value
        : fallback;

static ProbeEnvironment CaptureEnvironment(RepositoryProvenanceSnapshot repositoryProvenance)
{
    HostHardwareInfo hardware = HostEnvironmentProbe.Capture();
    return new ProbeEnvironment(
        repositoryProvenance.Commit,
        repositoryProvenance.WorkingTreeState,
        TryGetFileSha256(typeof(Store).Assembly.Location),
        TryGetFileSha256(typeof(BenchmarkProtocol).Assembly.Location),
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.Version.ToString(),
        hardware.LogicalProcessorCount,
        hardware.PhysicalCoreCount,
        hardware.TotalMemoryBytes,
        hardware.ProcessorModel,
        hardware.ProcessorModel,
        GCSettings.IsServerGC,
        Stopwatch.Frequency);
}

static SortedDictionary<string, ProbeStoreDimensions> CreateScenarioStoreDimensions(
    IReadOnlyList<ScenarioPlan> plans,
    int largeFrameBytes,
    int stickyOverflowSlotCount)
{
    var dimensions = new SortedDictionary<string, ProbeStoreDimensions>(StringComparer.Ordinal);
    foreach (ScenarioPlan plan in plans)
    {
        dimensions[plan.Name] = plan.Name switch
        {
            "acquire-release" or "publish-remove" => new ProbeStoreDimensions(
                SyncSlotCount,
                SyncValueBytes,
                MaxDescriptorBytes: 0,
                MaxKeyBytes,
                DefaultLeaseRecordCount,
                ParticipantRecordCount),
            "same-key-read" or "distributed-key-read" => new ProbeStoreDimensions(
                ReaderSlotCount,
                ReaderPayloadBytes,
                MaxDescriptorBytes: 0,
                MaxKeyBytes,
                DefaultLeaseRecordCount,
                ParticipantRecordCount),
            "broker-directed" or "large-ingest" => new ProbeStoreDimensions(
                BrokerSlotCount,
                largeFrameBytes,
                BenchmarkDescriptorBytes,
                MaxKeyBytes,
                DefaultLeaseRecordCount,
                ParticipantRecordCount),
            "mixed-churn" => new ProbeStoreDimensions(
                MixedSlotCount,
                MixedPayloadBytes,
                BenchmarkDescriptorBytes,
                MaxKeyBytes,
                MixedLeaseRecordCount,
                ParticipantRecordCount),
            "sticky-overflow-miss" => new ProbeStoreDimensions(
                stickyOverflowSlotCount,
                MaxValueBytes: 1,
                MaxDescriptorBytes: 0,
                MaxKeyBytes,
                DefaultLeaseRecordCount,
                ParticipantRecordCount),
            _ => throw new InvalidOperationException(
                $"Scenario '{plan.Name}' does not declare benchmark store dimensions.")
        };
    }

    return dimensions;
}

static string TryGetFileSha256(string path)
{
    try
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    catch
    {
        return "unknown";
    }
}
