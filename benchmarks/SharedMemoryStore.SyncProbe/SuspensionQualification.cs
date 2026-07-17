using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SharedMemoryStore;
using SharedMemoryStore.LockFree;

using Store = SharedMemoryStore.MemoryStore;

internal static class SuspensionQualification
{
    private const int SlotCount = 512;
    private const int MaxValueBytes = 256;
    private const int MaxDescriptorBytes = 16;
    private const int MaxKeyBytes = 8;
    private const int LeaseRecordCount = 128;
    private const int ParticipantRecordCount = 64;
    private const int StableKeyCount = 256;
    private const int ReadKeyCount = 128;
    private const int KeyBase = 1_000_000;
    private const int TokenKeyIndex = 900_000;
    private const int ExistingKeyIndex = 900_001;
    private const int OperationKeyBase = 910_000;
    private const int RecoveryKeyBase = 920_000;
    private const double DefaultMinimumRatio = 0.90;
    private const int DefaultBaselineSeconds = 1;
    private const int DefaultPauseSeconds = 1;
    private const int DefaultWarmupSeconds = 1;
    private static readonly TimeSpan ChildStartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChildExitTimeout = TimeSpan.FromSeconds(30);

    internal static async Task<int> RunControllerAsync(string[] arguments)
    {
        int baselineSeconds = ReadPositiveInt(arguments, "--suspension-baseline-seconds", DefaultBaselineSeconds);
        int pauseSeconds = ReadPositiveInt(arguments, "--suspension-pause-seconds", DefaultPauseSeconds);
        int warmupSeconds = ReadNonNegativeInt(arguments, "--warmup", DefaultWarmupSeconds);
        double minimumRatio = ReadRatio(arguments, "--suspension-minimum-ratio", DefaultMinimumRatio);
        bool affinityRequested = !arguments.Contains("--no-affinity", StringComparer.Ordinal);
        string? outputPath = ReadString(arguments, "--output");
        string[] workloadFilter = ParseList(arguments, "--suspension-workloads");
        string[] checkpointFilter = ParseList(arguments, "--suspension-checkpoints");

        string agentPath = FindAgentAssembly();
        CheckpointCatalogEntry[] catalog = await ReadCheckpointCatalog(agentPath);
        CheckpointCatalogEntry[] checkpoints = catalog
            .Where(static checkpoint => checkpoint.Family is not ("Participant" or "Disposal"))
            .Where(checkpoint => checkpointFilter.Length == 0
                || checkpointFilter.Contains(checkpoint.Id.ToString(CultureInfo.InvariantCulture), StringComparer.OrdinalIgnoreCase)
                || checkpointFilter.Contains(checkpoint.Name, StringComparer.OrdinalIgnoreCase))
            .OrderBy(static checkpoint => checkpoint.Id)
            .ToArray();
        if (checkpoints.Length == 0)
        {
            throw new ArgumentException("No steady-state checkpoints matched --suspension-checkpoints.");
        }

        SuspensionWorkload[] workloads = Workloads
            .Where(workload => workloadFilter.Length == 0
                || workloadFilter.Contains(workload.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (workloads.Length == 0)
        {
            throw new ArgumentException("No workload matched --suspension-workloads.");
        }

        int availableProcessors = GetAvailableProcessorCount();
        var results = new List<SuspensionCheckpointResult>(checkpoints.Length * workloads.Length);
        foreach (SuspensionWorkload workload in workloads)
        {
            foreach (CheckpointCatalogEntry checkpoint in checkpoints)
            {
                int requiredProcessors = workload.HealthyProcessCount + 1;
                if (affinityRequested && availableProcessors < requiredProcessors)
                {
                    results.Add(NotQualifiedForProcessors(
                        checkpoint,
                        workload,
                        baselineSeconds,
                        pauseSeconds,
                        minimumRatio,
                        availableProcessors,
                        requiredProcessors));
                    Console.Error.WriteLine(
                        $"suspension {workload.Name} checkpoint={checkpoint.Id}:{checkpoint.Name} "
                        + $"qualification=not-qualified-insufficient-processors available={availableProcessors} "
                        + $"required={requiredProcessors}");
                    continue;
                }

                SuspensionCheckpointResult result;
                try
                {
                    result = await RunCheckpointAsync(
                        agentPath,
                        checkpoint,
                        workload,
                        baselineSeconds,
                        pauseSeconds,
                        warmupSeconds,
                        minimumRatio,
                        affinityRequested,
                        availableProcessors,
                        requiredProcessors);
                }
                catch (Exception exception)
                {
                    result = HarnessFailure(
                        checkpoint,
                        workload,
                        baselineSeconds,
                        pauseSeconds,
                        minimumRatio,
                        availableProcessors,
                        requiredProcessors,
                        exception);
                }

                results.Add(result);
                Console.Error.WriteLine(
                    $"suspension {workload.Name} checkpoint={checkpoint.Id}:{checkpoint.Name} "
                        + $"baseline={result.BaselineCompletedCyclesPerSecond:N0}/s "
                        + $"paused={result.SuspendedCompletedCyclesPerSecond:N0}/s ratio={result.ThroughputRatio:N3} "
                    + $"healthy={result.HealthyProcessCount} failures={result.CorrectnessFailureCount} "
                    + $"qualification={result.Qualification}");
            }
        }

        var report = new SuspensionQualificationReport(
            SchemaVersion: 1,
            TimestampUtc: DateTimeOffset.UtcNow,
            Environment: CaptureEnvironment(availableProcessors),
            Configuration: new SuspensionConfiguration(
                baselineSeconds,
                pauseSeconds,
                warmupSeconds,
                minimumRatio,
                affinityRequested,
                "physical-core-first-then-siblings",
                checkpoints.Length,
                workloads.Select(static workload => workload.Name).ToArray(),
                catalog.Length,
                ["Participant", "Disposal"],
                "Same persistent healthy process set is measured immediately before and while one external participant is blocked inside a production checkpoint."),
            Results: results,
            RequiredResultCount: checkpoints.Length * workloads.Length,
            QualifiedPassCount: results.Count(static result => result.Qualification == "qualified-pass"),
            SmokePassCount: results.Count(static result => result.Qualification == "smoke-pass"),
            FailCount: results.Count(static result => result.Qualification.EndsWith("-fail", StringComparison.Ordinal)),
            NotQualifiedCount: results.Count(static result => result.Qualification.StartsWith("not-qualified-", StringComparison.Ordinal)),
            AllRequiredQualifiedAndPassed: results.Count == checkpoints.Length * workloads.Length
                && results.All(static result => result.Qualification == "qualified-pass"));
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            Console.WriteLine(json);
        }
        else
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, json);
            Console.Error.WriteLine("report=" + fullPath);
        }

