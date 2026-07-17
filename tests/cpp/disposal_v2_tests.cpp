#include "lifecycle_gate.hpp"
#include "test_support.hpp"

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <iostream>
#include <thread>

using namespace sms::detail;

namespace {

std::atomic<int> failures{};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

void entered_operation_drains_before_close() {
    LifecycleGate gate;
    LifecycleGate::Operation entered;
    expect(gate.try_enter(entered) == SMS_STATUS_SUCCESS,
           "initial operation enters");

    std::atomic<bool> close_started{};
    std::atomic<bool> close_owned{};
    std::atomic<bool> close_returned{};
    std::thread closer([&] {
        close_started.store(true, std::memory_order_release);
        close_owned.store(
            gate.begin_close_and_drain(), std::memory_order_release);
        gate.complete_close();
        close_returned.store(true, std::memory_order_release);
    });

    while (!close_started.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }
    LifecycleGate::Operation rejected;
    for (int attempt = 0; attempt < 10'000 && gate.is_open(); ++attempt) {
        std::this_thread::yield();
    }
    expect(gate.try_enter(rejected) == SMS_STATUS_STORE_DISPOSED,
           "closing rejects new operation entry");
    expect(!close_returned.load(std::memory_order_acquire),
           "close waits for entered operation");

    entered.reset();
    closer.join();
    expect(close_owned.load(std::memory_order_acquire),
           "one closer owns teardown");
    expect(close_returned.load(std::memory_order_acquire),
           "close returns after drain");
    expect(!gate.is_open(), "closed gate remains closed");
}

void concurrent_close_waits_for_owner_completion() {
    LifecycleGate gate;
    std::atomic<bool> owner_began{};
    std::atomic<bool> allow_completion{};
    std::atomic<bool> observer_returned{};

    std::thread owner([&] {
        expect(gate.begin_close_and_drain(), "first close owns teardown");
        owner_began.store(true, std::memory_order_release);
        while (!allow_completion.load(std::memory_order_acquire)) {
            std::this_thread::yield();
        }
        gate.complete_close();
    });
    while (!owner_began.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }
    std::thread observer([&] {
        expect(!gate.begin_close_and_drain(),
               "concurrent close observes existing owner");
        observer_returned.store(true, std::memory_order_release);
    });
    std::this_thread::sleep_for(std::chrono::milliseconds(2));
    expect(!observer_returned.load(std::memory_order_acquire),
           "concurrent close waits for teardown completion");
    allow_completion.store(true, std::memory_order_release);
    owner.join();
    observer.join();
    expect(observer_returned.load(std::memory_order_acquire),
           "concurrent close returns after completion");
}

void store_close_publishes_handoff_cleans_ownership_and_invalidates_views() {
    using namespace shared_memory_store;

    auto creator_options = sms_test_options("disposal-owned-resources", 3, 3);
    creator_options.participant_record_count = 3;
    creator_options.total_bytes = store_options::calculate_required_bytes(
        creator_options.slot_count,
        creator_options.max_value_bytes,
        creator_options.max_descriptor_bytes,
        creator_options.max_key_bytes,
        creator_options.lease_record_count,
        creator_options.participant_record_count);
    memory_store creator;
    expect(memory_store::try_create_or_open(creator_options, creator) ==
               open_status::success,
           "create disposal fixture");
    auto peer_options = creator_options;
    peer_options.mode = open_mode::open_existing;
    memory_store peer;
    expect(memory_store::try_create_or_open(peer_options, peer) ==
               open_status::success,
           "open disposal observer");

    const std::array<std::byte, 1> published_key{std::byte{1}};
    const std::array<std::byte, 3> published_value{
        std::byte{7}, std::byte{0}, std::byte{9}};
    const std::array<std::byte, 1> reserved_key{std::byte{2}};
    expect(creator.try_publish(published_key, published_value) ==
               status::success,
           "publish before close");
    value_lease lease;
    expect(creator.try_acquire(published_key, lease) == status::success &&
               lease.valid() && lease.value().size() == published_value.size(),
           "hold borrowed lease before close");
    value_reservation reservation;
    expect(creator.try_reserve(
               reserved_key, 4, {}, reservation) == status::success &&
               reservation.valid() && reservation.buffer().size() == 4,
           "hold writable reservation before close");

    diagnostics_snapshot before;
    expect(peer.try_get_diagnostics(before) == status::success &&
               before.active_participant_count() == 2 &&
               before.active_lease_count() == 1 &&
               before.active_reservation_count() == 1,
           "observer sees exact owned resources before close");

    creator.close();
    expect(!creator.valid(), "store handle closes");
    expect(!lease.valid() && lease.value().empty() &&
               lease.descriptor().empty(),
           "borrowed lease projection invalidates on store close");
    expect(!reservation.valid() && reservation.buffer().empty(),
           "writable reservation projection invalidates on store close");

    diagnostics_snapshot after;
    expect(peer.try_get_diagnostics(after) == status::success &&
               after.active_participant_count() == 1 &&
               after.free_participant_count() == 2 &&
               after.active_lease_count() == 0 &&
               after.active_reservation_count() == 0 &&
               after.free_lease_count() == 3 &&
               after.free_slot_count() == 2 &&
               after.published_slot_count() == 1,
           "Closing handoff releases resources before participant reuse");
    expect(peer.try_remove(published_key) == status::success,
           "released close-time lease permits final removal");
    expect(peer.try_get_diagnostics(after) == status::success &&
               after.free_slot_count() == 3,
           "final reclaim restores all slot capacity");
}

} // namespace

int main() {
    entered_operation_drains_before_close();
    concurrent_close_waits_for_owner_completion();
    store_close_publishes_handoff_cleans_ownership_and_invalidates_views();
    const auto count = failures.load(std::memory_order_relaxed);
    if (count == 0) {
        std::cout << "disposal_v2_tests: PASS\n";
        return 0;
    }
    std::cerr << "disposal_v2_tests: " << count << " failure(s)\n";
    return 1;
}
