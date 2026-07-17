#pragma once

// Canonical native checkpoint identities and their test-only, process-local
// transport. The catalog is private to repository builds: it is not installed,
// exported through the C ABI, or represented in SMS2 mapped state.

#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <string_view>
#include <thread>
#include <utility>

namespace sms::test_detail {

inline constexpr int abrupt_exit_code = 97;

#define SMS_CANONICAL_CHECKPOINTS(X) \
    X(1, PublishBeforeSlotClaim) \
    X(2, PublishAfterCommitPublication) \
    X(3, ReserveBeforeSlotClaim) \
    X(4, ReserveAfterReservationPublication) \
    X(5, CommitBeforePublicationCas) \
    X(6, CommitAfterPublicationCas) \
    X(7, AbortBeforeAbortCas) \
    X(8, AbortAfterUnlinkCompletion) \
    X(9, AcquireBeforeLeaseClaimCas) \
    X(10, AcquireAfterPublishedRevalidation) \
    X(11, ProjectBeforeHandleValidation) \
    X(12, ProjectAfterSpanProjection) \
    X(13, ReleaseBeforeActiveReleaseCas) \
    X(14, ReleaseAfterRecordRecycle) \
    X(15, RemoveBeforeLogicalRemovalCas) \
    X(16, RemoveAfterLeaseClassification) \
    X(17, ReclaimBeforeOwnershipCas) \
    X(18, ReclaimAfterGenerationAdvance) \
    X(19, DirectoryBeforeDescriptorPublication) \
    X(20, DirectoryAfterDescriptorClear) \
    X(21, DiagnosticsBeforeBoundedScan) \
    X(22, DiagnosticsAfterSnapshotAssembly) \
    X(23, RecoveryBeforeOwnerClassification) \
    X(24, RecoveryAfterExactRecoveryCas) \
    X(25, DisposalBeforeLocalGateClose) \
    X(26, DisposalAfterParticipantRelease) \
    X(27, ParticipantBeforeRegisteringCas) \
    X(28, ParticipantAfterActivePublication) \
    X(29, DirectoryAfterOperationValidation) \
    X(30, DirectoryAfterLocationValidation) \
    X(31, ReclaimAfterMetadataValidation) \
    X(32, ParticipantAfterIdentityKindWrite) \
    X(33, ParticipantAfterReservedWrite) \
    X(34, ParticipantAfterProcessStartWrite) \
    X(35, ParticipantAfterOpenSequenceWrite) \
    X(36, AbortAfterOwnershipReleaseCas) \
    X(37, SlotClaimAfterParticipantRecheck) \
    X(38, ReleaseAfterOwnershipReleaseCas) \
    X(39, AcquireAfterLeaseActivationBeforeFinalLookup) \
    X(40, ReserveAfterExistingLookup) \
    X(41, DirectoryBeforeSpillSummaryPublicationCas) \
    X(42, DirectoryAfterSpillSummaryPublication) \
    X(43, DirectoryAfterEmptySpillSummaryScan) \
    X(44, DirectoryAfterSpillSummaryClear) \
    X(45, ParticipantAfterRecoveryFenceBeforeReferenceScan) \
    X(46, AdvanceBeforeBytesAdvancedCas) \
    X(47, AdvanceAfterBytesAdvancedCas) \
    X(48, DisposalAfterParticipantClosingPublication) \
    X(49, ParticipantAfterRegistrationBeforeEngineConstruction) \
    X(50, DirectoryAfterUnlinkOperationValidationBeforeLocationRead) \
    X(51, DirectoryAfterLocationPublisherBindingValidation) \
    X(52, DirectoryAfterCurrentOperationRevalidationBeforeDispatch) \
    X(53, DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication) \
    X(54, DirectoryAfterInsertCompletionStateValidationBeforeLocationRead) \
    X(55, ReserveAfterDirectoryInsertBeforePendingClassification) \
    X(56, DirectoryBeforeInsertOuterLoopBudgetCheck) \
    X(57, DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation) \
    X(58, StoreFullAfterFirstCollectBeforeVerification) \
    X(59, StoreFullAfterExactDoubleCollect) \
    X(60, DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance) \
    X(61, ParticipantAfterPidNamespaceWrite) \
    X(62, DirectoryAfterCancelLocationClearBeforeDescriptorRejection) \
    X(63, ReclaimAfterLeaseScanBeforeOwnershipCas) \
    X(64, ParticipantBeforeReclaimGenerationAdvanceCas) \
    X(65, ProjectAfterMetadataReadBeforeControlRevalidation) \
    X(66, DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas) \
    X(67, DirectoryAfterLocationPublicationBeforeSourceRevalidation)

enum class CheckpointId : std::int32_t {
#define SMS_CHECKPOINT_ENUM(id, name) name = id,
    SMS_CANONICAL_CHECKPOINTS(SMS_CHECKPOINT_ENUM)
#undef SMS_CHECKPOINT_ENUM
};

inline constexpr std::int32_t checkpoint_count = 67;

[[nodiscard]] inline constexpr std::string_view checkpoint_name(
    CheckpointId checkpoint) noexcept {
    switch (checkpoint) {
#define SMS_CHECKPOINT_NAME(id, name) case CheckpointId::name: return #name;
    SMS_CANONICAL_CHECKPOINTS(SMS_CHECKPOINT_NAME)
#undef SMS_CHECKPOINT_NAME
    }
    return {};
}

class CheckpointObserver {
public:
    virtual void reach(CheckpointId checkpoint) noexcept = 0;

protected:
    ~CheckpointObserver() = default;
};

#if defined(SMS_ENABLE_TEST_CHECKPOINTS)

CheckpointObserver* set_thread_checkpoint_observer(
    CheckpointObserver* observer) noexcept;
void reach_checkpoint(CheckpointId checkpoint) noexcept;

class ScopedCheckpointObserver {
public:
    explicit ScopedCheckpointObserver(CheckpointObserver& observer) noexcept
        : previous_(set_thread_checkpoint_observer(&observer)) {}

