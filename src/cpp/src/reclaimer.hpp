#pragma once

#include "key_directory.hpp"
#include "lease_registry.hpp"
#include "operation_budget.hpp"
#include "slot_table.hpp"

#include <cstddef>
#include <cstdint>

namespace sms::detail {

// Cooperative SMS2 logical-remove and physical-reclamation coordinator. The
// only public ordering point is Published -> RemoveRequested; every later
// transition is unowned and may be completed by any participant.
class Reclaimer {
public:
    Reclaimer(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        SlotTable& slots,
        KeyDirectory& directory,
        LeaseRegistry& leases) noexcept;

    [[nodiscard]] bool valid() const noexcept;

    [[nodiscard]] sms_status try_logical_remove(
        std::uint64_t exact_binding,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] sms_status try_reclaim(
        std::uint64_t exact_binding,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] sms_status help_reclaimable_slots(
        const OperationBudget& budget,
        std::int32_t& reclaimed_count) noexcept;

private:
    [[nodiscard]] bool decode_binding(
        std::uint64_t exact_binding,
        IndexBinding& binding) const noexcept;
    [[nodiscard]] sms_status reclaim_remove_requested(
        std::uint64_t exact_binding,
        IndexBinding binding,
        ValueSlotMetadataV2& slot,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status classify_generation(
        std::uint64_t control,
        std::int64_t generation,
        SlotControl& decoded) const noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    SlotTable* slots_{};
    KeyDirectory* directory_{};
    LeaseRegistry* leases_{};
};

} // namespace sms::detail
