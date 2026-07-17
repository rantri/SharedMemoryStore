#include "lease_registry.hpp"

#include "checkpoint.hpp"
#include "mapped_atomic.hpp"

#include <atomic>
#include <limits>

namespace sms::detail {
namespace {

constexpr std::int32_t participant_active_state = 2;
constexpr std::int32_t recycle_confirmation_attempts = 8;

template <class T>
[[nodiscard]] T metadata_load(T& location) noexcept {
    return std::atomic_ref<T>(location).load(std::memory_order_acquire);
}

template <class T>
void metadata_store(T& location, T value) noexcept {
    std::atomic_ref<T>(location).store(value, std::memory_order_release);
}

static_assert(std::atomic_ref<std::int64_t>::is_always_lock_free);

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
        layout.lease_record_count < 1 ||
        layout.lease_record_count == std::numeric_limits<std::int32_t>::max() ||
        layout.slot_count < 1 || layout.slot_count > sms2_maximum_slot_count ||
        layout.participant_record_count < 1 ||
        layout.participant_record_count > sms2_maximum_participant_count ||
        layout.participant_generation_mask < 1 ||
        layout.participant_stride != sms2_participant_stride ||
        layout.lease_stride != sms2_lease_stride || layout.required_bytes < 0 ||
        static_cast<std::uint64_t>(layout.required_bytes) > mapping_length ||
        !product_equals(
            layout.participant_record_count,
            layout.participant_stride,
            layout.participant_length) ||
        !product_equals(
            layout.lease_record_count,
            layout.lease_stride,
            layout.lease_registry_length) ||
        !range_valid(
            layout.participant_offset, layout.participant_length, mapping_length) ||
        !range_valid(
            layout.lease_registry_offset,
            layout.lease_registry_length,
            mapping_length)) {
        return false;
    }
    return MappedAtomic64::is_aligned(mapping_base + layout.participant_offset) &&
        MappedAtomic64::is_aligned(mapping_base + layout.lease_registry_offset);
}

[[nodiscard]] bool encode_lease_control(
    LeaseState state,
    std::int64_t incarnation,
    std::uint32_t participant_token,
    std::uint64_t& control) noexcept {
    return LeaseControl::try_encode(
        static_cast<std::int32_t>(state),
        incarnation,
        participant_token,
        control);
}

} // namespace

bool LeaseParticipant::valid(std::int32_t participant_count) const noexcept {
    ParticipantToken decoded_token{};
    ParticipantControl decoded_control{};
    return token != 0 && active_control != 0 &&
        ParticipantToken::try_decode(token, participant_count, decoded_token) &&
        ParticipantControl::try_decode(active_control, decoded_control) &&
        decoded_control.state == participant_active_state &&
        decoded_control.incarnation == decoded_token.generation &&
        decoded_control.process_id > 0;
}