        return report.FailCount == 0 ? 0 : 2;
    }

    internal static int RunWorker(string[] arguments)
    {
        if (arguments.Length != 8
            || !int.TryParse(arguments[4], NumberStyles.None, CultureInfo.InvariantCulture, out int workerId)
            || !int.TryParse(arguments[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int affinityOrdinal)
            || !int.TryParse(arguments[7], NumberStyles.None, CultureInfo.InvariantCulture, out int warmupSeconds)
            || warmupSeconds < 0)
        {
            Console.Error.WriteLine(
                "suspension-worker requires <workload> <store-name> <role> <worker-id> <reserved> <affinity-ordinal> <warmup-seconds>.");
            return 64;
        }

        string workload = arguments[1];
        string storeName = arguments[2];
        string role = arguments[3];
        bool affinityApplied = ProcessorAffinityPlanner.TryApply(
            affinityOrdinal,
            out int assignedProcessor,
            out string affinityStrategy);
        StoreOpenStatus open = Store.TryCreateOrOpen(
            CreateOptions(storeName, OpenMode.OpenExisting),
            out Store? store);
        if (open != StoreOpenStatus.Success || store is null)
        {
            Console.Error.WriteLine("Suspension worker open failed: " + open);
            return 65;
        }

        using (store)
        {
            var state = new WorkerState(workload, role, workerId);
            if (!RunWarmup(store, state, warmupSeconds))
            {
                return 66;
            }

            Console.WriteLine(JsonSerializer.Serialize(new SuspensionWorkerReady(
                workerId,
                role,
                Environment.ProcessId,
                affinityApplied,
                assignedProcessor,
                affinityStrategy)));
            Console.Out.Flush();
            while (Console.ReadLine() is { } command)
            {
                if (string.Equals(command, "STOP", StringComparison.Ordinal))
                {
                    return 0;
                }

                string[] fields = command.Split('|');
                if (fields.Length != 3
                    || fields[0] != "MEASURE"
                    || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int durationMilliseconds)
                    || durationMilliseconds <= 0)
                {
                    return 67;
                }

                SuspensionWindowResult result = MeasureWindow(
                    store,
                    state,
                    fields[2],
                    durationMilliseconds,
                    affinityApplied,
                    assignedProcessor,
                    affinityStrategy);
                Console.WriteLine(JsonSerializer.Serialize(result));
                Console.Out.Flush();
            }
        }

        return 68;
    }

    private static readonly SuspensionWorkload[] Workloads =
    [
        new("distributed-key", ReaderCount: 6, WriterCount: 0),
        new("mixed-churn", ReaderCount: 12, WriterCount: 2)
    ];

    private static async Task<SuspensionCheckpointResult> RunCheckpointAsync(
        string agentPath,
        CheckpointCatalogEntry checkpoint,
        SuspensionWorkload workload,
        int baselineSeconds,
        int pauseSeconds,
        int warmupSeconds,
        double minimumRatio,
        bool affinityRequested,
        int availableProcessors,
        int requiredProcessors)
    {
        string storeName = $"sms-suspend-{Guid.NewGuid():N}";
        StoreOpenStatus open = Store.TryCreateOrOpen(
            CreateOptions(storeName, OpenMode.CreateNew),
            out Store? owner);
        if (open != StoreOpenStatus.Success || owner is null)
        {
            throw new InvalidOperationException("Suspension owner open failed: " + open);
        }

        using (owner)
        {
            Seed(owner, workload.Name);
            (int spillFirstBucket, int spillSecondBucket) = SelectUnusedSpillBucketPair(workload.Name);
            var workers = new List<SuspensionWorkerProcess>(workload.HealthyProcessCount);
            Process? pausedAgent = null;
            bool agentReleased = false;
            try
            {
                for (var readerId = 0; readerId < workload.ReaderCount; readerId++)
                {
                    workers.Add(StartWorker(
                        workload.Name,
                        storeName,
                        "reader",
                        readerId,
                        affinityRequested ? readerId : -1,
                        warmupSeconds));
                }

                for (var writerId = 0; writerId < workload.WriterCount; writerId++)
                {
                    workers.Add(StartWorker(
                        workload.Name,
                        storeName,
                        "writer",
                        writerId,
                        affinityRequested ? workload.ReaderCount + writerId : -1,
                        warmupSeconds));
                }

                SuspensionWorkerReady[] ready = await AwaitWorkersReady(workers);
                DiagnosticsSnapshot beforeBaseline = GetDiagnostics(owner);
                SuspensionWindowResult[] baseline = await MeasureWorkers(
                    workers,
                    baselineSeconds,
                    "baseline");
                DiagnosticsSnapshot afterBaseline = GetDiagnostics(owner);

                pausedAgent = StartPausedAgent(
                    agentPath,
                    storeName,
                    checkpoint.Id,
                    workload.Name,
                    spillFirstBucket,
                    spillSecondBucket);
                string signalLine = await ReadLine(pausedAgent, ChildStartupTimeout)
                    ?? throw new InvalidOperationException(
                        "Paused checkpoint agent exited before signaling: "
                        + (await pausedAgent.StandardError.ReadToEndAsync()).Trim());
                if (!signalLine.StartsWith("CHECKPOINT ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Unexpected checkpoint signal: " + signalLine);
                }

                CheckpointSignal signal = JsonSerializer.Deserialize<CheckpointSignal>(signalLine[11..])
                    ?? throw new InvalidOperationException("Checkpoint agent emitted invalid signal JSON.");
                if (signal.Id != checkpoint.Id)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint agent paused at {signal.Id}, expected {checkpoint.Id}.");
                }

                int pausedProcessor = -1;
                string pausedAffinityStrategy = "not-requested";
                bool pausedAffinityApplied = affinityRequested
                    && ProcessorAffinityPlanner.TryApply(
                        pausedAgent,
                        workload.HealthyProcessCount,
                        out pausedProcessor,
                        out pausedAffinityStrategy);

                if (IsStoreFullProofCheckpoint(checkpoint.Id))
                {
                    RemoveStoreFullFillers(owner, checkpoint.Id);
                }

                DiagnosticsSnapshot beforeSuspended = GetDiagnostics(owner);
                SuspensionWindowResult[] suspended = await MeasureWorkers(
                    workers,
                    pauseSeconds,
                    "suspended");
                DiagnosticsSnapshot afterSuspended = GetDiagnostics(owner);

                await pausedAgent.StandardInput.WriteLineAsync("CONTINUE");
                await pausedAgent.StandardInput.FlushAsync();
                agentReleased = true;
                Task<string> agentOutput = pausedAgent.StandardOutput.ReadToEndAsync();
                Task<string> agentError = pausedAgent.StandardError.ReadToEndAsync();
                await pausedAgent.WaitForExitAsync().WaitAsync(ChildExitTimeout);
                string trailingOutput = await agentOutput;
                string trailingError = await agentError;
                int agentExitCode = pausedAgent.ExitCode;
                DiagnosticsSnapshot afterResume = GetDiagnostics(owner);

                long baselineFailures = baseline.Sum(static result => result.Failures);
                long suspendedFailures = suspended.Sum(static result => result.Failures);
                long correctnessFailures = baselineFailures + suspendedFailures;
                var correctnessErrors = new List<string>();
                if (agentExitCode != 0)
                {
                    correctnessErrors.Add(
                        $"checkpoint-agent-exit={agentExitCode}; stderr={trailingError.Trim()}; stdout={trailingOutput.Trim()}");
                }

                if (IsStoreFullProofCheckpoint(checkpoint.Id)
                    && (beforeSuspended.ActiveReservationCount != 0
                        || beforeSuspended.InitializingSlotCount != 0
                        || beforeSuspended.ReservedSlotCount != 0
                        || beforeSuspended.ReclaimingSlotCount != 0))
                {
                    correctnessErrors.Add(
                        "store-full-proof-paused-ownership="
                        + $"reservations:{beforeSuspended.ActiveReservationCount},"
                        + $"initializing:{beforeSuspended.InitializingSlotCount},"
                        + $"reserved:{beforeSuspended.ReservedSlotCount},"
                        + $"reclaiming:{beforeSuspended.ReclaimingSlotCount}");
                }

                if (afterResume.ActiveLeaseCount != 0)
                {
                    correctnessErrors.Add("active-leases-after-resume=" + afterResume.ActiveLeaseCount);
                }

                if (afterResume.ActiveReservationCount != 0)
                {
                    correctnessErrors.Add("active-reservations-after-resume=" + afterResume.ActiveReservationCount);
                }

                int transitionalSlots = afterResume.InitializingSlotCount
                    + afterResume.ReservedSlotCount
                    + afterResume.ReclaimingSlotCount;
                if (transitionalSlots != 0)
                {
                    correctnessErrors.Add("transitional-slots-after-resume=" + transitionalSlots);
                }

                int transitionalLeases = afterResume.ClaimingLeaseCount + afterResume.RecoveringLeaseCount;
                if (transitionalLeases != 0)
                {
                    correctnessErrors.Add("transitional-leases-after-resume=" + transitionalLeases);
                }

                int transitionalParticipants = afterResume.RegisteringParticipantCount
                    + afterResume.ClosingParticipantCount
                    + afterResume.RecoveringParticipantCount
                    + afterResume.ReclaimingParticipantCount;
                if (transitionalParticipants != 0)
                {
                    correctnessErrors.Add("transitional-participants-after-resume=" + transitionalParticipants);
                }

                correctnessFailures += correctnessErrors.Count;
                double baselineThroughput = baseline.Sum(static result => result.CompletedCyclesPerSecond);
                double suspendedThroughput = suspended.Sum(static result => result.CompletedCyclesPerSecond);
                long baselineAttemptedCycles = baseline.Sum(static result => result.AttemptedCycles);
                long baselineCompletedCycles = baseline.Sum(static result => result.CompletedCycles);
                long baselineApiCalls = baseline.Sum(static result => result.ApiCalls);
                long suspendedAttemptedCycles = suspended.Sum(static result => result.AttemptedCycles);
                long suspendedCompletedCycles = suspended.Sum(static result => result.CompletedCycles);
                long suspendedApiCalls = suspended.Sum(static result => result.ApiCalls);
                double baselineApiCallsPerSecond = baseline.Sum(static result => result.ApiCallsPerSecond);
                double suspendedApiCallsPerSecond = suspended.Sum(static result => result.ApiCallsPerSecond);
                double ratio = baselineThroughput <= 0 ? 0 : suspendedThroughput / baselineThroughput;
                bool capacityPermits = CapacityPermits(afterBaseline, workload.HealthyProcessCount)
                    && CapacityPermits(beforeSuspended, workload.HealthyProcessCount)
                    && CapacityPermits(afterSuspended, workload.HealthyProcessCount);
                int healthyAffinityCount = ready.Count(static result => result.AffinityApplied);
                bool affinityQualified = !affinityRequested
                    || (healthyAffinityCount == workload.HealthyProcessCount && pausedAffinityApplied);
                bool releaseDurationEligible = pauseSeconds >= 30;
                string passLabel = releaseDurationEligible ? "qualified-pass" : "smoke-pass";
                string failLabel = releaseDurationEligible ? "qualified-fail" : "smoke-fail";
                string qualification = correctnessFailures != 0
                    ? failLabel
                    : !capacityPermits
                        ? "not-qualified-capacity-pressure"
                        : !affinityQualified
                            ? "not-qualified-affinity"
                            : !releaseDurationEligible || ratio >= minimumRatio
                                ? passLabel
                                : failLabel;

                return new SuspensionCheckpointResult(
                    checkpoint.Id,
                    checkpoint.Name,
                    checkpoint.Family,
                    checkpoint.Position,
                    checkpoint.Pause,
                    checkpoint.Crash,
                    checkpoint.Race,
                    checkpoint.IsPublicOrderingPoint,
                    workload.Name,
                    workload.ReaderCount,
                    workload.WriterCount,
                    workload.HealthyProcessCount,
                    baselineSeconds,
                    pauseSeconds,
                    baselineThroughput,
                    suspendedThroughput,
                    baselineAttemptedCycles,
                    baselineCompletedCycles,
                    baselineApiCalls,
                    suspendedAttemptedCycles,
                    suspendedCompletedCycles,
                    suspendedApiCalls,
                    baselineApiCallsPerSecond,
                    suspendedApiCallsPerSecond,
                    ratio,
                    minimumRatio,
                    capacityPermits,
                    correctnessFailures,
                    correctnessErrors.ToArray(),
                    healthyAffinityCount,
                    pausedAffinityApplied,
                    pausedProcessor,
                    pausedAffinityStrategy,
                    spillFirstBucket,
                    spillSecondBucket,
                    availableProcessors,
                    requiredProcessors,
                    signal.ProcessId,
                    agentExitCode,
                    qualification,
                    qualification.EndsWith("-pass", StringComparison.Ordinal),
                    baseline,
                    suspended,
                    CapacityEvidence(beforeBaseline),
                    CapacityEvidence(afterBaseline),
                    CapacityEvidence(beforeSuspended),
                    CapacityEvidence(afterSuspended),
                    CapacityEvidence(afterResume));
            }
            finally
            {
                if (pausedAgent is not null)
                {
                    try
                    {
                        if (!pausedAgent.HasExited && !agentReleased)
                        {
                            await pausedAgent.StandardInput.WriteLineAsync("CONTINUE");
                            await pausedAgent.StandardInput.FlushAsync();
                        }

                        if (!pausedAgent.HasExited)
                        {
                            using var timeout = new CancellationTokenSource(ChildExitTimeout);
                            await pausedAgent.WaitForExitAsync(timeout.Token);
                        }
                    }
                    catch
                    {
                        if (!pausedAgent.HasExited)
                        {
                            pausedAgent.Kill(entireProcessTree: true);
                        }
                    }
                    finally
                    {
                        pausedAgent.Dispose();
                    }
                }

                await StopWorkers(workers);
            }
        }
    }

    private static bool RunWarmup(Store store, WorkerState state, int seconds)
    {
        var counters = new WindowCounters();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed.TotalSeconds < seconds)
        {
            ExecuteCycle(store, state, counters);
        }

        return counters.Failures == 0;
    }

    private static SuspensionWindowResult MeasureWindow(
        Store store,
        WorkerState state,
        string window,
        int durationMilliseconds,
        bool affinityApplied,
        int assignedProcessor,
        string affinityStrategy)
    {
        var counters = new WindowCounters();
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < durationMilliseconds)
        {
            ExecuteCycle(store, state, counters);
        }

        elapsed.Stop();
        double seconds = elapsed.Elapsed.TotalSeconds;
        return new SuspensionWindowResult(
            state.WorkerId,
            state.Role,
            Environment.ProcessId,
            window,
            counters.AttemptedCycles,
            counters.CompletedCycles,
            counters.ApiCalls,
            seconds,
            seconds <= 0 ? 0 : counters.CompletedCycles / seconds,
            seconds <= 0 ? 0 : counters.ApiCalls / seconds,
            counters.Failures,
            affinityApplied,
            assignedProcessor,
            affinityStrategy,
            counters.ToHistogram());
    }

    private static void ExecuteCycle(Store store, WorkerState state, WindowCounters counters)
    {
        counters.AttemptedCycles++;
        if (state.Role == "reader")
        {
            int keyCount = StableKeyCount;
            int keyIndex = (int)((state.Cycle + state.WorkerId * 17L) % keyCount);
            byte[] key = state.Keys[keyIndex];
            StoreStatus acquire = store.TryAcquire(key, out ValueLease lease);
            counters.Record(OperationKind.Acquire, acquire);
            if (state.Workload == "mixed-churn" && acquire == StoreStatus.NotFound)
            {
                counters.CompletedCycles++;
                state.Cycle++;
                return;
            }

            if (acquire != StoreStatus.Success)
            {
                counters.Failures++;
                state.Cycle++;
                return;
            }

            bool descriptorValid = lease.DescriptorSpan.Length == MaxDescriptorBytes;
            long readerGeneration = descriptorValid
                ? System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(lease.DescriptorSpan)
                : long.MinValue;
            descriptorValid = descriptorValid
                && BenchmarkProtocol.ValidateDescriptor(
                    lease.DescriptorSpan,
                    keyIndex,
                    readerGeneration,
                    MaxValueBytes);
            bool payloadValid = descriptorValid
                && BenchmarkProtocol.ValidateGenerationPayload(lease.ValueSpan, keyIndex, readerGeneration);
            if (!descriptorValid || !payloadValid)
            {
                counters.Failures++;
                counters.RecordChecksumFailure();
            }

            StoreStatus release = lease.Release();
            counters.Record(OperationKind.Release, release);
            if (release != StoreStatus.Success)
            {
                counters.Failures++;
            }
            else if (descriptorValid && payloadValid)
            {
                counters.CompletedCycles++;
            }

            state.Cycle++;
            return;
        }

        int writerKeyIndex = ReadKeyCount
            + state.WorkerId
            + (int)((state.Cycle % ((StableKeyCount - ReadKeyCount) / 2)) * 2);
        byte[] writerKey = state.Keys[writerKeyIndex];
        if (!RemoveForRewrite(store, writerKey, counters))
        {
            counters.Failures++;
            state.Cycle++;
            return;
        }

        long writerGeneration = checked(((long)(state.WorkerId + 1) << 56) | (state.Cycle + 1));
        if (!ReserveAndCommit(store, writerKey, writerKeyIndex, writerGeneration, counters))
        {
            counters.Failures++;
        }
        else
        {
            counters.CompletedCycles++;
        }

        state.Cycle++;
    }

    private static bool RemoveForRewrite(Store store, byte[] key, WindowCounters counters)
    {
        for (var attempt = 0; attempt < 4096; attempt++)
        {
            StoreStatus status = store.TryRemove(key);
            counters.Record(OperationKind.Remove, status);
            if (status is StoreStatus.Success or StoreStatus.NotFound)
            {
                return true;
            }

            if (status is not (StoreStatus.RemovePending or StoreStatus.StoreBusy))
            {
                return false;
            }

            Thread.SpinWait(4 << Math.Min(attempt, 10));
        }

        return false;
    }

    private static bool ReserveAndCommit(
        Store store,
        byte[] key,
        int keyIndex,
        long generation,
        WindowCounters counters)
    {
        Span<byte> descriptor = stackalloc byte[MaxDescriptorBytes];
        BenchmarkProtocol.WriteDescriptor(descriptor, keyIndex, generation, MaxValueBytes);
        for (var attempt = 0; attempt < 4096; attempt++)
        {
            StoreStatus reserve = store.TryReserve(
                key,
                MaxValueBytes,
                descriptor,
                out ValueReservation reservation);
            counters.Record(OperationKind.Reserve, reserve);
            if (reserve == StoreStatus.Success)
            {
                BenchmarkProtocol.FillGenerationPayload(
                    reservation.GetSpan(MaxValueBytes),
                    keyIndex,
                    generation);
                StoreStatus advance = reservation.Advance(MaxValueBytes);
                counters.Record(OperationKind.Advance, advance);
                if (advance != StoreStatus.Success)
                {
                    _ = reservation.Abort();
                    return false;
                }

                StoreStatus commit = reservation.Commit();
                counters.Record(OperationKind.Commit, commit);
                return commit == StoreStatus.Success;
            }

            if (reserve is not (StoreStatus.DuplicateKey or StoreStatus.StoreBusy))
            {
                return false;
            }

            _ = RemoveForRewrite(store, key, counters);
            Thread.SpinWait(4 << Math.Min(attempt, 10));
        }

        return false;
    }

    private static void Seed(Store store, string workload)
    {
        byte[][] keys = CreateWorkloadKeys(workload);
        for (var keyIndex = 0; keyIndex < StableKeyCount; keyIndex++)
        {
            PublishSeed(store, keys[keyIndex], keyIndex, generation: 0);
        }

        PublishSeed(store, BenchmarkProtocol.Key(TokenKeyIndex), StableKeyCount, generation: 0);
        PublishSeed(store, BenchmarkProtocol.Key(ExistingKeyIndex), StableKeyCount + 1, generation: 0);
    }

    private static byte[][] CreateWorkloadKeys(string workload)
    {
        if (workload == "distributed-key")
        {
            return Enumerable.Range(0, StableKeyCount)
                .Select(static index => BenchmarkProtocol.Key(KeyBase + index))
                .ToArray();
        }

        return BenchmarkProtocol.CreateCollisionKeys(
            StableKeyCount,
            BenchmarkProtocol.CalculatePrimaryBucketCount(SlotCount));
    }

    private static (int First, int Second) SelectUnusedSpillBucketPair(string workload)
    {
        int bucketCount = BenchmarkProtocol.CalculatePrimaryBucketCount(SlotCount);
        var occupied = new bool[bucketCount];
        IEnumerable<byte[]> existingKeys = CreateWorkloadKeys(workload).Concat(
        [
            BenchmarkProtocol.Key(TokenKeyIndex),
            BenchmarkProtocol.Key(ExistingKeyIndex)
        ]);
        foreach (byte[] key in existingKeys)
        {
            (int first, int second) = BenchmarkProtocol.GetBucketPair(key, bucketCount);
            occupied[first] = true;
            occupied[second] = true;
        }

        int[] unused = Enumerable.Range(0, bucketCount)
            .Where(index => !occupied[index])
            .Take(2)
            .ToArray();
        if (unused.Length != 2)
        {
            throw new InvalidOperationException(
                "No isolated directory bucket pair is available for the paused spill transition.");
        }

        return (unused[0], unused[1]);
    }

    private static void PublishSeed(Store store, byte[] key, int keyIndex, long generation)
    {
        byte[] value = new byte[MaxValueBytes];
        BenchmarkProtocol.FillGenerationPayload(value, keyIndex, generation);
        Span<byte> descriptor = stackalloc byte[MaxDescriptorBytes];
        BenchmarkProtocol.WriteDescriptor(descriptor, keyIndex, generation, value.Length);
        StoreStatus status = store.TryPublish(key, value, descriptor);
        if (status != StoreStatus.Success)
        {
            throw new InvalidOperationException("Suspension seed publish failed: " + status);
        }
    }

    private static SuspensionWorkerProcess StartWorker(
        string workload,
        string storeName,
        string role,
        int workerId,
        int affinityOrdinal,
        int warmupSeconds)
    {
        ProcessStartInfo start = CreateSelfStartInfo();
        foreach (string argument in new[]
        {
            "suspension-worker",
            workload,
            storeName,
            role,
            workerId.ToString(CultureInfo.InvariantCulture),
            "reserved",
            affinityOrdinal.ToString(CultureInfo.InvariantCulture),
            warmupSeconds.ToString(CultureInfo.InvariantCulture)
        })
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start suspension worker.");
        return new SuspensionWorkerProcess(
            process,
            process.StandardError.ReadToEndAsync(),
            role,
            workerId);
    }

    private static async Task<SuspensionWorkerReady[]> AwaitWorkersReady(
        IReadOnlyList<SuspensionWorkerProcess> workers)
    {
        Task<SuspensionWorkerReady>[] tasks = workers.Select(async worker =>
        {
            string? line = await ReadLine(worker.Process, ChildStartupTimeout);
            if (line is null)
            {
                throw new InvalidOperationException(
                    await DescribeWorkerExit(worker, "before READY"));
            }

            return JsonSerializer.Deserialize<SuspensionWorkerReady>(line)
                ?? throw new InvalidOperationException("Suspension worker emitted invalid READY JSON.");
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    private static async Task<SuspensionWindowResult[]> MeasureWorkers(
        IReadOnlyList<SuspensionWorkerProcess> workers,
        int durationSeconds,
        string window)
    {
        string command = $"MEASURE|{checked(durationSeconds * 1000).ToString(CultureInfo.InvariantCulture)}|{window}";
        foreach (SuspensionWorkerProcess worker in workers)
        {
            await worker.Process.StandardInput.WriteLineAsync(command);
            await worker.Process.StandardInput.FlushAsync();
        }

        TimeSpan timeout = TimeSpan.FromSeconds(durationSeconds + 30);
        Task<SuspensionWindowResult>[] tasks = workers.Select(async worker =>
        {
            string? line = await ReadLine(worker.Process, timeout);
            if (line is null)
            {
                throw new InvalidOperationException(
                    await DescribeWorkerExit(worker, "during " + window));
            }

            return JsonSerializer.Deserialize<SuspensionWindowResult>(line)
                ?? throw new InvalidOperationException("Suspension worker emitted invalid window JSON.");
        }).ToArray();
        return await Task.WhenAll(tasks);
    }

    private static async Task StopWorkers(IEnumerable<SuspensionWorkerProcess> workers)
    {
        foreach (SuspensionWorkerProcess worker in workers)
        {
            Process process = worker.Process;
            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("STOP");
                    await process.StandardInput.FlushAsync();
                }
            }
            catch
            {
            }
        }

        foreach (SuspensionWorkerProcess worker in workers)
        {
            Process process = worker.Process;
            try
            {
                if (!process.HasExited)
                {
                    using var timeout = new CancellationTokenSource(ChildExitTimeout);
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
            catch
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            finally
            {
                try
                {
                    _ = await worker.StandardError.WaitAsync(ChildExitTimeout);
                }
                catch
                {
                }

                process.Dispose();
            }
        }
    }

    private static async Task<string> DescribeWorkerExit(
        SuspensionWorkerProcess worker,
        string phase)
    {
        Process process = worker.Process;
        try
        {
            if (!process.HasExited)
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch
        {
        }

        string exitCode = process.HasExited
            ? process.ExitCode.ToString(CultureInfo.InvariantCulture)
            : "not-exited";
        string standardError;
        try
        {
            standardError = (await worker.StandardError.WaitAsync(TimeSpan.FromSeconds(5))).Trim();
        }
        catch (Exception exception)
        {
            standardError = "unavailable:" + exception.GetType().Name + ":" + exception.Message;
        }

        const int maximumDiagnosticCharacters = 4096;
        if (standardError.Length > maximumDiagnosticCharacters)
        {
            standardError = standardError[^maximumDiagnosticCharacters..];
        }

        if (standardError.Length == 0)
        {
            standardError = "<empty>";
        }

        return $"Suspension worker role={worker.Role} workerId={worker.WorkerId} "
            + $"pid={process.Id} exited {phase}; "
            + $"exitCode={exitCode}; stderr={standardError}.";
    }

    private static Process StartPausedAgent(
        string agentPath,
        string storeName,
        int checkpointId,
        string workload,
        int spillFirstBucket,
        int spillSecondBucket)
    {
        ProcessStartInfo start = CreateAgentStartInfo(agentPath);
        byte[] value = new byte[32];
        BenchmarkProtocol.FillGenerationPayload(value, checkpointId, generation: 1);
        Span<byte> descriptor = stackalloc byte[MaxDescriptorBytes];
        BenchmarkProtocol.WriteDescriptor(descriptor, checkpointId, generation: 1, value.Length);
        int workloadOffset = workload == "distributed-key" ? 0 : 1_000;
        foreach (string argument in new[]
        {
            "checkpoint-pause",
            storeName,
            SlotCount.ToString(CultureInfo.InvariantCulture),
            MaxValueBytes.ToString(CultureInfo.InvariantCulture),
            MaxDescriptorBytes.ToString(CultureInfo.InvariantCulture),
            MaxKeyBytes.ToString(CultureInfo.InvariantCulture),
            LeaseRecordCount.ToString(CultureInfo.InvariantCulture),
            ParticipantRecordCount.ToString(CultureInfo.InvariantCulture),
            checkpointId.ToString(CultureInfo.InvariantCulture),
            Convert.ToHexString(BenchmarkProtocol.Key(TokenKeyIndex)),
            Convert.ToHexString(BenchmarkProtocol.Key(ExistingKeyIndex)),
            Convert.ToHexString(BenchmarkProtocol.Key(OperationKeyBase + workloadOffset + checkpointId)),
            Convert.ToHexString(BenchmarkProtocol.Key(RecoveryKeyBase + workloadOffset + checkpointId)),
            Convert.ToHexString(value),
            Convert.ToHexString(descriptor),
            spillFirstBucket.ToString(CultureInfo.InvariantCulture),
            spillSecondBucket.ToString(CultureInfo.InvariantCulture),
            "v2"
        })
        {
            start.ArgumentList.Add(argument);
        }

        return Process.Start(start) ?? throw new InvalidOperationException("Failed to start checkpoint agent.");
    }

    private static async Task<CheckpointCatalogEntry[]> ReadCheckpointCatalog(string agentPath)
    {
        ProcessStartInfo start = CreateAgentStartInfo(agentPath);
        start.ArgumentList.Add("checkpoint-catalog");
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException("Failed to start checkpoint catalog agent.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(ChildExitTimeout);
        string json = await stdout;
        string error = await stderr;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Checkpoint catalog agent exited {process.ExitCode}: {error.Trim()}");
        }

        return JsonSerializer.Deserialize<CheckpointCatalogEntry[]>(json)
            ?? throw new InvalidOperationException("Checkpoint catalog agent emitted invalid JSON.");
    }

    private static ProcessStartInfo CreateSelfStartInfo()
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
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        }

        return start;
    }

    private static ProcessStartInfo CreateAgentStartInfo(string agentPath)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(agentPath);
        return start;
    }

    private static string FindAgentAssembly()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Release";
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null
            && !File.Exists(Path.Combine(directory.FullName, "SharedMemoryStore.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("Cannot locate repository root for LockFreeAgent.");
        }

        string path = Path.Combine(
            directory.FullName,
            "tests",
            "SharedMemoryStore.LockFreeAgent",
            "bin",
            configuration,
            "net10.0",
            "SharedMemoryStore.LockFreeAgent.dll");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "LockFreeAgent must be built in the same configuration as SyncProbe.",
                path);
        }

        return path;
    }

    private static SharedMemoryStoreOptions CreateOptions(string name, OpenMode mode) =>
        SharedMemoryStoreOptions.Create(
            name,
            SlotCount,
            MaxValueBytes,
            MaxDescriptorBytes,
            MaxKeyBytes,
            LeaseRecordCount,
            ParticipantRecordCount,
            mode,
            enableLeaseRecovery: true);

    private static DiagnosticsSnapshot GetDiagnostics(Store store)
    {
        for (var attempt = 0; attempt < 64; attempt++)
        {
            StoreStatus status = store.TryGetDiagnostics(out DiagnosticsSnapshot snapshot);
            if (status == StoreStatus.Success)
            {
                return snapshot;
            }

            if (status != StoreStatus.StoreBusy)
            {
                throw new InvalidOperationException("Suspension diagnostics failed: " + status);
            }

            Thread.SpinWait(32 << Math.Min(attempt, 10));
        }

        throw new InvalidOperationException("Suspension diagnostics exhausted its bounded retry budget.");
    }

    private static bool IsStoreFullProofCheckpoint(int checkpointId) => checkpointId is
        (int)LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification
        or (int)LockFreeCheckpointId.StoreFullAfterExactDoubleCollect;

    private static void RemoveStoreFullFillers(Store store, int checkpointId)
    {
        for (var index = 0; index < SlotCount; index++)
        {
            byte[] key = CreateStoreFullFillerKey(checkpointId, index);
            for (var attempt = 0; attempt < 4096; attempt++)
            {
                StoreStatus status = store.TryRemove(key);
                if (status is StoreStatus.Success or StoreStatus.NotFound)
                {
                    break;
                }

                if (status is not (StoreStatus.RemovePending or StoreStatus.StoreBusy))
                {
                    throw new InvalidOperationException(
                        "StoreFull proof filler cleanup failed: " + status);
                }

                if (attempt == 4095)
                {
                    throw new InvalidOperationException(
                        "StoreFull proof filler cleanup exhausted its retry budget.");
                }

                Thread.SpinWait(4 << Math.Min(attempt, 10));
            }
        }
    }

    private static byte[] CreateStoreFullFillerKey(int checkpointId, int index) =>
        BitConverter.GetBytes(
            0x6f00_0000_0000_0000UL
            | ((ulong)(byte)checkpointId << 48)
            | checked((uint)(index + 1)));

    private static bool CapacityPermits(DiagnosticsSnapshot snapshot, int healthyProcesses) =>
        snapshot.FreeSlotCount >= 32
        && snapshot.FreeLeaseCount >= healthyProcesses + 1
        && snapshot.FreeParticipantCount >= 1
        && snapshot.GetFailureCount(StoreStatus.StoreFull) == 0
        && snapshot.GetFailureCount(StoreStatus.LeaseTableFull) == 0;

    private static SuspensionCapacityEvidence CapacityEvidence(DiagnosticsSnapshot snapshot) =>
        new(
            snapshot.FreeSlotCount,
            snapshot.PublishedSlotCount,
            snapshot.ActiveLeaseCount,
            snapshot.ActiveReservationCount,
            snapshot.InitializingSlotCount,
            snapshot.ReservedSlotCount,
            snapshot.ReclaimingSlotCount,
            snapshot.FreeLeaseCount,
            snapshot.ClaimingLeaseCount,
            snapshot.RecoveringLeaseCount,
            snapshot.FreeParticipantCount,
            snapshot.ActiveParticipantCount,
            snapshot.RegisteringParticipantCount,
            snapshot.ClosingParticipantCount,
            snapshot.RecoveringParticipantCount,
            snapshot.ReclaimingParticipantCount,
            snapshot.GetFailureCount(StoreStatus.StoreFull),
            snapshot.GetFailureCount(StoreStatus.LeaseTableFull),
            snapshot.ContentionBudgetExhaustionCount);

    private static async Task<string?> ReadLine(Process process, TimeSpan timeout) =>
        await process.StandardOutput.ReadLineAsync().WaitAsync(timeout);

    private static int GetAvailableProcessorCount()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return Environment.ProcessorCount;
        }

        try
        {
            using Process process = Process.GetCurrentProcess();
            return BitOperations.PopCount(unchecked((ulong)(nuint)process.ProcessorAffinity));
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }

    private static SuspensionEnvironment CaptureEnvironment(int availableProcessors)
    {
        string assembly = Assembly.GetExecutingAssembly().Location;
        return new SuspensionEnvironment(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.Version.ToString(),
            Environment.ProcessorCount,
            availableProcessors,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly))),
            Stopwatch.Frequency);
    }

    private static SuspensionCheckpointResult NotQualifiedForProcessors(
        CheckpointCatalogEntry checkpoint,
        SuspensionWorkload workload,
        int baselineSeconds,
        int pauseSeconds,
        double minimumRatio,
        int availableProcessors,
        int requiredProcessors) =>
        EmptyResult(
            checkpoint,
            workload,
            baselineSeconds,
            pauseSeconds,
            minimumRatio,
            availableProcessors,
            requiredProcessors,
            "not-qualified-insufficient-processors",
            [$"available-processors={availableProcessors}; required-processors={requiredProcessors}"]);

    private static SuspensionCheckpointResult HarnessFailure(
        CheckpointCatalogEntry checkpoint,
        SuspensionWorkload workload,
        int baselineSeconds,
        int pauseSeconds,
        double minimumRatio,
        int availableProcessors,
        int requiredProcessors,
        Exception exception) =>
        EmptyResult(
            checkpoint,
            workload,
            baselineSeconds,
            pauseSeconds,
            minimumRatio,
            availableProcessors,
            requiredProcessors,
            pauseSeconds >= 30 ? "qualified-fail" : "smoke-fail",
            ["harness-error=" + exception.GetType().Name + ": " + exception.Message]);

    private static SuspensionCheckpointResult EmptyResult(
        CheckpointCatalogEntry checkpoint,
        SuspensionWorkload workload,
        int baselineSeconds,
        int pauseSeconds,
        double minimumRatio,
        int availableProcessors,
        int requiredProcessors,
        string qualification,
        string[] errors) =>
        new(
            checkpoint.Id,
            checkpoint.Name,
            checkpoint.Family,
            checkpoint.Position,
            checkpoint.Pause,
            checkpoint.Crash,
            checkpoint.Race,
            checkpoint.IsPublicOrderingPoint,
            workload.Name,
            workload.ReaderCount,
            workload.WriterCount,
            workload.HealthyProcessCount,
            baselineSeconds,
            pauseSeconds,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            minimumRatio,
            false,
            errors.LongLength,
            errors,
            0,
            false,
            -1,
            "not-applied",
            -1,
            -1,
            availableProcessors,
            requiredProcessors,
            0,
            -1,
            qualification,
            false,
            [],
            [],
            default,
            default,
            default,
            default,
            default);

    private static int ReadPositiveInt(string[] arguments, string name, int fallback)
    {
        string? text = ReadString(arguments, name);
        return text is null
            ? fallback
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
                ? value
                : throw new ArgumentException(name + " must be a positive integer.");
    }

    private static int ReadNonNegativeInt(string[] arguments, string name, int fallback)
    {
        string? text = ReadString(arguments, name);
        return text is null
            ? fallback
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value >= 0
                ? value
                : throw new ArgumentException(name + " must be a non-negative integer.");
    }

    private static double ReadRatio(string[] arguments, string name, double fallback)
    {
        string? text = ReadString(arguments, name);
        return text is null
            ? fallback
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && value > 0
                && value <= 1
                    ? value
                    : throw new ArgumentException(name + " must be in (0,1].");
    }

    private static string[] ParseList(string[] arguments, string name) =>
        (ReadString(arguments, name) ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadString(string[] arguments, string name)
    {
        int index = Array.IndexOf(arguments, name);
        if (index < 0)
        {
            return null;
        }

        if (index == arguments.Length - 1)
        {
            throw new ArgumentException(name + " requires a value.");
        }

        return arguments[index + 1];
    }

    private sealed class WorkerState(string workload, string role, int workerId)
    {
        internal string Workload { get; } = workload;
        internal string Role { get; } = role;
        internal int WorkerId { get; } = workerId;
        internal byte[][] Keys { get; } = CreateWorkloadKeys(workload);
        internal long Cycle { get; set; }
    }

    private sealed class WindowCounters
    {
        private readonly StatusCounters _statusCounters = new();

        internal long AttemptedCycles { get; set; }
        internal long CompletedCycles { get; set; }
        internal long ApiCalls => _statusCounters.TotalOperations;
        internal long Failures { get; set; }

        internal void Record(OperationKind operation, StoreStatus status) =>
            _statusCounters.Record(operation, status);

        internal void RecordChecksumFailure() => _statusCounters.RecordChecksumFailure();

        internal SortedDictionary<string, long> ToHistogram() => _statusCounters.ToHistogram();
    }

    private sealed record SuspensionWorkerProcess(
        Process Process,
        Task<string> StandardError,
        string Role,
        int WorkerId);
}

