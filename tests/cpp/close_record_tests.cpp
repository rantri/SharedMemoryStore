#include "checkpoint.hpp"
#include "internal.hpp"
#include "test_support.hpp"

#include <atomic>
#include <thread>

using namespace sms::detail;
using namespace sms::test_detail;

namespace {
class FailureAccessObserver final : public CheckpointObserver {
public:
    int accesses{};
    void reach(CheckpointId checkpoint) noexcept override {
        if (checkpoint == CheckpointId::StoreBeforeFailureStateAccess) ++accesses;
    }
};

class PausedClose final : public CheckpointObserver {
public:
    std::atomic<bool> paused{};
    std::atomic<bool> resume{};
    void reach(CheckpointId checkpoint) noexcept override {
        if (checkpoint != CheckpointId::DisposalAfterParticipantClosingPublication) return;
        paused.store(true, std::memory_order_release);
        while (!resume.load(std::memory_order_acquire)) std::this_thread::yield();
    }
};

// Test every failure-recording entry point while the closer owns teardown.
// These calls have no entered-operation guard and must never inspect State,
// even while a paused closer happens to keep that allocation alive.
bool rejected_calls_avoid_state(Store& store) {
    FailureAccessObserver observer;
    ScopedCheckpointObserver observing(observer);
    const std::uint8_t key_byte{42};
    const std::span<const std::uint8_t> key{&key_byte, 1};
    const Wait wait{0};
    LifecycleId lifecycle{};
    std::int32_t slot{}, lease{};
    std::int64_t copied{};
    RecoveryReport report{};
    Diagnostics diagnostics{};
    bool ok = true;
    const auto rejected = [&](sms_status result) {
        ok = ok && result == SMS_STATUS_STORE_DISPOSED;
    };
    rejected(store.publish(key, {}, {}, wait));
    rejected(store.publish_segments(key, {}, {}, wait, copied));
    rejected(store.acquire(key, wait, slot, lifecycle, lease));
    rejected(store.release_lease(slot, lifecycle, lease, wait));
    rejected(store.remove(key, wait));
    rejected(store.reserve(key, 1, {}, wait, slot, lifecycle));
    rejected(store.advance_reservation(slot, lifecycle, 0, wait));
    rejected(store.commit_reservation(slot, lifecycle, wait));
    rejected(store.abort_reservation(slot, lifecycle, true, wait));
    rejected(store.recover_leases(false, wait, report));
    rejected(store.recover_reservations(false, wait, report));
    rejected(store.diagnostics(wait, diagnostics));
    if (observer.accesses != 0) {
        std::cerr << "Rejected operations accessed closing State "
                  << observer.accesses << " times\n";
    }
    return ok && observer.accesses == 0;
}
} // namespace

int main() {
    Options options{};
    options.name = sms_test_name("close-failure-record");
    options.open_mode = SMS_OPEN_MODE_CREATE_NEW;
    options.slot_count = 2;
    options.max_value_bytes = 8;
    options.max_descriptor_bytes = 8;
    options.max_key_bytes = 8;
    options.lease_record_count = 2;
    options.participant_record_count = 2;
    options.total_bytes = shared_memory_store::store_options::calculate_required_bytes(
        2, 8, 8, 8, 2, 2);
    std::shared_ptr<Store> store;
    SMS_CHECK(Store::open(options, Wait{1000}, store) == SMS_OPEN_SUCCESS);

    // Ordinary entered failures still contribute to diagnostics.
    SMS_CHECK(store->publish({}, {}, {}, Wait{1000}) == SMS_STATUS_INVALID_KEY);
    Diagnostics diagnostics{};
    SMS_CHECK(store->diagnostics(Wait{1000}, diagnostics) == SMS_STATUS_SUCCESS);
    SMS_CHECK(diagnostics.failures[SMS_STATUS_INVALID_KEY] == 1);

    PausedClose checkpoint;
    std::thread closer([&] {
        ScopedCheckpointObserver observing(checkpoint);
        store->close();
    });
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!checkpoint.paused.load(std::memory_order_acquire) &&
           std::chrono::steady_clock::now() < deadline) std::this_thread::yield();
    const bool paused = checkpoint.paused.load(std::memory_order_acquire);
    const bool safe_during_close = paused && rejected_calls_avoid_state(*store);
    checkpoint.resume.store(true, std::memory_order_release);
    closer.join();
    SMS_CHECK(paused);
    SMS_CHECK(safe_during_close);
    SMS_CHECK(rejected_calls_avoid_state(*store));
    return 0;
}
