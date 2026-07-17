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

    auto creator_options = sms_test_options("remove-reclaim-v2", 1, 4);
    memory_store creator;
    SMS_CHECK(memory_store::try_create_or_open(creator_options, creator) ==
              open_status::success);

    auto reader_options = creator_options;
    reader_options.mode = open_mode::open_existing;
    memory_store reader;
    SMS_CHECK(memory_store::try_create_or_open(reader_options, reader) ==
              open_status::success);

    const std::array<std::uint8_t, 3> key{0x00, 0x81, 0xff};
    const std::array<std::uint8_t, 4> value{1, 0, 2, 3};
    SMS_CHECK(creator.try_publish(bytes(key), bytes(value)) == status::success);

    value_lease foreign_lease;
    SMS_CHECK(reader.try_acquire(bytes(key), foreign_lease) == status::success);
    SMS_CHECK(creator.try_remove(bytes(key), wait_options::no_wait()) ==
              status::remove_pending);
    value_lease blocked;
    SMS_CHECK(creator.try_acquire(bytes(key), blocked) == status::not_found);
    SMS_CHECK(foreign_lease.valid());
    SMS_CHECK(foreign_lease.value().size() == value.size());
    SMS_CHECK(std::memcmp(
                  foreign_lease.value().data(), value.data(), value.size()) == 0);

    // No participant may reuse the only slot until the exact foreign lease is
    // released. Final release cooperatively unlinks and advances generation.
    const std::array<std::uint8_t, 1> replacement_key{0x90};
    SMS_CHECK(creator.try_publish(bytes(replacement_key), bytes(value), {},
                                  wait_options::no_wait()) ==
              status::store_full);
    SMS_CHECK(foreign_lease.release() == status::success);
    SMS_CHECK(!foreign_lease.valid());
    SMS_CHECK(creator.try_publish(bytes(replacement_key), bytes(value)) ==
              status::success);
    SMS_CHECK(creator.try_remove(bytes(replacement_key)) == status::success);

    // Repeated reuse of the single slot proves that unlink removes every
    // primary/overflow reference before the next generation becomes Free.
    for (std::uint8_t generation = 1; generation < 32; ++generation) {
        const std::array<std::uint8_t, 2> cycle_key{0xa0, generation};
        const std::array<std::uint8_t, 2> cycle_value{generation,
                                                      static_cast<std::uint8_t>(generation ^ 0xff)};
        SMS_CHECK(creator.try_publish(bytes(cycle_key), bytes(cycle_value)) ==
                  status::success);
        value_lease current;
        SMS_CHECK(reader.try_acquire(bytes(cycle_key), current) ==
                  status::success);
        SMS_CHECK(creator.try_remove(bytes(cycle_key)) ==
                  status::remove_pending);
        SMS_CHECK(current.release() == status::success);
        SMS_CHECK(creator.try_acquire(bytes(cycle_key), blocked) ==
                  status::not_found);
    }

    const std::array<std::uint8_t, 1> final_key{0xee};
    SMS_CHECK(creator.try_publish(bytes(final_key), bytes(value)) ==
              status::success);
    SMS_CHECK(creator.try_remove(bytes(final_key)) == status::success);
    SMS_CHECK(creator.try_remove(bytes(final_key)) == status::not_found);

    return 0;
}
