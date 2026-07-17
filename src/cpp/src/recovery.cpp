#include "recovery.hpp"

#include "checkpoint.hpp"
#include "mapped_atomic.hpp"

#include <algorithm>
#include <atomic>
#include <charconv>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <string_view>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#elif defined(__linux__)
#include <unistd.h>
#endif

namespace sms::detail {
namespace {

constexpr std::int32_t classification_retry_budget = 64;
constexpr std::int32_t recycle_confirmation_attempts = 8;

template <class T>
[[nodiscard]] T metadata_load(T& location) noexcept {
    return std::atomic_ref<T>(location).load(std::memory_order_acquire);
}

[[nodiscard, maybe_unused]] ProcessIdentityObservation unsupported_observation(
    void*,
    std::int32_t,
    std::int32_t) noexcept {
    return {};
}

#if defined(_WIN32)

[[nodiscard]] ProcessIdentityObservation observe_windows_process(
    void*,
    std::int32_t process_id,
    std::int32_t identity_kind) noexcept {
    if (process_id <= 0 ||
        (identity_kind != identity_unknown &&
         identity_kind != identity_windows_creation_file_time)) {
        return {};
    }

    const auto raw_process_id = static_cast<DWORD>(process_id);
    HANDLE process = OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION, FALSE, raw_process_id);
    if (process == nullptr) {
        return GetLastError() == ERROR_INVALID_PARAMETER
            ? ProcessIdentityObservation{ProcessObservationKind::missing, 0}
            : ProcessIdentityObservation{};
    }

    DWORD exit_code{};
    if (!GetExitCodeProcess(process, &exit_code)) {
        CloseHandle(process);
        return {};
    }
    if (exit_code != STILL_ACTIVE) {
        CloseHandle(process);
        return {ProcessObservationKind::missing, 0};
    }
    if (identity_kind == identity_unknown) {
        CloseHandle(process);
        return {ProcessObservationKind::available, 0};
    }

    FILETIME creation{};
    FILETIME exit{};
    FILETIME kernel{};
    FILETIME user{};
    const auto read = GetProcessTimes(
        process, &creation, &exit, &kernel, &user) != FALSE;
    CloseHandle(process);
    if (!read) return {};
    const auto value =
        (static_cast<std::uint64_t>(creation.dwHighDateTime) << 32U) |
        static_cast<std::uint64_t>(creation.dwLowDateTime);
    if (value == 0 ||
        value > static_cast<std::uint64_t>(
            std::numeric_limits<std::int64_t>::max())) {
        return {};
    }
    return {
        ProcessObservationKind::available,
        static_cast<std::int64_t>(value)};
}

#elif defined(__linux__)

[[nodiscard]] ProcessIdentityObservation observe_linux_process(
    void*,
    std::int32_t process_id,
    std::int32_t identity_kind) noexcept {
    try {
    if (process_id <= 0 ||
        (identity_kind != identity_unknown &&
         identity_kind != identity_linux_proc_start_ticks)) {
        return {};
    }

    const auto process_directory =
        std::filesystem::path("/proc") / std::to_string(process_id);
    std::ifstream input(process_directory / "stat");
    if (!input) {
        std::error_code error;
        const auto exists = std::filesystem::exists(process_directory, error);
        return !error && !exists
            ? ProcessIdentityObservation{ProcessObservationKind::missing, 0}
            : ProcessIdentityObservation{};
    }
    if (identity_kind == identity_unknown) {
        return {ProcessObservationKind::available, 0};
    }

    std::string stat;
    std::getline(input, stat);
    const auto command_end = stat.rfind(')');
    if (command_end == std::string::npos || command_end + 2U >= stat.size()) {
        return {};
    }
    std::string_view fields(stat);
    fields.remove_prefix(command_end + 2U);
    std::string_view start_field{};
    for (std::int32_t field = 0; field <= 19; ++field) {
        while (!fields.empty() && fields.front() == ' ') fields.remove_prefix(1);
        if (fields.empty()) return {};
        const auto separator = fields.find(' ');
        const auto current = fields.substr(0, separator);
        if (field == 19) {
            start_field = current;
            break;
        }
        if (separator == std::string_view::npos) return {};
        fields.remove_prefix(separator + 1U);
    }

    std::int64_t start_value{};
    const auto parsed = std::from_chars(
        start_field.data(),
        start_field.data() + start_field.size(),
        start_value);
    if (parsed.ec != std::errc{} || parsed.ptr != start_field.data() + start_field.size() ||
        start_value <= 0) {
        return {};
    }
    return {ProcessObservationKind::available, start_value};
    } catch (...) {
        // Liveness observation is conservative: allocation or filesystem
        // adapter failures are unsupported evidence, never process failure.
        return {};
    }
}

[[nodiscard]] std::uint64_t current_linux_pid_namespace_id() noexcept {
    char target[128]{};
    const auto length = readlink(
        "/proc/self/ns/pid", target, sizeof(target) - 1U);
    if (length <= 0 ||
        static_cast<std::size_t>(length) >= sizeof(target)) {
        return 0;
    }
    const std::string_view value(target, static_cast<std::size_t>(length));
    constexpr std::string_view prefix = "pid:[";
    if (!value.starts_with(prefix) || value.back() != ']' ||
        value.size() <= prefix.size() + 1U) {
        return 0;
    }
    const auto number = value.substr(
        prefix.size(), value.size() - prefix.size() - 1U);
    std::uint64_t result{};
    const auto parsed = std::from_chars(
        number.data(), number.data() + number.size(), result);
    return parsed.ec == std::errc{} &&
            parsed.ptr == number.data() + number.size() && result != 0
        ? result
        : 0;
}

#endif

[[nodiscard]] bool is_owner_state(std::int32_t state) noexcept {
    return state >= participant_registering && state <= participant_recovering;
}

[[nodiscard]] bool is_owned_slot_state(SlotState state) noexcept {
    return state == SlotState::initializing || state == SlotState::reserved;
}

[[nodiscard]] sms_status completion_status(
    sms_status status,
    const RecoveryScanReport& report) noexcept {
    return report.recovered > 0 ? SMS_STATUS_SUCCESS : status;
}

} // namespace

