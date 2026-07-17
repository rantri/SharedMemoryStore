#pragma once

#include "layout_v2.hpp"

#include <cstdint>
#include <limits>

namespace sms::detail {
namespace control_word_detail {

inline constexpr std::uint64_t participant_mask = 0x0fff'ffffULL;
inline constexpr std::uint64_t slot_generation_mask = 0x1'ffff'ffffULL;
inline constexpr std::uint64_t binding_index_mask = 0x7fff'ffffULL;

inline std::int32_t required_bits(std::uint32_t distinct_values) noexcept {
    std::int32_t bits = 0;
    std::uint32_t value = distinct_values - 1U;
    do {
        ++bits;
        value >>= 1U;
    } while (value != 0);
    return bits;
}

inline bool valid_participant_count(std::int32_t participant_count) noexcept {
    return participant_count >= 1 &&
        participant_count <= sms2_maximum_participant_count;
}

} // namespace control_word_detail

struct ParticipantControl {
    std::uint64_t value{};
    std::int32_t state{};
    std::int32_t incarnation{};
    std::int32_t process_id{};

    static bool try_encode(
        std::int32_t state_value,
        std::int32_t incarnation_value,
        std::int32_t process_id_value,
        std::uint64_t& result) noexcept {
        if (state_value < 0 || state_value > 6 || incarnation_value < 0 ||
            static_cast<std::uint32_t>(incarnation_value) >
                control_word_detail::participant_mask ||
            process_id_value < 0) {
            return false;
        }
        result = static_cast<std::uint32_t>(state_value) |
            (static_cast<std::uint64_t>(static_cast<std::uint32_t>(incarnation_value)) << 3U) |
            (static_cast<std::uint64_t>(static_cast<std::uint32_t>(process_id_value)) << 31U);
        return true;
    }

    static bool try_decode(std::uint64_t raw, ParticipantControl& result) noexcept {
        if ((raw >> 63U) != 0 || (raw & 0x7ULL) > 6) return false;
        ParticipantControl decoded{};
        decoded.value = raw;
        decoded.state = static_cast<std::int32_t>(raw & 0x7ULL);
        decoded.incarnation = static_cast<std::int32_t>(
            (raw >> 3U) & control_word_detail::participant_mask);
        decoded.process_id = static_cast<std::int32_t>((raw >> 31U) & 0xffff'ffffULL);
        result = decoded;
        return true;
    }

    [[nodiscard]] bool structurally_valid(std::int32_t generation_mask) const noexcept {
        ParticipantControl decoded{};
        if (generation_mask < 1 ||
            static_cast<std::uint32_t>(generation_mask) >
                control_word_detail::participant_mask ||
            !try_decode(value, decoded) || decoded.incarnation < 1 ||
            decoded.incarnation > generation_mask) {
            return false;
        }
        const bool owned = decoded.state >= 1 && decoded.state <= 4;
        return (owned ? decoded.process_id > 0 : decoded.process_id == 0) &&
            (decoded.state != 6 || decoded.incarnation == generation_mask);
    }
};

struct ParticipantToken {
    std::uint64_t value{};
    std::int32_t record_index{};
    std::int32_t generation{};
    std::int32_t index_bits{};
    std::int32_t generation_bits{};

    static bool try_encode(
        std::int32_t record_index_value,
        std::int32_t generation_value,
        std::int32_t participant_count,
        std::uint64_t& result) noexcept {
        if (!control_word_detail::valid_participant_count(participant_count) ||
            record_index_value < 0 || record_index_value >= participant_count) {
            return false;
        }
        const auto index_bit_count = control_word_detail::required_bits(
            static_cast<std::uint32_t>(participant_count) + 1U);
        const auto generation_bit_count = sms2_participant_token_bits - index_bit_count;
        const auto maximum_generation = static_cast<std::uint32_t>(
            (1U << generation_bit_count) - 1U);
        if (generation_value < 1 ||
            static_cast<std::uint32_t>(generation_value) > maximum_generation) {
            return false;
        }
        result = (static_cast<std::uint64_t>(static_cast<std::uint32_t>(generation_value))
                  << index_bit_count) |
            static_cast<std::uint32_t>(record_index_value + 1);
        return true;
    }

