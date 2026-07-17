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

enum class LeaseState : std::int32_t {
    free = 0,
    claiming = 1,
    active = 2,
    releasing = 3,
    recovering = 4,
    retired = 5
};

// Exact cold-registration output consumed by the lease state machine. Keeping
// this value-only seam avoids a hot dependency on participant-registry policy.
struct LeaseParticipant {
    std::uint32_t token{};
    std::uint64_t active_control{};

    [[nodiscard]] bool valid(std::int32_t participant_count) const noexcept;
};

// Generation-fenced local lease identity. It owns no mapping or OS resource;
// store wrappers retain the lifetime owner and use this token to revalidate
// every immutable projection.
struct LeaseToken {
    std::uint64_t store_id{};
    std::uint32_t participant_token{};
    std::uint64_t slot_binding{};
    std::uint64_t lease_binding{};

    [[nodiscard]] bool valid() const noexcept {
        return store_id != 0 && participant_token != 0 && slot_binding != 0 &&
            lease_binding != 0;
    }
};

class LeaseRegistry {
public:
    static constexpr std::int64_t terminal_incarnation =
        static_cast<std::int64_t>(control_word_detail::slot_generation_mask);

    [[nodiscard]] static sms_status initialize_mapping(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        const OperationBudget& budget) noexcept;

    LeaseRegistry(
        std::uint8_t* mapping_base,
        std::size_t mapping_length,
        const LayoutV2& layout,
        std::uint64_t store_id,
        LeaseParticipant participant);

    LeaseRegistry(const LeaseRegistry&) = delete;
    LeaseRegistry& operator=(const LeaseRegistry&) = delete;

    [[nodiscard]] bool valid() const noexcept;
    [[nodiscard]] bool locally_active() const noexcept;
    void invalidate_local() noexcept;

    [[nodiscard]] LeaseRecordV2* record(std::int32_t index) const noexcept;

    [[nodiscard]] static bool try_classify_structural_control(
        std::uint64_t control,
        std::int32_t participant_count,
        bool& occupied) noexcept;

    // Claim and activation remain separate. Active only protects the exact
    // generation; the store/directory must still revalidate its source word and
    // Published slot before a public lease or immutable bytes can escape.
    [[nodiscard]] sms_status try_claim(
        std::uint64_t slot_binding,
        std::int64_t acquire_sequence,
        const OperationBudget& budget,
        LeaseToken& lease) noexcept;
    [[nodiscard]] sms_status try_prove_lease_table_full(
        const OperationBudget& budget,
        bool& proven_full) noexcept;
    [[nodiscard]] sms_status try_activate(const LeaseToken& lease) noexcept;
    [[nodiscard]] sms_status try_cancel_claim(const LeaseToken& lease) noexcept;

    // This is the registry half of immutable projection validation. The caller
    // surrounds slot metadata with its own Published/RemoveRequested and exact
    // directory-source revalidation before constructing a borrowed span.
    [[nodiscard]] bool try_get_active_slot_binding(
        const LeaseToken& lease,
        std::uint64_t& slot_binding) const noexcept;
    [[nodiscard]] bool is_active(const LeaseToken& lease) const noexcept;

    // Public projection lifetime ends at the exact Active -> Releasing CAS.
    // Recycling afterward is helpable and never performs an ordinary write.
    [[nodiscard]] sms_status try_release(const LeaseToken& lease) noexcept;

    [[nodiscard]] sms_status scan_has_active_lease(
        std::uint64_t slot_binding,
        const OperationBudget& budget,
        bool& has_active_lease) const noexcept;

    [[nodiscard]] static bool try_advance_or_retire(
        std::int64_t incarnation,
        std::uint64_t& control) noexcept;

private:
    [[nodiscard]] sms_status owner_status() const noexcept;
    [[nodiscard]] bool valid_slot_binding(std::uint64_t binding) const noexcept;
    [[nodiscard]] bool try_decode_lease(
        const LeaseToken& lease,
        std::int32_t& record_index,
        std::int64_t& incarnation) const noexcept;
    [[nodiscard]] sms_status lease_status(
        std::uint64_t observed_control,
        std::int64_t expected_incarnation) const noexcept;
    [[nodiscard]] sms_status try_recycle(
        std::int32_t record_index,
        std::int64_t incarnation,
        std::uint64_t expected_transition,
        bool& recycled) noexcept;

    std::uint8_t* mapping_base_{};
    std::size_t mapping_length_{};
    LayoutV2 layout_{};
    std::uint64_t store_id_{};
    LeaseParticipant participant_{};
    std::vector<std::uint64_t> full_snapshot_;
    std::atomic<bool> local_active_{true};
    std::atomic_flag full_proof_gate_ = ATOMIC_FLAG_INIT;
};

} // namespace sms::detail
