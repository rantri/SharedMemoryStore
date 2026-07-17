#include "key_directory.hpp"

#include "checkpoint.hpp"
#include "mapped_atomic.hpp"

#include <algorithm>
#include <atomic>
#include <limits>

namespace sms::detail {
namespace {

constexpr std::int32_t slot_initializing = 1;
constexpr std::int32_t slot_reserved = 2;
constexpr std::int32_t slot_aborting = 5;
constexpr std::int32_t slot_reclaiming = 6;
constexpr std::int32_t slot_retired = 7;

[[nodiscard]] bool range_valid(
    std::int64_t offset,
    std::int64_t length,
    std::size_t mapping_length) noexcept {
    if (offset < 0 || length < 0) return false;
    const auto unsigned_offset = static_cast<std::uint64_t>(offset);
    const auto unsigned_length = static_cast<std::uint64_t>(length);
    return unsigned_offset <= mapping_length &&
        unsigned_length <= mapping_length - unsigned_offset;
}

[[nodiscard]] bool product_equals(
    std::int32_t count,
    std::int32_t stride,
    std::int64_t length) noexcept {
    return count >= 0 && stride >= 0 && length >= 0 &&
        static_cast<std::uint64_t>(count) * static_cast<std::uint64_t>(stride) ==
            static_cast<std::uint64_t>(length);
}

[[nodiscard]] bool mapping_shape_valid(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout) noexcept {
    if (mapping_base == nullptr || !MappedAtomic64::supported() ||
        !MappedAtomic64::is_aligned(mapping_base) ||
        layout.slot_count < 1 || layout.slot_count > sms2_maximum_slot_count ||
        layout.participant_record_count < 1 ||
        layout.participant_record_count > sms2_maximum_participant_count ||
        layout.primary_bucket_count < 2 ||
        (layout.primary_bucket_count & (layout.primary_bucket_count - 1)) != 0 ||
        layout.primary_lane_count !=
            layout.primary_bucket_count * sms2_primary_lanes_per_bucket ||
        layout.primary_bucket_stride != sms2_primary_bucket_stride ||
        layout.overflow_stride != sms2_overflow_stride ||
        layout.slot_metadata_stride != sms2_slot_metadata_stride ||
        layout.key_stride < layout.max_key_bytes || layout.required_bytes < 0 ||
        static_cast<std::uint64_t>(layout.required_bytes) > mapping_length ||
        !product_equals(
            layout.primary_bucket_count,
            layout.primary_bucket_stride,
            layout.primary_directory_length) ||
        !product_equals(
            layout.slot_count,
            layout.overflow_stride,
            layout.overflow_directory_length) ||
        !product_equals(
            layout.slot_count,
            layout.slot_metadata_stride,
            layout.slot_metadata_length) ||
        !product_equals(
            layout.slot_count,
            layout.key_stride,
            layout.key_storage_length) ||
        !range_valid(
            layout.primary_directory_offset,
            layout.primary_directory_length,
            mapping_length) ||
        !range_valid(
            layout.overflow_directory_offset,
            layout.overflow_directory_length,
            mapping_length) ||
        !range_valid(
            layout.slot_metadata_offset,
            layout.slot_metadata_length,
            mapping_length) ||
        !range_valid(
            layout.key_storage_offset,
            layout.key_storage_length,
            mapping_length)) {
        return false;
    }

    return MappedAtomic64::is_aligned(
               mapping_base + layout.primary_directory_offset) &&
        MappedAtomic64::is_aligned(
               mapping_base + layout.overflow_directory_offset) &&
        MappedAtomic64::is_aligned(
               mapping_base + layout.slot_metadata_offset);
}

[[nodiscard]] bool state_allows_directory_reference(std::int32_t state) noexcept {
    return state >= slot_initializing && state <= slot_reclaiming;
}

} // namespace

KeyDirectory::KeyDirectory(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    DirectoryHooks hooks) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout),
      hooks_(hooks),
      bucket_mask_(layout.primary_bucket_count - 1),
      valid_(mapping_shape_valid(mapping_base, mapping_length, layout)) {}

sms_status KeyDirectory::try_lookup(
    std::span<const std::byte> key,
    std::uint64_t key_hash,
    const OperationBudget& budget,
    DirectoryEntry& entry) noexcept {
    entry = {};
    if (!valid_ || key.empty() || key.size() >
            static_cast<std::size_t>(layout_.max_key_bytes)) {
        return SMS_STATUS_INVALID_KEY;
    }
    if (!budget.valid()) return SMS_STATUS_UNKNOWN_FAILURE;
    return find_exact(key, key_hash, 0, budget, entry);
}

sms_status KeyDirectory::confirm_exact_reference(
    const DirectoryLocation& location,
    std::uint64_t exact_binding,
    bool& remains_exact) noexcept {
    remains_exact = false;
    IndexBinding binding{};
    Cell cell{};
    if (!valid_ || !decode_binding(exact_binding, layout_.slot_count, binding) ||
        location.value == 0 || location.generation != binding.generation ||
        !try_get_cell(location.kind, location.index, cell)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    remains_exact = MappedAtomic64::load_acquire(*cell.word) == exact_binding;
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::try_insert(
    std::span<const std::byte> key,
    std::uint64_t key_hash,
    std::uint64_t candidate_binding,
    const OperationBudget& budget,
    DirectoryLocation& location) noexcept {
    location = {};
    if (!valid_ || key.empty() || key.size() >
            static_cast<std::size_t>(layout_.max_key_bytes) || !budget.valid()) {
        return SMS_STATUS_INVALID_RESERVATION;
    }

    IndexBinding decoded{};
    if (!decode_binding(candidate_binding, layout_.slot_count, decoded)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* const candidate_slot = slot(decoded.slot_index);
    if (candidate_slot == nullptr) return SMS_STATUS_CORRUPT_STORE;

    BindingValidation validation{};
    const auto validation_status = validate_binding(
        candidate_binding, &key_hash, key, budget, validation);
    if (validation_status != SMS_STATUS_SUCCESS) return validation_status;
    if (validation == BindingValidation::stale) {
        return SMS_STATUS_INVALID_RESERVATION;
    }
    if (validation != BindingValidation::exact) {
        return validation == BindingValidation::retry
            ? SMS_STATUS_STORE_BUSY
            : SMS_STATUS_CORRUPT_STORE;
    }

    const auto control = MappedAtomic64::load_acquire(candidate_slot->Control);
    SlotControl slot_control{};
    if (!SlotControl::try_decode(control, slot_control) ||
        slot_control.generation != decoded.generation ||
        (slot_control.state != slot_initializing &&
         slot_control.state != slot_reserved)) {
        return slot_control.generation > decoded.generation
            ? SMS_STATUS_INVALID_RESERVATION
            : SMS_STATUS_CORRUPT_STORE;
    }

    auto status = prepare_operation(
        *candidate_slot,
        candidate_binding,
        directory_intent_insert,
        decoded.generation,
        budget);
    if (status != SMS_STATUS_SUCCESS) return status;

    std::int32_t canonical{};
    std::int32_t alternate{};
    buckets_for_hash(key_hash, canonical, alternate);
    (void)alternate;
    status = claim_mutation(canonical, candidate_binding, budget);
    if (status != SMS_STATUS_SUCCESS) return status;

    for (std::int32_t attempt = 0;; ++attempt) {
        const auto operation_raw =
            MappedAtomic64::load_acquire(candidate_slot->DirectoryOperation);
        DirectoryOperation operation{};
        if (decode_operation_semantic(operation_raw, operation) &&
            operation.generation == decoded.generation &&
            operation.intent == directory_intent_insert) {
            if (operation.phase == directory_phase_rejected) {
                return SMS_STATUS_DUPLICATE_KEY;
            }
            if (operation.phase == directory_phase_complete) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DirectoryAfterInsertCompletionStateValidationBeforeLocationRead);
                const auto location_raw =
                    MappedAtomic64::load_acquire(candidate_slot->DirectoryLocation);
                if (decode_location_semantic(location_raw, location) &&
                    location.generation == decoded.generation) {
                    Cell cell{};
                    if (try_get_cell(location.kind, location.index, cell) &&
                        MappedAtomic64::load_acquire(*cell.word) ==
                            candidate_binding) {
                        return SMS_STATUS_SUCCESS;
                    }
                }
            }
        } else if (operation_raw == 0) {
            const auto current =
                MappedAtomic64::load_acquire(candidate_slot->Control);
            if (classify_control(current, decoded.generation) !=
                ControlBindingStatus::current) {
                return SMS_STATUS_INVALID_RESERVATION;
            }
            status = prepare_operation(
                *candidate_slot,
                candidate_binding,
                directory_intent_insert,
                decoded.generation,
                budget);
            if (status != SMS_STATUS_SUCCESS) return status;
            status = claim_mutation(canonical, candidate_binding, budget);
            if (status != SMS_STATUS_SUCCESS) return status;
        } else if (operation.generation > decoded.generation) {
            return SMS_STATUS_INVALID_RESERVATION;
        } else if (operation_raw != 0) {
            return SMS_STATUS_CORRUPT_STORE;
        }

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryBeforeInsertOuterLoopBudgetCheck);
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;

        status = help_mutation(canonical, budget, 8);
        if (status != SMS_STATUS_SUCCESS && status != SMS_STATUS_STORE_BUSY) {
            return status;
        }
        if (read_mutation(canonical) == 0 &&
            MappedAtomic64::load_acquire(candidate_slot->DirectoryOperation) != 0) {
            // Completion and rejection deliberately release the canonical
            // mutation before their terminal operation word is consumed.
            continue;
        }
        if (attempt + 1 >= default_retry_budget) {
            sms_status terminal{};
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
        }
    }
}

