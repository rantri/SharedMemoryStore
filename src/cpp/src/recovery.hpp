#pragma once

#include "key_directory.hpp"
#include "lease_registry.hpp"
#include "operation_budget.hpp"
#include "participant_registry.hpp"
#include "reclaimer.hpp"
#include "slot_table.hpp"

#include <cstddef>
#include <cstdint>

namespace sms::detail {

enum class ParticipantClassificationKind : std::int32_t {
    current_process = 0,
    live = 1,
    stale = 2,
    unsupported = 3,
    inconsistent = 4,
    changing = 5,
};

struct ParticipantIncarnation {
    std::int32_t record_index{-1};
    std::int32_t generation{};
    std::uint32_t token{};
    std::int32_t state{};
    std::int32_t process_id{};
    std::int32_t identity_kind{};
    std::int64_t process_start_value{};
    std::int64_t open_sequence{};
    std::uint64_t pid_namespace_id{};
    std::int32_t reserved_value{};
    std::uint64_t control{};
};

struct ParticipantClassification {
    ParticipantClassificationKind kind{
        ParticipantClassificationKind::inconsistent};
    ParticipantIncarnation incarnation{};
};

enum class ProcessObservationKind : std::int32_t {
    available = 0,
    missing = 1,
    unsupported = 2,
};

struct ProcessIdentityObservation {
    ProcessObservationKind kind{ProcessObservationKind::unsupported};
    std::int64_t process_start_value{};
};

enum class RecoveryPlatform : std::int32_t {
    windows = 1,
    linux = 2,
    unsupported = 3,
};

// Process observation is injected as a value-only seam so recovery schedules
// and PID-reuse cases are deterministic in white-box tests. A default-formed
// source selects the native observer in RecoveryCoordinator's constructor.
struct RecoveryObservationSource {
    using observe_callback = ProcessIdentityObservation (*)(
        void* context,
        std::int32_t process_id,
        std::int32_t identity_kind) noexcept;

    void* context{};
    observe_callback observe{};
    RecoveryPlatform platform{RecoveryPlatform::unsupported};
    std::int32_t current_process_id{};
    std::uint64_t current_pid_namespace_id{};
};

[[nodiscard]] RecoveryObservationSource
native_recovery_observation_source() noexcept;

struct RecoveryScanReport {
    std::int32_t scanned{};
    std::int32_t recovered{};
    std::int32_t active{};
    std::int32_t unsupported{};
    std::int32_t failed{};
};

enum class LeaseRecoveryDisposition : std::int32_t {
    retry = 0,
    failed = 1,
    unsupported = 2,
    active = 3,
    recover = 4,
};

// Explicit SMS2 recovery coordinator. It owns no mapping, OS handle, worker,
// or lock; all mutations are generation-fenced full-word CAS operations.
class RecoveryCoordinator {
public:
    RecoveryCoordinator(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        ParticipantRegistry& participants,
        SlotTable& slots,
        KeyDirectory& directory,
        LeaseRegistry& leases,
        Reclaimer& reclaimer,
        RecoveryObservationSource observations = {}) noexcept;

    [[nodiscard]] bool valid() const noexcept;

    [[nodiscard]] ParticipantClassification classify_participant(
        std::uint32_t participant_token) const noexcept;

    [[nodiscard]] sms_status try_recover_leases(
        bool recover_current_process_leases,
        const OperationBudget& budget,
        RecoveryScanReport& report) noexcept;

    [[nodiscard]] sms_status try_recover_reservations(
        bool recover_current_process_reservations,
        const OperationBudget& budget,
        RecoveryScanReport& report) noexcept;

    // After a resource scan, publish stale/closing participant handoffs and
    // retire only incarnations with no remaining exact slot or lease owner.
    [[nodiscard]] sms_status help_recovering_participants(
        const OperationBudget& budget,
        std::int32_t& retired_count) noexcept;

    [[nodiscard]] static LeaseRecoveryDisposition lease_disposition(
        LeaseState lease_state,
        const ParticipantClassification& classification,
        bool participant_handoff_published,
        bool recover_current_process_leases) noexcept;

    [[nodiscard]] static bool can_recover_reservation(
        SlotState slot_state,
        const ParticipantClassification& classification,
        bool recover_current_process_reservations) noexcept;

private:
    enum class ParticipantSnapshotStatus : std::int32_t {
        stable,
        stale,
        inconsistent,
        changing,
    };

    enum class LocationReferenceStatus : std::int32_t {
        none,
        older,
        current,
        invalid,
    };

    struct ReservationMetadataResult {
        bool lifecycle_still_current{};
        bool unreferenced_pre_metadata{};
    };

    [[nodiscard]] ParticipantSnapshotStatus read_participant_snapshot(
        std::uint32_t participant_token,
        ParticipantIncarnation& incarnation) const noexcept;
    [[nodiscard]] ParticipantClassification classify_snapshot_owner(
        const ParticipantIncarnation& incarnation) const noexcept;
    [[nodiscard]] bool is_pid_namespace_recovery_enabled() const noexcept;
    [[nodiscard]] bool valid_slot_binding(std::uint64_t binding) const noexcept;
    [[nodiscard]] bool same_owned_slot_lifecycle(
        std::uint64_t current,
        std::uint64_t classified) const noexcept;

    [[nodiscard]] sms_status recycle_lease(
        LeaseRecordV2& record,
        std::int64_t incarnation,
        std::uint64_t expected_transition,
        bool& recycled) noexcept;
    [[nodiscard]] sms_status validate_reservation_metadata(
        std::int32_t slot_index,
        std::uint64_t expected_control,
        ValueSlotMetadataV2& slot,
        const OperationBudget& budget,
        ReservationMetadataResult& result) noexcept;
    [[nodiscard]] bool try_decode_recovery_operation(
        std::uint64_t raw,
        std::int64_t generation,
        SlotState slot_state,
        DirectoryOperation& operation) const noexcept;
    [[nodiscard]] bool recovery_operation_location_valid(
        const DirectoryOperation& operation,
        std::uint64_t location_raw,
        SlotState slot_state) const noexcept;
    [[nodiscard]] LocationReferenceStatus classify_location_reference(
        std::uint64_t raw,
        std::int64_t generation) const noexcept;
    [[nodiscard]] bool directory_target_in_bounds(
        std::int32_t kind,
        std::int64_t index) const noexcept;
    [[nodiscard]] sms_status help_reservation(
        std::uint64_t exact_binding,
        bool unreferenced_pre_metadata,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status has_participant_references(
        std::uint32_t participant_token,
        const OperationBudget& budget,
        bool& referenced) const noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    ParticipantRegistry* participants_{};
    SlotTable* slots_{};
    KeyDirectory* directory_{};
    LeaseRegistry* leases_{};
    Reclaimer* reclaimer_{};
    RecoveryObservationSource observations_{};
};

} // namespace sms::detail
