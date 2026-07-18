#include "slot_table.hpp"

#include "checkpoint.hpp"
#include "mapped_atomic.hpp"

#include <algorithm>
#include <atomic>
#include <limits>

namespace sms::detail {
namespace {

constexpr std::int32_t participant_active_state = 2;
constexpr std::int32_t residue_retry_budget = 128;
constexpr std::int32_t advance_retry_budget = 128;

template <class T>
[[nodiscard]] T metadata_load(T& location) noexcept {
    return std::atomic_ref<T>(location).load(std::memory_order_acquire);
}

template <class T>
void metadata_store(T& location, T value) noexcept {
    std::atomic_ref<T>(location).store(value, std::memory_order_release);
}

static_assert(std::atomic_ref<std::int32_t>::is_always_lock_free);
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
        layout.slot_count < 1 || layout.slot_count > sms2_maximum_slot_count ||
        layout.participant_record_count < 1 ||
        layout.participant_record_count > sms2_maximum_participant_count ||
        layout.participant_generation_mask < 1 ||
        layout.participant_stride != sms2_participant_stride ||
        layout.slot_metadata_stride != sms2_slot_metadata_stride ||
        layout.key_stride < layout.max_key_bytes ||
        layout.descriptor_stride < layout.max_descriptor_bytes ||
        layout.payload_stride < layout.max_value_bytes ||
        layout.required_bytes < 0 ||
        static_cast<std::uint64_t>(layout.required_bytes) > mapping_length ||
        !product_equals(
            layout.participant_record_count,
            layout.participant_stride,
            layout.participant_length) ||
        !product_equals(
            layout.slot_count,
            layout.slot_metadata_stride,
            layout.slot_metadata_length) ||
        !product_equals(layout.slot_count, layout.key_stride, layout.key_storage_length) ||
        !product_equals(
            layout.slot_count,
            layout.descriptor_stride,
            layout.descriptor_storage_length) ||
        !product_equals(
            layout.slot_count,
            layout.payload_stride,
            layout.payload_storage_length) ||
        !range_valid(
            layout.participant_offset, layout.participant_length, mapping_length) ||
        !range_valid(
            layout.slot_metadata_offset, layout.slot_metadata_length, mapping_length) ||
        !range_valid(layout.key_storage_offset, layout.key_storage_length, mapping_length) ||
        !range_valid(
            layout.descriptor_storage_offset,
            layout.descriptor_storage_length,
            mapping_length) ||
        !range_valid(
            layout.payload_storage_offset,
            layout.payload_storage_length,
            mapping_length)) {
        return false;
    }

    const auto* first_participant = mapping_base + layout.participant_offset;
    const auto* first_slot = mapping_base + layout.slot_metadata_offset;
    return MappedAtomic64::is_aligned(first_participant) &&
        MappedAtomic64::is_aligned(first_slot);
}

[[nodiscard]] bool encode_slot_control(
    SlotState state,
    std::int64_t generation,
    std::uint32_t participant_token,
    std::uint64_t& control) noexcept {
    return SlotControl::try_encode(
        static_cast<std::int32_t>(state), generation, participant_token, control);
}

} // namespace

bool SlotParticipant::valid(std::int32_t participant_count) const noexcept {
    ParticipantToken decoded_token{};
    ParticipantControl decoded_control{};
    return token != 0 && active_control != 0 &&
        ParticipantToken::try_decode(token, participant_count, decoded_token) &&
        ParticipantControl::try_decode(active_control, decoded_control) &&
        decoded_control.state == participant_active_state &&
        decoded_control.incarnation == decoded_token.generation &&
        decoded_control.process_id > 0;
}

