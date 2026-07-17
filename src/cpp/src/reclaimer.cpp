#include "reclaimer.hpp"

#include "checkpoint.hpp"
#include "mapped_atomic.hpp"

namespace sms::detail {
namespace {

[[nodiscard]] bool encode_control(
    SlotState state,
    std::int64_t generation,
    std::uint64_t& control) noexcept {
    return SlotControl::try_encode(
        static_cast<std::int32_t>(state), generation, 0, control);
}

} // namespace

Reclaimer::Reclaimer(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    SlotTable& slots,
    KeyDirectory& directory,
    LeaseRegistry& leases) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout),
      slots_(&slots),
      directory_(&directory),
      leases_(&leases) {}

bool Reclaimer::valid() const noexcept {
    return mapping_base_ != nullptr && mapping_length_ > 0 &&
        slots_ != nullptr && slots_->valid() &&
        directory_ != nullptr && directory_->valid() &&
        leases_ != nullptr && leases_->valid() &&
        layout_.slot_count > 0;
}

bool Reclaimer::decode_binding(
    std::uint64_t exact_binding,
    IndexBinding& binding) const noexcept {
    return IndexBinding::try_decode(exact_binding, binding) &&
        binding.slot_index >= 0 && binding.slot_index < layout_.slot_count &&
        binding.generation >= 1 &&
        binding.generation <= SlotTable::terminal_generation;
}

sms_status Reclaimer::classify_generation(
    std::uint64_t control,
    std::int64_t generation,
    SlotControl& decoded) const noexcept {
    bool occupied{};
    if (!SlotTable::try_classify_structural_control(
            control, layout_.participant_record_count, occupied) ||
        !SlotControl::try_decode(control, decoded)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (decoded.generation > generation ||
        (decoded.generation == generation &&
         decoded.state == static_cast<std::int32_t>(SlotState::retired))) {
        return SMS_STATUS_NOT_FOUND;
    }
    return decoded.generation < generation
        ? SMS_STATUS_CORRUPT_STORE
        : SMS_STATUS_SUCCESS;
}

sms_status Reclaimer::try_logical_remove(
    std::uint64_t exact_binding,
    const OperationBudget& budget) noexcept {
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    const auto bound = budget.check();
    if (bound != SMS_STATUS_SUCCESS) return bound;
    IndexBinding binding{};
    if (!decode_binding(exact_binding, binding)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = slots_->slot(binding.slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;

    std::uint64_t published{};
    std::uint64_t remove_requested{};
    if (!encode_control(SlotState::published, binding.generation, published) ||
        !encode_control(
            SlotState::remove_requested,
            binding.generation,
            remove_requested)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = published;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::RemoveBeforeLogicalRemovalCas);
    if (MappedAtomic64::compare_exchange(
            current->Control, expected, remove_requested) ||
        expected == remove_requested) {
        return SMS_STATUS_SUCCESS;
    }

    SlotControl decoded{};
    const auto classified = classify_generation(
        expected, binding.generation, decoded);
    if (classified != SMS_STATUS_SUCCESS) return classified;
    return decoded.state == static_cast<std::int32_t>(SlotState::reserved) ||
           decoded.state == static_cast<std::int32_t>(SlotState::initializing)
        ? SMS_STATUS_NOT_FOUND
        : SMS_STATUS_REMOVE_PENDING;
}

sms_status Reclaimer::reclaim_remove_requested(
    std::uint64_t exact_binding,
    IndexBinding binding,
    ValueSlotMetadataV2& current,
    const OperationBudget& budget) noexcept {
    bool has_active_lease{};
    auto status = leases_->scan_has_active_lease(
        exact_binding, budget, has_active_lease);
    if (status != SMS_STATUS_SUCCESS) return status;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::RemoveAfterLeaseClassification);
    if (has_active_lease) return SMS_STATUS_REMOVE_PENDING;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ReclaimAfterLeaseScanBeforeOwnershipCas);

    std::uint64_t remove_requested{};
    std::uint64_t reclaiming{};
    if (!encode_control(
            SlotState::remove_requested,
            binding.generation,
            remove_requested) ||
        !encode_control(
            SlotState::reclaiming,
            binding.generation,
            reclaiming)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = remove_requested;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ReclaimBeforeOwnershipCas);
    if (!MappedAtomic64::compare_exchange(
            current.Control, expected, reclaiming) &&
        expected != reclaiming) {
        SlotControl decoded{};
        const auto classified = classify_generation(
            expected, binding.generation, decoded);
        if (classified == SMS_STATUS_NOT_FOUND) return SMS_STATUS_SUCCESS;
        return classified == SMS_STATUS_SUCCESS
            ? SMS_STATUS_STORE_BUSY
            : classified;
    }

    // Once Reclaiming is visible no new public lease can validate this exact
    // generation. The directory helper must clear every structural reference
    // before generation reuse is published.
    status = directory_->try_unlink(exact_binding, budget);
    if (status != SMS_STATUS_SUCCESS && status != SMS_STATUS_NOT_FOUND) {
        return status;
    }
    if (MappedAtomic64::load_acquire(current.DirectoryLocation) != 0 ||
        MappedAtomic64::load_acquire(current.DirectoryOperation) != 0) {
        return SMS_STATUS_STORE_BUSY;
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ReclaimAfterMetadataValidation);

    const auto final_bound = budget.check();
    if (final_bound != SMS_STATUS_SUCCESS) return final_bound;
    std::uint64_t terminal{};
    if (!SlotTable::try_advance_or_retire(
            binding.generation, terminal)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    expected = reclaiming;
    if (MappedAtomic64::compare_exchange(
            current.Control, expected, terminal) ||
        expected == terminal) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ReclaimAfterGenerationAdvance);
        return SMS_STATUS_SUCCESS;
    }
    SlotControl decoded{};
    const auto classified = classify_generation(
        expected, binding.generation, decoded);
    return classified == SMS_STATUS_NOT_FOUND
        ? SMS_STATUS_SUCCESS
        : classified == SMS_STATUS_SUCCESS
            ? SMS_STATUS_STORE_BUSY
            : classified;
}

