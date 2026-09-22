#include "shared_memory_store/detail/local_handle.hpp"
#include "test_support.hpp"

#include <atomic>
#include <optional>
#include <thread>
#include <vector>

namespace {
struct Observations {
    std::atomic<int> closed{};
    std::atomic<int> destroyed{};
};

struct Resource {
    explicit Resource(Observations& observations) : observations(observations) {}
    void close() noexcept { observations.closed.fetch_add(1); }
    ~Resource() { observations.destroyed.fetch_add(1); }
    Observations& observations;
    const int value{42};
};

using Handle = shared_memory_store::detail::local_handle<Resource>;

template<class Predicate>
bool eventually(Predicate predicate) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    while (!predicate()) {
        if (std::chrono::steady_clock::now() >= deadline) return false;
        std::this_thread::yield();
    }
    return true;
}

int paused_snapshot_does_not_block_other_calls() {
    Observations observations;
    Handle handle;
    handle.reset(std::make_unique<Resource>(observations));
    std::optional<Handle::snapshot> paused(handle.pin());
    std::atomic<bool> done{}, successful{true};
    std::thread reader([&] {
        for (int i = 0; i < 10000; ++i) {
            const auto pin = handle.pin();
            if (!pin || pin->value != 42) successful = false;
        }
        done = true;
    });
    const bool progressed = eventually([&] { return done.load(); });
    paused.reset();
    reader.join();
    SMS_CHECK(progressed && successful);
    SMS_CHECK(observations.closed == 0 && observations.destroyed == 0);
    handle.close();
    SMS_CHECK(observations.closed == 1 && observations.destroyed == 1);
    return 0;
}

int close_drains_snapshots_and_all_closers_observe_destruction() {
    Observations observations;
    Handle handle;
    handle.reset(std::make_unique<Resource>(observations));
    std::optional<Handle::snapshot> paused(handle.pin());
    std::atomic<int> returned{};
    std::atomic<bool> saw_destruction{true};
    std::vector<std::thread> closers;
    for (int i = 0; i < 4; ++i) {
        closers.emplace_back([&] {
            handle.close();
            if (observations.destroyed != 1) saw_destruction = false;
            ++returned;
        });
    }
    const bool logically_closed = eventually([&] { return observations.closed == 1; });
    const bool drained_later = logically_closed && returned == 0 &&
        observations.destroyed == 0 && !handle.pin() &&
        paused->operator->()->value == 42;
    paused.reset();
    for (auto& closer : closers) closer.join();
    SMS_CHECK(drained_later && saw_destruction && returned == 4);
    SMS_CHECK(observations.closed == 1 && observations.destroyed == 1);
    handle.close();
    SMS_CHECK(!handle.is_open() && !handle.pin());
    SMS_CHECK(observations.closed == 1 && observations.destroyed == 1);
    return 0;
}

int move_waits_for_snapshots_without_closing_the_resource() {
    Observations observations;
    Handle source;
    source.reset(std::make_unique<Resource>(observations));
    std::optional<Handle::snapshot> paused(source.pin());
    std::optional<Handle> moved;
    std::atomic<bool> returned{};
    std::thread mover([&] {
        moved.emplace(std::move(source));
        returned = true;
    });
    const bool closed_gate = eventually([&] { return !source.is_open(); });
    const bool deferred = closed_gate && !returned && observations.closed == 0 &&
        observations.destroyed == 0;
    paused.reset();
    mover.join();
    SMS_CHECK(deferred && returned && !source.pin());
    SMS_CHECK(moved->is_open() && moved->pin()->value == 42);
    SMS_CHECK(observations.closed == 0 && observations.destroyed == 0);
    moved.reset();
    SMS_CHECK(observations.closed == 1 && observations.destroyed == 1);
    return 0;
}

int enter_close_races_and_replacement_preserve_ownership() {
    for (int round = 0; round < 200; ++round) {
        Observations observations;
        Handle handle;
        handle.reset(std::make_unique<Resource>(observations));
        std::atomic<bool> start{}, valid{true};
        std::vector<std::thread> readers;
        for (int i = 0; i < 4; ++i) {
            readers.emplace_back([&] {
                while (!start.load()) std::this_thread::yield();
                for (int attempt = 0; attempt < 100; ++attempt) {
                    const auto pin = handle.pin();
                    if (pin && (pin->value != 42 || observations.destroyed != 0)) valid = false;
                }
            });
        }
        start = true;
        handle.close();
        for (auto& reader : readers) reader.join();
        SMS_CHECK(valid && observations.closed == 1 && observations.destroyed == 1);
        handle.reset(std::make_unique<Resource>(observations));
        SMS_CHECK(handle.is_open() && handle.pin()->value == 42);
        handle.close();
        SMS_CHECK(observations.closed == 2 && observations.destroyed == 2);
    }
    return 0;
}

int public_wrapper_move_and_close_keep_token_lifetimes() {
    using namespace shared_memory_store;
    memory_store source;
    SMS_CHECK(memory_store::try_create_or_open(sms_test_options("local-handle-move"), source)
              == open_status::success);
    SMS_CHECK(source.try_publish(sms_test_bytes("key"), sms_test_bytes("value")) == status::success);
    value_lease lease;
    SMS_CHECK(source.try_acquire(sms_test_bytes("key"), lease) == status::success);
    memory_store destination(std::move(source));
    SMS_CHECK(!source.valid() && destination.valid());
    SMS_CHECK(lease.valid() && lease.value().size() == 5);
    std::thread close_one([&] { destination.close(); });
    std::thread close_two([&] { destination.close(); });
    close_one.join();
    close_two.join();
    SMS_CHECK(!destination.valid() && !lease.valid());
    SMS_CHECK(destination.try_publish(sms_test_bytes("other"), {}) == status::store_disposed);
    SMS_CHECK(memory_store::try_create_or_open(sms_test_options("local-handle-reopen"), destination)
              == open_status::success);
    SMS_CHECK(destination.try_publish(sms_test_bytes("other"), {}) == status::success);
    return 0;
}
} // namespace

int main() {
    if (paused_snapshot_does_not_block_other_calls() != 0) return 1;
    if (close_drains_snapshots_and_all_closers_observe_destruction() != 0) return 1;
    if (move_waits_for_snapshots_without_closing_the_resource() != 0) return 1;
    if (enter_close_races_and_replacement_preserve_ownership() != 0) return 1;
    return public_wrapper_move_and_close_keep_token_lifetimes();
}