sms_status SlotTable::initialize_mapping(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    const OperationBudget& budget) noexcept {
    if (!mapping_shape_valid(mapping_base, mapping_length, layout)) {
        return SMS_STATUS_CORRUPT_STORE;
    }

    std::uint64_t free_control{};
    if (!encode_slot_control(SlotState::free, 1, 0, free_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    for (std::int32_t index = 0; index < layout.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = reinterpret_cast<ValueSlotMetadataV2*>(
            mapping_base + layout.slot_metadata_offset +
            static_cast<std::int64_t>(index) * layout.slot_metadata_stride);
        *current = ValueSlotMetadataV2{};
        current->KeyOffset = layout.key_storage_offset +
            static_cast<std::int64_t>(index) * layout.key_stride;
        current->DescriptorOffset = layout.descriptor_storage_offset +
            static_cast<std::int64_t>(index) * layout.descriptor_stride;
        current->PayloadOffset = layout.payload_storage_offset +
            static_cast<std::int64_t>(index) * layout.payload_stride;
        MappedAtomic64::store_release(current->Control, free_control);
    }
    return SMS_STATUS_SUCCESS;
}

SlotTable::SlotTable(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout,
    std::uint64_t store_id,
    SlotParticipant participant)
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout),
      store_id_(store_id),
      participant_(participant),
      full_snapshot_(layout.slot_count > 0
          ? static_cast<std::size_t>(layout.slot_count)
          : 0U) {
    ParticipantToken decoded{};
    if (layout.slot_count > 0 &&
        ParticipantToken::try_decode(
            participant.token, layout.participant_record_count, decoded)) {
        next_slot_.store(
            static_cast<std::uint32_t>(decoded.record_index) %
                static_cast<std::uint32_t>(layout.slot_count),
            std::memory_order_relaxed);
    }
}

bool SlotTable::valid() const noexcept {
    return store_id_ != 0 && participant_.valid(layout_.participant_record_count) &&
        full_snapshot_.size() == static_cast<std::size_t>(layout_.slot_count) &&
        mapping_shape_valid(mapping_base_, mapping_length_, layout_);
}

bool SlotTable::locally_active() const noexcept {
    return local_active_.load(std::memory_order_acquire);
}

void SlotTable::invalidate_local() noexcept {
    local_active_.store(false, std::memory_order_release);
}

ValueSlotMetadataV2* SlotTable::slot(std::int32_t slot_index) const noexcept {
    if (!mapping_shape_valid(mapping_base_, mapping_length_, layout_) ||
        slot_index < 0 || slot_index >= layout_.slot_count) {
        return nullptr;
    }
    return reinterpret_cast<ValueSlotMetadataV2*>(
        mapping_base_ + layout_.slot_metadata_offset +
        static_cast<std::int64_t>(slot_index) * layout_.slot_metadata_stride);
}

bool SlotTable::try_classify_structural_control(
    std::uint64_t control,
    std::int32_t participant_count,
    bool& occupied) noexcept {
    return SlotControl{control}.structurally_valid(participant_count, occupied);
}

sms_status SlotTable::owner_status() const noexcept {
    // This check must precede every mapped read so a locally closed wrapper can
    // reject operations after its outer lifetime gate has drained and unmapped.
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;

    ParticipantToken token{};
    if (!ParticipantToken::try_decode(
            participant_.token, layout_.participant_record_count, token)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    const auto offset = layout_.participant_offset +
        static_cast<std::int64_t>(token.record_index) * layout_.participant_stride;
    auto* record = reinterpret_cast<ParticipantRecordV2*>(mapping_base_ + offset);
    const auto observed = MappedAtomic64::load_acquire(record->Control);
    ParticipantControl control{observed};
    if (!control.structurally_valid(layout_.participant_generation_mask)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return observed == participant_.active_control
        ? SMS_STATUS_SUCCESS
        : SMS_STATUS_STORE_DISPOSED;
}

bool SlotTable::try_decode_reservation(
    const ReservationToken& reservation,
    std::int32_t& slot_index,
    std::int64_t& generation) const noexcept {
    slot_index = -1;
    generation = 0;
    if (reservation.store_id != store_id_ ||
        reservation.participant_token != participant_.token ||
        reservation.payload_length < 0 || reservation.slot_binding == 0) {
        return false;
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(reservation.slot_binding, binding) ||
        binding.slot_index < 0 || binding.slot_index >= layout_.slot_count) {
        return false;
    }
    slot_index = binding.slot_index;
    generation = binding.generation;
    return true;
}

sms_status SlotTable::reservation_status(
    std::uint64_t observed_control,
    std::int64_t expected_generation) const noexcept {
    bool occupied{};
    if (!try_classify_structural_control(
            observed_control, layout_.participant_record_count, occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    SlotControl decoded{};
    if (!SlotControl::try_decode(observed_control, decoded) ||
        decoded.generation != expected_generation) {
        return SMS_STATUS_INVALID_RESERVATION;
    }
    return decoded.state == static_cast<std::int32_t>(SlotState::published)
        ? SMS_STATUS_RESERVATION_ALREADY_COMPLETED
        : SMS_STATUS_INVALID_RESERVATION;
}

sms_status SlotTable::sanitize_older_directory_residue(
    ValueSlotMetadataV2& current,
    std::int64_t claimed_generation,
    const OperationBudget& budget,
    bool exact_generation_is_busy) noexcept {
    auto clear_location = [&]() noexcept -> sms_status {
        for (std::int32_t attempt = 0; ; ++attempt) {
            const auto bound = budget.check_periodic(attempt);
            if (bound != SMS_STATUS_SUCCESS) return bound;
            auto raw = MappedAtomic64::load_acquire(current.DirectoryLocation);
            if (raw == 0) return SMS_STATUS_SUCCESS;
            DirectoryLocation location{};
            if (!DirectoryLocation::try_decode(raw, location) ||
                location.generation > claimed_generation) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (location.generation == claimed_generation) {
                return exact_generation_is_busy
                    ? SMS_STATUS_STORE_BUSY
                    : SMS_STATUS_CORRUPT_STORE;
            }
            auto expected = raw;
            (void)MappedAtomic64::compare_exchange(
                current.DirectoryLocation, expected, 0);
            if ((attempt + 1) % residue_retry_budget == 0) {
                sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
                if (!budget.try_continue_after_contention(attempt, terminal)) {
                    return terminal;
                }
            }
        }
    };
    auto clear_operation = [&]() noexcept -> sms_status {
        for (std::int32_t attempt = 0; ; ++attempt) {
            const auto bound = budget.check_periodic(attempt);
            if (bound != SMS_STATUS_SUCCESS) return bound;
            auto raw = MappedAtomic64::load_acquire(current.DirectoryOperation);
            if (raw == 0) return SMS_STATUS_SUCCESS;
            DirectoryOperation operation{};
            if (!DirectoryOperation::try_decode(raw, operation) ||
                operation.generation > claimed_generation) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            if (operation.generation == claimed_generation) {
                return exact_generation_is_busy
                    ? SMS_STATUS_STORE_BUSY
                    : SMS_STATUS_CORRUPT_STORE;
            }
            auto expected = raw;
            (void)MappedAtomic64::compare_exchange(
                current.DirectoryOperation, expected, 0);
            if ((attempt + 1) % residue_retry_budget == 0) {
                sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
                if (!budget.try_continue_after_contention(attempt, terminal)) {
                    return terminal;
                }
            }
        }
    };

    const auto location_status = clear_location();
    return location_status == SMS_STATUS_SUCCESS
        ? clear_operation()
        : location_status;
}

sms_status SlotTable::try_claim_reservation(
    std::uint64_t key_hash,
    std::int32_t key_length,
    std::int32_t descriptor_length,
    std::int32_t payload_length,
    SlotPublicationIntent publication_intent,
    const OperationBudget& budget,
    ReservationToken& reservation) noexcept {
    reservation = {};
    const auto active = owner_status();
    if (active != SMS_STATUS_SUCCESS) return active;
    if ((publication_intent != SlotPublicationIntent::explicit_reservation &&
            publication_intent != SlotPublicationIntent::atomic_publication) ||
        key_length <= 0 || key_length > layout_.max_key_bytes ||
        descriptor_length < 0 || descriptor_length > layout_.max_descriptor_bytes ||
        payload_length < 0 || payload_length > layout_.max_value_bytes) {
        return SMS_STATUS_INVALID_RESERVATION;
    }

    const auto start = next_slot_.fetch_add(1, std::memory_order_relaxed) %
        static_cast<std::uint32_t>(layout_.slot_count);
    for (std::int32_t visited = 0; visited < layout_.slot_count; ++visited) {
        const auto bound = budget.check_periodic(visited);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto candidate = (start + static_cast<std::uint32_t>(visited)) %
            static_cast<std::uint32_t>(layout_.slot_count);
        auto* current = slot(static_cast<std::int32_t>(candidate));
        if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;

        const auto observed = MappedAtomic64::load_acquire(current->Control);
        bool occupied{};
        if (!try_classify_structural_control(
                observed, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        SlotControl decoded{};
        (void)SlotControl::try_decode(observed, decoded);
        if (decoded.state != static_cast<std::int32_t>(SlotState::free)) continue;

        std::uint64_t initializing{};
        if (!encode_slot_control(
                SlotState::initializing,
                decoded.generation,
                participant_.token,
                initializing)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        auto expected = observed;
        if (!MappedAtomic64::compare_exchange(
                current->Control, expected, initializing)) {
            if (!try_classify_structural_control(
                    expected, layout_.participant_record_count, occupied)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            continue;
        }

        std::uint64_t binding{};
        if (!IndexBinding::try_encode(
                static_cast<std::int32_t>(candidate), decoded.generation, binding)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        reservation = ReservationToken{
            store_id_, participant_.token, binding, payload_length};

        const auto residue = sanitize_older_directory_residue(
            *current, decoded.generation, budget, false);
        if (residue != SMS_STATUS_SUCCESS) {
            (void)try_begin_abort(reservation);
            (void)complete_reclaim(
                reservation.slot_binding, OperationBudget::structural_attempt());
            reservation = {};
            return residue;
        }

        const auto expected_key_offset = layout_.key_storage_offset +
            static_cast<std::int64_t>(candidate) * layout_.key_stride;
        const auto expected_descriptor_offset = layout_.descriptor_storage_offset +
            static_cast<std::int64_t>(candidate) * layout_.descriptor_stride;
        const auto expected_payload_offset = layout_.payload_storage_offset +
            static_cast<std::int64_t>(candidate) * layout_.payload_stride;
        if (current->KeyOffset != expected_key_offset ||
            current->DescriptorOffset != expected_descriptor_offset ||
            current->PayloadOffset != expected_payload_offset) {
            (void)try_begin_abort(reservation);
            (void)complete_reclaim(
                reservation.slot_binding, OperationBudget::structural_attempt());
            reservation = {};
            return SMS_STATUS_CORRUPT_STORE;
        }

        const auto revalidated = owner_status();
        if (revalidated != SMS_STATUS_SUCCESS) {
            (void)try_begin_abort(reservation);
            (void)complete_reclaim(
                reservation.slot_binding, OperationBudget::structural_attempt());
            reservation = {};
            return revalidated;
        }
        const auto final_bound = budget.check();
        if (final_bound != SMS_STATUS_SUCCESS) {
            (void)try_begin_abort(reservation);
            (void)complete_reclaim(
                reservation.slot_binding, OperationBudget::structural_attempt());
            reservation = {};
            return final_bound;
        }

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::SlotClaimAfterParticipantRecheck);

        MappedAtomic64::store_release(current->DirectoryBinding, binding);
        MappedAtomic64::store_release(current->KeyHash, key_hash);
        metadata_store(current->KeyLength, key_length);
        metadata_store(current->DescriptorLength, descriptor_length);
        metadata_store(current->ValueLength, payload_length);
        metadata_store(
            current->PublicationIntent,
            static_cast<std::int32_t>(publication_intent));
        MappedAtomic64::store_release(current->BytesAdvanced, 0);
        metadata_store(current->CommitSequence, std::int64_t{0});
        return SMS_STATUS_SUCCESS;
    }
    return SMS_STATUS_STORE_FULL;
}

sms_status SlotTable::try_prove_store_full(
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

    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = slot(index);
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
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::StoreFullAfterFirstCollectBeforeVerification);
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        auto* current = slot(index);
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
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::StoreFullAfterExactDoubleCollect);
    proven_full = true;
    return SMS_STATUS_SUCCESS;
}

sms_status SlotTable::mark_reserved(
    const ReservationToken& reservation) noexcept {
    const auto active = owner_status();
    if (active != SMS_STATUS_SUCCESS) return active;
    std::int32_t slot_index{};
    std::int64_t generation{};
    if (!try_decode_reservation(reservation, slot_index, generation)) {
        return SMS_STATUS_INVALID_RESERVATION;
    }
    auto* current = slot(slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t initializing{};
    std::uint64_t reserved{};
    if (!encode_slot_control(
            SlotState::initializing,
            generation,
            reservation.participant_token,
            initializing) ||
        !encode_slot_control(
            SlotState::reserved,
            generation,
            reservation.participant_token,
            reserved)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = initializing;
    return MappedAtomic64::compare_exchange(current->Control, expected, reserved)
        ? SMS_STATUS_SUCCESS
        : reservation_status(expected, generation);
}

bool SlotTable::try_read_projection(
    const ReservationToken& reservation,
    ReservationProjection& projection,
    sms_status& failure) const noexcept {
    projection = {};
    failure = owner_status();
    if (failure != SMS_STATUS_SUCCESS) return false;

    std::int32_t slot_index{};
    std::int64_t generation{};
    if (!try_decode_reservation(reservation, slot_index, generation)) {
        failure = SMS_STATUS_INVALID_RESERVATION;
        return false;
    }
    auto* current = slot(slot_index);
    if (current == nullptr) {
        failure = SMS_STATUS_CORRUPT_STORE;
        return false;
    }
    std::uint64_t reserved{};
    if (!encode_slot_control(
            SlotState::reserved,
            generation,
            reservation.participant_token,
            reserved)) {
        failure = SMS_STATUS_CORRUPT_STORE;
        return false;
    }
    const auto control1 = MappedAtomic64::load_acquire(current->Control);
    bool occupied{};
    if (!try_classify_structural_control(
            control1, layout_.participant_record_count, occupied)) {
        failure = SMS_STATUS_CORRUPT_STORE;
        return false;
    }
    if (control1 != reserved) {
        failure = reservation_status(control1, generation);
        return false;
    }

    const auto directory_binding =
        MappedAtomic64::load_acquire(current->DirectoryBinding);
    const auto key_length = metadata_load(current->KeyLength);
    const auto descriptor_length = metadata_load(current->DescriptorLength);
    const auto value_length = metadata_load(current->ValueLength);
    const auto intent = metadata_load(current->PublicationIntent);
    const auto advanced = MappedAtomic64::load_acquire(current->BytesAdvanced);
    const auto key_offset = current->KeyOffset;
    const auto descriptor_offset = current->DescriptorOffset;
    const auto payload_offset = current->PayloadOffset;
    const auto control2 = MappedAtomic64::load_acquire(current->Control);
    if (control2 != control1) {
        failure = reservation_status(control2, generation);
        return false;
    }

    failure = owner_status();
    if (failure != SMS_STATUS_SUCCESS) return false;
    const auto expected_key_offset = layout_.key_storage_offset +
        static_cast<std::int64_t>(slot_index) * layout_.key_stride;
    const auto expected_descriptor_offset = layout_.descriptor_storage_offset +
        static_cast<std::int64_t>(slot_index) * layout_.descriptor_stride;
    const auto expected_payload_offset = layout_.payload_storage_offset +
        static_cast<std::int64_t>(slot_index) * layout_.payload_stride;
    if (directory_binding != reservation.slot_binding ||
        key_length < 1 || key_length > layout_.max_key_bytes ||
        descriptor_length < 0 || descriptor_length > layout_.max_descriptor_bytes ||
        value_length < 0 || value_length > layout_.max_value_bytes ||
        value_length != reservation.payload_length ||
        (intent != static_cast<std::int32_t>(
                SlotPublicationIntent::explicit_reservation) &&
            intent != static_cast<std::int32_t>(
                SlotPublicationIntent::atomic_publication)) ||
        advanced > static_cast<std::uint64_t>(value_length) ||
        key_offset != expected_key_offset ||
        descriptor_offset != expected_descriptor_offset ||
        payload_offset != expected_payload_offset) {
        failure = SMS_STATUS_CORRUPT_STORE;
        return false;
    }

    projection.slot_index = slot_index;
    projection.generation = generation;
    projection.value_length = value_length;
    projection.bytes_advanced = advanced;
    failure = SMS_STATUS_SUCCESS;
    return true;
}

bool SlotTable::reservation_pending(
    const ReservationToken& reservation) const noexcept {
    ReservationProjection projection{};
    sms_status failure{};
    return try_read_projection(reservation, projection, failure);
}

std::int32_t SlotTable::bytes_advanced(
    const ReservationToken& reservation) const noexcept {
    ReservationProjection projection{};
    sms_status failure{};
    return try_read_projection(reservation, projection, failure)
        ? static_cast<std::int32_t>(projection.bytes_advanced)
        : 0;
}

bool SlotTable::try_get_writable_range(
    const ReservationToken& reservation,
    std::int32_t size_hint,
    WritableReservationRange& range) const noexcept {
    range = {};
    if (size_hint < 0) return false;
    ReservationProjection projection{};
    sms_status failure{};
    if (!try_read_projection(reservation, projection, failure)) return false;
    const auto advanced = static_cast<std::int32_t>(projection.bytes_advanced);
    const auto remaining = projection.value_length - advanced;
    if (remaining <= 0 || size_hint > remaining) return false;
    range = WritableReservationRange{projection.slot_index, advanced, remaining};
    return true;
}

sms_status SlotTable::advance_reservation(
    const ReservationToken& reservation,
    std::int32_t byte_count,
    const OperationBudget& budget) noexcept {
    ReservationProjection projection{};
    sms_status validation{};
    if (!try_read_projection(reservation, projection, validation)) return validation;
    auto* current = slot(projection.slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t reserved{};
    if (!encode_slot_control(
            SlotState::reserved,
            projection.generation,
            reservation.participant_token,
            reserved)) {
        return SMS_STATUS_CORRUPT_STORE;
    }

    for (std::int32_t attempt = 0; ; ++attempt) {
        const auto bound = budget.check_periodic(attempt);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto observed = MappedAtomic64::load_acquire(current->BytesAdvanced);
        if (observed > static_cast<std::uint64_t>(projection.value_length) ||
            metadata_load(current->ValueLength) != projection.value_length) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (byte_count < 0 ||
            static_cast<std::uint64_t>(byte_count) >
                static_cast<std::uint64_t>(projection.value_length) - observed) {
            return SMS_STATUS_RESERVATION_WRITE_OUT_OF_RANGE;
        }
        const auto final_bound = budget.check();
        if (final_bound != SMS_STATUS_SUCCESS) return final_bound;
        const auto observed_control = MappedAtomic64::load_acquire(current->Control);
        if (observed_control != reserved) {
            return reservation_status(observed_control, projection.generation);
        }
        auto expected = observed;
        const auto next = observed + static_cast<std::uint64_t>(byte_count);
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::AdvanceBeforeBytesAdvancedCas);
        if (MappedAtomic64::compare_exchange(
                current->BytesAdvanced, expected, next)) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::AdvanceAfterBytesAdvancedCas);
            const auto confirmed = MappedAtomic64::load_acquire(current->Control);
            return confirmed == reserved
                ? SMS_STATUS_SUCCESS
                : reservation_status(confirmed, projection.generation);
        }
        const auto control = MappedAtomic64::load_acquire(current->Control);
        if (control != reserved) {
            return reservation_status(control, projection.generation);
        }
        if (attempt + 1 >= advance_retry_budget) {
            sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
        }
    }
}

sms_status SlotTable::commit_reservation(
    const ReservationToken& reservation,
    std::int64_t commit_sequence) noexcept {
    ReservationProjection projection{};
    sms_status validation{};
    if (!try_read_projection(reservation, projection, validation)) return validation;
    if (projection.bytes_advanced !=
        static_cast<std::uint64_t>(projection.value_length)) {
        return SMS_STATUS_RESERVATION_INCOMPLETE;
    }
    auto* current = slot(projection.slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t reserved{};
    std::uint64_t published{};
    if (!encode_slot_control(
            SlotState::reserved,
            projection.generation,
            reservation.participant_token,
            reserved) ||
        !encode_slot_control(
            SlotState::published, projection.generation, 0, published)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    metadata_store(current->CommitSequence, commit_sequence);
    auto expected = reserved;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::CommitBeforePublicationCas);
    if (MappedAtomic64::compare_exchange(current->Control, expected, published)) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::CommitAfterPublicationCas);
        return SMS_STATUS_SUCCESS;
    }
    return reservation_status(expected, projection.generation);
}

sms_status SlotTable::try_begin_abort(
    const ReservationToken& reservation) noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    std::int32_t slot_index{};
    std::int64_t generation{};
    if (!try_decode_reservation(reservation, slot_index, generation)) {
        return SMS_STATUS_INVALID_RESERVATION;
    }
    auto* current = slot(slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t initializing{};
    std::uint64_t reserved{};
    std::uint64_t aborting{};
    if (!encode_slot_control(
            SlotState::initializing,
            generation,
            reservation.participant_token,
            initializing) ||
        !encode_slot_control(
            SlotState::reserved,
            generation,
            reservation.participant_token,
            reserved) ||
        !encode_slot_control(SlotState::aborting, generation, 0, aborting)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::AbortBeforeAbortCas);
    auto expected = initializing;
    if (MappedAtomic64::compare_exchange(current->Control, expected, aborting) ||
        expected == aborting) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::AbortAfterOwnershipReleaseCas);
        return SMS_STATUS_SUCCESS;
    }
    expected = reserved;
    if (MappedAtomic64::compare_exchange(current->Control, expected, aborting) ||
        expected == aborting) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::AbortAfterOwnershipReleaseCas);
        return SMS_STATUS_SUCCESS;
    }
    return reservation_status(expected, generation);
}

bool SlotTable::has_advanced_or_retired(
    std::uint64_t control,
    std::int64_t generation) const noexcept {
    SlotControl decoded{};
    return SlotControl::try_decode(control, decoded) &&
        (decoded.generation > generation ||
            (decoded.generation == generation &&
                decoded.state == static_cast<std::int32_t>(SlotState::retired)));
}

bool SlotTable::try_advance_or_retire(
    std::int64_t generation,
    std::uint64_t& control) noexcept {
    if (generation < 1 || generation > terminal_generation) return false;
    return generation == terminal_generation
        ? encode_slot_control(SlotState::retired, generation, 0, control)
        : encode_slot_control(SlotState::free, generation + 1, 0, control);
}

sms_status SlotTable::complete_reclaim(
    std::uint64_t exact_binding,
    const OperationBudget& budget) noexcept {
    if (!locally_active()) return SMS_STATUS_STORE_DISPOSED;
    if (!valid()) return SMS_STATUS_CORRUPT_STORE;
    const auto initial_bound = budget.check();
    if (initial_bound != SMS_STATUS_SUCCESS) return initial_bound;
    IndexBinding binding{};
    if (!IndexBinding::try_decode(exact_binding, binding) ||
        binding.slot_index < 0 || binding.slot_index >= layout_.slot_count) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = slot(binding.slot_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    std::uint64_t aborting{};
    std::uint64_t reclaiming{};
    if (!encode_slot_control(SlotState::aborting, binding.generation, 0, aborting) ||
        !encode_slot_control(
            SlotState::reclaiming, binding.generation, 0, reclaiming)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = aborting;
    if (!MappedAtomic64::compare_exchange(
            current->Control, expected, reclaiming)) {
        bool occupied{};
        if (!try_classify_structural_control(
                expected, layout_.participant_record_count, occupied)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (has_advanced_or_retired(expected, binding.generation)) {
            return SMS_STATUS_SUCCESS;
        }
        if (expected != reclaiming) {
            auto stable = expected;
            if (MappedAtomic64::compare_exchange(current->Control, stable, expected)) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            return SMS_STATUS_STORE_BUSY;
        }
    }

    // A current-generation descriptor may still belong to a concurrent
    // directory helper. Defer it without clearing; strictly older residue is
    // safe to exact-clear, while future-generation residue is structural
    // corruption. This mirrors the managed recovery completion contract.
    const auto residue = sanitize_older_directory_residue(
        *current, binding.generation, budget, true);
    if (residue != SMS_STATUS_SUCCESS) return residue;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ReclaimAfterMetadataValidation);
    const auto final_bound = budget.check();
    if (final_bound != SMS_STATUS_SUCCESS) return final_bound;
    std::uint64_t terminal{};
    if (!try_advance_or_retire(binding.generation, terminal)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    expected = reclaiming;
    if (MappedAtomic64::compare_exchange(current->Control, expected, terminal) ||
        expected == terminal ||
        has_advanced_or_retired(expected, binding.generation)) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ReclaimAfterGenerationAdvance);
        return SMS_STATUS_SUCCESS;
    }
    bool occupied{};
    if (!try_classify_structural_control(
            expected, layout_.participant_record_count, occupied)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto stable = expected;
    return MappedAtomic64::compare_exchange(current->Control, stable, expected)
        ? SMS_STATUS_CORRUPT_STORE
        : SMS_STATUS_STORE_BUSY;
}

sms_status SlotTable::abort_reservation(
    const ReservationToken& reservation,
    const OperationBudget& budget) noexcept {
    const auto bound = budget.check();
    if (bound != SMS_STATUS_SUCCESS) return bound;
    const auto begin = try_begin_abort(reservation);
    if (begin != SMS_STATUS_SUCCESS) return begin;
    // Aborting is the ordering point and clears ownership. Cancellation can no
    // longer be returned; finish the bounded local handoff or leave it helpable.
    return complete_reclaim(
        reservation.slot_binding, OperationBudget::structural_attempt());
}

} // namespace sms::detail