sms_status Reclaimer::try_reclaim(
    std::uint64_t exact_binding,
    const OperationBudget& budget) noexcept {
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    IndexBinding binding{};
    if (!decode_binding(exact_binding, binding)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = slots_->slot(binding.slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    auto control = MappedAtomic64::load_acquire(current->Control);
    SlotControl decoded{};
    auto status = classify_generation(control, binding.generation, decoded);
    if (status == SMS_STATUS_NOT_FOUND) return SMS_STATUS_SUCCESS;
    if (status != SMS_STATUS_SUCCESS) return status;

    if (decoded.state == static_cast<std::int32_t>(SlotState::aborting)) {
        status = directory_->try_unlink(exact_binding, budget);
        if (status != SMS_STATUS_SUCCESS && status != SMS_STATUS_NOT_FOUND) {
            return status;
        }
        return slots_->complete_reclaim(exact_binding, budget);
    }
    if (decoded.state == static_cast<std::int32_t>(SlotState::remove_requested) ||
        decoded.state == static_cast<std::int32_t>(SlotState::reclaiming)) {
        return reclaim_remove_requested(
            exact_binding, binding, *current, budget);
    }
    return decoded.state == static_cast<std::int32_t>(SlotState::published)
        ? SMS_STATUS_REMOVE_PENDING
        : SMS_STATUS_STORE_BUSY;
}

sms_status Reclaimer::help_reclaimable_slots(
    const OperationBudget& budget,
    std::int32_t& reclaimed_count) noexcept {
    reclaimed_count = 0;
    if (!valid()) return SMS_STATUS_STORE_DISPOSED;
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = slots_->slot(index);
        if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto control = MappedAtomic64::load_acquire(current->Control);
        bool occupied{};
        if (!SlotTable::try_classify_structural_control(
                control, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        SlotControl decoded{};
        (void)SlotControl::try_decode(control, decoded);
        if (decoded.state != static_cast<std::int32_t>(SlotState::aborting) &&
            decoded.state != static_cast<std::int32_t>(SlotState::remove_requested) &&
            decoded.state != static_cast<std::int32_t>(SlotState::reclaiming)) {
            continue;
        }
        std::uint64_t binding{};
        if (!IndexBinding::try_encode(index, decoded.generation, binding)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        const auto helped = try_reclaim(binding, budget);
        if (helped == SMS_STATUS_SUCCESS) {
            ++reclaimed_count;
        } else if (helped != SMS_STATUS_REMOVE_PENDING &&
                   helped != SMS_STATUS_STORE_BUSY) {
            return helped;
        }
    }
    return SMS_STATUS_SUCCESS;
}

} // namespace sms::detail
