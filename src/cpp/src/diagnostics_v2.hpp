#pragma once

#include "layout_v2.hpp"
#include "operation_budget.hpp"

#include <cstddef>
#include <cstdint>

namespace sms::detail {

// Cross-instant, observational SMS2 facts read directly from mapped state.
// No correctness decision may depend on a snapshot and no diagnostic scan
// mutates or helps the store.
struct StructuralDiagnosticsV2 {
    std::int64_t total_bytes{};
    std::uint64_t store_control{};

    std::int32_t slot_count{};
    std::int32_t free_slot_count{};
    std::int32_t initializing_slot_count{};
    std::int32_t reserved_slot_count{};
    std::int32_t published_slot_count{};
    std::int32_t pending_removal_count{};
    std::int32_t reclaiming_slot_count{};
    std::int32_t retired_slot_count{};

    std::int32_t lease_record_count{};
    std::int32_t free_lease_count{};
    std::int32_t claiming_lease_count{};
    std::int32_t active_lease_count{};
    std::int32_t recovering_lease_count{};
    std::int32_t retired_lease_count{};

    std::int32_t participant_record_count{};
    std::int32_t free_participant_count{};
    std::int32_t registering_participant_count{};
    std::int32_t active_participant_count{};
    std::int32_t closing_participant_count{};
    std::int32_t recovering_participant_count{};
    std::int32_t reclaiming_participant_count{};
    std::int32_t retired_participant_count{};

    std::int32_t index_entry_count{};
    std::int32_t occupied_index_entry_count{};
    std::int32_t empty_index_entry_count{};
    std::int32_t primary_directory_occupancy{};
    std::int32_t spilled_bucket_count{};
    std::int32_t overflow_directory_occupancy{};

    [[nodiscard]] std::int32_t active_reservation_count() const noexcept {
        return initializing_slot_count + reserved_slot_count;
    }

    [[nodiscard]] std::int32_t usable_index_capacity() const noexcept {
        return empty_index_entry_count;
    }
};

class DiagnosticsV2 {
public:
    DiagnosticsV2(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout) noexcept;

    [[nodiscard]] bool valid() const noexcept;

    [[nodiscard]] sms_status snapshot(
        const OperationBudget& budget,
        StructuralDiagnosticsV2& result) const noexcept;

private:
    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
};

} // namespace sms::detail
