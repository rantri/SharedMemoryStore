#include "participant_registry.hpp"

#include "checkpoint.hpp"

#include <cstring>
#include <limits>

namespace sms::detail {
namespace {

[[nodiscard]] ParticipantRegistrationStatus registration_control_status(
    StoreHeaderV2& header) noexcept {
    switch (MappedAtomic64::load_acquire(header.Control)) {
    case sms2_store_ready:
        return ParticipantRegistrationStatus::success;
    case sms2_store_initializing:
        return ParticipantRegistrationStatus::store_busy;
    case sms2_store_corrupt:
        return ParticipantRegistrationStatus::corrupt_store;
    case sms2_store_unsupported:
        return ParticipantRegistrationStatus::unsupported_platform;
    default:
        return ParticipantRegistrationStatus::incompatible_layout;
    }
}

} // namespace

ParticipantRegistry::ParticipantRegistry(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout) {}

bool ParticipantRegistry::valid() const noexcept {
    if (mapping_base_ == nullptr ||
        layout_.participant_record_count < 1 ||
        layout_.participant_generation_mask < 1 ||
        layout_.participant_offset < 0 ||
        layout_.participant_length < 0) {
        return false;
    }
    const auto offset = static_cast<std::uint64_t>(layout_.participant_offset);
    const auto length = static_cast<std::uint64_t>(layout_.participant_length);
    return offset <= mapping_length_ && length <= mapping_length_ - offset;
}

ParticipantRecordV2* ParticipantRegistry::record(std::int32_t index) const noexcept {
    if (!valid() || index < 0 || index >= layout_.participant_record_count) {
        return nullptr;
    }
    const auto offset = layout_.participant_offset +
        static_cast<std::int64_t>(index) * layout_.participant_stride;
    if (offset < 0 || static_cast<std::uint64_t>(offset) >
            mapping_length_ - sizeof(ParticipantRecordV2)) {
        return nullptr;
    }
    return reinterpret_cast<ParticipantRecordV2*>(mapping_base_ + offset);
}

bool ParticipantRegistry::initialize(const OperationBudget& budget) noexcept {
    if (!valid()) return false;
    std::uint64_t free_control{};
    if (!ParticipantControl::try_encode(
            participant_free, 1, 0, free_control)) {
        return false;
    }
    for (std::int32_t index = 0;
         index < layout_.participant_record_count;
         ++index) {
        if (budget.check_periodic(index) != SMS_STATUS_SUCCESS) return false;
        auto* current = record(index);
        if (current == nullptr) return false;
        current->IdentityKind = identity_unknown;
        current->Reserved = 0;
        current->ProcessStartValue = 0;
        current->OpenSequence = 0;
        current->PidNamespaceId = 0;
        std::memset(current->ReservedBytes, 0, sizeof(current->ReservedBytes));
        MappedAtomic64::store_release(current->Control, free_control);
    }
    return true;
}

bool ParticipantRegistry::structurally_valid(std::uint64_t control) const noexcept {
    ParticipantControl decoded{control};
    return decoded.structurally_valid(layout_.participant_generation_mask);
}

bool ParticipantRegistry::help_reclaiming(
    ParticipantRecordV2& current,
    std::int32_t generation) noexcept {
    std::uint64_t reclaiming{};
    if (!ParticipantControl::try_encode(
            participant_reclaiming, generation, 0, reclaiming)) {
        return false;
    }
    const auto next_generation = generation == layout_.participant_generation_mask
        ? generation
        : generation + 1;
    const auto next_state = generation == layout_.participant_generation_mask
        ? participant_retired
        : participant_free;
    std::uint64_t terminal{};
    if (!ParticipantControl::try_encode(
            next_state, next_generation, 0, terminal)) {
        return false;
    }

    auto observed = MappedAtomic64::load_acquire(current.Control);
    if (observed != reclaiming) return observed == terminal;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ParticipantBeforeReclaimGenerationAdvanceCas);
    auto expected = reclaiming;
    return MappedAtomic64::compare_exchange(current.Control, expected, terminal) ||
        expected == terminal;
}