RecoveryObservationSource native_recovery_observation_source() noexcept {
    RecoveryObservationSource result{};
#if defined(_WIN32)
    const auto process_id = GetCurrentProcessId();
    result.observe = &observe_windows_process;
    result.platform = RecoveryPlatform::windows;
    result.current_process_id =
        process_id <= static_cast<DWORD>(std::numeric_limits<std::int32_t>::max())
        ? static_cast<std::int32_t>(process_id)
        : 0;
#elif defined(__linux__)
    const auto process_id = getpid();
    result.observe = &observe_linux_process;
    result.platform = RecoveryPlatform::linux;
    result.current_process_id = process_id > 0
        ? static_cast<std::int32_t>(process_id)
        : 0;
    result.current_pid_namespace_id = current_linux_pid_namespace_id();
#else
    result.observe = &unsupported_observation;
    result.platform = RecoveryPlatform::unsupported;
#endif
    return result;
}

RecoveryCoordinator::RecoveryCoordinator(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    ParticipantRegistry& participants,
    SlotTable& slots,
    KeyDirectory& directory,
    LeaseRegistry& leases,
    Reclaimer& reclaimer,
    RecoveryObservationSource observations) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout),
      participants_(&participants),
      slots_(&slots),
      directory_(&directory),
      leases_(&leases),
      reclaimer_(&reclaimer),
      observations_(observations.observe == nullptr
            ? native_recovery_observation_source()
            : observations) {}

bool RecoveryCoordinator::valid() const noexcept {
    return mapping_base_ != nullptr &&
        mapping_length_ >= sizeof(StoreHeaderV2) &&
        MappedAtomic64::supported() &&
        MappedAtomic64::is_aligned(mapping_base_) &&
        layout_.participant_record_count > 0 &&
        layout_.participant_generation_mask > 0 &&
        layout_.slot_count > 0 &&
        layout_.lease_record_count > 0 &&
        participants_ != nullptr && participants_->valid() &&
        slots_ != nullptr && slots_->valid() &&
        directory_ != nullptr && directory_->valid() &&
        leases_ != nullptr && leases_->valid() &&
        reclaimer_ != nullptr && reclaimer_->valid() &&
        observations_.observe != nullptr;
}

RecoveryCoordinator::ParticipantSnapshotStatus
RecoveryCoordinator::read_participant_snapshot(
    std::uint32_t participant_token,
    ParticipantIncarnation& incarnation) const noexcept {
    incarnation = {};
    ParticipantToken token{};
    if (!ParticipantToken::try_decode(
            participant_token, layout_.participant_record_count, token) ||
        token.generation > layout_.participant_generation_mask) {
        return ParticipantSnapshotStatus::inconsistent;
    }

    auto* record = participants_->record(token.record_index);
    if (record == nullptr) return ParticipantSnapshotStatus::inconsistent;
    const auto control1 = MappedAtomic64::load_acquire(record->Control);
    const auto identity_kind = metadata_load(record->IdentityKind);
    const auto reserved = metadata_load(record->Reserved);
    const auto process_start_value = metadata_load(record->ProcessStartValue);
    const auto open_sequence = metadata_load(record->OpenSequence);
    const auto pid_namespace_id = MappedAtomic64::load_acquire(
        record->PidNamespaceId);
    const auto control2 = MappedAtomic64::load_acquire(record->Control);

    ParticipantControl control{};
    if (!ParticipantControl::try_decode(control1, control)) {
        incarnation.record_index = token.record_index;
        incarnation.token = participant_token;
        incarnation.control = control1;
        return control1 == control2
            ? ParticipantSnapshotStatus::inconsistent
            : ParticipantSnapshotStatus::changing;
    }
    incarnation = ParticipantIncarnation{
        token.record_index,
        control.incarnation,
        participant_token,
        control.state,
        control.process_id,
        identity_kind,
        process_start_value,
        open_sequence,
        pid_namespace_id,
        reserved,
        control1};

    if (control1 != control2) return ParticipantSnapshotStatus::changing;
    if (!control.structurally_valid(layout_.participant_generation_mask) ||
        reserved != 0 || identity_kind < identity_unknown ||
        identity_kind > identity_linux_proc_start_ticks ||
        process_start_value < 0 ||
        (is_owner_state(control.state) ? control.process_id <= 0
                                       : control.process_id != 0)) {
        return ParticipantSnapshotStatus::inconsistent;
    }
    if (control.incarnation != token.generation ||
        control.state == participant_free ||
        control.state == participant_reclaiming ||
        control.state == participant_retired) {
        return ParticipantSnapshotStatus::stale;
    }
    if ((control.state == participant_active ||
         control.state == participant_closing ||
         control.state == participant_recovering) &&
        (open_sequence <= 0 ||
         (identity_kind != identity_unknown && process_start_value == 0))) {
        return ParticipantSnapshotStatus::inconsistent;
    }
    return ParticipantSnapshotStatus::stable;
}

bool RecoveryCoordinator::is_pid_namespace_recovery_enabled() const noexcept {
    auto* header = reinterpret_cast<StoreHeaderV2*>(mapping_base_);
    return MappedAtomic64::load_acquire(header->PidNamespaceMode) ==
        sms2_pid_namespace_recovery_enabled;
}

