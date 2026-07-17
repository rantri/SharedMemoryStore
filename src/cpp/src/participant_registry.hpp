#pragma once

#include "control_words.hpp"
#include "layout_v2.hpp"
#include "mapped_atomic.hpp"
#include "operation_budget.hpp"

#include <cstdint>

namespace sms::detail {

inline constexpr std::int32_t participant_free = 0;
inline constexpr std::int32_t participant_registering = 1;
inline constexpr std::int32_t participant_active = 2;
inline constexpr std::int32_t participant_closing = 3;
inline constexpr std::int32_t participant_recovering = 4;
inline constexpr std::int32_t participant_reclaiming = 5;
inline constexpr std::int32_t participant_retired = 6;

inline constexpr std::int32_t identity_unknown = 0;
inline constexpr std::int32_t identity_windows_creation_file_time = 1;
inline constexpr std::int32_t identity_linux_proc_start_ticks = 2;

struct ParticipantIdentity {
    std::int32_t process_id{};
    std::int32_t identity_kind{};
    std::int64_t process_start_value{};
    std::uint64_t pid_namespace_id{};

    [[nodiscard]] bool valid() const noexcept {
        return process_id > 0 &&
            identity_kind >= identity_unknown &&
            identity_kind <= identity_linux_proc_start_ticks;
    }
};

struct ParticipantRegistration {
    std::int32_t record_index{-1};
    std::int32_t generation{};
    std::uint32_t token{};
    std::uint64_t active_control{};

    [[nodiscard]] bool valid(std::int32_t participant_count) const noexcept {
        ParticipantToken decoded{};
        return record_index >= 0 && generation > 0 && token != 0 &&
            ParticipantToken::try_decode(token, participant_count, decoded) &&
            decoded.record_index == record_index && decoded.generation == generation;
    }
};

enum class ParticipantRegistrationStatus {
    success,
    table_full,
    store_busy,
    operation_canceled,
    incompatible_layout,
    corrupt_store,
    unsupported_platform
};

class ParticipantRegistry {
public:
    ParticipantRegistry(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout) noexcept;

    [[nodiscard]] bool valid() const noexcept;
    [[nodiscard]] bool initialize(const OperationBudget& budget) noexcept;

    [[nodiscard]] ParticipantRegistrationStatus try_register(
        StoreHeaderV2& header,
        const ParticipantIdentity& identity,
        const OperationBudget& budget,
        ParticipantRegistration& registration) noexcept;

    // The caller must stop local entry and prove that no slot/lease record still
    // references this token before requesting final retirement.
    [[nodiscard]] bool close_and_retire(
        const ParticipantRegistration& registration) noexcept;

    // Orderly close publishes a claim-closed handoff before owned-resource
    // cleanup begins. The exact Active control prevents a stale handle from
    // closing a later incarnation.
    [[nodiscard]] sms_status try_begin_close(
        const ParticipantRegistration& registration,
        std::uint64_t& closing_control) noexcept;

    // Explicit recovery publishes an exact-incarnation Recovering handoff.
    // The coordinator must prove that no slot or lease still references the
    // token before calling try_begin_reclaim.
    [[nodiscard]] sms_status try_begin_recovery(
        std::uint32_t participant_token,
        std::uint64_t expected_control,
        std::uint64_t& recovering_control) noexcept;

    // Recovery and orderly close both enter Reclaiming only after a complete
    // exact-reference scan. The coordinator must scan again before completing
    // Reclaiming so reuse cannot overtake a late persistent reference.
    [[nodiscard]] sms_status try_begin_reclaim(
        std::uint32_t participant_token,
        std::uint64_t handoff_control,
        std::uint64_t& reclaiming_control) noexcept;

    [[nodiscard]] sms_status try_complete_reclaim(
        std::uint32_t participant_token,
        std::uint64_t reclaiming_control) noexcept;

    [[nodiscard]] bool is_active(std::uint32_t token) const noexcept;
    [[nodiscard]] ParticipantRecordV2* record(std::int32_t index) const noexcept;

private:
    [[nodiscard]] bool structurally_valid(std::uint64_t control) const noexcept;
    [[nodiscard]] bool help_reclaiming(
        ParticipantRecordV2& record,
        std::int32_t generation) noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
};

} // namespace sms::detail
