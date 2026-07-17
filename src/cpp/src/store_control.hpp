#pragma once

#include "control_words.hpp"
#include "layout_v2.hpp"
#include "mapped_atomic.hpp"
#include "operation_budget.hpp"

#include <cstddef>
#include <cstdint>

namespace sms::detail {

enum class StoreControlStatus {
    success,
    store_busy,
    incompatible_layout,
    corrupt_store,
    unsupported_platform
};

class StoreControlV2 {
public:
    StoreControlV2(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout) noexcept;

    [[nodiscard]] bool valid_mapping() const noexcept;
    [[nodiscard]] StoreHeaderV2* header() const noexcept;

    [[nodiscard]] bool initialize_creator(
        std::uint64_t store_id,
        std::uint64_t pid_namespace_id,
        std::uint64_t pid_namespace_mode,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] StoreControlStatus validate_existing() const noexcept;
    [[nodiscard]] sms_status ensure_ready() const noexcept;
    [[nodiscard]] bool latch_corrupt() noexcept;

private:
    [[nodiscard]] bool initialize_participant_records(
        const OperationBudget& budget) noexcept;
    [[nodiscard]] bool initialize_lease_records(
        const OperationBudget& budget) noexcept;
    [[nodiscard]] bool initialize_slot_records(
        const OperationBudget& budget) noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
};

} // namespace sms::detail
