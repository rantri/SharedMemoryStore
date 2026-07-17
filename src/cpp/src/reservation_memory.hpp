#pragma once

#include "layout_v2.hpp"
#include "slot_table.hpp"

#include <cstddef>
#include <cstdint>
#include <span>

namespace sms::detail {

// Borrowed writable projection over the payload section. This type owns no
// mapping, native handle, manager, or per-slot object: every request first
// revalidates the exact store/participant/slot generation through SlotTable.
// The returned span follows the public contract and ends at the next advance,
// commit, abort, recovery, local close, or store close.
class ReservationMemory {
public:
    ReservationMemory(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        SlotTable& slots) noexcept
        : mapping_base_(mapping_base),
          mapping_length_(mapping_length),
          layout_(layout),
          slots_(&slots) {}

    [[nodiscard]] bool valid() const noexcept {
        if (mapping_base_ == nullptr || slots_ == nullptr || !slots_->valid() ||
            layout_.payload_storage_offset < 0 ||
            layout_.payload_storage_length < 0 || layout_.payload_stride < 0) {
            return false;
        }
        const auto offset = static_cast<std::uint64_t>(layout_.payload_storage_offset);
        const auto length = static_cast<std::uint64_t>(layout_.payload_storage_length);
        return offset <= mapping_length_ && length <= mapping_length_ - offset;
    }

    [[nodiscard]] std::span<std::byte> get_span(
        const ReservationToken& reservation,
        std::int32_t size_hint) const noexcept {
        // try_get_writable_range checks local lifetime before touching mapped
        // state, so close can invalidate this borrowed projector before unmap.
        WritableReservationRange range{};
        if (slots_ == nullptr ||
            !slots_->try_get_writable_range(reservation, size_hint, range) ||
            !valid() || range.slot_index < 0 || range.offset < 0 ||
            range.length <= 0 || range.offset > layout_.payload_stride ||
            range.length > layout_.payload_stride - range.offset) {
            return {};
        }

        const auto absolute = layout_.payload_storage_offset +
            static_cast<std::int64_t>(range.slot_index) * layout_.payload_stride +
            range.offset;
        if (absolute < 0) return {};
        const auto offset = static_cast<std::uint64_t>(absolute);
        const auto length = static_cast<std::uint64_t>(range.length);
        if (offset > mapping_length_ || length > mapping_length_ - offset) return {};
        return {
            reinterpret_cast<std::byte*>(mapping_base_ + offset),
            static_cast<std::size_t>(length)};
    }

private:
    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    SlotTable* slots_{};
};

} // namespace sms::detail
