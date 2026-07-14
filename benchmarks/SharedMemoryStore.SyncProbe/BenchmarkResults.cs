internal sealed record WorkerResult(
    int WorkerId,
    string Role,
    long Cycles,
    long Operations,
    long BytesProcessed,
    long Failures,
    long MeasuredThreadAllocatedBytes,
    double ElapsedSeconds,
    bool AffinityApplied,
    int AssignedProcessor,
    string AffinityStrategy,
    SortedDictionary<string, long> StatusHistogram,
    double[] SamplesMicroseconds,
    double[] EarlySamplesMicroseconds,
    double[] LateSamplesMicroseconds,
    double MaximumSampleMicroseconds);

internal sealed record BrokerWorkerSummary(
    int WorkerId,
    string Role,
    long Frames,
    long Operations,
    long BytesProcessed,
    long Failures,
    double ElapsedSeconds,
    bool AffinityApplied,
    int AssignedProcessor,
    string AffinityStrategy,
    SortedDictionary<string, long> StatusHistogram);

internal readonly record struct PendingBrokerFrame(
    long FrameNumber,
    int KeyIndex,
    long Generation,
    long PublishedTimestamp,
    int ReaderId,
    bool ObserverSampled);

internal readonly record struct BrokerMeasuredResult(
    long Frames,
    long Failures,
    TimeSpan Elapsed,
    long MeasuredThreadAllocatedBytes,
    long ProducerStoreOperationAllocatedBytes,
    StatusCounters Counters,
    double[] SamplesMicroseconds,
    double[] EarlySamplesMicroseconds,
    double[] LateSamplesMicroseconds);

internal sealed record StickyOverflowEvidence(
    int SlotCount,
    int PrimaryBucketCount,
    int ExactBucketPairCollisionKeyCount,
    long CollisionCandidatesExamined,
    int ChurnCycles,
    int MissingSamplesPerWindow,
    int SpilledBucketCountBeforeChurn,
    int SpilledBucketCountDuringChurn,
    int OverflowDirectoryOccupancyDuringChurn,
    int SpilledBucketCountAfterFirstCleanup,
    int OverflowDirectoryOccupancyAfterFirstCleanup,
    int SpilledBucketCountAfterChurn,
    int OverflowDirectoryOccupancyAfterChurn,
    long OverflowScanCountBeforeFirstCleanup,
    long OverflowScanCountAfterFirstCleanup,
    int MaxObservedOverflowScanLengthAfterFirstCleanup,
    long OverflowScanCountBeforeLateWindow,
    long OverflowScanCountAfterLateWindow,
    int MaxObservedOverflowScanLength,
    double[] EarlyMissingSamplesMicroseconds,
    double[] LateMissingSamplesMicroseconds,
    double LateToEarlyP99Gate,
    bool DiagnosticsGatePassed,
    bool LatencyGatePassed);

internal sealed record RoleLatencyResult(
    string Role,
    int SampleCount,
    double EarlyP99Microseconds,
    double LateP99Microseconds,
    double LateToEarlyP99Ratio);

internal sealed record RunResult(
    string Profile,
    string Scenario,
    int ProcessCount,
    int ReaderProcessCount,
    int PublisherProcessCount,
    int ObserverProcessCount,
    int Trial,
    long Cycles,
    long Operations,
    double ApiCallsPerSecond,
    double P50Microseconds,
    double P95Microseconds,
    double P99Microseconds,
    double MaxMicroseconds,
    double EarlyP99Microseconds,
    double LateP99Microseconds,
    double LateToEarlyP99Ratio,
    RoleLatencyResult[] RoleLatency,
    long Frames,
    double FramesPerSecond,
    long BytesWritten,
    long BytesRead,
    double BytesPerSecond,
    long FullPayloadCopies,
    long MeasuredThreadAllocatedBytes,
    long Failures,
    double MeasuredSeconds,
    double WallSeconds,
    int SampleCount,
    double FairnessIndex,
    double MinWorkerApiCallsPerSecond,
    double MaxWorkerApiCallsPerSecond,
    double WorstWorkerP99Microseconds,
    int AffinityAppliedCount,
    int[] AssignedProcessors,
    string[] AffinityStrategies,
    bool Oversubscribed,
    string Qualification,
    SortedDictionary<string, long> StatusHistogram,
    long[] WorkerCycles,
    // Schema v6 is additive. FullPayloadCopies is retained above for schema
    // compatibility, but this tag states whether it is an instrumented event
    // counter or structural evidence and prevents a literal zero from being
    // presented as a measurement.
    bool FullPayloadCopyCountIsInstrumented = false,
    string FullPayloadCopyEvidenceKind = "not-applicable",
    long ProducerStoreOperationAllocatedBytes = 0,
    string AllocationMeasurementScope = "not-applicable",
    StickyOverflowEvidence? StickyOverflow = null,
    int EarlySampleCount = 0,
    int LateSampleCount = 0);

