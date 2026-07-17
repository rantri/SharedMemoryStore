#pragma once

#include "control_words.hpp"
#include "key_directory.hpp"
#include "layout_v2.hpp"
#include "mapped_atomic.hpp"

#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <span>
#include <stdexcept>
#include <string_view>
#include <vector>

namespace sms::test::directory {

inline std::span<const std::byte> bytes(std::string_view value) noexcept {
    return {
        reinterpret_cast<const std::byte*>(value.data()),
        value.size()};
}

class Fixture {
public:
    explicit Fixture(
        std::int32_t slots = 32,
        std::int32_t max_key = 128,
        sms::detail::DirectoryHooks hooks = {}) {
        sms::detail::LayoutV2 provisional{};
        if (!sms::detail::LayoutV2::calculate(
                0, slots, 16, 8, max_key, 8, 8, provisional) ||
            !sms::detail::LayoutV2::calculate(
                provisional.required_bytes,
                slots,
                16,
                8,
                max_key,
                8,
                8,
                layout_)) {
            throw std::runtime_error("Could not calculate the directory fixture layout.");
        }
        words_.resize(
            (static_cast<std::size_t>(layout_.required_bytes) +
             sizeof(std::uint64_t) - 1U) /
            sizeof(std::uint64_t));
        directory_ = new sms::detail::KeyDirectory(
            base(), words_.size() * sizeof(std::uint64_t), layout_, hooks);
        if (!directory_->valid()) {
            throw std::runtime_error("The directory fixture mapping is invalid.");
        }
    }

    ~Fixture() { delete directory_; }
    Fixture(const Fixture&) = delete;
    Fixture& operator=(const Fixture&) = delete;

    [[nodiscard]] sms::detail::KeyDirectory& directory() noexcept {
        return *directory_;
    }
    [[nodiscard]] const sms::detail::LayoutV2& layout() const noexcept {
        return layout_;
    }
    [[nodiscard]] std::uint8_t* base() noexcept {
        return reinterpret_cast<std::uint8_t*>(words_.data());
    }

    [[nodiscard]] sms::detail::ValueSlotMetadataV2& slot(
        std::int32_t index) noexcept {
        return *reinterpret_cast<sms::detail::ValueSlotMetadataV2*>(
            base() + layout_.slot_metadata_offset +
            static_cast<std::int64_t>(index) * layout_.slot_metadata_stride);
    }

    [[nodiscard]] std::uint64_t seed_slot(
        std::int32_t index,
        std::string_view key,
        std::uint64_t hash,
        std::int64_t generation = 1,
        std::int32_t state = 3) {
        if (index < 0 || index >= layout_.slot_count || key.empty() ||
            key.size() > static_cast<std::size_t>(layout_.max_key_bytes)) {
            throw std::invalid_argument("Invalid seeded slot.");
        }
        auto& value = slot(index);
        value = {};
        std::uint64_t binding{};
        if (!sms::detail::IndexBinding::try_encode(index, generation, binding)) {
            throw std::runtime_error("Could not encode fixture binding.");
        }
        const auto key_offset = layout_.key_storage_offset +
            static_cast<std::int64_t>(index) * layout_.key_stride;
        std::memcpy(base() + key_offset, key.data(), key.size());
        value.DirectoryBinding = binding;
        value.KeyHash = hash;
        value.KeyLength = static_cast<std::int32_t>(key.size());
        value.KeyOffset = key_offset;
        value.DescriptorOffset = layout_.descriptor_storage_offset +
            static_cast<std::int64_t>(index) * layout_.descriptor_stride;
        value.PayloadOffset = layout_.payload_storage_offset +
            static_cast<std::int64_t>(index) * layout_.payload_stride;
        value.PublicationIntent = state == 1 ? 2 : 1;

        std::uint32_t participant = 0;
        if (state == 1 || state == 2) {
            std::uint64_t encoded_token{};
            if (!sms::detail::ParticipantToken::try_encode(
                    0, 1, layout_.participant_record_count, encoded_token)) {
                throw std::runtime_error("Could not encode fixture participant token.");
            }
            participant = static_cast<std::uint32_t>(encoded_token);
        }
        std::uint64_t control{};
        if (!sms::detail::SlotControl::try_encode(
                state, generation, participant, control)) {
            throw std::runtime_error("Could not encode fixture slot control.");
        }
        sms::detail::MappedAtomic64::store_release(value.Control, control);
        return binding;
    }

