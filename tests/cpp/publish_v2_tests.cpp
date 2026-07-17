#include "test_support.hpp"

#include <array>
#include <cstddef>
#include <cstring>
#include <span>

namespace {

template <std::size_t N>
std::span<const std::byte> bytes(const std::array<std::uint8_t, N>& value) {
    return {
        reinterpret_cast<const std::byte*>(value.data()),
        value.size()};
}

} // namespace

int main() {
    using namespace shared_memory_store;

    auto options = sms_test_options("publish-v2", 2, 4);
    memory_store store;
    SMS_CHECK(memory_store::try_create_or_open(options, store) ==
              open_status::success);

    const std::array<std::uint8_t, 3> segmented_key{0x10, 0x00, 0x20};
    const std::array<std::uint8_t, 2> first{0x01, 0x00};
    const std::array<std::uint8_t, 3> second{0x02, 0x03, 0xff};
    const std::array<std::uint8_t, 2> descriptor{0x40, 0x00};
    const std::array<std::span<const std::byte>, 3> segments{
        bytes(first), std::span<const std::byte>{}, bytes(second)};
    std::int64_t copied{-1};
    SMS_CHECK(store.try_publish_segments(
                  bytes(segmented_key), segments, bytes(descriptor), copied) ==
              status::success);
    SMS_CHECK(copied == 5);

    value_lease lease;
    SMS_CHECK(store.try_acquire(bytes(segmented_key), lease) == status::success);
    const std::array<std::uint8_t, 5> expected{0x01, 0x00, 0x02, 0x03, 0xff};
    SMS_CHECK(lease.value().size() == expected.size());
    SMS_CHECK(std::memcmp(
                  lease.value().data(), expected.data(), expected.size()) == 0);
    SMS_CHECK(lease.descriptor().size() == descriptor.size());
    SMS_CHECK(std::memcmp(
                  lease.descriptor().data(), descriptor.data(),
                  descriptor.size()) == 0);
    SMS_CHECK(lease.release() == status::success);

    // A duplicate remains a duplicate even when every slot is otherwise in
    // use: lookup precedes the stable StoreFull proof.
    const std::array<std::uint8_t, 1> second_key{0x33};
    const std::array<std::uint8_t, 1> second_value{0x44};
    SMS_CHECK(store.try_publish(bytes(second_key), bytes(second_value)) ==
              status::success);
    SMS_CHECK(store.try_publish(bytes(segmented_key), bytes(second_value)) ==
              status::duplicate_key);
    const std::array<std::uint8_t, 1> full_key{0x55};
    SMS_CHECK(store.try_publish(bytes(full_key), bytes(second_value)) ==
              status::store_full);

    SMS_CHECK(store.try_remove(bytes(second_key)) == status::success);

    // Explicit reservations stay invisible until every declared payload byte
    // has been advanced and commit publishes the slot with release ordering.
    const std::array<std::uint8_t, 2> reserved_key{0x60, 0x00};
    const std::array<std::uint8_t, 1> reserved_descriptor{0x70};
    value_reservation reservation;
    SMS_CHECK(store.try_reserve(
                  bytes(reserved_key), 5, bytes(reserved_descriptor),
                  reservation) == status::success);
    SMS_CHECK(reservation.valid());
    SMS_CHECK(reservation.payload_length() == 5);
    SMS_CHECK(store.try_acquire(bytes(reserved_key), lease) ==
              status::not_found);
    auto writable = reservation.buffer();
    SMS_CHECK(writable.size() == expected.size());
    std::memcpy(writable.data(), expected.data(), expected.size());
    SMS_CHECK(reservation.advance(2) == status::success);
    SMS_CHECK(reservation.bytes_written() == 2);
    SMS_CHECK(reservation.commit() == status::reservation_incomplete);
    SMS_CHECK(reservation.advance(3) == status::success);
    SMS_CHECK(reservation.commit() == status::success);
    SMS_CHECK(!reservation.valid());
    SMS_CHECK(store.try_acquire(bytes(reserved_key), lease) == status::success);
    SMS_CHECK(lease.value().size() == expected.size());
    SMS_CHECK(std::memcmp(
                  lease.value().data(), expected.data(), expected.size()) == 0);
    SMS_CHECK(lease.release() == status::success);

    SMS_CHECK(store.try_remove(bytes(reserved_key)) == status::success);

    const std::array<std::uint8_t, 1> abort_key{0x71};
    SMS_CHECK(store.try_reserve(
                  bytes(abort_key), 1, {}, reservation) == status::success);
    SMS_CHECK(reservation.abort() == status::success);
    SMS_CHECK(!reservation.valid());
    SMS_CHECK(store.try_acquire(bytes(abort_key), lease) == status::not_found);
    SMS_CHECK(store.try_publish(bytes(abort_key), bytes(second_value)) ==
              status::success);

    const std::array<std::uint8_t, 1> canceled_key{0x72};
    cancellation_source canceled;
    SMS_CHECK(canceled.signal() == status::success);
    copied = -1;
    SMS_CHECK(store.try_publish_segments(
                  bytes(canceled_key), segments, {}, copied,
                  wait_options::infinite(canceled.token())) ==
              status::operation_canceled);
    SMS_CHECK(copied == 0);
    SMS_CHECK(store.try_acquire(bytes(canceled_key), lease) ==
              status::not_found);

    return 0;
}