    static bool try_decode(
        std::uint64_t raw,
        std::int32_t participant_count,
        ParticipantToken& result) noexcept {
        if (!control_word_detail::valid_participant_count(participant_count) || raw == 0 ||
            raw > control_word_detail::participant_mask) {
            return false;
        }
        const auto index_bit_count = control_word_detail::required_bits(
            static_cast<std::uint32_t>(participant_count) + 1U);
        const auto generation_bit_count = sms2_participant_token_bits - index_bit_count;
        const auto index_mask = (1ULL << index_bit_count) - 1ULL;
        const auto index_plus_one = raw & index_mask;
        const auto generation_value = raw >> index_bit_count;
        const auto maximum_generation = (1ULL << generation_bit_count) - 1ULL;
        if (index_plus_one == 0 ||
            index_plus_one > static_cast<std::uint32_t>(participant_count) ||
            generation_value == 0 || generation_value > maximum_generation) {
            return false;
        }
        ParticipantToken decoded{};
        decoded.value = raw;
        decoded.record_index = static_cast<std::int32_t>(index_plus_one - 1ULL);
        decoded.generation = static_cast<std::int32_t>(generation_value);
        decoded.index_bits = index_bit_count;
        decoded.generation_bits = generation_bit_count;
        result = decoded;
        return true;
    }

    [[nodiscard]] bool structurally_valid(std::int32_t participant_count) const noexcept {
        ParticipantToken decoded{};
        return try_decode(value, participant_count, decoded);
    }
};

struct SlotControl {
    std::uint64_t value{};
    std::int32_t state{};
    std::int64_t generation{};
    std::uint32_t participant_token{};

    static bool try_encode(
        std::int32_t state_value,
        std::int64_t generation_value,
        std::uint32_t participant_token_value,
        std::uint64_t& result) noexcept {
        if (state_value < 0 || state_value > 7 || generation_value < 1 ||
            static_cast<std::uint64_t>(generation_value) >
                control_word_detail::slot_generation_mask ||
            participant_token_value > control_word_detail::participant_mask) {
            return false;
        }
        result = static_cast<std::uint32_t>(state_value) |
            (static_cast<std::uint64_t>(generation_value) << 3U) |
            (static_cast<std::uint64_t>(participant_token_value) << 36U);
        return true;
    }

    static bool try_decode(std::uint64_t raw, SlotControl& result) noexcept {
        const auto generation_value =
            (raw >> 3U) & control_word_detail::slot_generation_mask;
        if (generation_value == 0) return false;
        SlotControl decoded{};
        decoded.value = raw;
        decoded.state = static_cast<std::int32_t>(raw & 0x7ULL);
        decoded.generation = static_cast<std::int64_t>(generation_value);
        decoded.participant_token = static_cast<std::uint32_t>(
            (raw >> 36U) & control_word_detail::participant_mask);
        result = decoded;
        return true;
    }

    [[nodiscard]] bool structurally_valid(
        std::int32_t participant_count,
        bool& occupied) const noexcept {
        occupied = true;
        SlotControl decoded{};
        if (!control_word_detail::valid_participant_count(participant_count) ||
            !try_decode(value, decoded)) {
            return false;
        }
        switch (decoded.state) {
        case 0:
            if (decoded.participant_token != 0) return false;
            occupied = false;
            return true;
        case 1:
        case 2: {
            ParticipantToken participant{};
            return ParticipantToken::try_decode(
                decoded.participant_token, participant_count, participant);
        }
        case 3:
        case 4:
        case 5:
        case 6:
            return decoded.participant_token == 0;
        case 7:
            return decoded.participant_token == 0 &&
                static_cast<std::uint64_t>(decoded.generation) ==
                    control_word_detail::slot_generation_mask;
        default:
            return false;
        }
    }
};

struct LeaseControl {
    std::uint64_t value{};
    std::int32_t state{};
    std::int64_t generation{};
    std::uint32_t participant_token{};

    static bool try_encode(
        std::int32_t state_value,
        std::int64_t generation_value,
        std::uint32_t participant_token_value,
        std::uint64_t& result) noexcept {
        return SlotControl::try_encode(
            state_value, generation_value, participant_token_value, result);
    }

    static bool try_decode(std::uint64_t raw, LeaseControl& result) noexcept {
        SlotControl common{};
        if (!SlotControl::try_decode(raw, common)) return false;
        result = LeaseControl{
            common.value, common.state, common.generation, common.participant_token};
        return true;
    }

