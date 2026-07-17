namespace SharedMemoryStore.InteropTests;

/// <summary>
/// Canonical JSON-lines contract shared by the managed, native, and Python
/// interoperability agents. The agents are updated independently; tests use
/// this catalog so command spelling and numeric protocol identities cannot
/// drift between runtimes.
/// </summary>
internal static class AgentProtocolCatalog
{
    internal const int AgentProtocolVersion = 2;
    internal const int CheckpointCatalogVersion = 1;
    internal const char FrameTerminator = '\n';
    internal const int AbruptExitCode = 97;

    internal static class ProtocolIdentity
    {
        internal const int LayoutMajorVersion = 2;
        internal const int LayoutMinorVersion = 0;
        internal const int ResourceProtocolVersion = 2;
        internal const ulong RequiredFeatures = 7;
        internal const ulong OptionalFeatures = 0;
    }

    internal static class ParticipantCapacity
    {
        internal const int Minimum = 1;
        internal const int Default = 64;
        internal const int Maximum = 1_048_575;
    }

    internal static class Runtime
    {
        internal const string DotNet = "dotnet";
        internal const string Cpp = "cpp";
        internal const string Python = "python";
    }

    internal static class Command
    {
        internal const string Ping = "ping";
        internal const string Open = "open";
        internal const string Close = "close";
        internal const string Publish = "publish";
        internal const string PublishSegments = "publishSegments";
        internal const string Acquire = "acquire";
        internal const string Read = "read";
        internal const string Checksum = "checksum";
        internal const string Release = "release";
        internal const string Remove = "remove";
        internal const string Reserve = "reserve";
        internal const string ReservationWrite = "reservationWrite";
        internal const string Advance = "advance";
        internal const string Commit = "commit";
        internal const string Abort = "abort";
        internal const string RecoverLeases = "recoverLeases";
        internal const string RecoverReservations = "recoverReservations";
        internal const string Diagnostics = "diagnostics";
        internal const string CheckpointCatalog = "checkpointCatalog";
        internal const string PauseAtCheckpoint = "pauseAtCheckpoint";
        internal const string ResumeCheckpoint = "resumeCheckpoint";
        internal const string CancelCheckpoint = "cancelCheckpoint";
        internal const string CrashAtCheckpoint = "crashAtCheckpoint";
        internal const string InjectRawFault = "injectRawFault";
        internal const string HoldColdLock = "holdColdLock";
        internal const string ReleaseColdLock = "releaseColdLock";
        internal const string Crash = "crash";
    }

    internal static class Field
    {
        internal const string Id = "id";
        internal const string Command = "command";
        internal const string Arguments = "arguments";
        internal const string Ok = "ok";
        internal const string Status = "status";
        internal const string Result = "result";
        internal const string Error = "error";
        internal const string Runtime = "runtime";
        internal const string ProtocolVersion = "protocolVersion";
        internal const string CheckpointCatalogVersion = "checkpointCatalogVersion";
        internal const string LayoutMajorVersion = "layoutMajorVersion";
        internal const string LayoutMinorVersion = "layoutMinorVersion";
        internal const string ResourceProtocolVersion = "resourceProtocolVersion";
        internal const string RequiredFeatures = "requiredFeatures";
        internal const string OptionalFeatures = "optionalFeatures";
        internal const string ParticipantRecordCount = "participantRecordCount";
        internal const string StoreId = "storeId";
        internal const string LeaseId = "leaseId";
        internal const string ReservationId = "reservationId";
        internal const string CheckpointId = "checkpointId";
        internal const string CheckpointName = "checkpointName";
        internal const string Operation = "operation";
        internal const string Seed = "seed";
        internal const string ExitCode = "exitCode";
    }

    internal static IReadOnlyList<string> Commands { get; } = Array.AsReadOnly(
    [
        Command.Ping,
        Command.Open,
        Command.Close,
        Command.Publish,
        Command.PublishSegments,
        Command.Acquire,
        Command.Read,
        Command.Checksum,
        Command.Release,
        Command.Remove,
        Command.Reserve,
        Command.ReservationWrite,
        Command.Advance,
        Command.Commit,
        Command.Abort,
        Command.RecoverLeases,
        Command.RecoverReservations,
        Command.Diagnostics,
        Command.CheckpointCatalog,
        Command.PauseAtCheckpoint,
        Command.ResumeCheckpoint,
        Command.CancelCheckpoint,
        Command.CrashAtCheckpoint,
        Command.InjectRawFault,
        Command.HoldColdLock,
        Command.ReleaseColdLock,
        Command.Crash
    ]);