    ~ScopedCheckpointObserver() {
        (void)set_thread_checkpoint_observer(previous_);
    }

    ScopedCheckpointObserver(const ScopedCheckpointObserver&) = delete;
    ScopedCheckpointObserver& operator=(const ScopedCheckpointObserver&) = delete;

private:
    CheckpointObserver* previous_{};
};

#else

inline void reach_checkpoint(CheckpointId) noexcept {}

#endif

// Legacy file transport retained for the standalone multiprocess fault agent.
// Canonical interop commands use ScopedCheckpointObserver instead.
class FileCheckpoint {
public:
    FileCheckpoint(
        std::filesystem::path ready_path,
        std::filesystem::path release_path) noexcept
        : ready_path_(std::move(ready_path)),
          release_path_(std::move(release_path)) {}

    [[nodiscard]] bool reach(
        std::string_view checkpoint,
        bool crash = false) const noexcept {
        try {
            std::ofstream ready(ready_path_, std::ios::binary | std::ios::trunc);
            ready.write(
                checkpoint.data(),
                static_cast<std::streamsize>(checkpoint.size()));
            ready.put('\n');
            ready.flush();
            if (!ready) return false;
            ready.close();
            if (crash) std::_Exit(abrupt_exit_code);

            const auto deadline = std::chrono::steady_clock::now() +
                std::chrono::minutes(2);
            while (std::chrono::steady_clock::now() < deadline) {
                std::error_code error;
                if (std::filesystem::exists(release_path_, error) && !error) {
                    return true;
                }
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
            }
        } catch (...) {
        }
        return false;
    }

private:
    std::filesystem::path ready_path_;
    std::filesystem::path release_path_;
};

} // namespace sms::test_detail