sms_status LeaseRegistry::initialize_mapping(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    const OperationBudget& budget) noexcept {
    if (!mapping_shape_valid(mapping_base, mapping_length, layout)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    std::uint64_t free_control{};
    if (!encode_lease_control(LeaseState::free, 1, 0, free_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t index = 0; index < layout.lease_record_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<LeaseRecordV2*>(
            mapping_base + layout.lease_registry_offset +
            static_cast<std::int64_t>(index) * layout.lease_stride);
        *current = LeaseRecordV2{};
        MappedAtomic64::store_release(current->Control, free_control);
    }
    return SMS_STATUS_SUCCESS;
}

LeaseRegistry::LeaseRegistry(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    std::uint64_t store_id,
    LeaseParticipant participant)
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout),
      store_id_(store_id),
      participant_(participant),
      full_snapshot_(layout.lease_record_count > 0
          ? static_cast<std::size_t>(layout.lease_record_count)
          : 0U) {}

bool LeaseRegistry::valid() const noexcept {
    return store_id_ != 0 && participant_.valid(layout_.participant_record_count) &&
        full_snapshot_.size() ==
            static_cast<std::size_t>(layout_.lease_record_count) &&
        mapping_shape_valid(mapping_base_, mapping_length_, layout_);
}

bool LeaseRegistry::locally_active() const noexcept {
    return local_active_.load(std::memory_order_acquire);
}

void LeaseRegistry::invalidate_local() noexcept {
    local_active_.store(false, std::memory_order_release);
}

LeaseRecordV2* LeaseRegistry::record(std::int32_t index) const noexcept {
    if (!mapping_shape_valid(mapping_base_, mapping_length_, layout_) ||
        index < 0 || index >= layout_.lease_record_count) {
        return nullptr;
    }
    return reinterpret_cast<LeaseRecordV2*>(
        mapping_base_ + layout_.lease_registry_offset +
        static_cast<std::int64_t>(index) * layout_.lease_stride);
}

bool LeaseRegistry::try_classify_structural_control(
    std::uint64_t control,
    std::int32_t participant_count,
    bool& occupied) noexcept {
    return LeaseControl{control}.structurally_valid(participant_count, occupied);
}

sms_status LeaseRegistry::owner_status() const noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    ParticipantToken token{};
    if (!ParticipantToken::try_decode(
            participant_.token, layout_.participant_record_count, token)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* participant_record = reinterpret_cast<ParticipantRecordV2*>(
        mapping_base_ + layout_.participant_offset +
        static_cast<std::int64_t>(token.record_index) * layout_.participant_stride);
    const auto observed = MappedAtomic64::load_acquire(participant_record->Control);
    ParticipantControl control{observed};
    if (!control.structurally_valid(layout_.participant_generation_mask)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return observed == participant_.active_control
        ? SMS_STATUS_SUCCESS
        : SMS_STATUS_STORE_DISPOSED;
}

bool LeaseRegistry::valid_slot_binding(std::uint64_t binding) const noexcept {
    IndexBinding decoded{};
    return IndexBinding::try_decode(binding, decoded) && decoded.slot_index >= 0 &&
        decoded.slot_index < layout_.slot_count && decoded.generation >= 1 &&
        decoded.generation <= terminal_incarnation;
}

bool LeaseRegistry::try_decode_lease(
    const LeaseToken& lease,
    std::int32_t& record_index,
    std::int64_t& incarnation) const noexcept {
    record_index = -1;
    incarnation = 0;
    if (lease.store_id != store_id_ ||
        lease.participant_token != participant_.token ||
        !valid_slot_binding(lease.slot_binding) || lease.lease_binding == 0) {
        return false;
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lease.lease_binding, binding) ||
        binding.slot_index < 0 || binding.slot_index >= layout_.lease_record_count ||
        binding.generation < 1 || binding.generation > terminal_incarnation) {
        return false;
    }
    record_index = binding.slot_index;
    incarnation = binding.generation;
    return true;
}

sms_status LeaseRegistry::lease_status(
    std::uint64_t observed_control,
    std::int64_t expected_incarnation) const noexcept {
    bool occupied{};
    if (!try_classify_structural_control(
            observed_control, layout_.participant_record_count, occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    LeaseControl decoded{};
    if (!LeaseControl::try_decode(observed_control, decoded) ||
        decoded.generation != expected_incarnation) {
        return SMS_STATUS_INVALID_LEASE;
    }
    return decoded.state == static_cast<std::int32_t>(LeaseState::free) ||
        decoded.state == static_cast<std::int32_t>(LeaseState::releasing) ||
        decoded.state == static_cast<std::int32_t>(LeaseState::recovering)
        ? SMS_STATUS_LEASE_ALREADY_RELEASED
        : SMS_STATUS_INVALID_LEASE;
}

sms_status LeaseRegistry::try_recycle(
    std::int32_t record_index,
    std::int64_t incarnation,
    std::uint64_t expected_transition,
    bool& recycled) noexcept {
    recycled = false;
    auto* current = record(record_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t terminal{};
    if (!try_advance_or_retire(incarnation, terminal)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t attempt = 0;
         attempt < recycle_confirmation_attempts;
         ++attempt) {
        auto expected = expected_transition;
        if (MappedAtomic64::compare_exchange(
                current->Control, expected, terminal)) {
            recycled = true;
            return SMS_STATUS_SUCCESS;
        }
        bool occupied{};
        if (!try_classify_structural_control(
                expected, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        LeaseControl decoded{};
        (void)LeaseControl::try_decode(expected, decoded);
        if (expected == terminal || decoded.generation > incarnation) {
            return SMS_STATUS_SUCCESS;
        }

        // Releasing/Recovering has one legal successor. Confirm a stable
        // lateral or regressed word before classifying persistent corruption.
        auto stable = expected;
        if (MappedAtomic64::compare_exchange(current->Control, stable, expected)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (!try_classify_structural_control(
                stable, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status LeaseRegistry::try_claim(
    std::uint64_t slot_binding,
    std::int64_t acquire_sequence,
    const OperationBudget& budget,
    LeaseToken& lease) noexcept {
    lease = {};
    auto active = owner_status();
    if (active != SMS_STATUS_SUCCESS) return active;
    if (!valid_slot_binding(slot_binding)) return SMS_STATUS_CORRUPT_STORE;

    ParticipantToken participant_token{};
    if (!ParticipantToken::try_decode(
            participant_.token,
            layout_.participant_record_count,
            participant_token)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    std::int32_t capacity_attempt = 0;
    for (;;) {
        if (capacity_attempt != 0) {
            active = owner_status();
            if (active != SMS_STATUS_SUCCESS) return active;
        }
        const auto start = participant_token.record_index % layout_.lease_record_count;
        for (std::int32_t visited = 0;
             visited < layout_.lease_record_count;
             ++visited) {
            const auto bound = budget.check_periodic(visited);
            if (bound != SMS_STATUS_SUCCESS) return bound;
            auto index = start + visited;
            if (index >= layout_.lease_record_count) index -= layout_.lease_record_count;
            auto* current = record(index);
            if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
            auto observed = MappedAtomic64::load_acquire(current->Control);
            bool occupied{};
            if (!try_classify_structural_control(
                    observed, layout_.participant_record_count, occupied)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            LeaseControl decoded{};
            (void)LeaseControl::try_decode(observed, decoded);
            if ((decoded.state == static_cast<std::int32_t>(LeaseState::releasing) ||
                    decoded.state == static_cast<std::int32_t>(LeaseState::recovering)) &&
                decoded.participant_token == 0) {
                bool recycled{};
                const auto recycle = try_recycle(
                    index, decoded.generation, observed, recycled);
                if (recycle != SMS_STATUS_SUCCESS) return recycle;
                observed = MappedAtomic64::load_acquire(current->Control);
                if (!try_classify_structural_control(
                        observed, layout_.participant_record_count, occupied)) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                (void)LeaseControl::try_decode(observed, decoded);
            }
            if (decoded.state != static_cast<std::int32_t>(LeaseState::free)) continue;

            std::uint64_t claiming{};
            if (!encode_lease_control(
                    LeaseState::claiming,
                    decoded.generation,
                    participant_.token,
                    claiming)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            auto expected = observed;
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::AcquireBeforeLeaseClaimCas);
            if (!MappedAtomic64::compare_exchange(
                    current->Control, expected, claiming)) {
                if (!try_classify_structural_control(
                        expected, layout_.participant_record_count, occupied)) {
                    return SMS_STATUS_CORRUPT_STORE;
                }
                continue;
            }

            std::uint64_t lease_binding{};
            if (!IndexBinding::try_encode(index, decoded.generation, lease_binding)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            lease = LeaseToken{
                store_id_, participant_.token, slot_binding, lease_binding};
            active = owner_status();
            if (active != SMS_STATUS_SUCCESS) {
                const auto cancel = active == SMS_STATUS_CORRUPT_STORE
                    ? active
                    : try_cancel_claim(lease);
                lease = {};
                return active == SMS_STATUS_CORRUPT_STORE ||
                    cancel == SMS_STATUS_CORRUPT_STORE
                    ? SMS_STATUS_CORRUPT_STORE
                    : SMS_STATUS_STORE_DISPOSED;
            }
            MappedAtomic64::store_release(current->SlotBinding, slot_binding);
            metadata_store(current->AcquireSequence, acquire_sequence);
            return SMS_STATUS_SUCCESS;
        }

        bool proven_full{};
        const auto proof = try_prove_lease_table_full(budget, proven_full);
        if (proof != SMS_STATUS_SUCCESS) return proof;
        if (proven_full) return SMS_STATUS_LEASE_TABLE_FULL;
        sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
        if (!budget.try_continue_after_contention(capacity_attempt, terminal)) {
            return terminal;
        }
        if (capacity_attempt < std::numeric_limits<std::int32_t>::max()) {
            ++capacity_attempt;
        }
    }
}

sms_status LeaseRegistry::try_prove_lease_table_full(
    const OperationBudget& budget,
    bool& proven_full) noexcept {
    proven_full = false;
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    if (full_proof_gate_.test_and_set(std::memory_order_acquire)) {
        return SMS_STATUS_SUCCESS;
    }
    struct GateRelease {
        std::atomic_flag& gate;
        ~GateRelease() { gate.clear(std::memory_order_release); }
    } release{full_proof_gate_};

    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = record(index);
        if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto control = MappedAtomic64::load_acquire(current->Control);
        bool occupied{};
        if (!try_classify_structural_control(
                control, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (!occupied) return SMS_STATUS_SUCCESS;
        full_snapshot_[static_cast<std::size_t>(index)] = control;
    }
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = record(index);
        if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto control = MappedAtomic64::load_acquire(current->Control);
        bool occupied{};
        if (!try_classify_structural_control(
                control, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (!occupied || control != full_snapshot_[static_cast<std::size_t>(index)]) {
            return SMS_STATUS_SUCCESS;
        }
    }
    proven_full = true;
    return SMS_STATUS_SUCCESS;
}

sms_status LeaseRegistry::try_activate(const LeaseToken& lease) noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    std::int32_t index{};
    std::int64_t incarnation{};
    if (!try_decode_lease(lease, index, incarnation)) {
        return SMS_STATUS_INVALID_LEASE;
    }
    auto* current = record(index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t claiming{};
    std::uint64_t active{};
    if (!encode_lease_control(
            LeaseState::claiming,
            incarnation,
            lease.participant_token,
            claiming) ||
        !encode_lease_control(
            LeaseState::active,
            incarnation,
            lease.participant_token,
            active)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    const auto observed = MappedAtomic64::load_acquire(current->Control);
    bool occupied{};
    if (!try_classify_structural_control(
            observed, layout_.participant_record_count, occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (observed != claiming ||
        MappedAtomic64::load_acquire(current->SlotBinding) != lease.slot_binding) {
        const auto cancel = try_cancel_claim(lease);
        return cancel == SMS_STATUS_CORRUPT_STORE
            ? SMS_STATUS_CORRUPT_STORE
            : SMS_STATUS_INVALID_LEASE;
    }
    auto participant_status = owner_status();
    if (participant_status != SMS_STATUS_SUCCESS) {
        const auto cancel = participant_status == SMS_STATUS_CORRUPT_STORE
            ? participant_status
            : try_cancel_claim(lease);
        return cancel == SMS_STATUS_CORRUPT_STORE
            ? SMS_STATUS_CORRUPT_STORE
            : participant_status;
    }
    auto expected = claiming;
    if (!MappedAtomic64::compare_exchange(current->Control, expected, active)) {
        return lease_status(expected, incarnation);
    }
    participant_status = owner_status();
    if (participant_status == SMS_STATUS_SUCCESS) return SMS_STATUS_SUCCESS;
    if (participant_status == SMS_STATUS_CORRUPT_STORE) {
        return SMS_STATUS_CORRUPT_STORE;
    }

    std::uint64_t recovering{};
    if (!encode_lease_control(LeaseState::recovering, incarnation, 0, recovering)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    expected = active;
    if (MappedAtomic64::compare_exchange(current->Control, expected, recovering)) {
        bool recycled{};
        (void)try_recycle(index, incarnation, recovering, recycled);
    } else if (!try_classify_structural_control(
                   expected, layout_.participant_record_count, occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return participant_status;
}

sms_status LeaseRegistry::try_cancel_claim(const LeaseToken& lease) noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    std::int32_t index{};
    std::int64_t incarnation{};
    if (!try_decode_lease(lease, index, incarnation)) {
        return SMS_STATUS_INVALID_LEASE;
    }
    auto* current = record(index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t claiming{};
    std::uint64_t recovering{};
    if (!encode_lease_control(
            LeaseState::claiming,
            incarnation,
            lease.participant_token,
            claiming) ||
        !encode_lease_control(LeaseState::recovering, incarnation, 0, recovering)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = claiming;
    const auto changed = MappedAtomic64::compare_exchange(
        current->Control, expected, recovering);
    bool occupied{};
    if (!try_classify_structural_control(
            changed ? claiming : expected,
            layout_.participant_record_count,
            occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (changed || expected == recovering) {
        bool recycled{};
        (void)try_recycle(index, incarnation, recovering, recycled);
        return SMS_STATUS_SUCCESS;
    }
    return lease_status(expected, incarnation);
}

bool LeaseRegistry::try_get_active_slot_binding(
    const LeaseToken& lease,
    std::uint64_t& slot_binding) const noexcept {
    slot_binding = 0;
    if (owner_status() != SMS_STATUS_SUCCESS) return false;
    std::int32_t index{};
    std::int64_t incarnation{};
    if (!try_decode_lease(lease, index, incarnation)) return false;
    auto* current = record(index);
    if (current == nullptr) return false;
    std::uint64_t active{};
    if (!encode_lease_control(
            LeaseState::active,
            incarnation,
            lease.participant_token,
            active)) {
        return false;
    }
    const auto control1 = MappedAtomic64::load_acquire(current->Control);
    bool occupied{};
    if (!try_classify_structural_control(
            control1, layout_.participant_record_count, occupied) ||
        control1 != active) {
        return false;
    }
    const auto observed_binding =
        MappedAtomic64::load_acquire(current->SlotBinding);
    const auto control2 = MappedAtomic64::load_acquire(current->Control);
    if (!try_classify_structural_control(
            control2, layout_.participant_record_count, occupied) ||
        control2 != control1 || !valid_slot_binding(observed_binding) ||
        observed_binding != lease.slot_binding) {
        return false;
    }
    if (owner_status() != SMS_STATUS_SUCCESS ||
        MappedAtomic64::load_acquire(current->Control) != control1) {
        return false;
    }
    slot_binding = observed_binding;
    return true;
}

bool LeaseRegistry::is_active(const LeaseToken& lease) const noexcept {
    std::uint64_t slot_binding{};
    return try_get_active_slot_binding(lease, slot_binding);
}

sms_status LeaseRegistry::try_release(const LeaseToken& lease) noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    std::int32_t index{};
    std::int64_t incarnation{};
    if (!try_decode_lease(lease, index, incarnation)) {
        return SMS_STATUS_INVALID_LEASE;
    }
    auto* current = record(index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t active{};
    std::uint64_t releasing{};
    if (!encode_lease_control(
            LeaseState::active,
            incarnation,
            lease.participant_token,
            active) ||
        !encode_lease_control(LeaseState::releasing, incarnation, 0, releasing)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t attempt = 0;
         attempt < recycle_confirmation_attempts;
         ++attempt) {
        auto expected = active;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ReleaseBeforeActiveReleaseCas);
        if (MappedAtomic64::compare_exchange(
                current->Control, expected, releasing)) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::ReleaseAfterOwnershipReleaseCas);
            bool recycled{};
            const auto recycle = try_recycle(
                index, incarnation, releasing, recycled);
            if (recycle == SMS_STATUS_SUCCESS) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::ReleaseAfterRecordRecycle);
            }
            return SMS_STATUS_SUCCESS;
        }
        bool occupied{};
        if (!try_classify_structural_control(
                expected, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        LeaseControl decoded{};
        (void)LeaseControl::try_decode(expected, decoded);
        if (decoded.generation == incarnation &&
            (decoded.state == static_cast<std::int32_t>(LeaseState::releasing) ||
                decoded.state == static_cast<std::int32_t>(LeaseState::recovering))) {
            bool recycled{};
            const auto result = try_recycle(
                index, incarnation, expected, recycled);
            if (result == SMS_STATUS_SUCCESS) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::ReleaseAfterRecordRecycle);
            }
            return result == SMS_STATUS_SUCCESS
                ? SMS_STATUS_LEASE_ALREADY_RELEASED
                : result;
        }
        if ((incarnation < terminal_incarnation &&
                decoded.state == static_cast<std::int32_t>(LeaseState::free) &&
                decoded.generation == incarnation + 1) ||
            (decoded.state == static_cast<std::int32_t>(LeaseState::retired) &&
                decoded.generation == incarnation)) {
            return SMS_STATUS_LEASE_ALREADY_RELEASED;
        }
        if (decoded.generation > incarnation) return SMS_STATUS_INVALID_LEASE;

        auto stable = expected;
        if (MappedAtomic64::compare_exchange(current->Control, stable, expected)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (!try_classify_structural_control(
                stable, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status LeaseRegistry::scan_has_active_lease(
    std::uint64_t slot_binding,
    const OperationBudget& budget,
    bool& has_active_lease) const noexcept {
    has_active_lease = false;
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid() || !valid_slot_binding(slot_binding)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = record(index);
        if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
        const auto control1 = MappedAtomic64::load_acquire(current->Control);
        bool occupied{};
        if (!try_classify_structural_control(
                control1, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        LeaseControl decoded{};
        (void)LeaseControl::try_decode(control1, decoded);
        if (decoded.state != static_cast<std::int32_t>(LeaseState::active)) continue;
        const auto observed_binding =
            MappedAtomic64::load_acquire(current->SlotBinding);
        const auto control2 = MappedAtomic64::load_acquire(current->Control);
        if (!try_classify_structural_control(
                control2, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (control1 != control2) continue;
        if (!valid_slot_binding(observed_binding)) return SMS_STATUS_CORRUPT_STORE;
        if (observed_binding == slot_binding) {
            has_active_lease = true;
            return SMS_STATUS_SUCCESS;
        }
    }
    return SMS_STATUS_SUCCESS;
}

bool LeaseRegistry::try_advance_or_retire(
    std::int64_t incarnation,
    std::uint64_t& control) noexcept {
    if (incarnation < 1 || incarnation > terminal_incarnation) return false;
    return incarnation == terminal_incarnation
        ? encode_lease_control(LeaseState::retired, incarnation, 0, control)
        : encode_lease_control(LeaseState::free, incarnation + 1, 0, control);
}

} // namespace sms::detail