    /// <summary>
    /// Stable checkpoint numbers mirror the managed production catalog. Values
    /// are append-only because they cross process and language boundaries.
    /// </summary>
    internal enum CheckpointId
    {
        PublishBeforeSlotClaim = 1,
        PublishAfterCommitPublication = 2,
        ReserveBeforeSlotClaim = 3,
        ReserveAfterReservationPublication = 4,
        CommitBeforePublicationCas = 5,
        CommitAfterPublicationCas = 6,
        AbortBeforeAbortCas = 7,
        AbortAfterUnlinkCompletion = 8,
        AcquireBeforeLeaseClaimCas = 9,
        AcquireAfterPublishedRevalidation = 10,
        ProjectBeforeHandleValidation = 11,
        ProjectAfterSpanProjection = 12,
        ReleaseBeforeActiveReleaseCas = 13,
        ReleaseAfterRecordRecycle = 14,
        RemoveBeforeLogicalRemovalCas = 15,
        RemoveAfterLeaseClassification = 16,
        ReclaimBeforeOwnershipCas = 17,
        ReclaimAfterGenerationAdvance = 18,
        DirectoryBeforeDescriptorPublication = 19,
        DirectoryAfterDescriptorClear = 20,
        DiagnosticsBeforeBoundedScan = 21,
        DiagnosticsAfterSnapshotAssembly = 22,
        RecoveryBeforeOwnerClassification = 23,
        RecoveryAfterExactRecoveryCas = 24,
        DisposalBeforeLocalGateClose = 25,
        DisposalAfterParticipantRelease = 26,
        ParticipantBeforeRegisteringCas = 27,
        ParticipantAfterActivePublication = 28,
        DirectoryAfterOperationValidation = 29,
        DirectoryAfterLocationValidation = 30,
        ReclaimAfterMetadataValidation = 31,
        ParticipantAfterIdentityKindWrite = 32,
        ParticipantAfterReservedWrite = 33,
        ParticipantAfterProcessStartWrite = 34,
        ParticipantAfterOpenSequenceWrite = 35,
        AbortAfterOwnershipReleaseCas = 36,
        SlotClaimAfterParticipantRecheck = 37,
        ReleaseAfterOwnershipReleaseCas = 38,
        AcquireAfterLeaseActivationBeforeFinalLookup = 39,
        ReserveAfterExistingLookup = 40,
        DirectoryBeforeSpillSummaryPublicationCas = 41,
        DirectoryAfterSpillSummaryPublication = 42,
        DirectoryAfterEmptySpillSummaryScan = 43,
        DirectoryAfterSpillSummaryClear = 44,
        ParticipantAfterRecoveryFenceBeforeReferenceScan = 45,
        AdvanceBeforeBytesAdvancedCas = 46,
        AdvanceAfterBytesAdvancedCas = 47,
        DisposalAfterParticipantClosingPublication = 48,
        ParticipantAfterRegistrationBeforeEngineConstruction = 49,
        DirectoryAfterUnlinkOperationValidationBeforeLocationRead = 50,
        DirectoryAfterLocationPublisherBindingValidation = 51,
        DirectoryAfterCurrentOperationRevalidationBeforeDispatch = 52,
        DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication = 53,
        DirectoryAfterInsertCompletionStateValidationBeforeLocationRead = 54,
        ReserveAfterDirectoryInsertBeforePendingClassification = 55,
        DirectoryBeforeInsertOuterLoopBudgetCheck = 56,
        DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation = 57,
        StoreFullAfterFirstCollectBeforeVerification = 58,
        StoreFullAfterExactDoubleCollect = 59,
        DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance = 60,
        ParticipantAfterPidNamespaceWrite = 61,
        DirectoryAfterCancelLocationClearBeforeDescriptorRejection = 62,
        ReclaimAfterLeaseScanBeforeOwnershipCas = 63,
        ParticipantBeforeReclaimGenerationAdvanceCas = 64,
        ProjectAfterMetadataReadBeforeControlRevalidation = 65,
        DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas = 66,
        DirectoryAfterLocationPublicationBeforeSourceRevalidation = 67
    }

    internal const int FirstCheckpointId = (int)CheckpointId.PublishBeforeSlotClaim;
    internal const int LastCheckpointId = (int)CheckpointId.DirectoryAfterLocationPublicationBeforeSourceRevalidation;
    internal const int CheckpointCount = LastCheckpointId - FirstCheckpointId + 1;

    internal static IReadOnlyList<CheckpointId> Checkpoints { get; } = Array.AsReadOnly(
        Enum.GetValues<CheckpointId>());
}
