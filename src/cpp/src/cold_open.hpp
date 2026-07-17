#pragma once

#include "layout_v2.hpp"
#include "operation_budget.hpp"
#include "participant_registry.hpp"

#include <cstddef>
#include <cstdint>

namespace sms::detail {

enum class ColdOpenMode {
    create_new = 0,
    open_existing = 1,
    create_or_open = 2
};

enum class ColdOpenStatus {
    success,
    invalid_options,
    already_exists,
    not_found,
    incompatible_layout,
    insufficient_capacity,
    participant_table_full,
    store_busy,
    operation_canceled,
    corrupt_store,
    unsupported_platform
};

struct ColdOpenResult {
    ColdOpenStatus status{ColdOpenStatus::incompatible_layout};
    ParticipantRegistration registration{};
    bool initialized{};
};

class ColdOpenV2 {
public:
    ColdOpenV2(
        std::uint8_t* mapping_base,
        std::size_t actual_capacity) noexcept;

    [[nodiscard]] ColdOpenResult attach(
        bool physical_creator,
        ColdOpenMode mode,
        const LayoutV2& requested_layout,
        const ParticipantIdentity& identity,
        std::uint64_t new_store_id,
        std::uint64_t pid_namespace_id,
        const OperationBudget& budget,
        bool architecture_supported = MappedAtomic64::supported()) noexcept;

private:
    std::uint8_t* mapping_base_{};
    std::size_t actual_capacity_{};
};

} // namespace sms::detail
