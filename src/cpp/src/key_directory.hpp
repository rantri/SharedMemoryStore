#pragma once

#include "control_words.hpp"
#include "layout_v2.hpp"
#include "operation_budget.hpp"
#include "shared_memory_store/c_api.h"

#include <cstddef>
#include <cstdint>
#include <span>

namespace sms::detail {

inline constexpr std::int32_t directory_target_primary = 1;
inline constexpr std::int32_t directory_target_overflow = 2;

inline constexpr std::int32_t directory_intent_insert = 1;
inline constexpr std::int32_t directory_intent_unlink = 2;

inline constexpr std::int32_t directory_phase_prepared = 1;
inline constexpr std::int32_t directory_phase_target_selected = 2;
inline constexpr std::int32_t directory_phase_binding_changed = 3;
inline constexpr std::int32_t directory_phase_rejected = 4;
inline constexpr std::int32_t directory_phase_complete = 5;

// Deterministic white-box schedule points. Production construction normally
// leaves DirectoryHooks empty; tests and fault agents may observe or pause an
// operation without adding an operation lock to the directory itself.
enum class DirectoryCheckpoint : std::int32_t {
    after_invalid_reference_confirmation = 1,
    after_insert_prepared = 2,
    after_mutation_claimed = 3,
    after_duplicate_scan = 4,
    after_target_selected = 5,
    before_target_binding_cas = 6,
    after_target_binding_cas = 7,
    before_source_revalidation = 8,
    before_location_arbitration = 9,
    after_location_arbitration = 10,
    before_spill_present_cas = 11,
    after_spill_present = 12,
    after_empty_overflow_scan = 13,
    after_spill_empty_cas = 14,
    after_unlink_target_selected = 15,
    after_unlink_binding_clear = 16,
    before_mutation_release = 17,
    before_reserved_publication = 18,
    after_reserved_publication = 19,
};

struct DirectoryHooks {
    using callback = void (*)(
        void* context,
        DirectoryCheckpoint checkpoint,
        std::uint64_t binding,
        std::uint64_t detail) noexcept;

    void* context{};
    callback reach{};
};

struct DirectoryEntry {
    std::uint64_t binding{};
    DirectoryLocation location{};

    [[nodiscard]] bool valid() const noexcept {
        return binding != 0 && location.value != 0;
    }
};

// Lock-free SMS2 key directory. All shared coordination is performed through
// naturally aligned mapped 64-bit words. The class owns no mapping, mutex,
// process-shared lock, or process-local operation gate.
class KeyDirectory {
public:
    static constexpr std::int32_t default_retry_budget = 128;

    KeyDirectory(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        DirectoryHooks hooks = {}) noexcept;

    KeyDirectory(const KeyDirectory&) = delete;
    KeyDirectory& operator=(const KeyDirectory&) = delete;

    [[nodiscard]] bool valid() const noexcept { return valid_; }

    [[nodiscard]] sms_status try_lookup(
        std::span<const std::byte> key,
        std::uint64_t key_hash,
        const OperationBudget& budget,
        DirectoryEntry& entry) noexcept;

    [[nodiscard]] sms_status confirm_exact_reference(
        const DirectoryLocation& location,
        std::uint64_t exact_binding,
        bool& remains_exact) noexcept;

    [[nodiscard]] sms_status try_insert(
        std::span<const std::byte> key,
        std::uint64_t key_hash,
        std::uint64_t candidate_binding,
        const OperationBudget& budget,
        DirectoryLocation& location) noexcept;

    [[nodiscard]] sms_status try_unlink(
        std::uint64_t exact_binding,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] sms_status help_mutation(
        std::int32_t canonical_bucket,
        const OperationBudget& budget,
        std::int32_t max_steps = default_retry_budget) noexcept;

    [[nodiscard]] sms_status contains_exact_reference(
        std::uint64_t exact_binding,
        const OperationBudget& budget,
        bool& contains) noexcept;

    [[nodiscard]] std::uint64_t read_spill_summary(
        std::int32_t canonical_bucket) const noexcept;
    [[nodiscard]] std::uint64_t read_mutation(
        std::int32_t canonical_bucket) const noexcept;

    void buckets_for_hash(
        std::uint64_t hash,
        std::int32_t& canonical,
        std::int32_t& alternate) const noexcept;
    [[nodiscard]] std::int32_t overflow_start_for_hash(
        std::uint64_t hash) const noexcept;

private:
    enum class BindingValidation : std::int32_t {
        exact,
        current_other,
        stale,
        invalid,
        retry,
    };

    enum class ControlBindingStatus : std::int32_t {
        current,
        stale,
        invalid,
    };

    struct Cell {
        std::uint64_t* word{};
        std::int32_t kind{};
        std::int64_t index{};
    };

    [[nodiscard]] sms_status find_exact(
        std::span<const std::byte> key,
        std::uint64_t key_hash,
        std::uint64_t excluded_binding,
        const OperationBudget& budget,
        DirectoryEntry& entry) noexcept;
    [[nodiscard]] sms_status scan_primary_bucket(
        std::int32_t bucket,
        std::span<const std::byte> key,
        std::uint64_t key_hash,
        std::uint64_t excluded_binding,
        const OperationBudget& budget,
        DirectoryEntry& entry) noexcept;
    [[nodiscard]] sms_status inspect_cell(
        Cell cell,
        std::span<const std::byte> key,
        std::uint64_t key_hash,
        std::uint64_t excluded_binding,
        const OperationBudget& budget,
        DirectoryEntry& entry) noexcept;

