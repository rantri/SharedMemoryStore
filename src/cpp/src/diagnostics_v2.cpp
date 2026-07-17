#include "diagnostics_v2.hpp"

#include "control_words.hpp"
#include "lease_registry.hpp"
#include "mapped_atomic.hpp"
#include "participant_registry.hpp"
#include "slot_table.hpp"

#include <limits>

namespace sms::detail {
namespace {

[[nodiscard]] bool section_within(
    std::int64_t offset,
    std::int64_t length,
    std::size_t mapping_length) noexcept {
    if (offset < 0 || length < 0) return false;
    const auto start = static_cast<std::uint64_t>(offset);
    const auto size = static_cast<std::uint64_t>(length);
    return start <= mapping_length && size <= mapping_length - start;
}

[[nodiscard]] bool valid_binding(
    std::uint64_t raw,
    std::int32_t slot_count) noexcept {
    if (raw == 0) return true;
    IndexBinding binding{};
    return IndexBinding::try_decode(raw, binding) &&
        binding.slot_index >= 0 && binding.slot_index < slot_count;
}

} // namespace

DiagnosticsV2::DiagnosticsV2(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout) {}

bool DiagnosticsV2::valid() const noexcept {
    return mapping_base_ != nullptr && mapping_length_ >= sizeof(StoreHeaderV2) &&
        layout_.slot_count > 0 && layout_.lease_record_count > 0 &&
        layout_.participant_record_count > 0 &&
        section_within(
            layout_.participant_offset,
            layout_.participant_length,
            mapping_length_) &&
        section_within(
            layout_.primary_directory_offset,
            layout_.primary_directory_length,
            mapping_length_) &&
        section_within(
            layout_.overflow_directory_offset,
            layout_.overflow_directory_length,
            mapping_length_) &&
        section_within(
            layout_.lease_registry_offset,
            layout_.lease_registry_length,
            mapping_length_) &&
        section_within(
            layout_.slot_metadata_offset,
            layout_.slot_metadata_length,
            mapping_length_);
}

sms_status DiagnosticsV2::snapshot(
    const OperationBudget& budget,
    StructuralDiagnosticsV2& result) const noexcept {
    result = {};
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;

    result.total_bytes = layout_.total_bytes;
    result.slot_count = layout_.slot_count;
    result.lease_record_count = layout_.lease_record_count;
    result.participant_record_count = layout_.participant_record_count;
    result.store_control = MappedAtomic64::load_acquire(
        reinterpret_cast<StoreHeaderV2*>(mapping_base_)->Control);

    for (std::int32_t index = 0;
         index < layout_.participant_record_count;
         ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<ParticipantRecordV2*>(
            mapping_base_ + layout_.participant_offset +
            static_cast<std::int64_t>(index) * layout_.participant_stride);
        const auto raw = MappedAtomic64::load_acquire(current->Control);
        ParticipantControl control{raw};
        if (!control.structurally_valid(layout_.participant_generation_mask) ||
            !ParticipantControl::try_decode(raw, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        switch (control.state) {
        case participant_free: ++result.free_participant_count; break;
        case participant_registering:
            ++result.registering_participant_count;
            break;
        case participant_active: ++result.active_participant_count; break;
        case participant_closing: ++result.closing_participant_count; break;
        case participant_recovering:
            ++result.recovering_participant_count;
            break;
        case participant_reclaiming:
            ++result.reclaiming_participant_count;
            break;
        case participant_retired: ++result.retired_participant_count; break;
        default: return SMS_STATUS_CORRUPT_STORE;
        }
    }

    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(
            layout_.participant_record_count + index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<ValueSlotMetadataV2*>(
            mapping_base_ + layout_.slot_metadata_offset +
            static_cast<std::int64_t>(index) * layout_.slot_metadata_stride);
        const auto raw = MappedAtomic64::load_acquire(current->Control);
        SlotControl control{};
        bool occupied{};
        if (!SlotTable::try_classify_structural_control(
                raw, layout_.participant_record_count, occupied) ||
            !SlotControl::try_decode(raw, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        switch (static_cast<SlotState>(control.state)) {
        case SlotState::free: ++result.free_slot_count; break;
        case SlotState::initializing: ++result.initializing_slot_count; break;
        case SlotState::reserved: ++result.reserved_slot_count; break;
        case SlotState::published: ++result.published_slot_count; break;
        case SlotState::remove_requested:
            ++result.pending_removal_count;
            break;
        case SlotState::aborting:
        case SlotState::reclaiming: ++result.reclaiming_slot_count; break;
        case SlotState::retired: ++result.retired_slot_count; break;
        default: return SMS_STATUS_CORRUPT_STORE;
        }
    }

    for (std::int32_t index = 0;
         index < layout_.lease_record_count;
         ++index) {
        const auto bound = budget.check_periodic(
            layout_.participant_record_count + layout_.slot_count + index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<LeaseRecordV2*>(
            mapping_base_ + layout_.lease_registry_offset +
            static_cast<std::int64_t>(index) * layout_.lease_stride);
        const auto raw = MappedAtomic64::load_acquire(current->Control);
        LeaseControl control{};
        bool occupied{};
        if (!LeaseRegistry::try_classify_structural_control(
                raw, layout_.participant_record_count, occupied) ||
            !LeaseControl::try_decode(raw, control)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        switch (static_cast<LeaseState>(control.state)) {
        case LeaseState::free: ++result.free_lease_count; break;
        case LeaseState::claiming: ++result.claiming_lease_count; break;
        case LeaseState::active: ++result.active_lease_count; break;
        case LeaseState::releasing:
        case LeaseState::recovering: ++result.recovering_lease_count; break;
        case LeaseState::retired: ++result.retired_lease_count; break;
        default: return SMS_STATUS_CORRUPT_STORE;
        }
    }

    result.index_entry_count = layout_.primary_lane_count + layout_.slot_count;
    for (std::int32_t bucket = 0;
         bucket < layout_.primary_bucket_count;
         ++bucket) {
        const auto bound = budget.check_periodic(
            layout_.participant_record_count + layout_.slot_count +
            layout_.lease_record_count + bucket);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<PrimaryDirectoryBucketV2*>(
            mapping_base_ + layout_.primary_directory_offset +
            static_cast<std::int64_t>(bucket) * layout_.primary_bucket_stride);
        SpillSummary spill{};
        const auto spill_raw = MappedAtomic64::load_acquire(
            current->SpillSummary);
        if (!SpillSummary::try_decode(spill_raw, spill) ||
            (!spill.is_initial() &&
             (spill.slot_index < 0 || spill.slot_index >= layout_.slot_count))) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (spill.is_present) ++result.spilled_bucket_count;
        if (!valid_binding(
                MappedAtomic64::load_acquire(current->Mutation),
                layout_.slot_count)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        for (auto& lane : current->Lanes) {
            const auto raw = MappedAtomic64::load_acquire(lane);
            if (!valid_binding(raw, layout_.slot_count)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (raw != 0) ++result.primary_directory_occupancy;
        }
    }

    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(
            layout_.participant_record_count + layout_.slot_count +
            layout_.lease_record_count + layout_.primary_bucket_count + index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto& current = *reinterpret_cast<std::uint64_t*>(
            mapping_base_ + layout_.overflow_directory_offset +
            static_cast<std::int64_t>(index) * layout_.overflow_stride);
        const auto raw = MappedAtomic64::load_acquire(current);
        if (!valid_binding(raw, layout_.slot_count)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (raw != 0) ++result.overflow_directory_occupancy;
    }

    result.occupied_index_entry_count =
        result.primary_directory_occupancy +
        result.overflow_directory_occupancy;
    if (result.occupied_index_entry_count < 0 ||
        result.occupied_index_entry_count > result.index_entry_count) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    result.empty_index_entry_count =
        result.index_entry_count - result.occupied_index_entry_count;
    return budget.check();
}

} // namespace sms::detail