    void set_slot_state(
        std::int32_t index,
        std::int64_t generation,
        std::int32_t state) {
        std::uint32_t participant = 0;
        if (state == 1 || state == 2) {
            std::uint64_t encoded_token{};
            if (!sms::detail::ParticipantToken::try_encode(
                    0, 1, layout_.participant_record_count, encoded_token)) {
                throw std::runtime_error("Could not encode fixture participant token.");
            }
            participant = static_cast<std::uint32_t>(encoded_token);
        }
        std::uint64_t control{};
        if (!sms::detail::SlotControl::try_encode(
                state, generation, participant, control)) {
            throw std::runtime_error("Could not encode fixture slot control.");
        }
        sms::detail::MappedAtomic64::store_release(slot(index).Control, control);
    }

    [[nodiscard]] std::uint64_t& primary(std::int64_t absolute_index) noexcept {
        const auto bucket =
            absolute_index / sms::detail::sms2_primary_lanes_per_bucket;
        const auto lane =
            absolute_index % sms::detail::sms2_primary_lanes_per_bucket;
        const auto offset = layout_.primary_directory_offset +
            bucket * layout_.primary_bucket_stride + 16 +
            lane * static_cast<std::int64_t>(sizeof(std::uint64_t));
        return *reinterpret_cast<std::uint64_t*>(base() + offset);
    }

    [[nodiscard]] std::uint64_t& overflow(std::int64_t index) noexcept {
        return *reinterpret_cast<std::uint64_t*>(
            base() + layout_.overflow_directory_offset +
            index * layout_.overflow_stride);
    }

    [[nodiscard]] std::uint64_t& spill(std::int32_t bucket) noexcept {
        return *reinterpret_cast<std::uint64_t*>(
            base() + layout_.primary_directory_offset +
            static_cast<std::int64_t>(bucket) * layout_.primary_bucket_stride);
    }

    [[nodiscard]] std::uint64_t& mutation(std::int32_t bucket) noexcept {
        return *reinterpret_cast<std::uint64_t*>(
            base() + layout_.primary_directory_offset +
            static_cast<std::int64_t>(bucket) * layout_.primary_bucket_stride + 8);
    }

    [[nodiscard]] sms::detail::DirectoryLocation location(
        std::int32_t kind,
        std::int64_t index,
        std::int64_t generation) const {
        std::uint64_t raw{};
        sms::detail::DirectoryLocation decoded{};
        if (!sms::detail::DirectoryLocation::try_encode(
                kind, index, generation, raw) ||
            !sms::detail::DirectoryLocation::try_decode(raw, decoded)) {
            throw std::runtime_error("Could not encode fixture directory location.");
        }
        return decoded;
    }

    void publish_reference(
        std::uint64_t binding,
        const sms::detail::DirectoryLocation& location) {
        sms::detail::IndexBinding decoded{};
        if (!sms::detail::IndexBinding::try_decode(binding, decoded)) {
            throw std::runtime_error("Could not decode fixture binding.");
        }
        auto& target = location.kind == sms::detail::directory_target_primary
            ? primary(location.index)
            : overflow(location.index);
        sms::detail::MappedAtomic64::store_release(target, binding);
        sms::detail::MappedAtomic64::store_release(
            slot(decoded.slot_index).DirectoryLocation, location.value);
        std::uint64_t operation{};
        if (!sms::detail::DirectoryOperation::try_encode(
                sms::detail::directory_intent_insert,
                sms::detail::directory_phase_complete,
                location.kind,
                location.index,
                decoded.generation,
                operation)) {
            throw std::runtime_error("Could not encode fixture operation.");
        }
        sms::detail::MappedAtomic64::store_release(
            slot(decoded.slot_index).DirectoryOperation, operation);
    }

private:
    sms::detail::LayoutV2 layout_{};
    std::vector<std::uint64_t> words_;
    sms::detail::KeyDirectory* directory_{};
};

} // namespace sms::test::directory