    [[nodiscard]] sms_status validate_binding(
        std::uint64_t raw,
        const std::uint64_t* expected_hash,
        std::span<const std::byte> expected_key,
        const OperationBudget& budget,
        BindingValidation& validation) noexcept;
    [[nodiscard]] sms_status revalidate_invalid_reference(
        Cell cell,
        std::uint64_t expected_reference,
        const std::uint64_t* expected_hash,
        std::span<const std::byte> expected_key,
        const OperationBudget& budget,
        BindingValidation& validation,
        bool& remains_exact) noexcept;
    [[nodiscard]] ControlBindingStatus classify_control(
        std::uint64_t control,
        std::int64_t expected_generation) const noexcept;
    [[nodiscard]] sms_status keys_equal(
        std::span<const std::byte> stored,
        std::span<const std::byte> expected,
        const OperationBudget& budget,
        bool& equal) const noexcept;

    [[nodiscard]] sms_status prepare_operation(
        ValueSlotMetadataV2& slot,
        std::uint64_t binding,
        std::int32_t intent,
        std::int64_t generation,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status publish_reserved(
        ValueSlotMetadataV2& slot,
        std::uint64_t binding) noexcept;
    [[nodiscard]] sms_status claim_mutation(
        std::int32_t canonical_bucket,
        std::uint64_t binding,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status help_insert(
        std::int32_t canonical_bucket,
        std::uint64_t binding,
        IndexBinding decoded,
        ValueSlotMetadataV2& slot,
        std::uint64_t operation_raw,
        DirectoryOperation operation,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status help_unlink(
        std::int32_t canonical_bucket,
        std::uint64_t binding,
        IndexBinding decoded,
        ValueSlotMetadataV2& slot,
        std::uint64_t operation_raw,
        DirectoryOperation operation,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] sms_status select_insert_target(
        std::uint64_t key_hash,
        std::int64_t generation,
        const OperationBudget& budget,
        DirectoryLocation& target) noexcept;
    [[nodiscard]] sms_status select_primary_target(
        std::int32_t bucket,
        std::int64_t generation,
        const OperationBudget& budget,
        DirectoryLocation& target) noexcept;
    [[nodiscard]] sms_status arbitrate_location(
        std::int32_t canonical_bucket,
        std::uint64_t binding,
        std::uint64_t operation_raw,
        const DirectoryLocation& proposed,
        ValueSlotMetadataV2& slot,
        DirectoryLocation& winner) noexcept;
    [[nodiscard]] sms_status find_first_binding_location(
        std::uint64_t binding,
        DirectoryLocation& location) noexcept;
    [[nodiscard]] sms_status clear_alternate_references(
        std::uint64_t binding,
        const DirectoryLocation* retained,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] sms_status publish_spill_present(
        std::int32_t canonical_bucket,
        std::uint64_t binding,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status refresh_spill_after_unlink(
        std::int32_t canonical_bucket,
        std::uint64_t removed_binding,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status find_overflow_witness(
        std::int32_t canonical_bucket,
        const OperationBudget& budget,
        std::uint64_t& witness,
        std::int64_t& witness_index) noexcept;

    [[nodiscard]] sms_status clear_exact(
        std::uint64_t& word,
        std::uint64_t expected,
        bool& changed) noexcept;
    [[nodiscard]] sms_status read_valid_reference(
        std::uint64_t& word,
        std::uint64_t& observed) noexcept;
    [[nodiscard]] bool try_get_cell(
        std::int32_t kind,
        std::int64_t index,
        Cell& cell) const noexcept;
    [[nodiscard]] ValueSlotMetadataV2* slot(
        std::int32_t slot_index) const noexcept;
    [[nodiscard]] std::span<const std::byte> stored_key(
        const ValueSlotMetadataV2& slot,
        std::int32_t slot_index) const noexcept;
    [[nodiscard]] std::uint64_t* spill_word(
        std::int32_t bucket) const noexcept;
    [[nodiscard]] std::uint64_t* mutation_word(
        std::int32_t bucket) const noexcept;
    [[nodiscard]] std::uint64_t* primary_word(
        std::int64_t absolute_index) const noexcept;
    [[nodiscard]] std::uint64_t* overflow_word(
        std::int64_t index) const noexcept;

    [[nodiscard]] static bool decode_binding(
        std::uint64_t raw,
        std::int32_t slot_count,
        IndexBinding& binding) noexcept;
    [[nodiscard]] static bool decode_location_semantic(
        std::uint64_t raw,
        DirectoryLocation& location) noexcept;
    [[nodiscard]] static bool decode_operation_semantic(
        std::uint64_t raw,
        DirectoryOperation& operation) noexcept;
    [[nodiscard]] bool decode_summary_semantic(
        std::uint64_t raw,
        SpillSummary& summary) const noexcept;
    [[nodiscard]] static std::uint64_t mix(std::uint64_t value) noexcept;

    void checkpoint(
        DirectoryCheckpoint point,
        std::uint64_t binding,
        std::uint64_t detail = 0) const noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    DirectoryHooks hooks_{};
    std::int32_t bucket_mask_{};
    bool valid_{};
};

} // namespace sms::detail
