#include "test_support.hpp"

#include <array>

int main() {
    using namespace shared_memory_store;
    auto options = sms_test_options("diagnostics", 2, 4);
    memory_store store;
    SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
    const std::array<std::uint8_t, 1> missing{9};
    value_lease lease;
    SMS_CHECK(store.try_acquire(sms_test_bytes(missing), lease) == status::not_found);
    const std::array<std::uint8_t, 1> key{1};
    const std::array<std::uint8_t, 1> value{2};
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(value)) == status::success);
    diagnostics_snapshot diagnostics;
    SMS_CHECK(store.try_get_diagnostics(diagnostics) == status::success);
    SMS_CHECK(diagnostics.slot_count() == 2);
    SMS_CHECK(diagnostics.published_slot_count() == 1);
    SMS_CHECK(diagnostics.free_slot_count() == 1);
    SMS_CHECK(diagnostics.occupied_index_entry_count() == 1);
    SMS_CHECK(diagnostics.failure_count(status::not_found) == 1);
    SMS_CHECK(diagnostics.last_failure_status() == status::not_found);
    return 0;
}