ParticipantClassification RecoveryCoordinator::classify_snapshot_owner(
    const ParticipantIncarnation& incarnation) const noexcept {
    auto unsupported = [&incarnation] {
        return ParticipantClassification{
            ParticipantClassificationKind::unsupported, incarnation};
    };
    auto inconsistent = [&incarnation] {
        return ParticipantClassification{
            ParticipantClassificationKind::inconsistent, incarnation};
    };

    if (incarnation.state == participant_registering) {
        if (!is_pid_namespace_recovery_enabled()) return unsupported();
        const auto* header = reinterpret_cast<const StoreHeaderV2*>(mapping_base_);
        const auto store_namespace = header->PidNamespaceId;
        if (observations_.platform == RecoveryPlatform::linux) {
            if (store_namespace == 0 ||
                observations_.current_pid_namespace_id == 0 ||
                observations_.current_pid_namespace_id != store_namespace) {
                return unsupported();
            }
        } else if (observations_.platform == RecoveryPlatform::windows) {
            if (store_namespace != 0) return unsupported();
        } else {
            return unsupported();
        }

        const auto observation = observations_.observe(
            observations_.context, incarnation.process_id, identity_unknown);
        if (observation.kind == ProcessObservationKind::missing) {
            return {ParticipantClassificationKind::stale, incarnation};
        }
        if (observation.kind != ProcessObservationKind::available) {
            return unsupported();
        }
        return {
            incarnation.process_id == observations_.current_process_id
                ? ParticipantClassificationKind::current_process
                : ParticipantClassificationKind::unsupported,
            incarnation};
    }

    if (observations_.platform == RecoveryPlatform::linux) {
        if (incarnation.pid_namespace_id == 0 ||
            observations_.current_pid_namespace_id == 0 ||
            incarnation.pid_namespace_id != observations_.current_pid_namespace_id) {
            return unsupported();
        }
        if (incarnation.identity_kind == identity_unknown) return unsupported();
        if (incarnation.identity_kind != identity_linux_proc_start_ticks) {
            return unsupported();
        }
    } else if (observations_.platform == RecoveryPlatform::windows) {
        if (incarnation.pid_namespace_id != 0) return inconsistent();
        if (incarnation.identity_kind == identity_unknown) return unsupported();
        if (incarnation.identity_kind != identity_windows_creation_file_time) {
            return unsupported();
        }
    } else {
        return unsupported();
    }

    const auto observation = observations_.observe(
        observations_.context,
        incarnation.process_id,
        incarnation.identity_kind);
    if (observation.kind == ProcessObservationKind::missing) {
        return {ParticipantClassificationKind::stale, incarnation};
    }
    if (observation.kind != ProcessObservationKind::available ||
        observation.process_start_value <= 0) {
        return unsupported();
    }
    if (observation.process_start_value != incarnation.process_start_value) {
        return {ParticipantClassificationKind::stale, incarnation};
    }
    return {
        incarnation.process_id == observations_.current_process_id
            ? ParticipantClassificationKind::current_process
            : ParticipantClassificationKind::live,
        incarnation};
}

ParticipantClassification RecoveryCoordinator::classify_participant(
    std::uint32_t participant_token) const noexcept {
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::RecoveryBeforeOwnerClassification);
    ParticipantIncarnation incarnation{};
    if (!valid()) {
        return {ParticipantClassificationKind::inconsistent, incarnation};
    }
    switch (read_participant_snapshot(participant_token, incarnation)) {
    case ParticipantSnapshotStatus::changing:
        return {ParticipantClassificationKind::changing, incarnation};
    case ParticipantSnapshotStatus::inconsistent:
        return {ParticipantClassificationKind::inconsistent, incarnation};
    case ParticipantSnapshotStatus::stale:
        return {ParticipantClassificationKind::stale, incarnation};
    case ParticipantSnapshotStatus::stable:
        return classify_snapshot_owner(incarnation);
    default:
        return {ParticipantClassificationKind::inconsistent, incarnation};
    }
}

bool RecoveryCoordinator::valid_slot_binding(std::uint64_t binding) const noexcept {
    IndexBinding decoded{};
    return IndexBinding::try_decode(binding, decoded) &&
        decoded.slot_index >= 0 && decoded.slot_index < layout_.slot_count &&
        decoded.generation >= 1 &&
        decoded.generation <= SlotTable::terminal_generation;
}

LeaseRecoveryDisposition RecoveryCoordinator::lease_disposition(
    LeaseState lease_state,
    const ParticipantClassification& classification,
    bool participant_handoff_published,
    bool recover_current_process_leases) noexcept {
    if (lease_state != LeaseState::claiming && lease_state != LeaseState::active) {
        return LeaseRecoveryDisposition::failed;
    }
    if (participant_handoff_published &&
        (classification.incarnation.state == participant_closing ||
         classification.incarnation.state == participant_recovering)) {
        return LeaseRecoveryDisposition::recover;
    }
    switch (classification.kind) {
    case ParticipantClassificationKind::changing:
        return LeaseRecoveryDisposition::retry;
    case ParticipantClassificationKind::inconsistent:
        return LeaseRecoveryDisposition::failed;
    case ParticipantClassificationKind::unsupported:
        return LeaseRecoveryDisposition::unsupported;
    case ParticipantClassificationKind::live:
        return LeaseRecoveryDisposition::active;
    case ParticipantClassificationKind::current_process:
        if (lease_state == LeaseState::claiming) {
            return LeaseRecoveryDisposition::active;
        }
        return recover_current_process_leases
            ? LeaseRecoveryDisposition::recover
            : LeaseRecoveryDisposition::active;
    case ParticipantClassificationKind::stale:
        return LeaseRecoveryDisposition::recover;
    default:
        return LeaseRecoveryDisposition::failed;
    }
}