sms_status KeyDirectory::try_unlink(
    std::uint64_t exact_binding,
    const OperationBudget& budget) noexcept {
    if (!valid_ || !budget.valid()) return SMS_STATUS_UNKNOWN_FAILURE;
    IndexBinding decoded{};
    if (!decode_binding(exact_binding, layout_.slot_count, decoded)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* const value_slot = slot(decoded.slot_index);
    if (value_slot == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto control = MappedAtomic64::load_acquire(value_slot->Control);
    SlotControl slot_control{};
    if (!SlotControl::try_decode(control, slot_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (slot_control.generation > decoded.generation) return SMS_STATUS_NOT_FOUND;
    if (slot_control.generation < decoded.generation ||
        value_slot->DirectoryBinding != exact_binding) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (slot_control.state != slot_aborting &&
        slot_control.state != slot_reclaiming) {
        return SMS_STATUS_STORE_BUSY;
    }

    auto status = prepare_operation(
        *value_slot,
        exact_binding,
        directory_intent_unlink,
        decoded.generation,
        budget);
    if (status != SMS_STATUS_SUCCESS) return status;

    std::int32_t canonical{};
    std::int32_t alternate{};
    buckets_for_hash(value_slot->KeyHash, canonical, alternate);
    (void)alternate;
    status = claim_mutation(canonical, exact_binding, budget);
    if (status != SMS_STATUS_SUCCESS) return status;

    for (std::int32_t attempt = 0;; ++attempt) {
        const auto operation_raw =
            MappedAtomic64::load_acquire(value_slot->DirectoryOperation);
        if (operation_raw == 0) return SMS_STATUS_SUCCESS;
        DirectoryOperation operation{};
        if (!decode_operation_semantic(operation_raw, operation) ||
            operation.generation != decoded.generation ||
            operation.intent != directory_intent_unlink) {
            const auto current = MappedAtomic64::load_acquire(value_slot->Control);
            return classify_control(current, decoded.generation) ==
                    ControlBindingStatus::stale
                ? SMS_STATUS_SUCCESS
                : SMS_STATUS_CORRUPT_STORE;
        }

        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        status = help_mutation(canonical, budget, 8);
        if (status != SMS_STATUS_SUCCESS && status != SMS_STATUS_STORE_BUSY) {
            return status;
        }
        if (attempt + 1 >= default_retry_budget) {
            sms_status terminal{};
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
        }
    }
}

sms_status KeyDirectory::help_mutation(
    std::int32_t canonical_bucket,
    const OperationBudget& budget,
    std::int32_t max_steps) noexcept {
    if (!valid_ || canonical_bucket < 0 ||
        canonical_bucket >= layout_.primary_bucket_count || max_steps <= 0 ||
        !budget.valid()) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* const mutation = mutation_word(canonical_bucket);
    if (mutation == nullptr) return SMS_STATUS_CORRUPT_STORE;

    for (std::int32_t step = 0; step < max_steps; ++step) {
        const auto bound = budget.check_periodic(step);
        if (bound != SMS_STATUS_SUCCESS) return bound;

        const auto mutation_raw = MappedAtomic64::load_acquire(*mutation);
        if (mutation_raw == 0) return SMS_STATUS_SUCCESS;

        IndexBinding decoded{};
        if (!decode_binding(mutation_raw, layout_.slot_count, decoded)) {
            if (MappedAtomic64::load_acquire(*mutation) == mutation_raw) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        auto* const value_slot = slot(decoded.slot_index);
        if (value_slot == nullptr) return SMS_STATUS_CORRUPT_STORE;

        const auto control1 = MappedAtomic64::load_acquire(value_slot->Control);
        const auto control_status = classify_control(control1, decoded.generation);
        if (control_status == ControlBindingStatus::stale) {
            bool changed{};
            const auto clear_status = clear_exact(*mutation, mutation_raw, changed);
            if (clear_status != SMS_STATUS_SUCCESS) return clear_status;
            continue;
        }
        if (control_status == ControlBindingStatus::invalid) {
            if (MappedAtomic64::load_acquire(*mutation) == mutation_raw &&
                MappedAtomic64::load_acquire(value_slot->Control) == control1) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }

        const auto directory_binding = value_slot->DirectoryBinding;
        const auto key_hash = value_slot->KeyHash;
        const auto operation_raw =
            MappedAtomic64::load_acquire(value_slot->DirectoryOperation);
        const auto control2 = MappedAtomic64::load_acquire(value_slot->Control);
        if (control1 != control2) continue;
        if (directory_binding != mutation_raw) {
            if (MappedAtomic64::load_acquire(*mutation) == mutation_raw &&
                MappedAtomic64::load_acquire(value_slot->Control) == control2 &&
                value_slot->DirectoryBinding == directory_binding) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }

        DirectoryOperation operation{};
        if (!decode_operation_semantic(operation_raw, operation)) {
            if (MappedAtomic64::load_acquire(*mutation) == mutation_raw &&
                MappedAtomic64::load_acquire(value_slot->DirectoryOperation) ==
                    operation_raw &&
                MappedAtomic64::load_acquire(value_slot->Control) == control2) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        if (operation.generation > decoded.generation) {
            // An old canonical descriptor never owns a later-generation slot
            // operation. It may retire only its exact mutation word.
            bool changed{};
            const auto clear_status = clear_exact(*mutation, mutation_raw, changed);
            if (clear_status != SMS_STATUS_SUCCESS) return clear_status;
            continue;
        }
        if (operation.generation < decoded.generation) {
            auto expected = operation_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot->DirectoryOperation, expected, 0);
            continue;
        }

        std::int32_t actual_canonical{};
        std::int32_t alternate{};
        buckets_for_hash(key_hash, actual_canonical, alternate);
        (void)alternate;
        if (actual_canonical != canonical_bucket) {
            if (MappedAtomic64::load_acquire(*mutation) == mutation_raw &&
                MappedAtomic64::load_acquire(value_slot->Control) == control2) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterOperationValidation);
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterCurrentOperationRevalidationBeforeDispatch);
        sms_status status{};
        if (operation.intent == directory_intent_insert) {
            status = help_insert(
                canonical_bucket,
                mutation_raw,
                decoded,
                *value_slot,
                operation_raw,
                operation,
                budget);
        } else if (operation.intent == directory_intent_unlink) {
            status = help_unlink(
                canonical_bucket,
                mutation_raw,
                decoded,
                *value_slot,
                operation_raw,
                operation,
                budget);
        } else {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (status != SMS_STATUS_SUCCESS) return status;
    }

    return MappedAtomic64::load_acquire(*mutation) == 0
        ? SMS_STATUS_SUCCESS
        : SMS_STATUS_STORE_BUSY;
}

sms_status KeyDirectory::contains_exact_reference(
    std::uint64_t exact_binding,
    const OperationBudget& budget,
    bool& contains) noexcept {
    contains = false;
    IndexBinding decoded{};
    if (!valid_ || !decode_binding(exact_binding, layout_.slot_count, decoded)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    std::int32_t probe = 0;
    for (std::int32_t bucket = 0; bucket < layout_.primary_bucket_count; ++bucket) {
        const auto bound = budget.check_periodic(probe++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        std::uint64_t observed{};
        auto status = read_valid_reference(*mutation_word(bucket), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == exact_binding) {
            contains = true;
            return SMS_STATUS_SUCCESS;
        }
        const auto summary_raw = MappedAtomic64::load_acquire(*spill_word(bucket));
        SpillSummary summary{};
        if (!decode_summary_semantic(summary_raw, summary)) {
            if (MappedAtomic64::load_acquire(*spill_word(bucket)) == summary_raw) {
                return SMS_STATUS_CORRUPT_STORE;
            }
        } else if (!summary.is_initial() && summary.is_present &&
                   summary.binding() == exact_binding) {
            contains = true;
            return SMS_STATUS_SUCCESS;
        }
    }
    for (std::int64_t index = 0; index < layout_.primary_lane_count; ++index) {
        const auto bound = budget.check_periodic(probe++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        std::uint64_t observed{};
        const auto status = read_valid_reference(*primary_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == exact_binding) {
            contains = true;
            return SMS_STATUS_SUCCESS;
        }
    }
    for (std::int64_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(probe++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        std::uint64_t observed{};
        const auto status = read_valid_reference(*overflow_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == exact_binding) {
            contains = true;
            return SMS_STATUS_SUCCESS;
        }
    }
    return SMS_STATUS_SUCCESS;
}

std::uint64_t KeyDirectory::read_spill_summary(
    std::int32_t canonical_bucket) const noexcept {
    auto* const word = spill_word(canonical_bucket);
    return word == nullptr ? 0 : MappedAtomic64::load_acquire(*word);
}

std::uint64_t KeyDirectory::read_mutation(
    std::int32_t canonical_bucket) const noexcept {
    auto* const word = mutation_word(canonical_bucket);
    return word == nullptr ? 0 : MappedAtomic64::load_acquire(*word);
}

void KeyDirectory::buckets_for_hash(
    std::uint64_t hash,
    std::int32_t& canonical,
    std::int32_t& alternate) const noexcept {
    canonical = static_cast<std::int32_t>(
        mix(hash) & static_cast<std::uint32_t>(bucket_mask_));
    alternate = static_cast<std::int32_t>(
        mix(hash ^ 0x9e37'79b9'7f4a'7c15ULL) &
        static_cast<std::uint32_t>(bucket_mask_));
    if (alternate == canonical) {
        alternate = (canonical + 1) & bucket_mask_;
    }
}

std::int32_t KeyDirectory::overflow_start_for_hash(
    std::uint64_t hash) const noexcept {
    return valid_ ? static_cast<std::int32_t>(
        mix(hash ^ 0xd6e8'feb8'6659'fd93ULL) %
        static_cast<std::uint32_t>(layout_.slot_count)) : 0;
}

sms_status KeyDirectory::find_exact(
    std::span<const std::byte> key,
    std::uint64_t key_hash,
    std::uint64_t excluded_binding,
    const OperationBudget& budget,
    DirectoryEntry& entry) noexcept {
    entry = {};
    std::int32_t first{};
    std::int32_t second{};
    buckets_for_hash(key_hash, first, second);
    auto status = scan_primary_bucket(
        first, key, key_hash, excluded_binding, budget, entry);
    if (status != SMS_STATUS_NOT_FOUND) return status;
    status = scan_primary_bucket(
        second, key, key_hash, excluded_binding, budget, entry);
    if (status != SMS_STATUS_NOT_FOUND) return status;

    auto* const summary_word = spill_word(first);
    if (summary_word == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto summary_raw = MappedAtomic64::load_acquire(*summary_word);
    SpillSummary summary{};
    if (!decode_summary_semantic(summary_raw, summary)) {
        if (MappedAtomic64::load_acquire(*summary_word) == summary_raw) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        return SMS_STATUS_STORE_BUSY;
    }
    if (!summary.is_present) return SMS_STATUS_NOT_FOUND;

    const auto start = overflow_start_for_hash(key_hash);
    for (std::int32_t offset = 0; offset < layout_.slot_count; ++offset) {
        const auto bound = budget.check_periodic(offset);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto index = (start + offset) % layout_.slot_count;
        status = inspect_cell(
            Cell{overflow_word(index), directory_target_overflow, index},
            key,
            key_hash,
            excluded_binding,
            budget,
            entry);
        if (status != SMS_STATUS_NOT_FOUND) return status;
    }
    return SMS_STATUS_NOT_FOUND;
}

sms_status KeyDirectory::scan_primary_bucket(
    std::int32_t bucket,
    std::span<const std::byte> key,
    std::uint64_t key_hash,
    std::uint64_t excluded_binding,
    const OperationBudget& budget,
    DirectoryEntry& entry) noexcept {
    const auto first_lane = bucket * sms2_primary_lanes_per_bucket;
    for (std::int32_t lane = 0; lane < sms2_primary_lanes_per_bucket; ++lane) {
        const auto bound = budget.check_periodic(lane);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto index = first_lane + lane;
        const auto status = inspect_cell(
            Cell{primary_word(index), directory_target_primary, index},
            key,
            key_hash,
            excluded_binding,
            budget,
            entry);
        if (status != SMS_STATUS_NOT_FOUND) return status;
    }
    return SMS_STATUS_NOT_FOUND;
}

sms_status KeyDirectory::inspect_cell(
    Cell cell,
    std::span<const std::byte> key,
    std::uint64_t key_hash,
    std::uint64_t excluded_binding,
    const OperationBudget& budget,
    DirectoryEntry& entry) noexcept {
    entry = {};
    if (cell.word == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto raw = MappedAtomic64::load_acquire(*cell.word);
    if (raw == 0 || raw == excluded_binding) return SMS_STATUS_NOT_FOUND;

    BindingValidation validation{};
    auto status = validate_binding(raw, &key_hash, key, budget, validation);
    if (status != SMS_STATUS_SUCCESS) return status;
    if (validation == BindingValidation::invalid) {
        bool remains_exact{};
        status = revalidate_invalid_reference(
            cell,
            raw,
            &key_hash,
            key,
            budget,
            validation,
            remains_exact);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (!remains_exact) return SMS_STATUS_NOT_FOUND;
    }
    if (validation == BindingValidation::stale) {
        bool changed{};
        status = clear_exact(*cell.word, raw, changed);
        return status == SMS_STATUS_SUCCESS
            ? SMS_STATUS_NOT_FOUND
            : status;
    }
    if (validation == BindingValidation::invalid) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (validation == BindingValidation::retry) return SMS_STATUS_STORE_BUSY;
    if (validation != BindingValidation::exact ||
        MappedAtomic64::load_acquire(*cell.word) != raw) {
        return SMS_STATUS_NOT_FOUND;
    }

    IndexBinding binding{};
    std::uint64_t location_raw{};
    if (!decode_binding(raw, layout_.slot_count, binding) ||
        !DirectoryLocation::try_encode(
            cell.kind, cell.index, binding.generation, location_raw) ||
        !DirectoryLocation::try_decode(location_raw, entry.location)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    entry.binding = raw;
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::validate_binding(
    std::uint64_t raw,
    const std::uint64_t* expected_hash,
    std::span<const std::byte> expected_key,
    const OperationBudget& budget,
    BindingValidation& validation) noexcept {
    validation = BindingValidation::invalid;
    IndexBinding binding{};
    if (!decode_binding(raw, layout_.slot_count, binding)) {
        return SMS_STATUS_SUCCESS;
    }
    auto* const value_slot = slot(binding.slot_index);
    if (value_slot == nullptr) return SMS_STATUS_CORRUPT_STORE;

    for (std::int32_t attempt = 0; attempt < 8; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto control1 = MappedAtomic64::load_acquire(value_slot->Control);
        const auto classified1 = classify_control(control1, binding.generation);
        if (classified1 != ControlBindingStatus::current) {
            validation = classified1 == ControlBindingStatus::stale
                ? BindingValidation::stale
                : BindingValidation::invalid;
            return SMS_STATUS_SUCCESS;
        }

        const auto directory_binding = value_slot->DirectoryBinding;
        if (directory_binding != raw) {
            const auto control2 = MappedAtomic64::load_acquire(value_slot->Control);
            if (control1 != control2) continue;
            const auto classified2 = classify_control(control2, binding.generation);
            validation = classified2 == ControlBindingStatus::stale
                ? BindingValidation::stale
                : BindingValidation::invalid;
            return SMS_STATUS_SUCCESS;
        }

        const auto observed_hash = value_slot->KeyHash;
        bool equal{};
        bool key_valid = true;
        if (expected_hash != nullptr && observed_hash == *expected_hash) {
            const auto key = stored_key(*value_slot, binding.slot_index);
            key_valid = !key.empty();
            if (key_valid) {
                const auto equality = keys_equal(key, expected_key, budget, equal);
                if (equality != SMS_STATUS_SUCCESS) return equality;
            }
        }

        const auto control2 = MappedAtomic64::load_acquire(value_slot->Control);
        if (control1 == control2 && value_slot->DirectoryBinding == raw) {
            if (expected_hash == nullptr) {
                validation = BindingValidation::current_other;
            } else if (observed_hash == *expected_hash && !key_valid) {
                validation = BindingValidation::invalid;
            } else {
                validation = equal
                    ? BindingValidation::exact
                    : BindingValidation::current_other;
            }
            return SMS_STATUS_SUCCESS;
        }

        const auto revalidated = classify_control(control2, binding.generation);
        if (revalidated != ControlBindingStatus::current ||
            value_slot->DirectoryBinding != raw) {
            validation = revalidated == ControlBindingStatus::stale
                ? BindingValidation::stale
                : BindingValidation::invalid;
            return SMS_STATUS_SUCCESS;
        }
    }

    validation = BindingValidation::retry;
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::revalidate_invalid_reference(
    Cell cell,
    std::uint64_t expected_reference,
    const std::uint64_t* expected_hash,
    std::span<const std::byte> expected_key,
    const OperationBudget& budget,
    BindingValidation& validation,
    bool& remains_exact) noexcept {
    remains_exact = false;
    if (cell.word == nullptr ||
        MappedAtomic64::load_acquire(*cell.word) != expected_reference) {
        return SMS_STATUS_SUCCESS;
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::DirectoryAfterInvalidReferenceConfirmationBeforeBindingRevalidation);
    checkpoint(
        DirectoryCheckpoint::after_invalid_reference_confirmation,
        expected_reference,
        static_cast<std::uint64_t>(cell.index));
    const auto status = validate_binding(
        expected_reference,
        expected_hash,
        expected_key,
        budget,
        validation);
    if (status != SMS_STATUS_SUCCESS) return status;
    remains_exact =
        MappedAtomic64::load_acquire(*cell.word) == expected_reference;
    return SMS_STATUS_SUCCESS;
}

KeyDirectory::ControlBindingStatus KeyDirectory::classify_control(
    std::uint64_t control,
    std::int64_t expected_generation) const noexcept {
    SlotControl decoded{};
    bool occupied{};
    if (!SlotControl::try_decode(control, decoded) ||
        !decoded.structurally_valid(layout_.participant_record_count, occupied) ||
        decoded.generation < 1 ||
        static_cast<std::uint64_t>(decoded.generation) >
            control_word_detail::slot_generation_mask) {
        return ControlBindingStatus::invalid;
    }
    if (decoded.generation > expected_generation) {
        return ControlBindingStatus::stale;
    }
    if (decoded.generation < expected_generation) {
        return ControlBindingStatus::invalid;
    }
    if (!occupied || !state_allows_directory_reference(decoded.state) ||
        decoded.state == slot_retired) {
        return decoded.state == slot_retired
            ? ControlBindingStatus::stale
            : ControlBindingStatus::invalid;
    }
    return ControlBindingStatus::current;
}

sms_status KeyDirectory::keys_equal(
    std::span<const std::byte> stored,
    std::span<const std::byte> expected,
    const OperationBudget& budget,
    bool& equal) const noexcept {
    equal = false;
    if (stored.size() != expected.size()) return SMS_STATUS_SUCCESS;
    constexpr std::size_t comparison_chunk = 64;
    std::int32_t chunk = 0;
    for (std::size_t offset = 0; offset < stored.size(); offset += comparison_chunk) {
        const auto bound = budget.check_periodic(chunk++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto length = std::min(comparison_chunk, stored.size() - offset);
        if (!std::equal(
                stored.begin() + static_cast<std::ptrdiff_t>(offset),
                stored.begin() + static_cast<std::ptrdiff_t>(offset + length),
                expected.begin() + static_cast<std::ptrdiff_t>(offset))) {
            return SMS_STATUS_SUCCESS;
        }
    }
    const auto completion = budget.check_periodic(chunk);
    if (completion != SMS_STATUS_SUCCESS) return completion;
    equal = true;
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::prepare_operation(
    ValueSlotMetadataV2& value_slot,
    std::uint64_t binding,
    std::int32_t intent,
    std::int64_t generation,
    const OperationBudget& budget) noexcept {
    std::uint64_t prepared{};
    if (!DirectoryOperation::try_encode(
            intent,
            directory_phase_prepared,
            0,
            0,
            generation,
            prepared)) {
        return SMS_STATUS_CORRUPT_STORE;
    }

    for (std::int32_t attempt = 0; attempt < default_retry_budget; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto observed = MappedAtomic64::load_acquire(value_slot.DirectoryOperation);
        if (observed == prepared) return SMS_STATUS_SUCCESS;
        if (observed == 0) {
            auto expected = std::uint64_t{};
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DirectoryBeforeDescriptorPublication);
            if (MappedAtomic64::compare_exchange(
                    value_slot.DirectoryOperation, expected, prepared)) {
                checkpoint(
                    DirectoryCheckpoint::after_insert_prepared,
                    binding,
                    prepared);
                return SMS_STATUS_SUCCESS;
            }
            continue;
        }

        DirectoryOperation decoded{};
        if (!decode_operation_semantic(observed, decoded)) {
            if (MappedAtomic64::load_acquire(value_slot.DirectoryOperation) == observed) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        if (decoded.generation > generation) {
            return intent == directory_intent_insert
                ? SMS_STATUS_INVALID_RESERVATION
                : SMS_STATUS_NOT_FOUND;
        }
        if (decoded.generation < generation) {
            auto expected = observed;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected, 0);
            continue;
        }
        if (decoded.intent == intent) return SMS_STATUS_SUCCESS;
        if (intent == directory_intent_unlink &&
            decoded.intent == directory_intent_insert) {
            auto expected = observed;
            if (MappedAtomic64::compare_exchange(
                    value_slot.DirectoryOperation, expected, prepared)) {
                return SMS_STATUS_SUCCESS;
            }
            continue;
        }
        return SMS_STATUS_CORRUPT_STORE;
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status KeyDirectory::publish_reserved(
    ValueSlotMetadataV2& value_slot,
    std::uint64_t binding) noexcept {
    IndexBinding decoded_binding{};
    if (!decode_binding(binding, layout_.slot_count, decoded_binding)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t attempt = 0; attempt < default_retry_budget; ++attempt) {
        auto observed = MappedAtomic64::load_acquire(value_slot.Control);
        SlotControl control{};
        bool occupied{};
        if (!SlotControl::try_decode(observed, control) ||
            !control.structurally_valid(layout_.participant_record_count, occupied) ||
            !occupied || control.generation != decoded_binding.generation) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (control.state == slot_reserved) return SMS_STATUS_SUCCESS;
        if (control.state != slot_initializing) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        std::uint64_t desired{};
        if (!SlotControl::try_encode(
                slot_reserved,
                control.generation,
                control.participant_token,
                desired)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = observed;
        if (MappedAtomic64::compare_exchange(value_slot.Control, expected, desired)) {
            return SMS_STATUS_SUCCESS;
        }
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status KeyDirectory::claim_mutation(
    std::int32_t canonical_bucket,
    std::uint64_t binding,
    const OperationBudget& budget) noexcept {
    auto* const mutation = mutation_word(canonical_bucket);
    if (mutation == nullptr) return SMS_STATUS_CORRUPT_STORE;
    for (std::int32_t attempt = 0;; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto observed = MappedAtomic64::load_acquire(*mutation);
        if (observed == binding) return SMS_STATUS_SUCCESS;
        if (observed == 0) {
            auto expected = std::uint64_t{};
            if (MappedAtomic64::compare_exchange(*mutation, expected, binding)) {
                checkpoint(
                    DirectoryCheckpoint::after_mutation_claimed,
                    binding,
                    static_cast<std::uint64_t>(canonical_bucket));
                return SMS_STATUS_SUCCESS;
            }
            continue;
        }

        std::uint64_t confirmed{};
        const auto reference_status = read_valid_reference(*mutation, confirmed);
        if (reference_status != SMS_STATUS_SUCCESS) return reference_status;
        if (confirmed == 0) continue;
        const auto help_status = help_mutation(canonical_bucket, budget, 8);
        if (help_status != SMS_STATUS_SUCCESS &&
            help_status != SMS_STATUS_STORE_BUSY) {
            return help_status;
        }
        if (attempt + 1 >= default_retry_budget) {
            sms_status terminal{};
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
        }
    }
}

sms_status KeyDirectory::help_insert(
    std::int32_t canonical_bucket,
    std::uint64_t binding,
    IndexBinding decoded,
    ValueSlotMetadataV2& value_slot,
    std::uint64_t operation_raw,
    DirectoryOperation operation,
    const OperationBudget& budget) noexcept {
    auto* const mutation = mutation_word(canonical_bucket);
    if (mutation == nullptr) return SMS_STATUS_CORRUPT_STORE;

    if (operation.phase == directory_phase_rejected) {
        bool changed{};
        return clear_exact(*mutation, binding, changed);
    }
    if (operation.phase == directory_phase_complete) {
        checkpoint(
            DirectoryCheckpoint::before_mutation_release,
            binding,
            operation_raw);
        bool changed{};
        return clear_exact(*mutation, binding, changed);
    }

    if (operation.phase == directory_phase_prepared) {
        const auto key = stored_key(value_slot, decoded.slot_index);
        if (key.empty()) return SMS_STATUS_CORRUPT_STORE;
        DirectoryEntry duplicate{};
        auto status = find_exact(
            key,
            value_slot.KeyHash,
            binding,
            budget,
            duplicate);
        if (status == SMS_STATUS_SUCCESS) {
            std::uint64_t rejected{};
            if (!DirectoryOperation::try_encode(
                    directory_intent_insert,
                    directory_phase_rejected,
                    0,
                    0,
                    decoded.generation,
                    rejected)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            auto expected = operation_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected, rejected);
            bool changed{};
            return clear_exact(*mutation, binding, changed);
        }
        if (status != SMS_STATUS_NOT_FOUND) return status;
        checkpoint(
            DirectoryCheckpoint::after_duplicate_scan,
            binding,
            value_slot.KeyHash);

        DirectoryLocation target{};
        status = select_insert_target(
            value_slot.KeyHash, decoded.generation, budget, target);
        if (status == SMS_STATUS_STORE_FULL) {
            auto expected = operation_raw;
            if (MappedAtomic64::compare_exchange(
                    value_slot.DirectoryOperation, expected, 0)) {
                bool changed{};
                const auto clear_status = clear_exact(*mutation, binding, changed);
                return clear_status == SMS_STATUS_SUCCESS
                    ? SMS_STATUS_STORE_FULL
                    : clear_status;
            }
            return SMS_STATUS_SUCCESS;
        }
        if (status != SMS_STATUS_SUCCESS) return status;
        std::uint64_t selected{};
        if (!DirectoryOperation::try_encode(
                directory_intent_insert,
                directory_phase_target_selected,
                target.kind,
                target.index,
                decoded.generation,
                selected)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation, expected, selected);
        checkpoint(
            DirectoryCheckpoint::after_target_selected,
            binding,
            target.value);
        return SMS_STATUS_SUCCESS;
    }

    if (operation.phase == directory_phase_target_selected) {
        Cell target_cell{};
        if (!try_get_cell(operation.target_kind, operation.target_index, target_cell)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        checkpoint(
            DirectoryCheckpoint::before_target_binding_cas,
            binding,
            operation.value);
        auto observed = MappedAtomic64::load_acquire(*target_cell.word);
        if (observed != binding) {
            if (observed == 0) {
                auto expected = std::uint64_t{};
                if (!MappedAtomic64::compare_exchange(
                        *target_cell.word, expected, binding)) {
                    observed = expected;
                } else {
                    observed = binding;
                }
            }
            if (observed != binding) {
                BindingValidation validation{};
                const auto status = validate_binding(
                    observed, nullptr, {}, budget, validation);
                if (status != SMS_STATUS_SUCCESS) return status;
                if (validation == BindingValidation::stale) {
                    bool changed{};
                    const auto clear_status =
                        clear_exact(*target_cell.word, observed, changed);
                    return clear_status;
                }
                if (validation == BindingValidation::invalid) {
                    bool remains{};
                    auto status2 = revalidate_invalid_reference(
                        target_cell,
                        observed,
                        nullptr,
                        {},
                        budget,
                        validation,
                        remains);
                    if (status2 != SMS_STATUS_SUCCESS) return status2;
                    if (remains && validation == BindingValidation::invalid) {
                        return SMS_STATUS_CORRUPT_STORE;
                    }
                }
                std::uint64_t prepared{};
                (void)DirectoryOperation::try_encode(
                    directory_intent_insert,
                    directory_phase_prepared,
                    0,
                    0,
                    decoded.generation,
                    prepared);
                auto expected_operation = operation_raw;
                (void)MappedAtomic64::compare_exchange(
                    value_slot.DirectoryOperation,
                    expected_operation,
                    prepared);
                return SMS_STATUS_SUCCESS;
            }
        }
        checkpoint(
            DirectoryCheckpoint::after_target_binding_cas,
            binding,
            operation.value);
        checkpoint(
            DirectoryCheckpoint::before_source_revalidation,
            binding,
            operation.value);

        const auto control = MappedAtomic64::load_acquire(value_slot.Control);
        const auto current_operation =
            MappedAtomic64::load_acquire(value_slot.DirectoryOperation);
        const auto current_mutation = MappedAtomic64::load_acquire(*mutation);

        // Losing the exact operation source is ordinary helping progress.  In
        // particular, a second helper may have published this same target and
        // advanced TargetSelected to BindingChanged/Complete.  Rolling the
        // cell back in that case would remove a committed directory entry and
        // incorrectly report InvalidReservation to the original inserter.
        if (current_operation != operation_raw || current_mutation != binding) {
            DirectoryOperation advanced{};
            const auto same_or_later_insert =
                decode_operation_semantic(current_operation, advanced) &&
                advanced.intent == directory_intent_insert &&
                advanced.generation == operation.generation &&
                advanced.target_kind == operation.target_kind &&
                advanced.target_index == operation.target_index &&
                (advanced.phase == directory_phase_binding_changed ||
                 advanced.phase == directory_phase_complete);
            if (!same_or_later_insert) {
                bool rolled_back{};
                const auto rollback_status =
                    clear_exact(*target_cell.word, binding, rolled_back);
                if (rollback_status != SMS_STATUS_SUCCESS) return rollback_status;
            }
            return SMS_STATUS_SUCCESS;
        }

        if (classify_control(control, decoded.generation) !=
                ControlBindingStatus::current ||
            value_slot.DirectoryBinding != binding) {
            bool rolled_back{};
            const auto rollback_status =
                clear_exact(*target_cell.word, binding, rolled_back);
            if (rollback_status != SMS_STATUS_SUCCESS) return rollback_status;
            bool mutation_cleared{};
            (void)clear_exact(*mutation, binding, mutation_cleared);
            auto expected_operation = operation_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected_operation, 0);
            return SMS_STATUS_INVALID_RESERVATION;
        }

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterLocationPublisherBindingValidation);

        DirectoryLocation proposed{};
        std::uint64_t location_raw{};
        if (!DirectoryLocation::try_encode(
                operation.target_kind,
                operation.target_index,
                decoded.generation,
                location_raw) ||
            !DirectoryLocation::try_decode(location_raw, proposed)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        checkpoint(
            DirectoryCheckpoint::before_location_arbitration,
            binding,
            proposed.value);
        DirectoryLocation winner{};
        auto status = arbitrate_location(
            canonical_bucket,
            binding,
            operation_raw,
            proposed,
            value_slot,
            winner);
        if (status != SMS_STATUS_SUCCESS) return status;
        checkpoint(
            DirectoryCheckpoint::after_location_arbitration,
            binding,
            winner.value);

        if (winner.value != proposed.value) {
            bool changed{};
            status = clear_exact(*target_cell.word, binding, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
        }
        std::uint64_t changed_operation{};
        if (!DirectoryOperation::try_encode(
                directory_intent_insert,
                directory_phase_binding_changed,
                winner.kind,
                winner.index,
                decoded.generation,
                changed_operation)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected_operation = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation,
            expected_operation,
            changed_operation);
        return SMS_STATUS_SUCCESS;
    }

    if (operation.phase == directory_phase_binding_changed) {
        Cell target{};
        const auto location_raw =
            MappedAtomic64::load_acquire(value_slot.DirectoryLocation);
        DirectoryLocation location{};
        if (!decode_location_semantic(location_raw, location) ||
            location.generation != decoded.generation ||
            location.kind != operation.target_kind ||
            location.index != operation.target_index ||
            !try_get_cell(location.kind, location.index, target)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (MappedAtomic64::load_acquire(*target.word) != binding) {
            auto expected_location = location_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryLocation, expected_location, 0);
            std::uint64_t prepared{};
            (void)DirectoryOperation::try_encode(
                directory_intent_insert,
                directory_phase_prepared,
                0,
                0,
                decoded.generation,
                prepared);
            auto expected_operation = operation_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected_operation, prepared);
            return SMS_STATUS_SUCCESS;
        }
        if (location.kind == directory_target_overflow) {
            const auto status = publish_spill_present(
                canonical_bucket, binding, budget);
            if (status != SMS_STATUS_SUCCESS) return status;
        }
        const auto control = MappedAtomic64::load_acquire(value_slot.Control);
        SlotControl current{};
        if (!SlotControl::try_decode(control, current) ||
            current.generation != decoded.generation) {
            return current.generation > decoded.generation
                ? SMS_STATUS_INVALID_RESERVATION
                : SMS_STATUS_CORRUPT_STORE;
        }
        if (current.state == slot_aborting || current.state == slot_reclaiming) {
            bool changed{};
            auto status = clear_exact(*target.word, binding, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (location.kind == directory_target_overflow) {
                status = refresh_spill_after_unlink(
                    canonical_bucket, binding, budget);
                if (status != SMS_STATUS_SUCCESS) return status;
            }
            auto expected_location = location_raw;
            const auto location_cleared = MappedAtomic64::compare_exchange(
                value_slot.DirectoryLocation, expected_location, 0) ||
                expected_location == 0;
            if (location_cleared) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DirectoryAfterCancelLocationClearBeforeDescriptorRejection);
            }
            auto expected_operation = operation_raw;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected_operation, 0);
            bool mutation_cleared{};
            (void)clear_exact(*mutation, binding, mutation_cleared);
            return SMS_STATUS_INVALID_RESERVATION;
        }
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterInsertBindingChangedStateValidationBeforeReservedPublication);
        checkpoint(
            DirectoryCheckpoint::before_reserved_publication,
            binding,
            control);
        const auto reserved_status = publish_reserved(value_slot, binding);
        if (reserved_status != SMS_STATUS_SUCCESS) return reserved_status;
        checkpoint(
            DirectoryCheckpoint::after_reserved_publication,
            binding,
            MappedAtomic64::load_acquire(value_slot.Control));
        std::uint64_t complete{};
        if (!DirectoryOperation::try_encode(
                directory_intent_insert,
                directory_phase_complete,
                location.kind,
                location.index,
                decoded.generation,
                complete)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected_operation = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation, expected_operation, complete);
        return SMS_STATUS_SUCCESS;
    }

    return SMS_STATUS_CORRUPT_STORE;
}

sms_status KeyDirectory::help_unlink(
    std::int32_t canonical_bucket,
    std::uint64_t binding,
    IndexBinding decoded,
    ValueSlotMetadataV2& value_slot,
    std::uint64_t operation_raw,
    DirectoryOperation operation,
    const OperationBudget& budget) noexcept {
    auto* const mutation = mutation_word(canonical_bucket);
    if (mutation == nullptr) return SMS_STATUS_CORRUPT_STORE;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::DirectoryAfterUnlinkOperationValidationBeforeLocationRead);

    if (operation.phase == directory_phase_prepared) {
        DirectoryLocation selected{};
        const auto location_raw =
            MappedAtomic64::load_acquire(value_slot.DirectoryLocation);
        DirectoryLocation existing{};
        Cell existing_cell{};
        if (decode_location_semantic(location_raw, existing) &&
            existing.generation == decoded.generation &&
            try_get_cell(existing.kind, existing.index, existing_cell) &&
            MappedAtomic64::load_acquire(*existing_cell.word) == binding) {
            selected = existing;
        } else {
            const auto status = find_first_binding_location(binding, selected);
            if (status != SMS_STATUS_SUCCESS && status != SMS_STATUS_NOT_FOUND) {
                return status;
            }
        }

        std::uint64_t selected_operation{};
        if (selected.value == 0) {
            if (!DirectoryOperation::try_encode(
                    directory_intent_unlink,
                    directory_phase_complete,
                    0,
                    0,
                    decoded.generation,
                    selected_operation)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
        } else if (!DirectoryOperation::try_encode(
                       directory_intent_unlink,
                       directory_phase_target_selected,
                       selected.kind,
                       selected.index,
                       decoded.generation,
                       selected_operation)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation, expected, selected_operation);
        checkpoint(
            DirectoryCheckpoint::after_unlink_target_selected,
            binding,
            selected.value);
        return SMS_STATUS_SUCCESS;
    }

    if (operation.phase == directory_phase_target_selected) {
        Cell target{};
        if (!try_get_cell(operation.target_kind, operation.target_index, target)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterLocationValidation);
        auto observed = MappedAtomic64::load_acquire(*target.word);
        if (observed == binding) {
            auto expected = binding;
            (void)MappedAtomic64::compare_exchange(*target.word, expected, 0);
            observed = MappedAtomic64::load_acquire(*target.word);
        }
        if (observed != 0 && observed != binding) {
            DirectoryLocation alternate{};
            const auto find_status = find_first_binding_location(binding, alternate);
            if (find_status == SMS_STATUS_SUCCESS) {
                std::uint64_t retargeted{};
                (void)DirectoryOperation::try_encode(
                    directory_intent_unlink,
                    directory_phase_target_selected,
                    alternate.kind,
                    alternate.index,
                    decoded.generation,
                    retargeted);
                auto expected = operation_raw;
                (void)MappedAtomic64::compare_exchange(
                    value_slot.DirectoryOperation, expected, retargeted);
                return SMS_STATUS_SUCCESS;
            }
            if (find_status != SMS_STATUS_NOT_FOUND) return find_status;
        }
        checkpoint(
            DirectoryCheckpoint::after_unlink_binding_clear,
            binding,
            operation.value);
        std::uint64_t changed{};
        if (!DirectoryOperation::try_encode(
                directory_intent_unlink,
                directory_phase_binding_changed,
                operation.target_kind,
                operation.target_index,
                decoded.generation,
                changed)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation, expected, changed);
        return SMS_STATUS_SUCCESS;
    }

    if (operation.phase == directory_phase_binding_changed) {
        auto status = clear_alternate_references(binding, nullptr, budget);
        if (status != SMS_STATUS_SUCCESS) return status;
        status = refresh_spill_after_unlink(
            canonical_bucket, binding, budget);
        if (status != SMS_STATUS_SUCCESS) return status;
        std::uint64_t complete{};
        if (!DirectoryOperation::try_encode(
                directory_intent_unlink,
                directory_phase_complete,
                0,
                0,
                decoded.generation,
                complete)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = operation_raw;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryOperation, expected, complete);
        return SMS_STATUS_SUCCESS;
    }

    if (operation.phase == directory_phase_complete) {
        DirectoryLocation unexpected{};
        const auto remaining = find_first_binding_location(binding, unexpected);
        if (remaining == SMS_STATUS_SUCCESS) {
            const auto cleanup = clear_alternate_references(binding, nullptr, budget);
            return cleanup == SMS_STATUS_SUCCESS
                ? SMS_STATUS_STORE_BUSY
                : cleanup;
        }
        if (remaining != SMS_STATUS_NOT_FOUND) return remaining;
        auto status = refresh_spill_after_unlink(
            canonical_bucket, binding, budget);
        if (status != SMS_STATUS_SUCCESS) return status;

        const auto location_raw =
            MappedAtomic64::load_acquire(value_slot.DirectoryLocation);
        DirectoryLocation location{};
        if (location_raw != 0) {
            if (!decode_location_semantic(location_raw, location)) {
                if (MappedAtomic64::load_acquire(value_slot.DirectoryLocation) ==
                    location_raw) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                return SMS_STATUS_STORE_BUSY;
            }
            if (location.generation > decoded.generation) {
                bool changed{};
                (void)clear_exact(*mutation, binding, changed);
                return SMS_STATUS_NOT_FOUND;
            }
            if (location.generation == decoded.generation) {
                auto expected_location = location_raw;
                (void)MappedAtomic64::compare_exchange(
                    value_slot.DirectoryLocation, expected_location, 0);
            }
        }

        auto expected_operation = operation_raw;
        if (MappedAtomic64::compare_exchange(
                value_slot.DirectoryOperation, expected_operation, 0)) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DirectoryAfterDescriptorClear);
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DirectoryAfterUnlinkDescriptorClearBeforeGenerationAdvance);
            checkpoint(
                DirectoryCheckpoint::before_mutation_release,
                binding,
                operation_raw);
            bool changed{};
            return clear_exact(*mutation, binding, changed);
        }
        return SMS_STATUS_SUCCESS;
    }

    return SMS_STATUS_CORRUPT_STORE;
}

sms_status KeyDirectory::select_insert_target(
    std::uint64_t key_hash,
    std::int64_t generation,
    const OperationBudget& budget,
    DirectoryLocation& target) noexcept {
    target = {};
    std::int32_t first{};
    std::int32_t second{};
    buckets_for_hash(key_hash, first, second);
    auto status = select_primary_target(first, generation, budget, target);
    if (status == SMS_STATUS_SUCCESS) return status;
    if (status != SMS_STATUS_NOT_FOUND) return status;
    status = select_primary_target(second, generation, budget, target);
    if (status == SMS_STATUS_SUCCESS) return status;
    if (status != SMS_STATUS_NOT_FOUND) return status;

    const auto start = overflow_start_for_hash(key_hash);
    for (std::int32_t offset = 0; offset < layout_.slot_count; ++offset) {
        const auto bound = budget.check_periodic(offset);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto index = (start + offset) % layout_.slot_count;
        auto* const word = overflow_word(index);
        auto raw = MappedAtomic64::load_acquire(*word);
        if (raw == 0) {
            std::uint64_t encoded{};
            if (!DirectoryLocation::try_encode(
                    directory_target_overflow,
                    index,
                    generation,
                    encoded) ||
                !DirectoryLocation::try_decode(encoded, target)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            return SMS_STATUS_SUCCESS;
        }
        BindingValidation validation{};
        status = validate_binding(raw, nullptr, {}, budget, validation);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (validation == BindingValidation::stale) {
            bool changed{};
            status = clear_exact(*word, raw, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (MappedAtomic64::load_acquire(*word) == 0) {
                std::uint64_t encoded{};
                if (!DirectoryLocation::try_encode(
                        directory_target_overflow,
                        index,
                        generation,
                        encoded) ||
                    !DirectoryLocation::try_decode(encoded, target)) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                return SMS_STATUS_SUCCESS;
            }
        } else if (validation == BindingValidation::invalid) {
            Cell cell{word, directory_target_overflow, index};
            bool remains{};
            status = revalidate_invalid_reference(
                cell, raw, nullptr, {}, budget, validation, remains);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (remains && validation == BindingValidation::invalid) {
                return SMS_STATUS_CORRUPT_STORE;
            }
        }
    }
    return SMS_STATUS_STORE_FULL;
}

sms_status KeyDirectory::select_primary_target(
    std::int32_t bucket,
    std::int64_t generation,
    const OperationBudget& budget,
    DirectoryLocation& target) noexcept {
    target = {};
    const auto first_lane = bucket * sms2_primary_lanes_per_bucket;
    for (std::int32_t lane = 0; lane < sms2_primary_lanes_per_bucket; ++lane) {
        const auto bound = budget.check_periodic(lane);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto index = first_lane + lane;
        auto* const word = primary_word(index);
        auto raw = MappedAtomic64::load_acquire(*word);
        if (raw == 0) {
            std::uint64_t encoded{};
            if (!DirectoryLocation::try_encode(
                    directory_target_primary,
                    index,
                    generation,
                    encoded) ||
                !DirectoryLocation::try_decode(encoded, target)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            return SMS_STATUS_SUCCESS;
        }
        BindingValidation validation{};
        auto status = validate_binding(raw, nullptr, {}, budget, validation);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (validation == BindingValidation::stale) {
            bool changed{};
            status = clear_exact(*word, raw, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (MappedAtomic64::load_acquire(*word) == 0) {
                std::uint64_t encoded{};
                if (!DirectoryLocation::try_encode(
                        directory_target_primary,
                        index,
                        generation,
                        encoded) ||
                    !DirectoryLocation::try_decode(encoded, target)) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                return SMS_STATUS_SUCCESS;
            }
        } else if (validation == BindingValidation::invalid) {
            Cell cell{word, directory_target_primary, index};
            bool remains{};
            status = revalidate_invalid_reference(
                cell, raw, nullptr, {}, budget, validation, remains);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (remains && validation == BindingValidation::invalid) {
                return SMS_STATUS_CORRUPT_STORE;
            }
        }
    }
    return SMS_STATUS_NOT_FOUND;
}

sms_status KeyDirectory::arbitrate_location(
    std::int32_t canonical_bucket,
    std::uint64_t binding,
    std::uint64_t operation_raw,
    const DirectoryLocation& proposed,
    ValueSlotMetadataV2& value_slot,
    DirectoryLocation& winner) noexcept {
    winner = {};
    auto* const mutation = mutation_word(canonical_bucket);
    if (mutation == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto revalidate_exact_source = [&]() noexcept -> sms_status {
        const auto control1 =
            MappedAtomic64::load_acquire(value_slot.Control);
        const auto classified = classify_control(
            control1, proposed.generation);
        const auto directory_binding =
            MappedAtomic64::load_acquire(value_slot.DirectoryBinding);
        const auto current_operation =
            MappedAtomic64::load_acquire(value_slot.DirectoryOperation);
        const auto current_mutation =
            MappedAtomic64::load_acquire(*mutation);
        const auto control2 =
            MappedAtomic64::load_acquire(value_slot.Control);
        if (control1 != control2) return SMS_STATUS_STORE_BUSY;
        if (classified == ControlBindingStatus::invalid) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        return classified == ControlBindingStatus::current &&
                directory_binding == binding &&
                current_operation == operation_raw &&
                current_mutation == binding
            ? SMS_STATUS_SUCCESS
            : SMS_STATUS_STORE_BUSY;
    };

    for (std::int32_t attempt = 0; attempt < 8; ++attempt) {
        auto observed = MappedAtomic64::load_acquire(value_slot.DirectoryLocation);
        if (observed == 0) {
            auto expected = std::uint64_t{};
            const auto source = revalidate_exact_source();
            if (source != SMS_STATUS_SUCCESS) return source;
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas);
            if (MappedAtomic64::compare_exchange(
                    value_slot.DirectoryLocation, expected, proposed.value)) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DirectoryAfterLocationPublicationBeforeSourceRevalidation);
                const auto published_source = revalidate_exact_source();
                if (published_source != SMS_STATUS_SUCCESS) {
                    return published_source;
                }
                winner = proposed;
                return SMS_STATUS_SUCCESS;
            }
            observed = expected;
        }
        DirectoryLocation existing{};
        if (!decode_location_semantic(observed, existing)) {
            if (MappedAtomic64::load_acquire(value_slot.DirectoryLocation) == observed) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        if (existing.generation > proposed.generation) {
            return SMS_STATUS_INVALID_RESERVATION;
        }
        if (existing.generation < proposed.generation) {
            auto expected = observed;
            (void)MappedAtomic64::compare_exchange(
                value_slot.DirectoryLocation, expected, 0);
            continue;
        }
        Cell existing_cell{};
        if (!try_get_cell(existing.kind, existing.index, existing_cell)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (MappedAtomic64::load_acquire(*existing_cell.word) == binding) {
            const auto source = revalidate_exact_source();
            if (source != SMS_STATUS_SUCCESS) return source;
            winner = existing;
            return SMS_STATUS_SUCCESS;
        }
        auto expected = observed;
        (void)MappedAtomic64::compare_exchange(
            value_slot.DirectoryLocation, expected, 0);
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status KeyDirectory::find_first_binding_location(
    std::uint64_t binding,
    DirectoryLocation& location) noexcept {
    location = {};
    IndexBinding decoded{};
    if (!decode_binding(binding, layout_.slot_count, decoded)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int64_t index = 0; index < layout_.primary_lane_count; ++index) {
        std::uint64_t observed{};
        const auto status = read_valid_reference(*primary_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == binding) {
            std::uint64_t raw{};
            if (!DirectoryLocation::try_encode(
                    directory_target_primary,
                    index,
                    decoded.generation,
                    raw) ||
                !DirectoryLocation::try_decode(raw, location)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            return SMS_STATUS_SUCCESS;
        }
    }
    for (std::int64_t index = 0; index < layout_.slot_count; ++index) {
        std::uint64_t observed{};
        const auto status = read_valid_reference(*overflow_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == binding) {
            std::uint64_t raw{};
            if (!DirectoryLocation::try_encode(
                    directory_target_overflow,
                    index,
                    decoded.generation,
                    raw) ||
                !DirectoryLocation::try_decode(raw, location)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            return SMS_STATUS_SUCCESS;
        }
    }
    return SMS_STATUS_NOT_FOUND;
}

sms_status KeyDirectory::clear_alternate_references(
    std::uint64_t binding,
    const DirectoryLocation* retained,
    const OperationBudget& budget) noexcept {
    std::int32_t probe = 0;
    for (std::int64_t index = 0; index < layout_.primary_lane_count; ++index) {
        const auto bound = budget.check_periodic(probe++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        if (retained != nullptr && retained->kind == directory_target_primary &&
            retained->index == index) {
            continue;
        }
        std::uint64_t observed{};
        auto status = read_valid_reference(*primary_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == binding) {
            bool changed{};
            status = clear_exact(*primary_word(index), binding, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
        }
    }
    for (std::int64_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(probe++);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        if (retained != nullptr && retained->kind == directory_target_overflow &&
            retained->index == index) {
            continue;
        }
        std::uint64_t observed{};
        auto status = read_valid_reference(*overflow_word(index), observed);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (observed == binding) {
            bool changed{};
            status = clear_exact(*overflow_word(index), binding, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
        }
    }
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::publish_spill_present(
    std::int32_t canonical_bucket,
    std::uint64_t binding,
    const OperationBudget& budget) noexcept {
    auto* const word = spill_word(canonical_bucket);
    if (word == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t desired{};
    if (!SpillSummary::try_encode_present(binding, desired)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t attempt = 0;; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto observed = MappedAtomic64::load_acquire(*word);
        SpillSummary summary{};
        if (!decode_summary_semantic(observed, summary)) {
            if (MappedAtomic64::load_acquire(*word) == observed) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        if (summary.is_present) return SMS_STATUS_SUCCESS;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryBeforeSpillSummaryPublicationCas);
        checkpoint(
            DirectoryCheckpoint::before_spill_present_cas,
            binding,
            observed);
        auto expected = observed;
        if (MappedAtomic64::compare_exchange(*word, expected, desired)) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DirectoryAfterSpillSummaryPublication);
            checkpoint(
                DirectoryCheckpoint::after_spill_present,
                binding,
                desired);
            return SMS_STATUS_SUCCESS;
        }
    }
}

sms_status KeyDirectory::refresh_spill_after_unlink(
    std::int32_t canonical_bucket,
    std::uint64_t removed_binding,
    const OperationBudget& budget) noexcept {
    std::uint64_t witness{};
    std::int64_t witness_index{-1};
    auto status = find_overflow_witness(
        canonical_bucket, budget, witness, witness_index);
    if (status != SMS_STATUS_SUCCESS) return status;
    if (witness == 0) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::DirectoryAfterEmptySpillSummaryScan);
    }
    checkpoint(
        DirectoryCheckpoint::after_empty_overflow_scan,
        removed_binding,
        witness);

    std::uint64_t desired{};
    const auto encoded = witness == 0
        ? SpillSummary::try_encode_empty(removed_binding, desired)
        : SpillSummary::try_encode_present(witness, desired);
    if (!encoded) return SMS_STATUS_CORRUPT_STORE;
    auto* const word = spill_word(canonical_bucket);
    for (std::int32_t attempt = 0;; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        if (witness != 0 &&
            (witness_index < 0 ||
             MappedAtomic64::load_acquire(*overflow_word(witness_index)) !=
                 witness)) {
            status = find_overflow_witness(
                canonical_bucket, budget, witness, witness_index);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (!(witness == 0
                      ? SpillSummary::try_encode_empty(removed_binding, desired)
                      : SpillSummary::try_encode_present(witness, desired))) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        auto observed = MappedAtomic64::load_acquire(*word);
        SpillSummary summary{};
        if (!decode_summary_semantic(observed, summary)) {
            if (MappedAtomic64::load_acquire(*word) == observed) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        auto expected = observed;
        if (MappedAtomic64::compare_exchange(*word, expected, desired)) {
            if (witness != 0 &&
                MappedAtomic64::load_acquire(*overflow_word(witness_index)) !=
                    witness) {
                status = find_overflow_witness(
                    canonical_bucket, budget, witness, witness_index);
                if (status != SMS_STATUS_SUCCESS) return status;
                if (!(witness == 0
                          ? SpillSummary::try_encode_empty(
                                removed_binding, desired)
                          : SpillSummary::try_encode_present(
                                witness, desired))) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                continue;
            }
            if (witness == 0) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DirectoryAfterSpillSummaryClear);
            } else {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DirectoryAfterSpillSummaryPublication);
            }
            checkpoint(
                witness == 0
                    ? DirectoryCheckpoint::after_spill_empty_cas
                    : DirectoryCheckpoint::after_spill_present,
                removed_binding,
                desired);
            return SMS_STATUS_SUCCESS;
        }
    }
}

sms_status KeyDirectory::find_overflow_witness(
    std::int32_t canonical_bucket,
    const OperationBudget& budget,
    std::uint64_t& witness,
    std::int64_t& witness_index) noexcept {
    witness = 0;
    witness_index = -1;
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* const word = overflow_word(index);
        const auto raw = MappedAtomic64::load_acquire(*word);
        if (raw == 0) continue;
        BindingValidation validation{};
        auto status = validate_binding(raw, nullptr, {}, budget, validation);
        if (status != SMS_STATUS_SUCCESS) return status;
        if (validation == BindingValidation::stale) {
            bool changed{};
            status = clear_exact(*word, raw, changed);
            if (status != SMS_STATUS_SUCCESS) return status;
            continue;
        }
        if (validation == BindingValidation::invalid) {
            Cell cell{word, directory_target_overflow, index};
            bool remains{};
            status = revalidate_invalid_reference(
                cell, raw, nullptr, {}, budget, validation, remains);
            if (status != SMS_STATUS_SUCCESS) return status;
            if (remains && validation == BindingValidation::invalid) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }
        if (validation == BindingValidation::retry) return SMS_STATUS_STORE_BUSY;
        IndexBinding binding{};
        if (!decode_binding(raw, layout_.slot_count, binding)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto* const value_slot = slot(binding.slot_index);
        std::int32_t actual{};
        std::int32_t alternate{};
        buckets_for_hash(value_slot->KeyHash, actual, alternate);
        (void)alternate;
        if (actual == canonical_bucket &&
            MappedAtomic64::load_acquire(*word) == raw) {
            witness = raw;
            witness_index = index;
            return SMS_STATUS_SUCCESS;
        }
    }
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::clear_exact(
    std::uint64_t& word,
    std::uint64_t expected,
    bool& changed) noexcept {
    changed = false;
    if (!MappedAtomic64::is_aligned(&word)) return SMS_STATUS_CORRUPT_STORE;
    auto comparison = expected;
    changed = MappedAtomic64::compare_exchange(word, comparison, 0);
    return SMS_STATUS_SUCCESS;
}

sms_status KeyDirectory::read_valid_reference(
    std::uint64_t& word,
    std::uint64_t& observed) noexcept {
    for (std::int32_t attempt = 0; attempt < 8; ++attempt) {
        observed = MappedAtomic64::load_acquire(word);
        IndexBinding decoded{};
        if (observed == 0 ||
            decode_binding(observed, layout_.slot_count, decoded)) {
            return SMS_STATUS_SUCCESS;
        }
        checkpoint(
            DirectoryCheckpoint::after_invalid_reference_confirmation,
            observed,
            static_cast<std::uint64_t>(attempt));
        if (MappedAtomic64::load_acquire(word) == observed) {
            return SMS_STATUS_CORRUPT_STORE;
        }
    }
    observed = 0;
    return SMS_STATUS_STORE_BUSY;
}

bool KeyDirectory::try_get_cell(
    std::int32_t kind,
    std::int64_t index,
    Cell& cell) const noexcept {
    if (kind == directory_target_primary && index >= 0 &&
        index < layout_.primary_lane_count) {
        cell = Cell{primary_word(index), kind, index};
        return cell.word != nullptr;
    }
    if (kind == directory_target_overflow && index >= 0 &&
        index < layout_.slot_count) {
        cell = Cell{overflow_word(index), kind, index};
        return cell.word != nullptr;
    }
    cell = {};
    return false;
}

ValueSlotMetadataV2* KeyDirectory::slot(
    std::int32_t slot_index) const noexcept {
    if (!valid_ || slot_index < 0 || slot_index >= layout_.slot_count) {
        return nullptr;
    }
    const auto offset = layout_.slot_metadata_offset +
        static_cast<std::int64_t>(slot_index) * layout_.slot_metadata_stride;
    if (!range_valid(offset, sizeof(ValueSlotMetadataV2), mapping_length_)) {
        return nullptr;
    }
    return reinterpret_cast<ValueSlotMetadataV2*>(mapping_base_ + offset);
}

std::span<const std::byte> KeyDirectory::stored_key(
    const ValueSlotMetadataV2& value_slot,
    std::int32_t slot_index) const noexcept {
    if (slot_index < 0 || slot_index >= layout_.slot_count ||
        value_slot.KeyLength <= 0 || value_slot.KeyLength > layout_.max_key_bytes) {
        return {};
    }
    const auto expected_offset = layout_.key_storage_offset +
        static_cast<std::int64_t>(slot_index) * layout_.key_stride;
    if (value_slot.KeyOffset != expected_offset ||
        !range_valid(expected_offset, value_slot.KeyLength, mapping_length_) ||
        expected_offset < layout_.key_storage_offset ||
        value_slot.KeyLength >
            layout_.key_storage_offset + layout_.key_storage_length -
                expected_offset) {
        return {};
    }
    return {
        reinterpret_cast<const std::byte*>(mapping_base_ + expected_offset),
        static_cast<std::size_t>(value_slot.KeyLength)};
}

std::uint64_t* KeyDirectory::spill_word(std::int32_t bucket) const noexcept {
    if (!valid_ || bucket < 0 || bucket >= layout_.primary_bucket_count) {
        return nullptr;
    }
    const auto offset = layout_.primary_directory_offset +
        static_cast<std::int64_t>(bucket) * layout_.primary_bucket_stride;
    return reinterpret_cast<std::uint64_t*>(mapping_base_ + offset);
}

std::uint64_t* KeyDirectory::mutation_word(std::int32_t bucket) const noexcept {
    auto* const spill = spill_word(bucket);
    return spill == nullptr ? nullptr : spill + 1;
}

std::uint64_t* KeyDirectory::primary_word(
    std::int64_t absolute_index) const noexcept {
    if (!valid_ || absolute_index < 0 ||
        absolute_index >= layout_.primary_lane_count) {
        return nullptr;
    }
    const auto bucket = absolute_index / sms2_primary_lanes_per_bucket;
    const auto lane = absolute_index % sms2_primary_lanes_per_bucket;
    const auto offset = layout_.primary_directory_offset +
        bucket * layout_.primary_bucket_stride + 16 +
        lane * static_cast<std::int64_t>(sizeof(std::uint64_t));
    return reinterpret_cast<std::uint64_t*>(mapping_base_ + offset);
}

std::uint64_t* KeyDirectory::overflow_word(std::int64_t index) const noexcept {
    if (!valid_ || index < 0 || index >= layout_.slot_count) return nullptr;
    const auto offset = layout_.overflow_directory_offset +
        index * layout_.overflow_stride;
    return reinterpret_cast<std::uint64_t*>(mapping_base_ + offset);
}

bool KeyDirectory::decode_binding(
    std::uint64_t raw,
    std::int32_t slot_count,
    IndexBinding& binding) noexcept {
    return IndexBinding::try_decode(raw, binding) && binding.slot_index >= 0 &&
        binding.slot_index < slot_count && binding.generation >= 1 &&
        static_cast<std::uint64_t>(binding.generation) <=
            control_word_detail::slot_generation_mask;
}

bool KeyDirectory::decode_location_semantic(
    std::uint64_t raw,
    DirectoryLocation& location) noexcept {
    return raw != 0 && DirectoryLocation::try_decode(raw, location) &&
        (location.kind == directory_target_primary ||
         location.kind == directory_target_overflow);
}

bool KeyDirectory::decode_operation_semantic(
    std::uint64_t raw,
    DirectoryOperation& operation) noexcept {
    if (raw == 0 || !DirectoryOperation::try_decode(raw, operation) ||
        (operation.intent != directory_intent_insert &&
         operation.intent != directory_intent_unlink) ||
        operation.phase < directory_phase_prepared ||
        operation.phase > directory_phase_complete) {
        return false;
    }
    if (operation.phase == directory_phase_prepared) {
        return operation.target_kind == 0 && operation.target_index == 0;
    }
    if (operation.phase == directory_phase_rejected) {
        return operation.intent == directory_intent_insert &&
            operation.target_kind == 0 && operation.target_index == 0;
    }
    if (operation.phase == directory_phase_complete &&
        operation.intent == directory_intent_unlink &&
        operation.target_kind == 0) {
        return operation.target_index == 0;
    }
    return operation.target_kind == directory_target_primary ||
        operation.target_kind == directory_target_overflow;
}

bool KeyDirectory::decode_summary_semantic(
    std::uint64_t raw,
    SpillSummary& summary) const noexcept {
    return SpillSummary::try_decode(raw, summary) &&
        (summary.is_initial() ||
         (summary.slot_index >= 0 && summary.slot_index < layout_.slot_count));
}

std::uint64_t KeyDirectory::mix(std::uint64_t value) noexcept {
    value ^= value >> 30U;
    value *= 0xbf58'476d'1ce4'e5b9ULL;
    value ^= value >> 27U;
    value *= 0x94d0'49bb'1331'11ebULL;
    return value ^ (value >> 31U);
}

void KeyDirectory::checkpoint(
    DirectoryCheckpoint point,
    std::uint64_t binding,
    std::uint64_t detail) const noexcept {
    if (hooks_.reach != nullptr) {
        hooks_.reach(hooks_.context, point, binding, detail);
    }
}

} // namespace sms::detail
