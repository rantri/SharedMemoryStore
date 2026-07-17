#pragma once

#include "control_words.hpp"
#include "layout_v2.hpp"
#include "operation_budget.hpp"
#include "shared_memory_store/c_api.h"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <vector>

namespace sms::detail {

enum class SlotState : std::int32_t {
    free = 0,
    initializing = 1,
    reserved = 2,
    published = 3,
    remove_requested = 4,
    aborting = 5,
    reclaiming = 6,
    retired = 7
};

enum class SlotPublicationIntent : std::int32_t {
    none = 0,
    explicit_reservation = 1,
    atomic_publication = 2
};

// The slot layer consumes the exact participant incarnation but deliberately
// does not depend on ParticipantRegistry. Store orchestration obtains these two
// words during cold registration and supplies them to every hot-path module.
struct SlotParticipant {
    std::uint32_t token{};
    std::uint64_t active_control{};

    [[nodiscard]] bool valid(std::int32_t participant_count) const noexcept;
};

// Engine-internal generation fence for one reservation. The public C/C++
// wrappers retain ownership and lifetime; this value owns no mapped resource.
struct ReservationToken {
    std::uint64_t store_id{};
    std::uint32_t participant_token{};
    std::uint64_t slot_binding{};
    std::int32_t payload_length{};

    [[nodiscard]] bool valid() const noexcept {
        return store_id != 0 && participant_token != 0 && slot_binding != 0 &&
            payload_length >= 0;
    }
};

struct WritableReservationRange {
    std::int32_t slot_index{-1};
    std::int32_t offset{};
    std::int32_t length{};
};

class SlotTable {
public:
    static constexpr std::int64_t terminal_generation =
        static_cast<std::int64_t>(control_word_detail::slot_generation_mask);

    // Cold-create initialization. The region must already be exclusively
    // owned and zeroed; the release store of Control publishes immutable
    // per-slot storage offsets and generation-one Free state.
    [[nodiscard]] static sms_status initialize_mapping(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        const OperationBudget& budget) noexcept;

    SlotTable(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        std::uint64_t store_id,
        SlotParticipant participant);

    SlotTable(const SlotTable&) = delete;
    SlotTable& operator=(const SlotTable&) = delete;

    [[nodiscard]] bool valid() const noexcept;
    [[nodiscard]] bool locally_active() const noexcept;
    void invalidate_local() noexcept;

    [[nodiscard]] ValueSlotMetadataV2* slot(std::int32_t slot_index) const noexcept;

    [[nodiscard]] static bool try_classify_structural_control(
        std::uint64_t control,
        std::int32_t participant_count,
        bool& occupied) noexcept;

    // A scan-exhaustion result is an internal candidate. The caller exposes
    // StoreFull only after try_prove_store_full confirms an exact double
    // collect, matching the managed engine's public ordering contract.
    [[nodiscard]] sms_status try_claim_reservation(
        std::uint64_t key_hash,
        std::int32_t key_length,
        std::int32_t descriptor_length,
        std::int32_t payload_length,
        SlotPublicationIntent publication_intent,
        const OperationBudget& budget,
        ReservationToken& reservation) noexcept;

    [[nodiscard]] sms_status try_prove_store_full(
        const OperationBudget& budget,
        bool& proven_full) noexcept;

    // Directory insertion and key/descriptor writes occur while Initializing.
    // The directory module owns the Insert/Prepared metadata-ready marker;
    // this later exact CAS is the explicit-reservation ordering point.
    [[nodiscard]] sms_status mark_reserved(
        const ReservationToken& reservation) noexcept;

    [[nodiscard]] bool reservation_pending(
        const ReservationToken& reservation) const noexcept;
    [[nodiscard]] std::int32_t bytes_advanced(
        const ReservationToken& reservation) const noexcept;
    [[nodiscard]] bool try_get_writable_range(
        const ReservationToken& reservation,
        std::int32_t size_hint,
        WritableReservationRange& range) const noexcept;

    [[nodiscard]] sms_status advance_reservation(
        const ReservationToken& reservation,
        std::int32_t byte_count,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status commit_reservation(
        const ReservationToken& reservation,
        std::int64_t commit_sequence) noexcept;

    // Exact owner release is intentionally separate from physical completion:
    // after Aborting is visible, any participant may finish the generation.
    [[nodiscard]] sms_status try_begin_abort(
        const ReservationToken& reservation) noexcept;
    [[nodiscard]] sms_status complete_reclaim(
        std::uint64_t exact_binding,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] sms_status abort_reservation(
        const ReservationToken& reservation,
        const OperationBudget& budget) noexcept;

    [[nodiscard]] static bool try_advance_or_retire(
        std::int64_t generation,
        std::uint64_t& control) noexcept;

private:
    struct ReservationProjection {
        std::int32_t slot_index{-1};
        std::int64_t generation{};
        std::int32_t value_length{};
        std::uint64_t bytes_advanced{};
    };

    [[nodiscard]] sms_status owner_status() const noexcept;
    [[nodiscard]] bool try_decode_reservation(
        const ReservationToken& reservation,
        std::int32_t& slot_index,
        std::int64_t& generation) const noexcept;
    [[nodiscard]] sms_status reservation_status(
        std::uint64_t observed_control,
        std::int64_t expected_generation) const noexcept;
    [[nodiscard]] bool try_read_projection(
        const ReservationToken& reservation,
        ReservationProjection& projection,
        sms_status& failure) const noexcept;
    [[nodiscard]] sms_status sanitize_older_directory_residue(
        ValueSlotMetadataV2& slot,
        std::int64_t claimed_generation,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] bool has_advanced_or_retired(
        std::uint64_t control,
        std::int64_t generation) const noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    std::uint64_t store_id_{};
    SlotParticipant participant_{};
    std::vector<std::uint64_t> full_snapshot_;
    std::atomic<std::uint32_t> next_slot_{0};
    std::atomic<bool> local_active_{true};
    std::atomic_flag full_proof_gate_ = ATOMIC_FLAG_INIT;
};

} // namespace sms::detail
