#include "test_support.hpp"

#include <array>
#include <cstring>

int main() {
    using namespace shared_memory_store;
    auto options = sms_test_options("lifecycle", 3, 6);
    memory_store store;
    SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
    const std::array<std::uint8_t, 1> key{1};
    const std::array<std::uint8_t, 2> descriptor{7, 8};
    value_reservation reservation;
    SMS_CHECK(store.try_reserve(sms_test_bytes(key), 5, sms_test_bytes(descriptor), reservation) == status::success);
    SMS_CHECK(reservation.valid());
    SMS_CHECK(reservation.remaining_bytes() == 5);
    SMS_CHECK(reservation.commit() == status::reservation_incomplete);
    auto buffer = reservation.buffer(5);
    SMS_CHECK(buffer.size() == 5);
    const std::array<std::uint8_t, 5> payload{10, 11, 0, 12, 13};
    std::memcpy(buffer.data(), payload.data(), payload.size());
    SMS_CHECK(reservation.advance(6) == status::reservation_write_out_of_range);
    SMS_CHECK(reservation.advance(5) == status::success);
    SMS_CHECK(reservation.commit() == status::success);
    SMS_CHECK(!reservation.valid());

    value_lease lease;
    SMS_CHECK(store.try_acquire(sms_test_bytes(key), lease) == status::success);
    SMS_CHECK(std::memcmp(lease.value().data(), payload.data(), payload.size()) == 0);
    SMS_CHECK(lease.release() == status::success);
    SMS_CHECK(store.try_remove(sms_test_bytes(key)) == status::success);

    const std::array<std::uint8_t, 1> segment_key{2};
    const std::array<std::byte, 2> first{std::byte{1}, std::byte{2}};
    const std::array<std::byte, 3> second{std::byte{3}, std::byte{0}, std::byte{4}};
    const std::array<std::span<const std::byte>, 2> segments{first, second};
    std::int64_t copied{};
    SMS_CHECK(store.try_publish_segments(sms_test_bytes(segment_key), segments, {}, copied) == status::success);
    SMS_CHECK(copied == 5);

    const std::array<std::uint8_t, 1> recover_key{3};
    value_reservation abandoned;
    SMS_CHECK(store.try_reserve(sms_test_bytes(recover_key), 1, {}, abandoned) == status::success);
    recovery_report report{};
    SMS_CHECK(store.try_recover_reservations(true, report) == status::success);
    SMS_CHECK(report.recovered_count == 1);
    SMS_CHECK(!abandoned.valid());
    return 0;
}
