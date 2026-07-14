using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace SharedMemoryStore.LockFree;

/// <summary>
/// Stable identifiers for deterministic pauses immediately around protocol
/// transitions. Numeric assignments are append-only test/protocol identities.
/// </summary>
internal enum LockFreeCheckpointId
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
    ProjectAfterMetadataReadBeforeControlRevalidation = 65
}

internal enum LockFreeCheckpointFamily
{
    Publish,
    Reserve,
    Commit,
    Abort,
    Acquire,
    Project,
    Release,
    Remove,
    Reclaim,
    Directory,
    Diagnostics,
    Recovery,
    Disposal,
    Participant,
    Advance
}

internal enum LockFreeCheckpointPosition
{
    Before,
    After
}

internal enum LockFreePauseClassification
{
    Unspecified,
    NoSharedOwnership,
    BoundedOwnership,
    PublishedState
}

internal enum LockFreeCrashClassification
{
    Unspecified,
    NoSharedEffect,
    ExplicitRecovery,
    Helpable,
    DurableOutcome
}

internal enum LockFreeRaceClassification
{
    Unspecified,
    ValidationWindow,
    OrderingPoint,
    HelpWindow,
    ProjectionLifetime,
    SnapshotWindow
}

/// <summary>One canonical deterministic checkpoint and its safety metadata.</summary>
internal readonly record struct LockFreeCheckpointEntry(
    LockFreeCheckpointId Id,
    LockFreeCheckpointFamily Family,
    LockFreeCheckpointPosition Position,
    LockFreePauseClassification Pause,
    LockFreeCrashClassification Crash,
    LockFreeRaceClassification Race,
    bool IsPublicOrderingPoint,
    string Description);

/// <summary>
/// Physical value-slot lifecycle transitions exposed only to friend-test
/// instrumentation. These observations are not protocol state and never
/// change the public API or the shared-memory layout.
/// </summary>
internal enum LockFreeSlotResourceEventKind
{
    Claim,
    Free,
    Retire
}

/// <summary>
/// One exact winning slot-control CAS. <see cref="Generation"/> identifies the
/// occupied lifecycle: a release to <c>Free(g + 1)</c> is therefore reported as
/// <c>Free(g)</c>, pairing it with the claim that occupied generation g.
/// </summary>
internal readonly record struct LockFreeSlotResourceEvent(
    LockFreeSlotResourceEventKind Kind,
    int SlotIndex,
    long Generation);

/// <summary>
/// Test-only semantic recorder for the StoreFull candidate instant and its
/// later verification. Production's statically specialized no-op strategy does
/// not create a token, callback, allocation, or process-wide counter.
/// </summary>
internal interface ILockFreeStoreFullProofObserver
{
    long BeginCandidate(int slotCount);

    void CompleteCandidate(long token, bool confirmed);
}

/// <summary>
/// Test-only semantic recorder for the LeaseTableFull candidate instant and
/// its later verification. Production's statically specialized no-op strategy
/// does not create a token, callback, allocation, or process-wide counter.
/// </summary>
internal interface ILockFreeLeaseTableFullProofObserver
{
    long BeginCandidate(int leaseRecordCount);

    void CompleteCandidate(long token, bool confirmed);
}