internal sealed record SummaryResult(
    string Profile,
    string Scenario,
    int ProcessCount,
    double MedianApiCallsPerSecond,
    double MedianP50Microseconds,
    double MedianP95Microseconds,
    double MedianP99Microseconds,
    double MedianMaxMicroseconds,
    double MedianEarlyP99Microseconds,
    double MedianLateP99Microseconds,
    double MedianLateToEarlyP99Ratio,
    double MedianFramesPerSecond,
    double MedianBytesPerSecond,
    double MedianFairnessIndex,
    double MedianWorstWorkerP99Microseconds,
    long TotalFrames,
    long TotalBytesWritten,
    long TotalFullPayloadCopies,
    long TotalMeasuredThreadAllocatedBytes,
    long TotalFailures,
    SortedDictionary<string, long> StatusHistogram,
    bool FullPayloadCopyCountsAreInstrumented,
    string[] FullPayloadCopyEvidenceKinds,
    long TotalProducerStoreOperationAllocatedBytes,
    string[] AllocationMeasurementScopes);

internal sealed record ProbeEnvironment(
    string RepositoryCommit,
    string RepositoryWorkingTreeState,
    string SharedMemoryStoreAssemblySha256,
    string ProbeAssemblySha256,
    string OperatingSystem,
    string OperatingSystemArchitecture,
    string ProcessArchitecture,
    string Framework,
    string RuntimeVersion,
    int LogicalProcessorCount,
    string ProcessorIdentifier,
    bool ServerGarbageCollection,
    long StopwatchFrequency);

internal sealed record ProbeConfiguration(
    string Mode,
    int DurationSeconds,
    int Trials,
    string[] Profiles,
    string[] Scenarios,
    SortedDictionary<string, int[]> ScenarioProcessCounts,
    int ReaderKeyCount,
    int ReaderPayloadBytes,
    int BrokerRotatingKeyCount,
    int LargeFrameBytes,
    long LargeFrames,
    long MixedOperationTarget,
    int MixedCollisionKeyCount,
    int MixedPrimaryBucketCount,
    int WarmupCycles,
    int WarmupSeconds,
    int BrokerObserverSamplingInterval,
    int SamplingInterval,
    int MaxLatencySamplesPerWorker,
    bool AffinityRequested,
    string AffinityPolicy,
    int StickyOverflowSlotCount,
    int StickyOverflowChurnCycles,
    int StickyOverflowMissingSamplesPerWindow,
    string LegacyFullPayloadCopiesFieldSemantics,
    int SyncKeysPerWorker,
    int SyncMaximumWorkerCount,
    int SyncCanonicalBucketCount,
    string SyncKeyCatalogSha256,
    int[] SyncKeyCanonicalBucketAssignments);

internal sealed record ProbeReport(
    int SchemaVersion,
    DateTimeOffset TimestampUtc,
    ProbeEnvironment Environment,
    ProbeConfiguration Configuration,
    IReadOnlyList<RunResult> Runs,
    IReadOnlyList<SummaryResult> Summary,
    int MinimumCompatibleSchemaVersion,
    string SchemaCompatibility);

internal static class ProbeReportSchema
{
    internal const int CurrentVersion = 6;
    internal const int MinimumCompatibleVersion = 3;
    internal const string Compatibility =
        "Schema v6 is additive over v3-v5: all existing property names and meanings are retained; "
        + "new evidence tags disambiguate structural assertions from measured counters, and "
        + "overflow qualification fields expose the spill/cleanup/late-window transitions; "
        + "sync topology fields identify the deterministic key catalog, and early/late sample "
        + "counts identify the autonomous latency reservoirs; autonomous MaxMicroseconds "
        + "retains every sampled candidate even when reservoir replacement discards it.";

    internal const string LegacyFullPayloadCopiesFieldSemantics =
        "Retained for v3-v5 readers. Consult FullPayloadCopyCountIsInstrumented and "
        + "FullPayloadCopyEvidenceKind before interpreting the value as a measured event count.";
}
