#pragma once

#include <array>
#include <cstdint>
#include <string_view>

namespace sms::interop_test {

inline constexpr std::int32_t checkpoint_catalog_version = 1;
inline constexpr std::int32_t abrupt_exit_code = 97;

struct checkpoint_entry {
    std::int32_t id;
    std::string_view name;
    std::string_view family;
    std::string_view position;
    std::string_view pause;
    std::string_view crash;
    std::string_view race;
    bool is_public_ordering_point;
    std::string_view description;
};

// Append-only catalog mirrored by LockFreeCheckpointCatalog and
// tests/python/interop_checkpoint_catalog.py. It is test-only and never enters
// the SMS2 mapped protocol or the installed native package.
inline constexpr std::array<checkpoint_entry, 67> checkpoints{{
    {1, "PublishBeforeSlotClaim", "Publish", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "Before a simple publisher claims a value slot."},
    {2, "PublishAfterCommitPublication", "Publish", "After", "PublishedState", "DurableOutcome", "OrderingPoint", true, "After the value generation becomes published."},
    {3, "ReserveBeforeSlotClaim", "Reserve", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "Before a reservation claims a value slot."},
    {4, "ReserveAfterReservationPublication", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", true, "After exact-key reservation ownership is published."},
    {5, "CommitBeforePublicationCas", "Commit", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", false, "Immediately before the reservation publication CAS."},
    {6, "CommitAfterPublicationCas", "Commit", "After", "PublishedState", "DurableOutcome", "OrderingPoint", true, "Immediately after the reservation publication CAS."},
    {7, "AbortBeforeAbortCas", "Abort", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", false, "Before reservation ownership changes to aborting."},
    {8, "AbortAfterUnlinkCompletion", "Abort", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", false, "After the aborted key binding is unlinked."},
    {9, "AcquireBeforeLeaseClaimCas", "Acquire", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", false, "Before claiming a lease record."},
    {10, "AcquireAfterPublishedRevalidation", "Acquire", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", true, "After an active lease revalidates the published generation."},
    {11, "ProjectBeforeHandleValidation", "Project", "Before", "NoSharedOwnership", "NoSharedEffect", "ProjectionLifetime", false, "Before a token validates its exact incarnation."},
    {12, "ProjectAfterSpanProjection", "Project", "After", "BoundedOwnership", "DurableOutcome", "ProjectionLifetime", false, "After a validated mapped-memory span is projected."},
    {13, "ReleaseBeforeActiveReleaseCas", "Release", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", false, "Before ending active lease protection."},
    {14, "ReleaseAfterRecordRecycle", "Release", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", true, "After the exact lease-record incarnation is recycled or retired."},
    {15, "RemoveBeforeLogicalRemovalCas", "Remove", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", false, "Before published changes to logically removed."},
    {16, "RemoveAfterLeaseClassification", "Remove", "After", "PublishedState", "Helpable", "OrderingPoint", true, "After the stable exact-lease classification scan."},
    {17, "ReclaimBeforeOwnershipCas", "Reclaim", "Before", "NoSharedOwnership", "Helpable", "OrderingPoint", false, "Before one helper claims exact reclamation."},
    {18, "ReclaimAfterGenerationAdvance", "Reclaim", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", true, "After exact helper cleanup and generation advance."},
    {19, "DirectoryBeforeDescriptorPublication", "Directory", "Before", "BoundedOwnership", "ExplicitRecovery", "HelpWindow", false, "Before publishing a complete directory mutation descriptor."},
    {20, "DirectoryAfterDescriptorClear", "Directory", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", true, "After completing and clearing the exact directory descriptor."},
    {21, "DiagnosticsBeforeBoundedScan", "Diagnostics", "Before", "NoSharedOwnership", "NoSharedEffect", "SnapshotWindow", false, "Before a diagnostics caller begins bounded scans."},
    {22, "DiagnosticsAfterSnapshotAssembly", "Diagnostics", "After", "NoSharedOwnership", "NoSharedEffect", "SnapshotWindow", false, "After the moment-in-time snapshot is assembled."},
    {23, "RecoveryBeforeOwnerClassification", "Recovery", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "Before classifying an exact participant incarnation."},
    {24, "RecoveryAfterExactRecoveryCas", "Recovery", "After", "NoSharedOwnership", "Helpable", "HelpWindow", true, "After the exact stale-owner recovery CAS."},
    {25, "DisposalBeforeLocalGateClose", "Disposal", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", false, "Before the local handle rejects new operations."},
    {26, "DisposalAfterParticipantRelease", "Disposal", "After", "PublishedState", "ExplicitRecovery", "HelpWindow", true, "After bounded local cleanup and the exact participant retirement attempt."},
    {27, "ParticipantBeforeRegisteringCas", "Participant", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", false, "Before publishing PID and participant incarnation."},
    {28, "ParticipantAfterActivePublication", "Participant", "After", "NoSharedOwnership", "DurableOutcome", "OrderingPoint", true, "After the participant record becomes active and usable by data claims."},
    {29, "DirectoryAfterOperationValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After exact operation/binding/control generation validation and before a helper side effect."},
    {30, "DirectoryAfterLocationValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After exact location generation validation and before unlink side effects."},
    {31, "ReclaimAfterMetadataValidation", "Reclaim", "After", "NoSharedOwnership", "Helpable", "ValidationWindow", false, "After exact reclaim-generation validation and before the final generation-advance CAS."},
    {32, "ParticipantAfterIdentityKindWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a Registering owner writes identity kind while older ordinary fields may remain."},
    {33, "ParticipantAfterReservedWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a Registering owner clears the reserved field while older ordinary fields may remain."},
    {34, "ParticipantAfterProcessStartWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a Registering owner writes process-start identity before Active publication."},
    {35, "ParticipantAfterOpenSequenceWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a Registering owner writes open sequence before Active publication."},
    {36, "AbortAfterOwnershipReleaseCas", "Abort", "After", "NoSharedOwnership", "Helpable", "HelpWindow", true, "After reservation ownership changes to universally helpable Aborting."},
    {37, "SlotClaimAfterParticipantRecheck", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a slot claim revalidates its participant and before ordinary metadata writes."},
    {38, "ReleaseAfterOwnershipReleaseCas", "Release", "After", "PublishedState", "Helpable", "HelpWindow", false, "After an active lease publishes unowned Releasing and before exact-incarnation recycle."},
    {39, "AcquireAfterLeaseActivationBeforeFinalLookup", "Acquire", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After lease activation and before the acquire operation's final directory revalidation."},
    {40, "ReserveAfterExistingLookup", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "After reserve/publish observes an existing key and before its final existing-generation lookup."},
    {41, "DirectoryBeforeSpillSummaryPublicationCas", "Directory", "Before", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After loading the prior spill-summary version and revalidating the exact insert, before its publication CAS."},
    {42, "DirectoryAfterSpillSummaryPublication", "Directory", "After", "BoundedOwnership", "Helpable", "OrderingPoint", true, "After publishing Present(candidate) and before any overflow-cell publication CAS."},
    {43, "DirectoryAfterEmptySpillSummaryScan", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After a stable empty full overflow scan and before the exact versioned-empty clear CAS."},
    {44, "DirectoryAfterSpillSummaryClear", "Directory", "After", "BoundedOwnership", "Helpable", "OrderingPoint", true, "After Present(X) becomes Empty(X) and before releasing the exact canonical mutation."},
    {45, "ParticipantAfterRecoveryFenceBeforeReferenceScan", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After fencing one stale participant as Recovering and before scanning exact owned references."},
    {46, "AdvanceBeforeBytesAdvancedCas", "Advance", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", false, "After exact reservation and range validation, immediately before the BytesAdvanced CAS."},
    {47, "AdvanceAfterBytesAdvancedCas", "Advance", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", true, "Immediately after the checked BytesAdvanced CAS advances the exact reservation."},
    {48, "DisposalAfterParticipantClosingPublication", "Disposal", "After", "PublishedState", "ExplicitRecovery", "OrderingPoint", true, "After exact Active-to-Closing publication and before bounded local resource cleanup."},
    {49, "ParticipantAfterRegistrationBeforeEngineConstruction", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After participant Active publication returns successfully and before the engine can escape construction."},
    {50, "DirectoryAfterUnlinkOperationValidationBeforeLocationRead", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After an unlink helper validates its exact operation and before reading the generation-tagged location word."},
    {51, "DirectoryAfterLocationPublisherBindingValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After a location publisher validates its exact slot binding and before reading or publishing the location word."},
    {52, "DirectoryAfterCurrentOperationRevalidationBeforeDispatch", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After a helper revalidates the exact current directory operation and slot state, before dispatching the phase-specific side effect."},
    {53, "DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After a BindingChanged insert helper validates a non-canceling slot state and before publishing Reserved."},
    {54, "DirectoryAfterInsertCompletionStateValidationBeforeLocationRead", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After TryInsert validates a completed non-canceling insert and before reading its generation-tagged location."},
    {55, "ReserveAfterDirectoryInsertBeforePendingClassification", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After directory insertion succeeds and before the reserve path classifies whether the reservation remains pending."},
    {56, "DirectoryBeforeInsertOuterLoopBudgetCheck", "Directory", "Before", "BoundedOwnership", "Helpable", "HelpWindow", false, "Immediately before TryInsert checks its operation-wide budget at the outer helper-loop boundary."},
    {57, "DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After an invalid binding's exact source word is confirmed and before the binding and source are jointly revalidated."},
    {58, "StoreFullAfterFirstCollectBeforeVerification", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "After the first all-occupied slot-control collect and before the exact verification collect."},
    {59, "StoreFullAfterExactDoubleCollect", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", false, "After the second collect confirms the StoreFull candidate observed between the two collects."},
    {60, "DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After an exact unlink descriptor is clear and before the reclaiming slot generation advances."},
    {61, "ParticipantAfterPidNamespaceWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", false, "After a Registering owner writes its Linux PID-namespace identity before Active publication."},
    {62, "DirectoryAfterCancelLocationClearBeforeDescriptorRejection", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After a canceled insert clears its exact cell/location and before publishing the Rejected descriptor."},
    {63, "ReclaimAfterLeaseScanBeforeOwnershipCas", "Reclaim", "After", "NoSharedOwnership", "Helpable", "ValidationWindow", false, "After proving no exact active lease remains and before RemoveRequested changes to Reclaiming."},
    {64, "ParticipantBeforeReclaimGenerationAdvanceCas", "Participant", "Before", "NoSharedOwnership", "Helpable", "ValidationWindow", false, "Immediately before an unowned Reclaiming participant record advances or retires its generation."},
    {65, "ProjectAfterMetadataReadBeforeControlRevalidation", "Project", "After", "BoundedOwnership", "ExplicitRecovery", "ProjectionLifetime", false, "After lease projection metadata is read and before its exact slot control is revalidated."},
    {66, "DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", false, "After an empty location and its exact publication source are revalidated, immediately before the zero-to-location CAS."},
    {67, "DirectoryAfterLocationPublicationBeforeSourceRevalidation", "Directory", "After", "PublishedState", "Helpable", "ValidationWindow", true, "Immediately after a zero-to-location CAS succeeds and before the publisher revalidates its exact source."},
}};

[[nodiscard]] inline constexpr const checkpoint_entry* find_checkpoint(
    std::int32_t id) noexcept {
    return id >= 1 && id <= static_cast<std::int32_t>(checkpoints.size())
        ? &checkpoints[static_cast<std::size_t>(id - 1)]
        : nullptr;
}

} // namespace sms::interop_test
