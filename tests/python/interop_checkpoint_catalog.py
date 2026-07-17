"""Canonical checkpoint metadata shared with the managed SMS2 test catalog."""

from __future__ import annotations

from typing import NamedTuple


class Checkpoint(NamedTuple):
    id: int
    name: str
    family: str
    position: str
    pause: str
    crash: str
    race: str
    is_public_ordering_point: bool
    description: str

    def protocol_result(self) -> dict[str, object]:
        return {
            "id": self.id,
            "name": self.name,
            "family": self.family,
            "position": self.position,
            "pause": self.pause,
            "crash": self.crash,
            "race": self.race,
            "isPublicOrderingPoint": self.is_public_ordering_point,
            "description": self.description,
        }


CHECKPOINTS = (
    Checkpoint(1, "PublishBeforeSlotClaim", "Publish", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "Before a simple publisher claims a value slot."),
    Checkpoint(2, "PublishAfterCommitPublication", "Publish", "After", "PublishedState", "DurableOutcome", "OrderingPoint", True, "After the value generation becomes published."),
    Checkpoint(3, "ReserveBeforeSlotClaim", "Reserve", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "Before a reservation claims a value slot."),
    Checkpoint(4, "ReserveAfterReservationPublication", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", True, "After exact-key reservation ownership is published."),
    Checkpoint(5, "CommitBeforePublicationCas", "Commit", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", False, "Immediately before the reservation publication CAS."),
    Checkpoint(6, "CommitAfterPublicationCas", "Commit", "After", "PublishedState", "DurableOutcome", "OrderingPoint", True, "Immediately after the reservation publication CAS."),
    Checkpoint(7, "AbortBeforeAbortCas", "Abort", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", False, "Before reservation ownership changes to aborting."),
    Checkpoint(8, "AbortAfterUnlinkCompletion", "Abort", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", False, "After the aborted key binding is unlinked."),
    Checkpoint(9, "AcquireBeforeLeaseClaimCas", "Acquire", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", False, "Before claiming a lease record."),
    Checkpoint(10, "AcquireAfterPublishedRevalidation", "Acquire", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", True, "After an active lease revalidates the published generation."),
    Checkpoint(11, "ProjectBeforeHandleValidation", "Project", "Before", "NoSharedOwnership", "NoSharedEffect", "ProjectionLifetime", False, "Before a token validates its exact incarnation."),
    Checkpoint(12, "ProjectAfterSpanProjection", "Project", "After", "BoundedOwnership", "DurableOutcome", "ProjectionLifetime", False, "After a validated mapped-memory span is projected."),
    Checkpoint(13, "ReleaseBeforeActiveReleaseCas", "Release", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", False, "Before ending active lease protection."),
    Checkpoint(14, "ReleaseAfterRecordRecycle", "Release", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", True, "After the exact lease-record incarnation is recycled or retired."),
    Checkpoint(15, "RemoveBeforeLogicalRemovalCas", "Remove", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", False, "Before published changes to logically removed."),
    Checkpoint(16, "RemoveAfterLeaseClassification", "Remove", "After", "PublishedState", "Helpable", "OrderingPoint", True, "After the stable exact-lease classification scan."),
    Checkpoint(17, "ReclaimBeforeOwnershipCas", "Reclaim", "Before", "NoSharedOwnership", "Helpable", "OrderingPoint", False, "Before one helper claims exact reclamation."),
    Checkpoint(18, "ReclaimAfterGenerationAdvance", "Reclaim", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", True, "After exact helper cleanup and generation advance."),
    Checkpoint(19, "DirectoryBeforeDescriptorPublication", "Directory", "Before", "BoundedOwnership", "ExplicitRecovery", "HelpWindow", False, "Before publishing a complete directory mutation descriptor."),
    Checkpoint(20, "DirectoryAfterDescriptorClear", "Directory", "After", "NoSharedOwnership", "NoSharedEffect", "HelpWindow", True, "After completing and clearing the exact directory descriptor."),
    Checkpoint(21, "DiagnosticsBeforeBoundedScan", "Diagnostics", "Before", "NoSharedOwnership", "NoSharedEffect", "SnapshotWindow", False, "Before a diagnostics caller begins bounded scans."),
    Checkpoint(22, "DiagnosticsAfterSnapshotAssembly", "Diagnostics", "After", "NoSharedOwnership", "NoSharedEffect", "SnapshotWindow", False, "After the moment-in-time snapshot is assembled."),
    Checkpoint(23, "RecoveryBeforeOwnerClassification", "Recovery", "Before", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "Before classifying an exact participant incarnation."),
    Checkpoint(24, "RecoveryAfterExactRecoveryCas", "Recovery", "After", "NoSharedOwnership", "Helpable", "HelpWindow", True, "After the exact stale-owner recovery CAS."),
    Checkpoint(25, "DisposalBeforeLocalGateClose", "Disposal", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", False, "Before the local handle rejects new operations."),
    Checkpoint(26, "DisposalAfterParticipantRelease", "Disposal", "After", "PublishedState", "ExplicitRecovery", "HelpWindow", True, "After bounded local cleanup and the exact participant retirement attempt."),
    Checkpoint(27, "ParticipantBeforeRegisteringCas", "Participant", "Before", "NoSharedOwnership", "NoSharedEffect", "OrderingPoint", False, "Before publishing PID and participant incarnation."),
    Checkpoint(28, "ParticipantAfterActivePublication", "Participant", "After", "NoSharedOwnership", "DurableOutcome", "OrderingPoint", True, "After the participant record becomes active and usable by data claims."),
    Checkpoint(29, "DirectoryAfterOperationValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After exact operation/binding/control generation validation and before a helper side effect."),
    Checkpoint(30, "DirectoryAfterLocationValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After exact location generation validation and before unlink side effects."),
    Checkpoint(31, "ReclaimAfterMetadataValidation", "Reclaim", "After", "NoSharedOwnership", "Helpable", "ValidationWindow", False, "After exact reclaim-generation validation and before the final generation-advance CAS."),
    Checkpoint(32, "ParticipantAfterIdentityKindWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a Registering owner writes identity kind while older ordinary fields may remain."),
    Checkpoint(33, "ParticipantAfterReservedWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a Registering owner clears the reserved field while older ordinary fields may remain."),
    Checkpoint(34, "ParticipantAfterProcessStartWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a Registering owner writes process-start identity before Active publication."),
    Checkpoint(35, "ParticipantAfterOpenSequenceWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a Registering owner writes open sequence before Active publication."),
    Checkpoint(36, "AbortAfterOwnershipReleaseCas", "Abort", "After", "NoSharedOwnership", "Helpable", "HelpWindow", True, "After reservation ownership changes to universally helpable Aborting."),
    Checkpoint(37, "SlotClaimAfterParticipantRecheck", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a slot claim revalidates its participant and before ordinary metadata writes."),
    Checkpoint(38, "ReleaseAfterOwnershipReleaseCas", "Release", "After", "PublishedState", "Helpable", "HelpWindow", False, "After an active lease publishes unowned Releasing and before exact-incarnation recycle."),
    Checkpoint(39, "AcquireAfterLeaseActivationBeforeFinalLookup", "Acquire", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After lease activation and before the acquire operation's final directory revalidation."),
    Checkpoint(40, "ReserveAfterExistingLookup", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "After reserve/publish observes an existing key and before its final existing-generation lookup."),
    Checkpoint(41, "DirectoryBeforeSpillSummaryPublicationCas", "Directory", "Before", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After loading the prior spill-summary version and revalidating the exact insert, before its publication CAS."),
    Checkpoint(42, "DirectoryAfterSpillSummaryPublication", "Directory", "After", "BoundedOwnership", "Helpable", "OrderingPoint", True, "After publishing Present(candidate) and before any overflow-cell publication CAS."),
    Checkpoint(43, "DirectoryAfterEmptySpillSummaryScan", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After a stable empty full overflow scan and before the exact versioned-empty clear CAS."),
    Checkpoint(44, "DirectoryAfterSpillSummaryClear", "Directory", "After", "BoundedOwnership", "Helpable", "OrderingPoint", True, "After Present(X) becomes Empty(X) and before releasing the exact canonical mutation."),
    Checkpoint(45, "ParticipantAfterRecoveryFenceBeforeReferenceScan", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After fencing one stale participant as Recovering and before scanning exact owned references."),
    Checkpoint(46, "AdvanceBeforeBytesAdvancedCas", "Advance", "Before", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", False, "After exact reservation and range validation, immediately before the BytesAdvanced CAS."),
    Checkpoint(47, "AdvanceAfterBytesAdvancedCas", "Advance", "After", "BoundedOwnership", "ExplicitRecovery", "OrderingPoint", True, "Immediately after the checked BytesAdvanced CAS advances the exact reservation."),
    Checkpoint(48, "DisposalAfterParticipantClosingPublication", "Disposal", "After", "PublishedState", "ExplicitRecovery", "OrderingPoint", True, "After exact Active-to-Closing publication and before bounded local resource cleanup."),
    Checkpoint(49, "ParticipantAfterRegistrationBeforeEngineConstruction", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After participant Active publication returns successfully and before the engine can escape construction."),
    Checkpoint(50, "DirectoryAfterUnlinkOperationValidationBeforeLocationRead", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After an unlink helper validates its exact operation and before reading the generation-tagged location word."),
    Checkpoint(51, "DirectoryAfterLocationPublisherBindingValidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After a location publisher validates its exact slot binding and before reading or publishing the location word."),
    Checkpoint(52, "DirectoryAfterCurrentOperationRevalidationBeforeDispatch", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After a helper revalidates the exact current directory operation and slot state, before dispatching the phase-specific side effect."),
    Checkpoint(53, "DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After a BindingChanged insert helper validates a non-canceling slot state and before publishing Reserved."),
    Checkpoint(54, "DirectoryAfterInsertCompletionStateValidationBeforeLocationRead", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After TryInsert validates a completed non-canceling insert and before reading its generation-tagged location."),
    Checkpoint(55, "ReserveAfterDirectoryInsertBeforePendingClassification", "Reserve", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After directory insertion succeeds and before the reserve path classifies whether the reservation remains pending."),
    Checkpoint(56, "DirectoryBeforeInsertOuterLoopBudgetCheck", "Directory", "Before", "BoundedOwnership", "Helpable", "HelpWindow", False, "Immediately before TryInsert checks its operation-wide budget at the outer helper-loop boundary."),
    Checkpoint(57, "DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After an invalid binding's exact source word is confirmed and before the binding and source are jointly revalidated."),
    Checkpoint(58, "StoreFullAfterFirstCollectBeforeVerification", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "After the first all-occupied slot-control collect and before the exact verification collect."),
    Checkpoint(59, "StoreFullAfterExactDoubleCollect", "Reserve", "After", "NoSharedOwnership", "NoSharedEffect", "ValidationWindow", False, "After the second collect confirms the StoreFull candidate observed between the two collects."),
    Checkpoint(60, "DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After an exact unlink descriptor is clear and before the reclaiming slot generation advances."),
    Checkpoint(61, "ParticipantAfterPidNamespaceWrite", "Participant", "After", "BoundedOwnership", "ExplicitRecovery", "ValidationWindow", False, "After a Registering owner writes its Linux PID-namespace identity before Active publication."),
    Checkpoint(62, "DirectoryAfterCancelLocationClearBeforeDescriptorRejection", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After a canceled insert clears its exact cell/location and before publishing the Rejected descriptor."),
    Checkpoint(63, "ReclaimAfterLeaseScanBeforeOwnershipCas", "Reclaim", "After", "NoSharedOwnership", "Helpable", "ValidationWindow", False, "After proving no exact active lease remains and before RemoveRequested changes to Reclaiming."),
    Checkpoint(64, "ParticipantBeforeReclaimGenerationAdvanceCas", "Participant", "Before", "NoSharedOwnership", "Helpable", "ValidationWindow", False, "Immediately before an unowned Reclaiming participant record advances or retires its generation."),
    Checkpoint(65, "ProjectAfterMetadataReadBeforeControlRevalidation", "Project", "After", "BoundedOwnership", "ExplicitRecovery", "ProjectionLifetime", False, "After lease projection metadata is read and before its exact slot control is revalidated."),
    Checkpoint(66, "DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas", "Directory", "After", "BoundedOwnership", "Helpable", "ValidationWindow", False, "After an empty location and its exact publication source are revalidated, immediately before the zero-to-location CAS."),
    Checkpoint(67, "DirectoryAfterLocationPublicationBeforeSourceRevalidation", "Directory", "After", "PublishedState", "Helpable", "ValidationWindow", True, "Immediately after a zero-to-location CAS succeeds and before the publisher revalidates its exact source."),
)


CHECKPOINTS_BY_ID = {entry.id: entry for entry in CHECKPOINTS}


__all__ = ["CHECKPOINTS", "CHECKPOINTS_BY_ID", "Checkpoint"]