internal sealed record SuspensionWorkload(string Name, int ReaderCount, int WriterCount)
{
    internal int HealthyProcessCount => ReaderCount + WriterCount;
}

internal sealed record CheckpointCatalogEntry(
    int Id,
    string Name,
    string Family,
    string Position,
    string Pause,
    string Crash,
    string Race,
    bool IsPublicOrderingPoint,
    string Description);

internal sealed record CheckpointSignal(
    int Id,
    string Name,
    string Family,
    string Position,
    string Crash,
    int ProcessId,
    ulong StoreId,
    ulong ParticipantToken,
    ulong SlotBinding,
    ulong LeaseToken);

internal sealed record SuspensionWorkerReady(
    int WorkerId,
    string Role,
    int ProcessId,
    bool AffinityApplied,
    int AssignedProcessor,
    string AffinityStrategy);

internal sealed record SuspensionWindowResult(
    int WorkerId,
    string Role,
    int ProcessId,
    string Window,
    long AttemptedCycles,
    long CompletedCycles,
    long ApiCalls,
    double ElapsedSeconds,
    double CompletedCyclesPerSecond,
    double ApiCallsPerSecond,
    long Failures,
    bool AffinityApplied,
    int AssignedProcessor,
    string AffinityStrategy,
    SortedDictionary<string, long> StatusHistogram);

