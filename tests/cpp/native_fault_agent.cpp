#include "checkpoint.hpp"

#include <shared_memory_store/store.hpp>

#include <algorithm>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr int invalid_arguments = 64;
constexpr int open_failed = 65;
constexpr int operation_failed = 66;
constexpr int checkpoint_failed = 68;

bool parse_positive(std::string_view text, std::int32_t& result) noexcept {
    result = 0;
    const auto parsed = std::from_chars(
        text.data(), text.data() + text.size(), result);
    return parsed.ec == std::errc{} &&
        parsed.ptr == text.data() + text.size() && result > 0;
}

int hex_digit(char value) noexcept {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    return -1;
}

bool parse_hex(std::string_view text, std::vector<std::byte>& result) {
    result.clear();
    if (text.empty() || (text.size() & 1U) != 0U) return false;
    result.reserve(text.size() / 2U);
    for (std::size_t index = 0; index < text.size(); index += 2U) {
        const auto high = hex_digit(text[index]);
        const auto low = hex_digit(text[index + 1U]);
        if (high < 0 || low < 0) return false;
        result.push_back(static_cast<std::byte>((high << 4) | low));
    }
    return true;
}

bool bytes_equal(
    std::span<const std::byte> left,
    std::span<const std::byte> right) noexcept {
    return left.size() == right.size() &&
        std::equal(left.begin(), left.end(), right.begin());
}

} // namespace

int main(int argc, char** argv) {
    using namespace shared_memory_store;
    if (argc != 13) return invalid_arguments;

    const std::string_view command(argv[1]);
    std::int32_t slot_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t lease_count{};
    std::int32_t participant_count{};
    std::vector<std::byte> key;
    std::vector<std::byte> value;
    if (!parse_positive(argv[3], slot_count) ||
        !parse_positive(argv[4], max_value_bytes) ||
        !parse_positive(argv[5], max_descriptor_bytes) ||
        !parse_positive(argv[6], max_key_bytes) ||
        !parse_positive(argv[7], lease_count) ||
        !parse_positive(argv[8], participant_count) ||
        !parse_hex(argv[9], key) || !parse_hex(argv[10], value) ||
        key.size() > static_cast<std::size_t>(max_key_bytes) ||
        value.size() > static_cast<std::size_t>(max_value_bytes)) {
        return invalid_arguments;
    }

    auto options = store_options::create(
        argv[2],
        slot_count,
        max_value_bytes,
        max_descriptor_bytes,
        max_key_bytes,
        lease_count,
        participant_count,
        open_mode::open_existing,
        true);
    memory_store store;
    if (memory_store::try_create_or_open(options, store) !=
        open_status::success) {
        return open_failed;
    }

    const sms::test_detail::FileCheckpoint checkpoint(argv[11], argv[12]);
    if (command == "idle") {
        return checkpoint.reach("ParticipantAfterActivePublication")
            ? 0
            : checkpoint_failed;
    }
    if (command == "pause-before-publish") {
        if (!checkpoint.reach("PublishBeforeSlotClaim")) {
            return checkpoint_failed;
        }
        return store.try_publish(key, value) == status::success
            ? 0
            : operation_failed;
    }
    if (command == "publish-and-pause") {
        if (store.try_publish(key, value) != status::success) {
            return operation_failed;
        }
        return checkpoint.reach("PublishAfterCommitPublication")
            ? 0
            : checkpoint_failed;
    }
    if (command == "hold-reservation" ||
        command == "crash-reservation") {
        value_reservation reservation;
        if (store.try_reserve(
                key,
                static_cast<std::int32_t>(value.size()),
                {},
                reservation) != status::success) {
            return operation_failed;
        }
        auto destination = reservation.buffer(
            static_cast<std::int32_t>(value.size()));
        if (destination.size() != value.size()) return operation_failed;
        std::copy(value.begin(), value.end(), destination.begin());
        if (!value.empty() && reservation.advance(
                static_cast<std::int32_t>(value.size())) != status::success) {
            return operation_failed;
        }
        if (!checkpoint.reach(
                "ReserveAfterReservationPublication",
                command == "crash-reservation")) {
            return checkpoint_failed;
        }
        return reservation.abort() == status::success
            ? 0
            : operation_failed;
    }
    if (command == "hold-lease" || command == "crash-lease") {
        value_lease lease;
        if (store.try_acquire(key, lease) != status::success ||
            !bytes_equal(lease.value(), value)) {
            return operation_failed;
        }
        if (!checkpoint.reach(
                "AcquireAfterPublishedRevalidation",
                command == "crash-lease")) {
            return checkpoint_failed;
        }
        return lease.release() == status::success
            ? 0
            : operation_failed;
    }
    return invalid_arguments;
}