    [[nodiscard]] bool structurally_valid(
        std::int32_t participant_count,
        bool& occupied) const noexcept {
        occupied = true;
        LeaseControl decoded{};
        if (!control_word_detail::valid_participant_count(participant_count) ||
            !try_decode(value, decoded)) {
            return false;
        }
        switch (decoded.state) {
        case 0:
            if (decoded.participant_token != 0) return false;
            occupied = false;
            return true;
        case 1:
        case 2: {
            ParticipantToken participant{};
            return ParticipantToken::try_decode(
                decoded.participant_token, participant_count, participant);
        }
        case 3:
        case 4:
            return decoded.participant_token == 0;
        case 5:
            return decoded.participant_token == 0 &&
                static_cast<std::uint64_t>(decoded.generation) ==
                    control_word_detail::slot_generation_mask;
        default:
            return false;
        }
    }
};

struct IndexBinding {
    std::uint64_t value{};
    std::int32_t slot_index{};
    std::int64_t generation{};

    static bool try_encode(
        std::int32_t slot_index_value,
        std::int64_t generation_value,
        std::uint64_t& result) noexcept {
        if (slot_index_value < 0 ||
            slot_index_value == std::numeric_limits<std::int32_t>::max() ||
            generation_value < 1 ||
            static_cast<std::uint64_t>(generation_value) >
                control_word_detail::slot_generation_mask) {
            return false;
        }
        result = (static_cast<std::uint64_t>(generation_value) << 31U) |
            static_cast<std::uint32_t>(slot_index_value + 1);
        return true;
    }

    static bool try_decode(std::uint64_t raw, IndexBinding& result) noexcept {
        const auto index_plus_one = raw & control_word_detail::binding_index_mask;
        const auto generation_value = raw >> 31U;
        if (index_plus_one == 0 || generation_value == 0) return false;
        IndexBinding decoded{};
        decoded.value = raw;
        decoded.slot_index = static_cast<std::int32_t>(index_plus_one - 1ULL);
        decoded.generation = static_cast<std::int64_t>(generation_value);
        result = decoded;
        return true;
    }
};

struct SpillSummary {
    static constexpr std::uint64_t index_mask = (1ULL << 20U) - 1ULL;
    static constexpr std::uint64_t generation_mask = (1ULL << 33U) - 1ULL;
    static constexpr std::uint64_t identity_mask = (1ULL << 53U) - 1ULL;
    static constexpr std::uint64_t present_mask = 1ULL << 53U;
    static constexpr std::uint64_t encoded_mask = (1ULL << 54U) - 1ULL;

    std::uint64_t value{};
    bool is_present{};
    std::int32_t slot_index{};
    std::int64_t generation{};

    static bool try_encode_empty(
        std::uint64_t binding,
        std::uint64_t& result) noexcept {
        return try_encode(binding, false, result);
    }

    static bool try_encode_present(
        std::uint64_t binding,
        std::uint64_t& result) noexcept {
        return try_encode(binding, true, result);
    }

    static bool try_decode(std::uint64_t raw, SpillSummary& result) noexcept {
        if (raw == 0) {
            result = {};
            return true;
        }
        if ((raw & ~encoded_mask) != 0) return false;
        const auto index_plus_one = raw & index_mask;
        const auto generation_value = (raw >> 20U) & generation_mask;
        if (index_plus_one == 0 ||
            index_plus_one > static_cast<std::uint32_t>(sms2_maximum_slot_count) ||
            generation_value == 0) {
            return false;
        }
        SpillSummary decoded{};
        decoded.value = raw;
        decoded.is_present = (raw & present_mask) != 0;
        decoded.slot_index = static_cast<std::int32_t>(index_plus_one - 1ULL);
        decoded.generation = static_cast<std::int64_t>(generation_value);
        result = decoded;
        return true;
    }

    [[nodiscard]] bool is_initial() const noexcept { return value == 0; }

    [[nodiscard]] std::uint64_t binding() const noexcept {
        return is_initial() ? 0 :
            (static_cast<std::uint64_t>(generation) << 31U) |
                static_cast<std::uint32_t>(slot_index + 1);
    }

    [[nodiscard]] std::uint64_t empty_value() const noexcept {
        return value & identity_mask;
    }

private:
    static bool try_encode(
        std::uint64_t binding,
        bool present,
        std::uint64_t& result) noexcept {
        IndexBinding decoded{};
        if (!IndexBinding::try_decode(binding, decoded) || decoded.slot_index < 0 ||
            decoded.slot_index >= sms2_maximum_slot_count) {
            return false;
        }
        result = (static_cast<std::uint64_t>(decoded.generation) << 20U) |
            static_cast<std::uint32_t>(decoded.slot_index + 1);
        if (present) result |= present_mask;
        return true;
    }
};

struct DirectoryLocation {
    static constexpr std::uint64_t index_mask = (1ULL << 22U) - 1ULL;
    static constexpr std::uint64_t generation_mask = (1ULL << 33U) - 1ULL;
    static constexpr std::uint64_t used_bits_mask = (1ULL << 57U) - 1ULL;