ParticipantRegistrationStatus ParticipantRegistry::try_register(
    StoreHeaderV2& header,
    const ParticipantIdentity& identity,
    const OperationBudget& budget,
    ParticipantRegistration& registration) noexcept {
    registration = {};
    if (!valid() || !identity.valid()) {
        return ParticipantRegistrationStatus::incompatible_layout;
    }
    auto store_status = registration_control_status(header);
    if (store_status != ParticipantRegistrationStatus::success) {
        return store_status;
    }

    for (std::int32_t index = 0;
         index < layout_.participant_record_count;
         ++index) {
        const auto bound = budget.check_periodic(index);
        if (bound == SMS_STATUS_OPERATION_CANCELED) {
            return ParticipantRegistrationStatus::operation_canceled;
        }
        if (bound != SMS_STATUS_SUCCESS) {
            return ParticipantRegistrationStatus::store_busy;
        }
        store_status = registration_control_status(header);
        if (store_status != ParticipantRegistrationStatus::success) {
            return store_status;
        }

        auto* current = record(index);
        if (current == nullptr) {
            return ParticipantRegistrationStatus::incompatible_layout;
        }
        auto observed = MappedAtomic64::load_acquire(current->Control);
        if (!structurally_valid(observed)) {
            return ParticipantRegistrationStatus::incompatible_layout;
        }
        ParticipantControl decoded{};
        (void)ParticipantControl::try_decode(observed, decoded);
        if (decoded.state == participant_reclaiming) {
            (void)help_reclaiming(*current, decoded.incarnation);
            observed = MappedAtomic64::load_acquire(current->Control);
            if (!structurally_valid(observed)) {
                return ParticipantRegistrationStatus::incompatible_layout;
            }
            (void)ParticipantControl::try_decode(observed, decoded);
        }
        if (decoded.state != participant_free || decoded.process_id != 0) continue;

        store_status = registration_control_status(header);
        if (store_status != ParticipantRegistrationStatus::success) {
            return store_status;
        }

        std::uint64_t registering{};
        if (!ParticipantControl::try_encode(
                participant_registering,
                decoded.incarnation,
                identity.process_id,
                registering)) {
            return ParticipantRegistrationStatus::incompatible_layout;
        }
        auto expected = observed;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantBeforeRegisteringCas);
        if (!MappedAtomic64::compare_exchange(
                current->Control, expected, registering)) {
            if (!structurally_valid(expected)) {
                return ParticipantRegistrationStatus::incompatible_layout;
            }
            continue;
        }

        auto rollback = [&]() noexcept {
            current->IdentityKind = identity_unknown;
            current->Reserved = 0;
            current->ProcessStartValue = 0;
            current->OpenSequence = 0;
            current->PidNamespaceId = 0;
            auto claimed = registering;
            (void)MappedAtomic64::compare_exchange(
                current->Control, claimed, observed);
        };
        store_status = registration_control_status(header);
        if (store_status != ParticipantRegistrationStatus::success) {
            rollback();
            return store_status;
        }

        current->IdentityKind = identity.identity_kind;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterIdentityKindWrite);
        current->Reserved = 0;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterReservedWrite);
        current->ProcessStartValue = identity.process_start_value;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterProcessStartWrite);
        current->PidNamespaceId = identity.pid_namespace_id;
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterPidNamespaceWrite);
        current->OpenSequence = static_cast<std::int64_t>(
            MappedAtomic64::fetch_add(header.Sequence, 1) + 1);
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterOpenSequenceWrite);

        store_status = registration_control_status(header);
        if (store_status != ParticipantRegistrationStatus::success) {
            rollback();
            return store_status;
        }

        std::uint64_t active{};
        std::uint64_t token{};
        if (!ParticipantControl::try_encode(
                participant_active,
                decoded.incarnation,
                identity.process_id,
                active) ||
            !ParticipantToken::try_encode(
                index,
                decoded.incarnation,
                layout_.participant_record_count,
                token) ||
            token > std::numeric_limits<std::uint32_t>::max()) {
            return ParticipantRegistrationStatus::incompatible_layout;
        }
        registration = ParticipantRegistration{
            index,
            decoded.incarnation,
            static_cast<std::uint32_t>(token),
            active};
        MappedAtomic64::store_release(current->Control, active);
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterActivePublication);
        store_status = registration_control_status(header);
        if (store_status != ParticipantRegistrationStatus::success) {
            (void)close_and_retire(registration);
            registration = {};
            return store_status;
        }
        return ParticipantRegistrationStatus::success;
    }
    return ParticipantRegistrationStatus::table_full;
}

bool ParticipantRegistry::close_and_retire(
    const ParticipantRegistration& registration) noexcept {
    std::uint64_t closing{};
    std::uint64_t reclaiming{};
    if (try_begin_close(registration, closing) != SMS_STATUS_SUCCESS ||
        try_begin_reclaim(
            registration.token, closing, reclaiming) != SMS_STATUS_SUCCESS) {
        return false;
    }
    return try_complete_reclaim(
        registration.token, reclaiming) == SMS_STATUS_SUCCESS;
}