/// <summary>
/// Canonical checkpoint inventory consumed by schedulers and cross-process
/// agents. New transition identifiers must be appended and classified here.
/// </summary>
internal static class LockFreeCheckpointCatalog
{
    private static readonly ReadOnlyCollection<LockFreeCheckpointEntry> Catalog = Array.AsReadOnly(
        new[]
        {
            Before(LockFreeCheckpointId.PublishBeforeSlotClaim, LockFreeCheckpointFamily.Publish,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow, "Before a simple publisher claims a value slot."),
            After(LockFreeCheckpointId.PublishAfterCommitPublication, LockFreeCheckpointFamily.Publish,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.DurableOutcome,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After the value generation becomes published."),

            Before(LockFreeCheckpointId.ReserveBeforeSlotClaim, LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow, "Before a reservation claims a value slot."),
            After(LockFreeCheckpointId.ReserveAfterReservationPublication, LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After exact-key reservation ownership is published."),

            Before(LockFreeCheckpointId.CommitBeforePublicationCas, LockFreeCheckpointFamily.Commit,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, "Immediately before the reservation publication CAS."),
            After(LockFreeCheckpointId.CommitAfterPublicationCas, LockFreeCheckpointFamily.Commit,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.DurableOutcome,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "Immediately after the reservation publication CAS."),

            Before(LockFreeCheckpointId.AbortBeforeAbortCas, LockFreeCheckpointFamily.Abort,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, "Before reservation ownership changes to aborting."),
            After(LockFreeCheckpointId.AbortAfterUnlinkCompletion, LockFreeCheckpointFamily.Abort,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.HelpWindow, "After the aborted key binding is unlinked."),

            Before(LockFreeCheckpointId.AcquireBeforeLeaseClaimCas, LockFreeCheckpointFamily.Acquire,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.OrderingPoint, "Before claiming a lease record."),
            After(LockFreeCheckpointId.AcquireAfterPublishedRevalidation, LockFreeCheckpointFamily.Acquire,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After an active lease revalidates the published generation."),

            Before(LockFreeCheckpointId.ProjectBeforeHandleValidation, LockFreeCheckpointFamily.Project,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ProjectionLifetime, "Before a token validates its exact incarnation."),
            After(LockFreeCheckpointId.ProjectAfterSpanProjection, LockFreeCheckpointFamily.Project,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.DurableOutcome,
                LockFreeRaceClassification.ProjectionLifetime, "After a validated mapped-memory span is projected."),

            Before(LockFreeCheckpointId.ReleaseBeforeActiveReleaseCas, LockFreeCheckpointFamily.Release,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, "Before ending active lease protection."),
            After(LockFreeCheckpointId.ReleaseAfterRecordRecycle, LockFreeCheckpointFamily.Release,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After the exact lease-record incarnation is recycled or retired."),

            Before(LockFreeCheckpointId.RemoveBeforeLogicalRemovalCas, LockFreeCheckpointFamily.Remove,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.OrderingPoint, "Before published changes to logically removed."),
            After(LockFreeCheckpointId.RemoveAfterLeaseClassification, LockFreeCheckpointFamily.Remove,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After the stable exact-lease classification scan."),

            Before(LockFreeCheckpointId.ReclaimBeforeOwnershipCas, LockFreeCheckpointFamily.Reclaim,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.OrderingPoint, "Before one helper claims exact reclamation."),
            After(LockFreeCheckpointId.ReclaimAfterGenerationAdvance, LockFreeCheckpointFamily.Reclaim,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After exact helper cleanup and generation advance."),

            Before(LockFreeCheckpointId.DirectoryBeforeDescriptorPublication, LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.HelpWindow, "Before publishing a complete directory mutation descriptor."),
            After(LockFreeCheckpointId.DirectoryAfterDescriptorClear, LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After completing and clearing the exact directory descriptor."),

            Before(LockFreeCheckpointId.DiagnosticsBeforeBoundedScan, LockFreeCheckpointFamily.Diagnostics,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.SnapshotWindow, "Before a diagnostics caller begins bounded scans."),
            After(LockFreeCheckpointId.DiagnosticsAfterSnapshotAssembly, LockFreeCheckpointFamily.Diagnostics,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.SnapshotWindow, "After the moment-in-time snapshot is assembled."),

            Before(LockFreeCheckpointId.RecoveryBeforeOwnerClassification, LockFreeCheckpointFamily.Recovery,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow, "Before classifying an exact participant incarnation."),
            After(LockFreeCheckpointId.RecoveryAfterExactRecoveryCas, LockFreeCheckpointFamily.Recovery,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After the exact stale-owner recovery CAS."),

            Before(LockFreeCheckpointId.DisposalBeforeLocalGateClose, LockFreeCheckpointFamily.Disposal,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.OrderingPoint, "Before the local handle rejects new operations."),
            After(LockFreeCheckpointId.DisposalAfterParticipantRelease, LockFreeCheckpointFamily.Disposal,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After bounded local cleanup and the exact participant retirement attempt."),

            Before(LockFreeCheckpointId.ParticipantBeforeRegisteringCas, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.OrderingPoint, "Before publishing PID and participant incarnation."),
            After(LockFreeCheckpointId.ParticipantAfterActivePublication, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.DurableOutcome,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After the participant record becomes active and usable by data claims."),

            After(LockFreeCheckpointId.DirectoryAfterOperationValidation, LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After exact operation/binding/control generation validation and before a helper side effect."),
            After(LockFreeCheckpointId.DirectoryAfterLocationValidation, LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After exact location generation validation and before unlink side effects."),
            After(LockFreeCheckpointId.ReclaimAfterMetadataValidation, LockFreeCheckpointFamily.Reclaim,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After exact reclaim-generation validation and before the final generation-advance CAS."),

            After(LockFreeCheckpointId.ParticipantAfterIdentityKindWrite, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a Registering owner writes identity kind while older ordinary fields may remain."),
            After(LockFreeCheckpointId.ParticipantAfterReservedWrite, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a Registering owner clears the reserved field while older ordinary fields may remain."),
            After(LockFreeCheckpointId.ParticipantAfterProcessStartWrite, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a Registering owner writes process-start identity before Active publication."),
            After(LockFreeCheckpointId.ParticipantAfterOpenSequenceWrite, LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a Registering owner writes open sequence before Active publication."),
            After(LockFreeCheckpointId.AbortAfterOwnershipReleaseCas, LockFreeCheckpointFamily.Abort,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.HelpWindow, orderingPoint: true,
                "After reservation ownership changes to universally helpable Aborting."),
            After(LockFreeCheckpointId.SlotClaimAfterParticipantRecheck, LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a slot claim revalidates its participant and before ordinary metadata writes."),
            After(LockFreeCheckpointId.ReleaseAfterOwnershipReleaseCas, LockFreeCheckpointFamily.Release,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.HelpWindow,
                "After an active lease publishes unowned Releasing and before exact-incarnation recycle."),
            After(LockFreeCheckpointId.AcquireAfterLeaseActivationBeforeFinalLookup, LockFreeCheckpointFamily.Acquire,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After lease activation and before the acquire operation's final directory revalidation."),
            After(LockFreeCheckpointId.ReserveAfterExistingLookup, LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow,
                "After reserve/publish observes an existing key and before its final existing-generation lookup."),
            Before(LockFreeCheckpointId.DirectoryBeforeSpillSummaryPublicationCas,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After loading the prior spill-summary version and revalidating the exact insert, before its publication CAS."),
            After(LockFreeCheckpointId.DirectoryAfterSpillSummaryPublication,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After publishing Present(candidate) and before any overflow-cell publication CAS."),
            After(LockFreeCheckpointId.DirectoryAfterEmptySpillSummaryScan,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After a stable empty full overflow scan and before the exact versioned-empty clear CAS."),
            After(LockFreeCheckpointId.DirectoryAfterSpillSummaryClear,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After Present(X) becomes Empty(X) and before releasing the exact canonical mutation."),
            After(LockFreeCheckpointId.ParticipantAfterRecoveryFenceBeforeReferenceScan,
                LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After fencing one stale participant as Recovering and before scanning exact owned references."),
            Before(LockFreeCheckpointId.AdvanceBeforeBytesAdvancedCas,
                LockFreeCheckpointFamily.Advance,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint,
                "After exact reservation and range validation, immediately before the BytesAdvanced CAS."),
            After(LockFreeCheckpointId.AdvanceAfterBytesAdvancedCas,
                LockFreeCheckpointFamily.Advance,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "Immediately after the checked BytesAdvanced CAS advances the exact reservation."),
            After(LockFreeCheckpointId.DisposalAfterParticipantClosingPublication,
                LockFreeCheckpointFamily.Disposal,
                LockFreePauseClassification.PublishedState, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.OrderingPoint, orderingPoint: true,
                "After exact Active-to-Closing publication and before bounded local resource cleanup."),
            After(LockFreeCheckpointId.ParticipantAfterRegistrationBeforeEngineConstruction,
                LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After participant Active publication returns successfully and before the engine can escape construction."),
            After(LockFreeCheckpointId.DirectoryAfterUnlinkOperationValidationBeforeLocationRead,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After an unlink helper validates its exact operation and before reading the generation-tagged location word."),
            After(LockFreeCheckpointId.DirectoryAfterLocationPublisherBindingValidation,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After a location publisher validates its exact slot binding and before reading or publishing the location word."),
            After(LockFreeCheckpointId.DirectoryAfterCurrentOperationRevalidationBeforeDispatch,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After a helper revalidates the exact current directory operation and slot state, before dispatching the phase-specific side effect."),
            After(LockFreeCheckpointId.DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After a BindingChanged insert helper validates a non-canceling slot state and before publishing Reserved."),
            After(LockFreeCheckpointId.DirectoryAfterInsertCompletionStateValidationBeforeLocationRead,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After TryInsert validates a completed non-canceling insert and before reading its generation-tagged location."),
            After(LockFreeCheckpointId.ReserveAfterDirectoryInsertBeforePendingClassification,
                LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After directory insertion succeeds and before the reserve path classifies whether the reservation remains pending."),
            Before(LockFreeCheckpointId.DirectoryBeforeInsertOuterLoopBudgetCheck,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.HelpWindow,
                "Immediately before TryInsert checks its operation-wide budget at the outer helper-loop boundary."),
            After(LockFreeCheckpointId.DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After an invalid binding's exact source word is confirmed and before the binding and source are jointly revalidated."),
            After(LockFreeCheckpointId.StoreFullAfterFirstCollectBeforeVerification,
                LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow,
                "After the first all-occupied slot-control collect and before the exact verification collect."),
            After(LockFreeCheckpointId.StoreFullAfterExactDoubleCollect,
                LockFreeCheckpointFamily.Reserve,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.NoSharedEffect,
                LockFreeRaceClassification.ValidationWindow,
                "After the second collect confirms the StoreFull candidate observed between the two collects."),
            After(LockFreeCheckpointId.DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After an exact unlink descriptor is clear and before the reclaiming slot generation advances."),
            After(LockFreeCheckpointId.ParticipantAfterPidNamespaceWrite,
                LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ValidationWindow,
                "After a Registering owner writes its Linux PID-namespace identity before Active publication."),
            After(LockFreeCheckpointId.DirectoryAfterCancelLocationClearBeforeDescriptorRejection,
                LockFreeCheckpointFamily.Directory,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After a canceled insert clears its exact cell/location and before publishing the Rejected descriptor."),
            After(LockFreeCheckpointId.ReclaimAfterLeaseScanBeforeOwnershipCas,
                LockFreeCheckpointFamily.Reclaim,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "After proving no exact active lease remains and before RemoveRequested changes to Reclaiming."),
            Before(LockFreeCheckpointId.ParticipantBeforeReclaimGenerationAdvanceCas,
                LockFreeCheckpointFamily.Participant,
                LockFreePauseClassification.NoSharedOwnership, LockFreeCrashClassification.Helpable,
                LockFreeRaceClassification.ValidationWindow,
                "Immediately before an unowned Reclaiming participant record advances or retires its generation."),
            After(LockFreeCheckpointId.ProjectAfterMetadataReadBeforeControlRevalidation,
                LockFreeCheckpointFamily.Project,
                LockFreePauseClassification.BoundedOwnership, LockFreeCrashClassification.ExplicitRecovery,
                LockFreeRaceClassification.ProjectionLifetime,
                "After lease projection metadata is read and before its exact slot control is revalidated.")
        });

    internal static IReadOnlyList<LockFreeCheckpointEntry> Entries => Catalog;

    internal static LockFreeCheckpointEntry Get(LockFreeCheckpointId id)
    {
        int index = (int)id - 1;
        if ((uint)index >= (uint)Catalog.Count || Catalog[index].Id != id)
        {
            throw new ArgumentOutOfRangeException(nameof(id));
        }

        return Catalog[index];
    }

    private static LockFreeCheckpointEntry Before(
        LockFreeCheckpointId id,
        LockFreeCheckpointFamily family,
        LockFreePauseClassification pause,
        LockFreeCrashClassification crash,
        LockFreeRaceClassification race,
        string description) =>
        new(id, family, LockFreeCheckpointPosition.Before, pause, crash, race, false, description);

    private static LockFreeCheckpointEntry After(
        LockFreeCheckpointId id,
        LockFreeCheckpointFamily family,
        LockFreePauseClassification pause,
        LockFreeCrashClassification crash,
        LockFreeRaceClassification race,
        string description) =>
        After(id, family, pause, crash, race, orderingPoint: false, description);

    private static LockFreeCheckpointEntry After(
        LockFreeCheckpointId id,
        LockFreeCheckpointFamily family,
        LockFreePauseClassification pause,
        LockFreeCrashClassification crash,
        LockFreeRaceClassification race,
        bool orderingPoint,
        string description) =>
        new(id, family, LockFreeCheckpointPosition.After, pause, crash, race, orderingPoint, description);
}

/// <summary>
/// Static strategy contract used by generic protocol code. The strategy value
/// is passed by reference so instrumented tests can carry scheduler state while
/// the empty no-op specialization is completely elidable.
/// </summary>
internal interface ILockFreeCheckpointStrategy<TSelf>
    where TSelf : struct, ILockFreeCheckpointStrategy<TSelf>
{
    static abstract void Reach(ref TSelf strategy, LockFreeCheckpointId checkpoint);

    static abstract void ObserveSlotResource(
        ref TSelf strategy,
        LockFreeSlotResourceEventKind kind,
        int slotIndex,
        long generation);

    static abstract long BeginStoreFullProof(ref TSelf strategy, int slotCount);

    static abstract void CompleteStoreFullProof(
        ref TSelf strategy,
        long token,
        bool confirmed);

    static abstract long BeginLeaseTableFullProof(
        ref TSelf strategy,
        int leaseRecordCount);

    static abstract void CompleteLeaseTableFullProof(
        ref TSelf strategy,
        long token,
        bool confirmed);
}

/// <summary>Ordinary production specialization; deliberately contains no state or side effect.</summary>
internal struct NoOpLockFreeCheckpoint : ILockFreeCheckpointStrategy<NoOpLockFreeCheckpoint>
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Reach(ref NoOpLockFreeCheckpoint strategy, LockFreeCheckpointId checkpoint)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ObserveSlotResource(
        ref NoOpLockFreeCheckpoint strategy,
        LockFreeSlotResourceEventKind kind,
        int slotIndex,
        long generation)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long BeginStoreFullProof(ref NoOpLockFreeCheckpoint strategy, int slotCount) => 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CompleteStoreFullProof(
        ref NoOpLockFreeCheckpoint strategy,
        long token,
        bool confirmed)
    {
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long BeginLeaseTableFullProof(
        ref NoOpLockFreeCheckpoint strategy,
        int leaseRecordCount) => 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CompleteLeaseTableFullProof(
        ref NoOpLockFreeCheckpoint strategy,
        long token,
        bool confirmed)
    {
    }
}

/// <summary>Friend-test specialization forwarding checkpoints to a controlled observer.</summary>
internal struct InstrumentedLockFreeCheckpoint : ILockFreeCheckpointStrategy<InstrumentedLockFreeCheckpoint>
{
    private readonly Action<LockFreeCheckpointEntry> _observer;
    private readonly Action<LockFreeSlotResourceEvent>? _slotResourceObserver;
    private readonly ILockFreeStoreFullProofObserver? _storeFullProofObserver;
    private readonly ILockFreeLeaseTableFullProofObserver? _leaseTableFullProofObserver;

    internal InstrumentedLockFreeCheckpoint(Action<LockFreeCheckpointEntry> observer)
        : this(
            observer,
            slotResourceObserver: null,
            storeFullProofObserver: null,
            leaseTableFullProofObserver: null)
    {
    }

    internal InstrumentedLockFreeCheckpoint(
        Action<LockFreeCheckpointEntry> observer,
        Action<LockFreeSlotResourceEvent>? slotResourceObserver,
        ILockFreeStoreFullProofObserver? storeFullProofObserver = null,
        ILockFreeLeaseTableFullProofObserver? leaseTableFullProofObserver = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        _observer = observer;
        _slotResourceObserver = slotResourceObserver;
        _storeFullProofObserver = storeFullProofObserver;
        _leaseTableFullProofObserver = leaseTableFullProofObserver;
    }

    public static void Reach(ref InstrumentedLockFreeCheckpoint strategy, LockFreeCheckpointId checkpoint)
    {
        strategy._observer(LockFreeCheckpointCatalog.Get(checkpoint));
    }

    public static void ObserveSlotResource(
        ref InstrumentedLockFreeCheckpoint strategy,
        LockFreeSlotResourceEventKind kind,
        int slotIndex,
        long generation)
    {
        strategy._slotResourceObserver?.Invoke(new LockFreeSlotResourceEvent(
            kind,
            slotIndex,
            generation));
    }

    public static long BeginStoreFullProof(
        ref InstrumentedLockFreeCheckpoint strategy,
        int slotCount) => strategy._storeFullProofObserver?.BeginCandidate(slotCount) ?? 0;

    public static void CompleteStoreFullProof(
        ref InstrumentedLockFreeCheckpoint strategy,
        long token,
        bool confirmed)
    {
        if (token != 0)
        {
            strategy._storeFullProofObserver?.CompleteCandidate(token, confirmed);
        }
    }

    public static long BeginLeaseTableFullProof(
        ref InstrumentedLockFreeCheckpoint strategy,
        int leaseRecordCount) =>
        strategy._leaseTableFullProofObserver?.BeginCandidate(leaseRecordCount) ?? 0;

    public static void CompleteLeaseTableFullProof(
        ref InstrumentedLockFreeCheckpoint strategy,
        long token,
        bool confirmed)
    {
        if (token != 0)
        {
            strategy._leaseTableFullProofObserver?.CompleteCandidate(token, confirmed);
        }
    }
}

/// <summary>Inlining gateway used by generic lock-free protocol components.</summary>
internal static class LockFreeCheckpoint
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Reach<TCheckpoint>(ref TCheckpoint strategy, LockFreeCheckpointId checkpoint)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        TCheckpoint.Reach(ref strategy, checkpoint);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ObserveSlotResource<TCheckpoint>(
        ref TCheckpoint strategy,
        LockFreeSlotResourceEventKind kind,
        int slotIndex,
        long generation)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        TCheckpoint.ObserveSlotResource(ref strategy, kind, slotIndex, generation);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long BeginStoreFullProof<TCheckpoint>(
        ref TCheckpoint strategy,
        int slotCount)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint> =>
        TCheckpoint.BeginStoreFullProof(ref strategy, slotCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CompleteStoreFullProof<TCheckpoint>(
        ref TCheckpoint strategy,
        long token,
        bool confirmed)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        TCheckpoint.CompleteStoreFullProof(ref strategy, token, confirmed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long BeginLeaseTableFullProof<TCheckpoint>(
        ref TCheckpoint strategy,
        int leaseRecordCount)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint> =>
        TCheckpoint.BeginLeaseTableFullProof(ref strategy, leaseRecordCount);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void CompleteLeaseTableFullProof<TCheckpoint>(
        ref TCheckpoint strategy,
        long token,
        bool confirmed)
        where TCheckpoint : struct, ILockFreeCheckpointStrategy<TCheckpoint>
    {
        TCheckpoint.CompleteLeaseTableFullProof(ref strategy, token, confirmed);
    }
}

/// <summary>
/// Factory intentionally exposed only through friend assemblies. Public store
/// construction never accepts checkpoint instrumentation.
/// </summary>
internal static class LockFreeCheckpointFactory
{
    internal static InstrumentedLockFreeCheckpoint CreateInstrumented(
        Action<LockFreeCheckpointEntry> observer) => new(observer);

    internal static InstrumentedLockFreeCheckpoint CreateInstrumented(
        Action<LockFreeCheckpointEntry> observer,
        Action<LockFreeSlotResourceEvent> slotResourceObserver)
    {
        ArgumentNullException.ThrowIfNull(slotResourceObserver);
        return new InstrumentedLockFreeCheckpoint(observer, slotResourceObserver);
    }

    internal static InstrumentedLockFreeCheckpoint CreateInstrumented(
        Action<LockFreeCheckpointEntry> observer,
        Action<LockFreeSlotResourceEvent> slotResourceObserver,
        ILockFreeStoreFullProofObserver storeFullProofObserver)
    {
        ArgumentNullException.ThrowIfNull(slotResourceObserver);
        ArgumentNullException.ThrowIfNull(storeFullProofObserver);
        return new InstrumentedLockFreeCheckpoint(
            observer,
            slotResourceObserver,
            storeFullProofObserver);
    }

    internal static InstrumentedLockFreeCheckpoint CreateInstrumented(
        Action<LockFreeCheckpointEntry> observer,
        ILockFreeLeaseTableFullProofObserver leaseTableFullProofObserver)
    {
        ArgumentNullException.ThrowIfNull(leaseTableFullProofObserver);
        return new InstrumentedLockFreeCheckpoint(
            observer,
            slotResourceObserver: null,
            storeFullProofObserver: null,
            leaseTableFullProofObserver);
    }

    internal static InstrumentedLockFreeCheckpoint CreateInstrumented(
        Action<LockFreeCheckpointEntry> observer,
        Action<LockFreeSlotResourceEvent> slotResourceObserver,
        ILockFreeStoreFullProofObserver storeFullProofObserver,
        ILockFreeLeaseTableFullProofObserver leaseTableFullProofObserver)
    {
        ArgumentNullException.ThrowIfNull(slotResourceObserver);
        ArgumentNullException.ThrowIfNull(storeFullProofObserver);
        ArgumentNullException.ThrowIfNull(leaseTableFullProofObserver);
        return new InstrumentedLockFreeCheckpoint(
            observer,
            slotResourceObserver,
            storeFullProofObserver,
            leaseTableFullProofObserver);
    }
}

/// <summary>
/// Friend-instrumentation bridge for facade ordering points that occur before
/// control enters the layout-v2 engine. Production engines still execute the
/// statically elidable no-op checkpoint path.
/// </summary>
internal interface ILockFreeCheckpointEmitter
{
    void ReachCheckpoint(LockFreeCheckpointId checkpoint);
}
