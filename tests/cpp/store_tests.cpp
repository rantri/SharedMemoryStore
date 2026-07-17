#include "test_support.hpp"

#include <array>
#include <atomic>
#include <cstring>
#include <thread>
#include <type_traits>
#include <vector>

static_assert(!std::is_copy_constructible_v<shared_memory_store::memory_store>);
static_assert(std::is_nothrow_move_constructible_v<shared_memory_store::memory_store>);
static_assert(!std::is_copy_constructible_v<shared_memory_store::value_lease>);
static_assert(std::is_nothrow_move_constructible_v<shared_memory_store::value_lease>);
static_assert(!std::is_copy_constructible_v<shared_memory_store::value_reservation>);
static_assert(std::is_nothrow_move_constructible_v<shared_memory_store::value_reservation>);
static_assert(!std::is_copy_constructible_v<shared_memory_store::cancellation_source>);
static_assert(std::is_nothrow_move_constructible_v<shared_memory_store::cancellation_source>);

int main() {
    using namespace shared_memory_store;
    auto undersized_options = sms_test_options("undersized-open");
    const auto required_bytes = undersized_options.total_bytes;
    undersized_options.total_bytes = 1;
    memory_store undersized;
    SMS_CHECK(memory_store::try_create_or_open(
                  undersized_options, undersized) ==
              open_status::insufficient_capacity);
    SMS_CHECK(!undersized.valid());
    // The failed preflight must not create a platform resource under the name.
    undersized_options.total_bytes = required_bytes;
    SMS_CHECK(memory_store::try_create_or_open(
                  undersized_options, undersized) == open_status::success);
    undersized.close();

    auto options = sms_test_options("store");
    memory_store store;
    SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
    SMS_CHECK(options.participant_record_count == 64);
    SMS_CHECK((store.protocol() == protocol_info{2, 0, 2, 7, 0}));

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

    cancellation_source cancellation;
    const auto canceled_wait = wait_options::defaults(cancellation.token());
    SMS_CHECK(cancellation.signal() == status::success);
    SMS_CHECK(cancellation.is_signaled());
    SMS_CHECK(store.try_acquire(sms_test_bytes(key), lease, canceled_wait) ==
              status::operation_canceled);

    std::atomic<bool> start_close_race{};
    std::atomic<bool> stop_workers{};
    std::atomic<std::int32_t> entered_calls{};
    std::atomic<std::int32_t> unexpected_statuses{};
    std::vector<std::thread> workers;
    for (int index = 0; index < 8; ++index) {
        workers.emplace_back([&] {
            while (!start_close_race.load(std::memory_order_acquire)) {
                std::this_thread::yield();
            }
            while (!stop_workers.load(std::memory_order_acquire)) {
                diagnostics_snapshot snapshot;
                entered_calls.fetch_add(1, std::memory_order_release);
                const auto result = store.try_get_diagnostics(snapshot);
                if (result == status::store_disposed) break;
                if (result != status::success) {
                    unexpected_statuses.fetch_add(1, std::memory_order_relaxed);
                    break;
                }
            }
        });
    }
    start_close_race.store(true, std::memory_order_release);
    while (entered_calls.load(std::memory_order_acquire) < 8) {
        std::this_thread::yield();
    }
    std::thread first_close([&] { store.close(); });
    std::thread second_close([&] { store.close(); });
    first_close.join();
    second_close.join();
    stop_workers.store(true, std::memory_order_release);
    for (auto& worker : workers) worker.join();
    SMS_CHECK(unexpected_statuses.load(std::memory_order_acquire) == 0);

    SMS_CHECK(store.protocol() == protocol_info{});
    SMS_CHECK(store.try_publish(sms_test_bytes(key), sms_test_bytes(value)) == status::store_disposed);

    for (int iteration = 0; iteration < 64; ++iteration) {
        memory_store reopened;
        SMS_CHECK(memory_store::try_create_or_open(options, reopened) ==
                  open_status::success);
        reopened.close();
        reopened.close();
    }
    return 0;
}