sms_status RecoveryCoordinator::recycle_lease(
    LeaseRecordV2& record,
    std::int64_t incarnation,
    std::uint64_t expected_transition,
    bool& recycled) noexcept {
    recycled = false;
    std::uint64_t terminal{};
    if (!LeaseRegistry::try_advance_or_retire(incarnation, terminal)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t attempt = 0;
         attempt < recycle_confirmation_attempts;
         ++attempt) {
        auto expected = expected_transition;
        if (MappedAtomic64::compare_exchange(record.Control, expected, terminal)) {
            recycled = true;
            return SMS_STATUS_SUCCESS;
        }
        bool occupied{};
        if (!LeaseRegistry::try_classify_structural_control(
                expected, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        LeaseControl decoded{};
        (void)LeaseControl::try_decode(expected, decoded);
        if (expected == terminal || decoded.generation > incarnation) {
            return SMS_STATUS_SUCCESS;
        }
        const auto confirmed = MappedAtomic64::load_acquire(record.Control);
        if (confirmed != expected) continue;
        return SMS_STATUS_CORRUPT_STORE;
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status RecoveryCoordinator::try_recover_leases(
    bool recover_current_process_leases,
    const OperationBudget& budget,
    RecoveryScanReport& report) noexcept {
    report = {};
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    for (std::int32_t index = 0;
         index < layout_.lease_record_count;
         ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return completion_status(bound, report);
        ++report.scanned;
        auto* record = leases_->record(index);
        if (record == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto initial = MappedAtomic64::load_acquire(record->Control);
        bool occupied{};
        if (!LeaseRegistry::try_classify_structural_control(
                initial, layout_.participant_record_count, occupied)) {
            ++report.failed;
            return SMS_STATUS_CORRUPT_STORE;
        }
        LeaseControl first{};
        (void)LeaseControl::try_decode(initial, first);
        const auto target_incarnation = first.generation;
        const auto target_participant = first.participant_token;
        const auto initial_state = static_cast<LeaseState>(first.state);
        if (initial_state == LeaseState::free ||
            initial_state == LeaseState::retired) {
            continue;
        }

        bool completed = false;
        for (std::int32_t attempt = 0; !completed; ++attempt) {
            if (attempt >= classification_retry_budget) {
                sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
                if (!budget.try_continue_after_contention(attempt, terminal)) {
                    return completion_status(terminal, report);
                }
            }
            const auto attempt_bound = budget.check_periodic(attempt);
            if (attempt_bound != SMS_STATUS_SUCCESS) {
                return completion_status(attempt_bound, report);
            }

            const auto observed = MappedAtomic64::load_acquire(record->Control);
            if (!LeaseRegistry::try_classify_structural_control(
                    observed, layout_.participant_record_count, occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            LeaseControl decoded{};
            (void)LeaseControl::try_decode(observed, decoded);
            if (decoded.generation != target_incarnation ||
                decoded.state == static_cast<std::int32_t>(LeaseState::free) ||
                decoded.state == static_cast<std::int32_t>(LeaseState::retired)) {
                completed = true;
                break;
            }
            if (decoded.state == static_cast<std::int32_t>(LeaseState::releasing) ||
                decoded.state == static_cast<std::int32_t>(LeaseState::recovering)) {
                bool recycled{};
                const auto status = recycle_lease(
                    *record, target_incarnation, observed, recycled);
                if (status != SMS_STATUS_SUCCESS) return status;
                completed = true;
                break;
            }
            const auto state = static_cast<LeaseState>(decoded.state);
            if ((state != LeaseState::claiming && state != LeaseState::active) ||
                decoded.participant_token != target_participant ||
                (initial_state == LeaseState::active && state != LeaseState::active)) {
                ++report.failed;
                completed = true;
                break;
            }

            const auto slot_binding = MappedAtomic64::load_acquire(
                record->SlotBinding);
            const auto confirmed = MappedAtomic64::load_acquire(record->Control);
            if (!LeaseRegistry::try_classify_structural_control(
                    confirmed, layout_.participant_record_count, occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (confirmed != observed) continue;
            if ((state == LeaseState::active && !valid_slot_binding(slot_binding)) ||
                (state == LeaseState::claiming && slot_binding != 0 &&
                 !valid_slot_binding(slot_binding))) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            const auto before_classification = budget.check();
            if (before_classification != SMS_STATUS_SUCCESS) {
                return completion_status(before_classification, report);
            }
            const auto classification = classify_participant(target_participant);
            const auto revalidated = MappedAtomic64::load_acquire(record->Control);
            if (!LeaseRegistry::try_classify_structural_control(
                    revalidated, layout_.participant_record_count, occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (revalidated != observed) continue;

            const auto handoff =
                classification.incarnation.token == target_participant &&
                classification.kind != ParticipantClassificationKind::changing &&
                classification.kind != ParticipantClassificationKind::inconsistent &&
                (classification.incarnation.state == participant_closing ||
                 classification.incarnation.state == participant_recovering);
            switch (lease_disposition(
                state,
                classification,
                handoff,
                recover_current_process_leases)) {
            case LeaseRecoveryDisposition::retry:
                continue;
            case LeaseRecoveryDisposition::failed:
                ++report.failed;
                if (classification.kind ==
                    ParticipantClassificationKind::inconsistent) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                completed = true;
                break;
            case LeaseRecoveryDisposition::unsupported:
                ++report.unsupported;
                completed = true;
                break;
            case LeaseRecoveryDisposition::active:
                ++report.active;
                completed = true;
                break;
            case LeaseRecoveryDisposition::recover: {
                const auto cas_bound = budget.check();
                if (cas_bound != SMS_STATUS_SUCCESS) {
                    return completion_status(cas_bound, report);
                }
                std::uint64_t recovering{};
                if (!LeaseControl::try_encode(
                        static_cast<std::int32_t>(LeaseState::recovering),
                        target_incarnation,
                        0,
                        recovering)) {
                    ++report.failed;
                    return SMS_STATUS_CORRUPT_STORE;
                }
                auto expected = observed;
                if (!MappedAtomic64::compare_exchange(
                        record->Control, expected, recovering)) {
                    if (!LeaseRegistry::try_classify_structural_control(
                            expected,
                            layout_.participant_record_count,
                            occupied)) {
                        ++report.failed;
                        return SMS_STATUS_CORRUPT_STORE;
                    }
                    continue;
                }
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::RecoveryAfterExactRecoveryCas);
                bool recycled{};
                const auto recycled_status = recycle_lease(
                    *record, target_incarnation, recovering, recycled);
                if (recycled_status != SMS_STATUS_SUCCESS) {
                    return recycled_status;
                }
                ++report.recovered;

                if (slot_binding != 0) {
                    IndexBinding binding{};
                    (void)IndexBinding::try_decode(slot_binding, binding);
                    auto* slot = slots_->slot(binding.slot_index);
                    if (slot == nullptr) return SMS_STATUS_CORRUPT_STORE;
                    const auto slot_control =
                        MappedAtomic64::load_acquire(slot->Control);
                    bool slot_occupied{};
                    if (!SlotTable::try_classify_structural_control(
                            slot_control,
                            layout_.participant_record_count,
                            slot_occupied)) {
                        ++report.failed;
                        return SMS_STATUS_CORRUPT_STORE;
                    }
                    SlotControl decoded_slot{};
                    (void)SlotControl::try_decode(slot_control, decoded_slot);
                    if (decoded_slot.generation < binding.generation) {
                        ++report.failed;
                        return SMS_STATUS_CORRUPT_STORE;
                    }
                    if (decoded_slot.generation == binding.generation &&
                        decoded_slot.state == static_cast<std::int32_t>(
                            SlotState::remove_requested)) {
                        const auto reclaim = reclaimer_->try_reclaim(
                            slot_binding,
                            OperationBudget::structural_attempt());
                        if (reclaim == SMS_STATUS_CORRUPT_STORE) {
                            ++report.failed;
                            return reclaim;
                        }
                    }
                }
                completed = true;
                break;
            }
            default:
                ++report.failed;
                completed = true;
                break;
            }
        }
    }
    return SMS_STATUS_SUCCESS;
}

bool RecoveryCoordinator::directory_target_in_bounds(
    std::int32_t kind,
    std::int64_t index) const noexcept {
    return (kind == directory_target_primary && index >= 0 &&
            index < layout_.primary_lane_count) ||
        (kind == directory_target_overflow && index >= 0 &&
         index < layout_.slot_count);
}

RecoveryCoordinator::LocationReferenceStatus
RecoveryCoordinator::classify_location_reference(
    std::uint64_t raw,
    std::int64_t generation) const noexcept {
    if (raw == 0) return LocationReferenceStatus::none;
    DirectoryLocation location{};
    if (!DirectoryLocation::try_decode(raw, location)) {
        return LocationReferenceStatus::invalid;
    }
    if (location.generation < generation) {
        return LocationReferenceStatus::older;
    }
    if (location.generation > generation ||
        !directory_target_in_bounds(location.kind, location.index)) {
        return LocationReferenceStatus::invalid;
    }
    return LocationReferenceStatus::current;
}

bool RecoveryCoordinator::try_decode_recovery_operation(
    std::uint64_t raw,
    std::int64_t generation,
    SlotState slot_state,
    DirectoryOperation& operation) const noexcept {
    if (!DirectoryOperation::try_decode(raw, operation) ||
        operation.value != raw || operation.generation != generation ||
        (operation.intent != directory_intent_insert &&
         operation.intent != directory_intent_unlink) ||
        (is_owned_slot_state(slot_state) &&
         operation.intent != directory_intent_insert)) {
        return false;
    }
    switch (operation.phase) {
    case directory_phase_prepared:
        return operation.target_kind == 0 && operation.target_index == 0;
    case directory_phase_rejected:
        return operation.intent == directory_intent_insert &&
            operation.target_kind == 0 && operation.target_index == 0;
    case directory_phase_target_selected:
    case directory_phase_binding_changed:
        return directory_target_in_bounds(
            operation.target_kind, operation.target_index);
    case directory_phase_complete:
        if (operation.intent == directory_intent_unlink &&
            operation.target_kind == 0) {
            return operation.target_index == 0;
        }
        return directory_target_in_bounds(
            operation.target_kind, operation.target_index);
    default:
        return false;
    }
}

bool RecoveryCoordinator::recovery_operation_location_valid(
    const DirectoryOperation& operation,
    std::uint64_t location_raw,
    SlotState slot_state) const noexcept {
    if (location_raw == 0) {
        if (operation.intent == directory_intent_insert) {
            return operation.phase == directory_phase_prepared ||
                operation.phase == directory_phase_target_selected ||
                operation.phase == directory_phase_rejected ||
                ((slot_state == SlotState::aborting ||
                  slot_state == SlotState::reclaiming) &&
                 (operation.phase == directory_phase_binding_changed ||
                  operation.phase == directory_phase_complete));
        }
        return operation.intent == directory_intent_unlink;
    }

    const auto status = classify_location_reference(
        location_raw, operation.generation);
    if (status == LocationReferenceStatus::older) {
        return operation.intent == directory_intent_insert &&
            (operation.phase == directory_phase_prepared ||
             operation.phase == directory_phase_target_selected);
    }
    if (status != LocationReferenceStatus::current) return false;
    if (operation.phase == directory_phase_prepared) {
        return operation.intent == directory_intent_unlink;
    }
    if (operation.phase == directory_phase_rejected ||
        (operation.target_kind != directory_target_primary &&
         operation.target_kind != directory_target_overflow)) {
        return false;
    }
    std::uint64_t expected{};
    return DirectoryLocation::try_encode(
            operation.target_kind,
            operation.target_index,
            operation.generation,
            expected) &&
        expected == location_raw;
}

sms_status RecoveryCoordinator::validate_reservation_metadata(
    std::int32_t slot_index,
    std::uint64_t expected_control,
    ValueSlotMetadataV2& slot,
    const OperationBudget& budget,
    ReservationMetadataResult& result) noexcept {
    result = {};
    SlotControl decoded_control{};
    if (!SlotControl::try_decode(expected_control, decoded_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    std::uint64_t exact_binding{};
    if (!IndexBinding::try_encode(
            slot_index, decoded_control.generation, exact_binding)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    const auto slot_state = static_cast<SlotState>(decoded_control.state);

    for (std::int32_t attempt = 0; ; ++attempt) {
        if (attempt >= classification_retry_budget) {
            sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
        }
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto operation_raw = MappedAtomic64::load_acquire(
            slot.DirectoryOperation);
        const auto location_raw = MappedAtomic64::load_acquire(
            slot.DirectoryLocation);
        const auto directory_binding = MappedAtomic64::load_acquire(
            slot.DirectoryBinding);

        if (operation_raw == 0) {
            bool has_reference{};
            const auto reference = directory_->contains_exact_reference(
                exact_binding, budget, has_reference);
            if (reference != SMS_STATUS_SUCCESS) return reference;
            const auto control2 = MappedAtomic64::load_acquire(slot.Control);
            bool occupied{};
            if (!SlotTable::try_classify_structural_control(
                    control2, layout_.participant_record_count, occupied)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            const auto operation2 = MappedAtomic64::load_acquire(
                slot.DirectoryOperation);
            const auto location2 = MappedAtomic64::load_acquire(
                slot.DirectoryLocation);
            const auto binding2 = MappedAtomic64::load_acquire(
                slot.DirectoryBinding);
            if (control2 != expected_control) return SMS_STATUS_SUCCESS;
            if (operation2 != operation_raw || location2 != location_raw ||
                binding2 != directory_binding) {
                continue;
            }
            const auto location_status = classify_location_reference(
                location_raw, decoded_control.generation);
            result.lifecycle_still_current = true;
            if (slot_state == SlotState::reserved || has_reference ||
                location_status == LocationReferenceStatus::current ||
                location_status == LocationReferenceStatus::invalid) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            result.unreferenced_pre_metadata = true;
            return SMS_STATUS_SUCCESS;
        }

        DirectoryOperation operation{};
        const auto operation_valid = try_decode_recovery_operation(
            operation_raw,
            decoded_control.generation,
            slot_state,
            operation);
        const auto location_valid = operation_valid &&
            recovery_operation_location_valid(
                operation, location_raw, slot_state);
        const auto publication_intent = metadata_load(slot.PublicationIntent);
        const auto control2 = MappedAtomic64::load_acquire(slot.Control);
        bool occupied{};
        if (!SlotTable::try_classify_structural_control(
                control2, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        const auto operation2 = MappedAtomic64::load_acquire(
            slot.DirectoryOperation);
        const auto location2 = MappedAtomic64::load_acquire(
            slot.DirectoryLocation);
        const auto binding2 = MappedAtomic64::load_acquire(
            slot.DirectoryBinding);
        const auto publication_intent2 = metadata_load(slot.PublicationIntent);
        if (control2 != expected_control) return SMS_STATUS_SUCCESS;
        if (operation2 != operation_raw || location2 != location_raw ||
            binding2 != directory_binding ||
            publication_intent2 != publication_intent) {
            continue;
        }
        result.lifecycle_still_current = true;
        if (!operation_valid || !location_valid ||
            directory_binding != exact_binding ||
            (publication_intent != static_cast<std::int32_t>(
                    SlotPublicationIntent::explicit_reservation) &&
             publication_intent != static_cast<std::int32_t>(
                    SlotPublicationIntent::atomic_publication))) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        return SMS_STATUS_SUCCESS;
    }
}

bool RecoveryCoordinator::same_owned_slot_lifecycle(
    std::uint64_t current,
    std::uint64_t classified) const noexcept {
    SlotControl current_decoded{};
    SlotControl classified_decoded{};
    return SlotControl::try_decode(current, current_decoded) &&
        SlotControl::try_decode(classified, classified_decoded) &&
        is_owned_slot_state(static_cast<SlotState>(current_decoded.state)) &&
        is_owned_slot_state(static_cast<SlotState>(classified_decoded.state)) &&
        current_decoded.generation == classified_decoded.generation &&
        current_decoded.participant_token != 0 &&
        current_decoded.participant_token ==
            classified_decoded.participant_token;
}

bool RecoveryCoordinator::can_recover_reservation(
    SlotState slot_state,
    const ParticipantClassification& classification,
    bool recover_current_process_reservations) noexcept {
    if (slot_state != SlotState::initializing &&
        slot_state != SlotState::reserved) {
        return false;
    }
    if (classification.kind == ParticipantClassificationKind::stale) {
        return true;
    }
    const auto handoff =
        classification.incarnation.token != 0 &&
        classification.kind != ParticipantClassificationKind::changing &&
        classification.kind != ParticipantClassificationKind::inconsistent &&
        (classification.incarnation.state == participant_closing ||
         classification.incarnation.state == participant_recovering);
    if (slot_state == SlotState::initializing) return handoff;
    return handoff ||
        (recover_current_process_reservations &&
         classification.kind ==
            ParticipantClassificationKind::current_process);
}

sms_status RecoveryCoordinator::help_reservation(
    std::uint64_t exact_binding,
    bool unreferenced_pre_metadata,
    const OperationBudget& budget) noexcept {
    return unreferenced_pre_metadata
        ? slots_->complete_reclaim(exact_binding, budget)
        : reclaimer_->try_reclaim(exact_binding, budget);
}

sms_status RecoveryCoordinator::try_recover_reservations(
    bool recover_current_process_reservations,
    const OperationBudget& budget,
    RecoveryScanReport& report) noexcept {
    report = {};
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    for (std::int32_t slot_index = 0;
         slot_index < layout_.slot_count;
         ++slot_index) {
        const auto bound = budget.check_periodic(slot_index);
        if (bound != SMS_STATUS_SUCCESS) return completion_status(bound, report);
        auto* slot = slots_->slot(slot_index);
        if (slot == nullptr) return SMS_STATUS_CORRUPT_STORE;
        auto observed = MappedAtomic64::load_acquire(slot->Control);
        bool occupied{};
        if (!SlotTable::try_classify_structural_control(
                observed, layout_.participant_record_count, occupied)) {
            ++report.failed;
            return SMS_STATUS_CORRUPT_STORE;
        }
        SlotControl decoded{};
        (void)SlotControl::try_decode(observed, decoded);
        auto state = static_cast<SlotState>(decoded.state);

        if (state == SlotState::aborting || state == SlotState::reclaiming) {
            ReservationMetadataResult metadata{};
            const auto validation = validate_reservation_metadata(
                slot_index, observed, *slot, budget, metadata);
            if (validation != SMS_STATUS_SUCCESS) {
                if (validation == SMS_STATUS_CORRUPT_STORE) ++report.failed;
                return completion_status(validation, report);
            }
            if (!metadata.lifecycle_still_current) continue;
            std::uint64_t binding{};
            if (!IndexBinding::try_encode(
                    slot_index, decoded.generation, binding)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            const auto helped = help_reservation(
                binding,
                metadata.unreferenced_pre_metadata,
                budget);
            if (helped == SMS_STATUS_STORE_BUSY ||
                helped == SMS_STATUS_OPERATION_CANCELED) {
                return completion_status(helped, report);
            }
            if (helped != SMS_STATUS_SUCCESS &&
                helped != SMS_STATUS_NOT_FOUND) {
                ++report.failed;
                if (helped == SMS_STATUS_CORRUPT_STORE) return helped;
            }
            continue;
        }
        if (!is_owned_slot_state(state)) continue;

        ++report.scanned;
        const auto participant_token = decoded.participant_token;
        ReservationMetadataResult metadata{};
        auto validation = validate_reservation_metadata(
            slot_index, observed, *slot, budget, metadata);
        if (validation != SMS_STATUS_SUCCESS) {
            if (validation == SMS_STATUS_CORRUPT_STORE) ++report.failed;
            return completion_status(validation, report);
        }
        if (!metadata.lifecycle_still_current) continue;

        ParticipantClassification classification{};
        bool classified = false;
        for (std::int32_t attempt = 0; ; ++attempt) {
            classification = classify_participant(participant_token);
            if (classification.kind ==
                ParticipantClassificationKind::inconsistent) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (classification.kind != ParticipantClassificationKind::changing) {
                classified = true;
                break;
            }
            const auto current = MappedAtomic64::load_acquire(slot->Control);
            if (!SlotTable::try_classify_structural_control(
                    current, layout_.participant_record_count, occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (!same_owned_slot_lifecycle(current, observed)) break;
            const auto retry_bound = budget.check_periodic(attempt);
            if (retry_bound != SMS_STATUS_SUCCESS) {
                return completion_status(retry_bound, report);
            }
            if (attempt + 1 >= classification_retry_budget) {
                sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
                if (!budget.try_continue_after_contention(attempt, terminal)) {
                    return completion_status(terminal, report);
                }
            }
        }
        if (!classified) continue;

        if (!can_recover_reservation(
                state,
                classification,
                recover_current_process_reservations)) {
            const auto current = MappedAtomic64::load_acquire(slot->Control);
            if (!SlotTable::try_classify_structural_control(
                    current, layout_.participant_record_count, occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (same_owned_slot_lifecycle(current, observed)) {
                switch (classification.kind) {
                case ParticipantClassificationKind::current_process:
                case ParticipantClassificationKind::live:
                    ++report.active;
                    break;
                case ParticipantClassificationKind::unsupported:
                    ++report.unsupported;
                    break;
                default:
                    ++report.failed;
                    break;
                }
            }
            continue;
        }

        auto expected_control = observed;
        auto unreferenced_pre_metadata = metadata.unreferenced_pre_metadata;
        bool completed = false;
        for (std::int32_t attempt = 0; !completed; ++attempt) {
            if (attempt >= classification_retry_budget) {
                sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
                if (!budget.try_continue_after_contention(attempt, terminal)) {
                    return completion_status(terminal, report);
                }
            }
            const auto cas_bound = budget.check();
            if (cas_bound != SMS_STATUS_SUCCESS) {
                return completion_status(cas_bound, report);
            }
            if (unreferenced_pre_metadata) {
                ReservationMetadataResult fresh{};
                validation = validate_reservation_metadata(
                    slot_index,
                    expected_control,
                    *slot,
                    budget,
                    fresh);
                if (validation != SMS_STATUS_SUCCESS) {
                    if (validation == SMS_STATUS_CORRUPT_STORE) ++report.failed;
                    return completion_status(validation, report);
                }
                if (!fresh.lifecycle_still_current) {
                    completed = true;
                    break;
                }
                unreferenced_pre_metadata = fresh.unreferenced_pre_metadata;
            }

            SlotControl expected_decoded{};
            if (!SlotControl::try_decode(expected_control, expected_decoded) ||
                !is_owned_slot_state(
                    static_cast<SlotState>(expected_decoded.state))) {
                completed = true;
                break;
            }
            std::uint64_t aborting{};
            if (!SlotControl::try_encode(
                    static_cast<std::int32_t>(SlotState::aborting),
                    expected_decoded.generation,
                    0,
                    aborting)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            auto cas_expected = expected_control;
            if (MappedAtomic64::compare_exchange(
                    slot->Control, cas_expected, aborting)) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::RecoveryAfterExactRecoveryCas);
                std::uint64_t binding{};
                if (!IndexBinding::try_encode(
                        slot_index, expected_decoded.generation, binding)) {
                    ++report.failed;
                    return SMS_STATUS_CORRUPT_STORE;
                }
                const auto helped = help_reservation(
                    binding,
                    unreferenced_pre_metadata,
                    OperationBudget::structural_attempt());
                if (helped == SMS_STATUS_CORRUPT_STORE) {
                    ++report.failed;
                    return helped;
                }
                ++report.recovered;
                completed = true;
                break;
            }
            if (!SlotTable::try_classify_structural_control(
                    cas_expected,
                    layout_.participant_record_count,
                    occupied)) {
                ++report.failed;
                return SMS_STATUS_CORRUPT_STORE;
            }
            SlotControl changed{};
            (void)SlotControl::try_decode(cas_expected, changed);
            if (changed.generation != expected_decoded.generation ||
                changed.state == static_cast<std::int32_t>(SlotState::aborting) ||
                changed.state == static_cast<std::int32_t>(SlotState::reclaiming) ||
                changed.state == static_cast<std::int32_t>(SlotState::free) ||
                changed.state == static_cast<std::int32_t>(SlotState::retired)) {
                completed = true;
                break;
            }
            if (!same_owned_slot_lifecycle(cas_expected, observed)) {
                completed = true;
                break;
            }
            expected_control = cas_expected;
            state = static_cast<SlotState>(changed.state);
            ReservationMetadataResult changed_metadata{};
            validation = validate_reservation_metadata(
                slot_index,
                expected_control,
                *slot,
                budget,
                changed_metadata);
            if (validation != SMS_STATUS_SUCCESS) {
                if (validation == SMS_STATUS_CORRUPT_STORE) ++report.failed;
                return completion_status(validation, report);
            }
            if (!changed_metadata.lifecycle_still_current ||
                !can_recover_reservation(
                    state,
                    classification,
                    recover_current_process_reservations)) {
                completed = true;
                break;
            }
            unreferenced_pre_metadata =
                changed_metadata.unreferenced_pre_metadata;
        }
    }
    return SMS_STATUS_SUCCESS;
}

sms_status RecoveryCoordinator::has_participant_references(
    std::uint32_t participant_token,
    const OperationBudget& budget,
    bool& referenced) const noexcept {
    referenced = false;
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    ParticipantToken decoded_token{};
    if (!ParticipantToken::try_decode(
            participant_token,
            layout_.participant_record_count,
            decoded_token) ||
        decoded_token.generation > layout_.participant_generation_mask) {
        return SMS_STATUS_CORRUPT_STORE;
    }

    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* slot = slots_->slot(index);
        if (slot == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto raw = MappedAtomic64::load_acquire(slot->Control);
        bool occupied{};
        SlotControl control{};
        if (!SlotTable::try_classify_structural_control(
                raw, layout_.participant_record_count, occupied) ||
            !SlotControl::try_decode(raw, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if ((control.state == static_cast<std::int32_t>(
                 SlotState::initializing) ||
             control.state == static_cast<std::int32_t>(
                 SlotState::reserved)) &&
            control.participant_token == participant_token) {
            referenced = true;
            return SMS_STATUS_SUCCESS;
        }
    }

    for (std::int32_t index = 0;
         index < layout_.lease_record_count;
         ++index) {
        const auto bound = budget.check_periodic(layout_.slot_count + index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* lease = leases_->record(index);
        if (lease == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto raw = MappedAtomic64::load_acquire(lease->Control);
        bool occupied{};
        LeaseControl control{};
        if (!LeaseRegistry::try_classify_structural_control(
                raw, layout_.participant_record_count, occupied) ||
            !LeaseControl::try_decode(raw, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if ((control.state == static_cast<std::int32_t>(LeaseState::claiming) ||
             control.state == static_cast<std::int32_t>(LeaseState::active)) &&
            control.participant_token == participant_token) {
            referenced = true;
            return SMS_STATUS_SUCCESS;
        }
    }
    return budget.check();
}

sms_status RecoveryCoordinator::help_recovering_participants(
    const OperationBudget& budget,
    std::int32_t& retired_count) noexcept {
    retired_count = 0;
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;

    const auto bounded_result = [&retired_count](sms_status status) noexcept {
        return retired_count > 0 ? SMS_STATUS_SUCCESS : status;
    };
    for (std::int32_t index = 0;
         index < layout_.participant_record_count;
         ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bounded_result(bound);
        auto* record = participants_->record(index);
        if (record == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto observed = MappedAtomic64::load_acquire(record->Control);
        ParticipantControl control{observed};
        if (!control.structurally_valid(layout_.participant_generation_mask) ||
            !ParticipantControl::try_decode(observed, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (control.state == participant_free ||
            control.state == participant_retired) {
            continue;
        }

        std::uint64_t token_raw{};
        if (!ParticipantToken::try_encode(
                index,
                control.incarnation,
                layout_.participant_record_count,
                token_raw) ||
            token_raw > std::numeric_limits<std::uint32_t>::max()) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        const auto token = static_cast<std::uint32_t>(token_raw);

        if (control.state == participant_reclaiming) {
            bool referenced{};
            auto status = has_participant_references(token, budget, referenced);
            if (status != SMS_STATUS_SUCCESS) return bounded_result(status);
            if (referenced) return SMS_STATUS_CORRUPT_STORE;
            status = participants_->try_complete_reclaim(
                token, observed);
            if (status == SMS_STATUS_SUCCESS) ++retired_count;
            else if (status == SMS_STATUS_CORRUPT_STORE) return status;
            continue;
        }

        if (control.state == participant_registering ||
            control.state == participant_active) {
            const auto classification = classify_participant(token);
            if (classification.kind ==
                ParticipantClassificationKind::changing) {
                continue;
            }
            if (classification.kind ==
                ParticipantClassificationKind::inconsistent) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (classification.kind != ParticipantClassificationKind::stale ||
                classification.incarnation.control != observed ||
                classification.incarnation.token != token) {
                continue;
            }
        } else if (control.state != participant_closing &&
                   control.state != participant_recovering) {
            return SMS_STATUS_CORRUPT_STORE;
        }

        std::uint64_t recovering{};
        auto status = participants_->try_begin_recovery(
            token, observed, recovering);
        if (status == SMS_STATUS_CORRUPT_STORE) return status;
        if (status != SMS_STATUS_SUCCESS) continue;

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterRecoveryFenceBeforeReferenceScan);

        bool referenced{};
        status = has_participant_references(token, budget, referenced);
        if (status != SMS_STATUS_SUCCESS) return bounded_result(status);
        if (referenced) continue;

        std::uint64_t reclaiming{};
        status = participants_->try_begin_reclaim(
            token, recovering, reclaiming);
        if (status == SMS_STATUS_CORRUPT_STORE) return status;
        if (status != SMS_STATUS_SUCCESS) continue;

        // Reclaiming is ownerless and claim-closed. Repeating the complete
        // scan after its publication is the participant-token reuse fence.
        status = has_participant_references(token, budget, referenced);
        if (status != SMS_STATUS_SUCCESS) return bounded_result(status);
        if (referenced) return SMS_STATUS_CORRUPT_STORE;
        status = participants_->try_complete_reclaim(token, reclaiming);
        if (status == SMS_STATUS_SUCCESS) ++retired_count;
        else if (status == SMS_STATUS_CORRUPT_STORE) return status;
    }
    return SMS_STATUS_SUCCESS;
}

} // namespace sms::detail
