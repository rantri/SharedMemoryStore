#include "control_words.hpp"
#include "test_support.hpp"

#include <cstdint>
#include <limits>

int main() {
    using namespace sms::detail;

    std::uint64_t raw{};
    ParticipantControl participant{};
    SMS_CHECK(ParticipantControl::try_encode(2, 0x0123'4567, 0x0765'4321, raw));
    SMS_CHECK(raw == 0x03b2'a190'891a'2b3aULL);
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(participant.value == raw);
    SMS_CHECK(participant.state == 2);
    SMS_CHECK(participant.incarnation == 0x0123'4567);
    SMS_CHECK(participant.process_id == 0x0765'4321);
    SMS_CHECK(participant.structurally_valid(0x0fff'ffff));

    SMS_CHECK(ParticipantControl::try_encode(0, 1, 0, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(ParticipantControl::try_encode(0, 1, 42, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(!participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(ParticipantControl::try_encode(2, 1, 0, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(!participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(ParticipantControl::try_encode(6, 0x0fff'ffff, 0, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(ParticipantControl::try_encode(6, 0x0fff'fffe, 0, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(!participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(ParticipantControl::try_encode(2, 0, 7, raw));
    SMS_CHECK(ParticipantControl::try_decode(raw, participant));
    SMS_CHECK(!participant.structurally_valid(0x0fff'ffff));
    SMS_CHECK(!ParticipantControl::try_encode(-1, 1, 1, raw));
    SMS_CHECK(!ParticipantControl::try_encode(7, 1, 1, raw));
    SMS_CHECK(!ParticipantControl::try_encode(1, -1, 1, raw));
    SMS_CHECK(!ParticipantControl::try_decode(1ULL << 63, participant));

    ParticipantToken token{};
    SMS_CHECK(ParticipantToken::try_encode(2, 17, 4, raw));
    SMS_CHECK(raw == 0x8bULL);
    SMS_CHECK(ParticipantToken::try_decode(raw, 4, token));
    SMS_CHECK(token.value == raw);
    SMS_CHECK(token.record_index == 2);
    SMS_CHECK(token.generation == 17);
    SMS_CHECK(token.index_bits == 3);
    SMS_CHECK(token.generation_bits == 25);
    SMS_CHECK(token.structurally_valid(4));
    SMS_CHECK(ParticipantToken::try_encode(1'048'574, 255, 1'048'575, raw));
    SMS_CHECK(raw == 0x0fff'ffffULL);
    SMS_CHECK(ParticipantToken::try_decode(raw, 1'048'575, token));
    SMS_CHECK(token.record_index == 1'048'574);
    SMS_CHECK(token.generation == 255);
    SMS_CHECK(!ParticipantToken::try_encode(0, 0, 1, raw));
    SMS_CHECK(!ParticipantToken::try_encode(1, 1, 1, raw));
    SMS_CHECK(!ParticipantToken::try_encode(0, 1, 0, raw));
    SMS_CHECK(!ParticipantToken::try_encode(0, 1, 1'048'576, raw));
    SMS_CHECK(!ParticipantToken::try_decode(0, 4, token));
    SMS_CHECK(!ParticipantToken::try_decode(0x1000'0000ULL, 4, token));
    SMS_CHECK(!ParticipantToken::try_decode((17ULL << 3) | 7ULL, 4, token));

    SlotControl slot{};
    SMS_CHECK(SlotControl::try_encode(2, 0x1'2345'6789LL, 0x0abc'deffU, raw));
    SMS_CHECK(raw == 0xabcd'eff9'1a2b'3c4aULL);
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(slot.value == raw);
    SMS_CHECK(slot.state == 2);
    SMS_CHECK(slot.generation == 0x1'2345'6789LL);
    SMS_CHECK(slot.participant_token == 0x0abc'deffU);
    bool occupied = false;
    SMS_CHECK(slot.structurally_valid(1'048'575, occupied));
    SMS_CHECK(occupied);
    SMS_CHECK(SlotControl::try_encode(0, 1, 0, raw));
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(slot.structurally_valid(4, occupied));
    SMS_CHECK(!occupied);
    SMS_CHECK(SlotControl::try_encode(1, 1, 0, raw));
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(!slot.structurally_valid(4, occupied));
    SMS_CHECK(SlotControl::try_encode(3, 1, 1, raw));
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(!slot.structurally_valid(4, occupied));
    SMS_CHECK(SlotControl::try_encode(7, 0x1'ffff'ffffLL, 0, raw));
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(slot.structurally_valid(4, occupied));
    SMS_CHECK(SlotControl::try_encode(7, 0x1'ffff'fffeLL, 0, raw));
    SMS_CHECK(SlotControl::try_decode(raw, slot));
    SMS_CHECK(!slot.structurally_valid(4, occupied));
    SMS_CHECK(!SlotControl::try_encode(0, 0, 0, raw));
    SMS_CHECK(!SlotControl::try_encode(0, 0x2'0000'0000LL, 0, raw));
    SMS_CHECK(!SlotControl::try_encode(0, 1, 0x1000'0000U, raw));
    SMS_CHECK(!SlotControl::try_decode(0, slot));

    LeaseControl lease{};
    SMS_CHECK(LeaseControl::try_encode(2, 0x1'2345'6789LL, 0x0abc'deffU, raw));
    SMS_CHECK(raw == 0xabcd'eff9'1a2b'3c4aULL);
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(lease.state == 2);
    SMS_CHECK(lease.generation == 0x1'2345'6789LL);
    SMS_CHECK(lease.participant_token == 0x0abc'deffU);
    SMS_CHECK(lease.structurally_valid(1'048'575, occupied));
    SMS_CHECK(occupied);
    SMS_CHECK(LeaseControl::try_encode(0, 1, 0, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(lease.structurally_valid(4, occupied));
    SMS_CHECK(!occupied);
    SMS_CHECK(LeaseControl::try_encode(1, 1, 0, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(!lease.structurally_valid(4, occupied));
    SMS_CHECK(LeaseControl::try_encode(3, 1, 1, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(!lease.structurally_valid(4, occupied));
    SMS_CHECK(LeaseControl::try_encode(5, 0x1'ffff'ffffLL, 0, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(lease.structurally_valid(4, occupied));
    SMS_CHECK(LeaseControl::try_encode(5, 0x1'ffff'fffeLL, 0, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(!lease.structurally_valid(4, occupied));
    SMS_CHECK(LeaseControl::try_encode(6, 1, 0, raw));
    SMS_CHECK(LeaseControl::try_decode(raw, lease));
    SMS_CHECK(!lease.structurally_valid(4, occupied));
    SMS_CHECK(!LeaseControl::try_encode(0, 0, 0, raw));
    SMS_CHECK(!LeaseControl::try_encode(0, 0x2'0000'0000LL, 0, raw));
    SMS_CHECK(!LeaseControl::try_encode(0, 1, 0x1000'0000U, raw));
    SMS_CHECK(!LeaseControl::try_decode(0, lease));

    IndexBinding binding{};
    SMS_CHECK(IndexBinding::try_encode(42, 17, raw));
    SMS_CHECK(raw == 0x0000'0008'8000'002bULL);
    SMS_CHECK(IndexBinding::try_decode(raw, binding));
    SMS_CHECK(binding.value == raw);
    SMS_CHECK(binding.slot_index == 42);
    SMS_CHECK(binding.generation == 17);
    SMS_CHECK(IndexBinding::try_encode(
        std::numeric_limits<std::int32_t>::max() - 1, 0x1'ffff'ffffLL, raw));
    SMS_CHECK(raw == std::numeric_limits<std::uint64_t>::max());
    SMS_CHECK(IndexBinding::try_decode(raw, binding));
    SMS_CHECK(!IndexBinding::try_encode(-1, 1, raw));
    SMS_CHECK(!IndexBinding::try_encode(std::numeric_limits<std::int32_t>::max(), 1, raw));
    SMS_CHECK(!IndexBinding::try_encode(0, 0, raw));
    SMS_CHECK(!IndexBinding::try_encode(0, 0x2'0000'0000LL, raw));
    SMS_CHECK(!IndexBinding::try_decode(0, binding));
    SMS_CHECK(!IndexBinding::try_decode(1, binding));
    SMS_CHECK(!IndexBinding::try_decode(1ULL << 31, binding));

    SMS_CHECK(IndexBinding::try_encode(42, 17, raw));
    SpillSummary spill{};
    std::uint64_t spill_empty{};
    SMS_CHECK(SpillSummary::try_encode_empty(raw, spill_empty));
    SMS_CHECK(spill_empty == 0x0000'0000'0110'002bULL);
    std::uint64_t spill_present{};
    SMS_CHECK(SpillSummary::try_encode_present(raw, spill_present));
    SMS_CHECK(spill_present == 0x0020'0000'0110'002bULL);
    SMS_CHECK(SpillSummary::try_decode(spill_present, spill));
    SMS_CHECK(spill.value == spill_present);
    SMS_CHECK(spill.is_present);
    SMS_CHECK(!spill.is_initial());
    SMS_CHECK(spill.slot_index == 42);
    SMS_CHECK(spill.generation == 17);
    SMS_CHECK(spill.binding() == raw);
    SMS_CHECK(spill.empty_value() == spill_empty);
    SMS_CHECK(SpillSummary::try_decode(spill_empty, spill));
    SMS_CHECK(!spill.is_present);
    SMS_CHECK(SpillSummary::try_decode(0, spill));
    SMS_CHECK(spill.is_initial());
    SMS_CHECK(IndexBinding::try_encode(1'048'575, 1, raw));
    SMS_CHECK(!SpillSummary::try_encode_present(raw, spill_present));
    SMS_CHECK(!SpillSummary::try_decode(1ULL << 54, spill));
    SMS_CHECK(!SpillSummary::try_decode(1, spill));
    SMS_CHECK(!SpillSummary::try_decode(1ULL << 20, spill));

    DirectoryLocation location{};
    SMS_CHECK(DirectoryLocation::try_encode(2, 0x023456, 0x1'2345'6789LL, raw));
    SMS_CHECK(raw == 0x0123'4567'8908'd15aULL);
    SMS_CHECK(DirectoryLocation::try_decode(raw, location));
    SMS_CHECK(location.value == raw);
    SMS_CHECK(location.kind == 2);
    SMS_CHECK(location.index == 0x023456);
    SMS_CHECK(location.generation == 0x1'2345'6789LL);
    SMS_CHECK(DirectoryLocation::try_decode(0, location));
    SMS_CHECK(location.value == 0 && location.kind == 0 && location.index == 0 &&
              location.generation == 0);
    SMS_CHECK(!DirectoryLocation::try_encode(0, 0, 1, raw));
    SMS_CHECK(!DirectoryLocation::try_encode(3, 0, 1, raw));
    SMS_CHECK(!DirectoryLocation::try_encode(1, -1, 1, raw));
    SMS_CHECK(!DirectoryLocation::try_encode(1, 1LL << 22, 1, raw));
    SMS_CHECK(!DirectoryLocation::try_encode(1, 0, 0, raw));
    SMS_CHECK(!DirectoryLocation::try_encode(1, 0, 1LL << 33, raw));
    SMS_CHECK(!DirectoryLocation::try_decode(3, location));
    SMS_CHECK(!DirectoryLocation::try_decode(1ULL << 57, location));
    SMS_CHECK(!DirectoryLocation::try_decode(1, location));

    DirectoryOperation operation{};
    SMS_CHECK(DirectoryOperation::try_encode(
        2, 5, 2, 0x023456, 0x1'2345'6789LL, raw));
    SMS_CHECK(raw == 0x2468'acf1'211a'2b56ULL);
    SMS_CHECK(DirectoryOperation::try_decode(raw, operation));
    SMS_CHECK(operation.value == raw);
    SMS_CHECK(operation.intent == 2);
    SMS_CHECK(operation.phase == 5);
    SMS_CHECK(operation.target_kind == 2);
    SMS_CHECK(operation.target_index == 0x023456);
    SMS_CHECK(operation.generation == 0x1'2345'6789LL);
    SMS_CHECK(DirectoryOperation::try_decode(0, operation));
    SMS_CHECK(operation.value == 0 && operation.intent == 0 && operation.phase == 0 &&
              operation.target_kind == 0 && operation.target_index == 0 &&
              operation.generation == 0);
    SMS_CHECK(!DirectoryOperation::try_encode(3, 0, 0, 0, 1, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(0, 6, 0, 0, 1, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(0, 0, 3, 0, 1, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(0, 0, 0, -1, 1, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(0, 0, 0, 1LL << 22, 1, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(1, 1, 1, 0, 0, raw));
    SMS_CHECK(!DirectoryOperation::try_encode(1, 1, 1, 0, 1LL << 33, raw));
    SMS_CHECK(!DirectoryOperation::try_decode(3ULL | (1ULL << 29), operation));
    SMS_CHECK(!DirectoryOperation::try_decode((6ULL << 2) | (1ULL << 29), operation));
    SMS_CHECK(!DirectoryOperation::try_decode((3ULL << 5) | (1ULL << 29), operation));
    SMS_CHECK(!DirectoryOperation::try_decode(1ULL << 62, operation));
    SMS_CHECK(!DirectoryOperation::try_decode(1, operation));
    return 0;
}
