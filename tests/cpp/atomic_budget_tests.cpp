#include "mapped_atomic.hpp"
#include "operation_budget.hpp"
#include "test_support.hpp"

#include <array>
#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <thread>

static_assert(sizeof(void*) == 8);
static_assert(sms::detail::MappedAtomic64::required_alignment == 8);
static_assert(sms::detail::MappedAtomic64::is_always_lock_free);

int main() {
    using namespace std::chrono_literals;
    using sms::detail::CancellationFlag;
    using sms::detail::MappedAtomic64;
    using sms::detail::OperationBudget;

    SMS_CHECK(MappedAtomic64::supported());

    alignas(8) std::uint64_t word = 0;
    SMS_CHECK(MappedAtomic64::is_aligned(&word));
    alignas(8) std::array<std::byte, 24> storage{};
    SMS_CHECK(MappedAtomic64::is_aligned(storage.data() + 8));
    SMS_CHECK(!MappedAtomic64::is_aligned(storage.data() + 1));

    MappedAtomic64::store_release(word, 0x0123'4567'89ab'cdefULL);
    SMS_CHECK(MappedAtomic64::load_acquire(word) == 0x0123'4567'89ab'cdefULL);

    std::uint64_t expected = 0x0123'4567'89ab'cdefULL;
    SMS_CHECK(MappedAtomic64::compare_exchange(
        word, expected, 0xfedc'ba98'7654'3210ULL));
    SMS_CHECK(expected == 0x0123'4567'89ab'cdefULL);
    SMS_CHECK(MappedAtomic64::load_acquire(word) == 0xfedc'ba98'7654'3210ULL);
    expected = 0;
    SMS_CHECK(!MappedAtomic64::compare_exchange(word, expected, 1));
    SMS_CHECK(expected == 0xfedc'ba98'7654'3210ULL);

    MappedAtomic64::store_release(word, 41);
    SMS_CHECK(MappedAtomic64::fetch_add(word, 1) == 41);
    SMS_CHECK(MappedAtomic64::load_acquire(word) == 42);

    alignas(8) std::uint64_t publication = 0;
    std::uint64_t payload = 0;
    std::atomic<bool> saw_payload{false};
    std::atomic<bool> timed_out{false};
    std::thread reader([&] {
        const auto deadline = std::chrono::steady_clock::now() + 5s;
        while (MappedAtomic64::load_acquire(publication) == 0) {
            if (std::chrono::steady_clock::now() >= deadline) {
                timed_out.store(true, std::memory_order_relaxed);
                return;
            }
            std::this_thread::yield();
        }
        saw_payload.store(payload == 0xa5a5'5a5a'1234'5678ULL, std::memory_order_relaxed);
    });
    payload = 0xa5a5'5a5a'1234'5678ULL;
    MappedAtomic64::store_release(publication, 1);
    reader.join();
    SMS_CHECK(!timed_out.load(std::memory_order_relaxed));
    SMS_CHECK(saw_payload.load(std::memory_order_relaxed));

    const auto now = OperationBudget::clock::now();
    auto no_wait = OperationBudget::start_at(0ms, now);
    SMS_CHECK(no_wait.is_no_wait());
    SMS_CHECK(!no_wait.is_infinite());
    SMS_CHECK(no_wait.check() == SMS_STATUS_SUCCESS);
    SMS_CHECK(no_wait.check_periodic(0) == SMS_STATUS_SUCCESS);
    SMS_CHECK(no_wait.check_periodic(1) == SMS_STATUS_SUCCESS);
    SMS_CHECK(no_wait.check_periodic(63) == SMS_STATUS_SUCCESS);
    SMS_CHECK(no_wait.check_periodic(64) == SMS_STATUS_STORE_BUSY);
    sms_status terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(!no_wait.try_continue_after_contention(0, terminal));
    SMS_CHECK(terminal == SMS_STATUS_STORE_BUSY);

    auto finite = OperationBudget::start_at(1s, now);
    SMS_CHECK(!finite.is_no_wait());
    SMS_CHECK(!finite.is_infinite());
    SMS_CHECK(finite.check() == SMS_STATUS_SUCCESS);
    terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(finite.try_continue_after_contention(0, terminal));
    SMS_CHECK(terminal == SMS_STATUS_SUCCESS);

    auto expired = OperationBudget::start_at(1ms, now - 10ms);
    SMS_CHECK(expired.check() == SMS_STATUS_STORE_BUSY);
    SMS_CHECK(expired.check_periodic(64) == SMS_STATUS_STORE_BUSY);
    terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(!expired.try_continue_after_contention(3, terminal));
    SMS_CHECK(terminal == SMS_STATUS_STORE_BUSY);

    auto infinite = OperationBudget::start_at(-1ms, now - 24h);
    SMS_CHECK(infinite.is_infinite());
    SMS_CHECK(!infinite.is_no_wait());
    SMS_CHECK(infinite.check() == SMS_STATUS_SUCCESS);
    SMS_CHECK(infinite.check_periodic(64) == SMS_STATUS_SUCCESS);
    terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(infinite.try_continue_after_contention(63, terminal));
    SMS_CHECK(terminal == SMS_STATUS_SUCCESS);

    CancellationFlag cancellation;
    auto cancelable = OperationBudget::start_at(1h, now, &cancellation);
    SMS_CHECK(cancelable.check() == SMS_STATUS_SUCCESS);
    SMS_CHECK(!cancellation.is_canceled());
    cancellation.cancel();
    SMS_CHECK(cancellation.is_canceled());
    SMS_CHECK(cancelable.check() == SMS_STATUS_OPERATION_CANCELED);
    SMS_CHECK(cancelable.check_periodic(64) == SMS_STATUS_OPERATION_CANCELED);
    terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(!cancelable.try_continue_after_contention(0, terminal));
    SMS_CHECK(terminal == SMS_STATUS_OPERATION_CANCELED);

    CancellationFlag already_canceled;
    already_canceled.cancel();
    auto canceled_no_wait = OperationBudget::start_at(0ms, now, &already_canceled);
    SMS_CHECK(canceled_no_wait.check() == SMS_STATUS_OPERATION_CANCELED);
    terminal = SMS_STATUS_UNKNOWN_FAILURE;
    SMS_CHECK(!canceled_no_wait.try_continue_after_contention(0, terminal));
    SMS_CHECK(terminal == SMS_STATUS_OPERATION_CANCELED);
    return 0;
}