    std::uint64_t value{};
    std::int32_t kind{};
    std::int64_t index{};
    std::int64_t generation{};

    static bool try_encode(
        std::int32_t kind_value,
        std::int64_t index_value,
        std::int64_t generation_value,
        std::uint64_t& result) noexcept {
        if (kind_value < 1 || kind_value > 2 || index_value < 0 ||
            static_cast<std::uint64_t>(index_value) > index_mask ||
            generation_value < 1 ||
            static_cast<std::uint64_t>(generation_value) > generation_mask) {
            return false;
        }
        result = static_cast<std::uint32_t>(kind_value) |
            (static_cast<std::uint64_t>(index_value) << 2U) |
            (static_cast<std::uint64_t>(generation_value) << 24U);
        return true;
    }

    static bool try_decode(std::uint64_t raw, DirectoryLocation& result) noexcept {
        if (raw == 0) {
            result = {};
            return true;
        }
        const auto kind_value = static_cast<std::int32_t>(raw & 0x3ULL);
        const auto generation_value = (raw >> 24U) & generation_mask;
        if ((raw & ~used_bits_mask) != 0 || kind_value < 1 || kind_value > 2 ||
            generation_value == 0) {
            return false;
        }
        DirectoryLocation decoded{};
        decoded.value = raw;
        decoded.kind = kind_value;
        decoded.index = static_cast<std::int64_t>((raw >> 2U) & index_mask);
        decoded.generation = static_cast<std::int64_t>(generation_value);
        result = decoded;
        return true;
    }
};

struct DirectoryOperation {
    static constexpr std::uint64_t index_mask = (1ULL << 22U) - 1ULL;
    static constexpr std::uint64_t generation_mask = (1ULL << 33U) - 1ULL;
    static constexpr std::uint64_t used_bits_mask = (1ULL << 62U) - 1ULL;

    std::uint64_t value{};
    std::int32_t intent{};
    std::int32_t phase{};
    std::int32_t target_kind{};
    std::int64_t target_index{};
    std::int64_t generation{};

    static bool try_encode(
        std::int32_t intent_value,
        std::int32_t phase_value,
        std::int32_t target_kind_value,
        std::int64_t target_index_value,
        std::int64_t generation_value,
        std::uint64_t& result) noexcept {
        if (intent_value < 0 || intent_value > 2 || phase_value < 0 ||
            phase_value > 5 || target_kind_value < 0 || target_kind_value > 2 ||
            target_index_value < 0 ||
            static_cast<std::uint64_t>(target_index_value) > index_mask) {
            return false;
        }
        if (intent_value == 0 && phase_value == 0 && target_kind_value == 0 &&
            target_index_value == 0 && generation_value == 0) {
            result = 0;
            return true;
        }
        if (generation_value < 1 ||
            static_cast<std::uint64_t>(generation_value) > generation_mask) {
            return false;
        }
        result = static_cast<std::uint32_t>(intent_value) |
            (static_cast<std::uint64_t>(static_cast<std::uint32_t>(phase_value)) << 2U) |
            (static_cast<std::uint64_t>(static_cast<std::uint32_t>(target_kind_value)) << 5U) |
            (static_cast<std::uint64_t>(target_index_value) << 7U) |
            (static_cast<std::uint64_t>(generation_value) << 29U);
        return true;
    }

    static bool try_decode(std::uint64_t raw, DirectoryOperation& result) noexcept {
        if (raw == 0) {
            result = {};
            return true;
        }
        const auto intent_value = static_cast<std::int32_t>(raw & 0x3ULL);
        const auto phase_value = static_cast<std::int32_t>((raw >> 2U) & 0x7ULL);
        const auto target_kind_value = static_cast<std::int32_t>((raw >> 5U) & 0x3ULL);
        const auto generation_value = (raw >> 29U) & generation_mask;
        if ((raw & ~used_bits_mask) != 0 || generation_value == 0 ||
            intent_value > 2 || phase_value > 5 || target_kind_value > 2) {
            return false;
        }
        DirectoryOperation decoded{};
        decoded.value = raw;
        decoded.intent = intent_value;
        decoded.phase = phase_value;
        decoded.target_kind = target_kind_value;
        decoded.target_index = static_cast<std::int64_t>((raw >> 7U) & index_mask);
        decoded.generation = static_cast<std::int64_t>(generation_value);
        result = decoded;
        return true;
    }
};

} // namespace sms::detail
