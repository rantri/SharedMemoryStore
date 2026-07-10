#include "test_support.hpp"

#include <array>
#include <cstring>

int main() {
    using namespace shared_memory_store;
    auto options = sms_test_options("store");
    memory_store store;
    SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);

    const std::array<std::uint8_t, 3> key{0, 1, 255};
    const std::array<std::uint8_t, 5> value{9, 0, 8, 7, 255};
    const std::array<std::uint8_t, 2> descriptor{4, 0};
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(value), sms_test_bytes(descriptor)) == status::success);
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(value)) == status::duplicate_key);

    value_lease lease;
    SMS_CHECK(store.try_acquire(sms_test_bytes(key), lease) == status::success);
    SMS_CHECK(lease.valid());
    SMS_CHECK(lease.value().size() == value.size());
    SMS_CHECK(std::memcmp(lease.value().data(), value.data(), value.size()) == 0);
    SMS_CHECK(std::memcmp(lease.descriptor().data(), descriptor.data(), descriptor.size()) == 0);
    SMS_CHECK(store.try_remove(sms_test_bytes(key)) == status::remove_pending);
    SMS_CHECK(lease.value().size() == value.size());
    SMS_CHECK(lease.release() == status::success);
    SMS_CHECK(!lease.valid());
    SMS_CHECK(lease.release() == status::lease_already_released);
    SMS_CHECK(store.try_acquire(sms_test_bytes(key), lease) == status::not_found);

    const std::array<std::uint8_t, 1> replacement{42};
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(replacement)) == status::success);
    SMS_CHECK(store.try_remove(sms_test_bytes(key)) == status::success);
    SMS_CHECK(store.try_remove(sms_test_bytes(key)) == status::not_found);
    store.close();
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(value)) == status::store_disposed);
    return 0;
}