internal readonly record struct SuspensionCapacityEvidence(
    int FreeSlotCount,
    int PublishedSlotCount,
    int ActiveLeaseCount,
    int ActiveReservationCount,
    int InitializingSlotCount,
    int ReservedSlotCount,
    int ReclaimingSlotCount,
    int FreeLeaseCount,
    int ClaimingLeaseCount,
    int RecoveringLeaseCount,
    int FreeParticipantCount,
    int ActiveParticipantCount,
    int RegisteringParticipantCount,
    int ClosingParticipantCount,
    int RecoveringParticipantCount,
    int ReclaimingParticipantCount,
    long StoreFullFailures,
    long LeaseTableFullFailures,
    long ContentionBudgetExhaustionCount);

internal sealed record SuspensionCheckpointResult(
    int CheckpointId,
    string CheckpointName,
    string CheckpointFamily,
    string CheckpointPosition,
    string PauseClassification,
    string CrashClassification,
    string RaceClassification,
    bool IsPublicOrderingPoint,
    string Workload,
    int ReaderProcessCount,
    int WriterProcessCount,
    int HealthyProcessCount,
    int BaselineWindowSeconds,
    int SuspendedWindowSeconds,
    double BaselineCompletedCyclesPerSecond,
    double SuspendedCompletedCyclesPerSecond,
    long BaselineAttemptedCycles,
    long BaselineCompletedCycles,
    long BaselineApiCalls,
    long SuspendedAttemptedCycles,
    long SuspendedCompletedCycles,
    long SuspendedApiCalls,
    double BaselineApiCallsPerSecond,
    double SuspendedApiCallsPerSecond,
    double ThroughputRatio,
    double MinimumThroughputRatio,
    bool CapacityPermits,
    long CorrectnessFailureCount,
    string[] CorrectnessErrors,
    int HealthyAffinityAppliedCount,
    bool PausedParticipantAffinityApplied,
    int PausedParticipantProcessor,
    string PausedParticipantAffinityStrategy,
    int AgentSpillFirstBucket,
    int AgentSpillSecondBucket,
    int AvailableProcessorCount,
    int RequiredProcessorCount,
    int PausedParticipantProcessId,
    int PausedParticipantExitCode,
    string Qualification,
    bool GatePassed,
    SuspensionWindowResult[] BaselineWorkers,
    SuspensionWindowResult[] SuspendedWorkers,
    SuspensionCapacityEvidence BeforeBaselineCapacity,
    SuspensionCapacityEvidence AfterBaselineCapacity,
    SuspensionCapacityEvidence BeforeSuspendedCapacity,
    SuspensionCapacityEvidence AfterSuspendedCapacity,
    SuspensionCapacityEvidence AfterResumeCapacity);

internal sealed record SuspensionConfiguration(
    int BaselineWindowSeconds,
    int SuspendedWindowSeconds,
    int WarmupSeconds,
    double MinimumThroughputRatio,
    bool AffinityRequested,
    string AffinityPolicy,
    int IncludedCheckpointCount,
    string[] Workloads,
    int CatalogCheckpointCount,
    string[] ExcludedFamilies,
    string ComparisonMethod);

internal sealed record SuspensionEnvironment(
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    string Framework,
    string RuntimeVersion,
    int LogicalProcessorCount,
    int AvailableProcessorCount,
    string ProbeAssemblySha256,
    long StopwatchFrequency);

internal sealed record SuspensionQualificationReport(
    int SchemaVersion,
    DateTimeOffset TimestampUtc,
    SuspensionEnvironment Environment,
    SuspensionConfiguration Configuration,
    IReadOnlyList<SuspensionCheckpointResult> Results,
    int RequiredResultCount,
    int QualifiedPassCount,
    int SmokePassCount,
    int FailCount,
    int NotQualifiedCount,
    bool AllRequiredQualifiedAndPassed);