sms_status ParticipantRegistry::try_begin_close(
    const ParticipantRegistration& registration,
    std::uint64_t& closing_control) noexcept {
    closing_control = 0;
    if (!registration.valid(layout_.participant_record_count)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = record(registration.record_index);
    ParticipantControl active{};
    if (current == nullptr ||
        !ParticipantControl::try_decode(registration.active_control, active) ||
        active.state != participant_active ||
        active.incarnation != registration.generation ||
        active.process_id <= 0 ||
        !ParticipantControl::try_encode(
            participant_closing,
            registration.generation,
            active.process_id,
            closing_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto expected = registration.active_control;
    if (MappedAtomic64::compare_exchange(
            current->Control, expected, closing_control) ||
        expected == closing_control) {
        return SMS_STATUS_SUCCESS;
    }
    ParticipantControl observed{};
    if (!ParticipantControl::try_decode(expected, observed) ||
        !observed.structurally_valid(layout_.participant_generation_mask)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return observed.incarnation == registration.generation
        ? SMS_STATUS_STORE_BUSY
        : SMS_STATUS_NOT_FOUND;
}

sms_status ParticipantRegistry::try_begin_recovery(
    std::uint32_t participant_token,
    std::uint64_t expected_control,
    std::uint64_t& recovering_control) noexcept {
    recovering_control = 0;
    ParticipantToken token{};
    ParticipantControl expected_decoded{};
    if (!ParticipantToken::try_decode(
            participant_token,
            layout_.participant_record_count,
            token) ||
        !ParticipantControl::try_decode(
            expected_control, expected_decoded) ||
        expected_decoded.incarnation != token.generation ||
        expected_decoded.process_id <= 0 ||
        (expected_decoded.state != participant_registering &&
         expected_decoded.state != participant_active &&
         expected_decoded.state != participant_closing &&
         expected_decoded.state != participant_recovering) ||
        !ParticipantControl::try_encode(
            participant_recovering,
            token.generation,
            expected_decoded.process_id,
            recovering_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = record(token.record_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    if (expected_control == recovering_control) return SMS_STATUS_SUCCESS;
    auto expected = expected_control;
    if (MappedAtomic64::compare_exchange(
            current->Control, expected, recovering_control) ||
        expected == recovering_control) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::RecoveryAfterExactRecoveryCas);
        return SMS_STATUS_SUCCESS;
    }
    ParticipantControl observed{};
    if (!ParticipantControl::try_decode(expected, observed) ||
        !observed.structurally_valid(layout_.participant_generation_mask)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return observed.incarnation == token.generation
        ? SMS_STATUS_STORE_BUSY
        : SMS_STATUS_NOT_FOUND;
}

sms_status ParticipantRegistry::try_begin_reclaim(
    std::uint32_t participant_token,
    std::uint64_t handoff_control,
    std::uint64_t& reclaiming_control) noexcept {
    reclaiming_control = 0;
    ParticipantToken token{};
    ParticipantControl handoff{};
    if (!ParticipantToken::try_decode(
            participant_token,
            layout_.participant_record_count,
            token) ||
        !ParticipantControl::try_decode(
            handoff_control, handoff) ||
        (handoff.state != participant_closing &&
         handoff.state != participant_recovering) ||
        handoff.incarnation != token.generation ||
        handoff.process_id <= 0 ||
        !ParticipantControl::try_encode(
            participant_reclaiming,
            token.generation,
            0,
            reclaiming_control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = record(token.record_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    auto expected = handoff_control;
    if (!MappedAtomic64::compare_exchange(
            current->Control, expected, reclaiming_control) &&
        expected != reclaiming_control) {
        ParticipantControl observed{};
        if (!ParticipantControl::try_decode(expected, observed) ||
            !observed.structurally_valid(
                layout_.participant_generation_mask)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        return observed.incarnation == token.generation
            ? SMS_STATUS_STORE_BUSY
            : SMS_STATUS_NOT_FOUND;
    }
    return SMS_STATUS_SUCCESS;
}

sms_status ParticipantRegistry::try_complete_reclaim(
    std::uint32_t participant_token,
    std::uint64_t reclaiming_control) noexcept {
    ParticipantToken token{};
    ParticipantControl reclaiming{};
    if (!ParticipantToken::try_decode(
            participant_token,
            layout_.participant_record_count,
            token) ||
        !ParticipantControl::try_decode(
            reclaiming_control, reclaiming) ||
        reclaiming.state != participant_reclaiming ||
        reclaiming.incarnation != token.generation ||
        reclaiming.process_id != 0) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto* current = record(token.record_index);
    if (current == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto observed = MappedAtomic64::load_acquire(current->Control);
    if (observed == reclaiming_control) {
        return help_reclaiming(*current, token.generation)
            ? SMS_STATUS_SUCCESS
            : SMS_STATUS_STORE_BUSY;
    }
    ParticipantControl decoded{};
    if (!ParticipantControl::try_decode(observed, decoded) ||
        !decoded.structurally_valid(layout_.participant_generation_mask)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    return decoded.incarnation == token.generation
        ? SMS_STATUS_STORE_BUSY
        : SMS_STATUS_SUCCESS;
}

bool ParticipantRegistry::is_active(std::uint32_t token) const noexcept {
    ParticipantToken decoded{};
    if (!ParticipantToken::try_decode(
            token, layout_.participant_record_count, decoded)) {
        return false;
    }
    const auto* current = record(decoded.record_index);
    if (current == nullptr) return false;
    ParticipantControl control{};
    return ParticipantControl::try_decode(
            MappedAtomic64::load_acquire(
                const_cast<std::uint64_t&>(current->Control)),
            control) &&
        control.state == participant_active &&
        control.incarnation == decoded.generation;
}

} // namespace sms::detail
