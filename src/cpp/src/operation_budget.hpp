#pragma once

#include "shared_memory_store/c_api.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <thread>

namespace sms::detail {

class CancellationFlag {
public:
    CancellationFlag() noexcept = default;
    CancellationFlag(const CancellationFlag&) = delete;
    CancellationFlag& operator=(const CancellationFlag&) = delete;

    void cancel() noexcept {
        canceled_.store(true, std::memory_order_release);
    }

    [[nodiscard]] bool is_canceled() const noexcept {
        return canceled_.load(std::memory_order_acquire);
    }

private:
    std::atomic<bool> canceled_{false};
};

class OperationBudget {
public:
    using clock = std::chrono::steady_clock;

    [[nodiscard]] static OperationBudget start(
        std::chrono::milliseconds timeout,
        const CancellationFlag* cancellation = nullptr) noexcept {
        return OperationBudget(timeout, clock::now(), cancellation, false);
    }

    [[nodiscard]] static OperationBudget start_at(
        std::chrono::milliseconds timeout,
        clock::time_point started,
        const CancellationFlag* cancellation = nullptr) noexcept {
        return OperationBudget(timeout, started, cancellation, false);
    }

    [[nodiscard]] static OperationBudget structural_attempt() noexcept {
        return OperationBudget(
            std::chrono::milliseconds::zero(), clock::time_point{}, nullptr, true);
    }

    [[nodiscard]] static OperationBudget unbounded_scan(
        const CancellationFlag* cancellation = nullptr) noexcept {
        return OperationBudget(
            std::chrono::milliseconds{-1}, clock::time_point{}, cancellation, false);
    }

    [[nodiscard]] bool valid() const noexcept {
        return timeout_ >= std::chrono::milliseconds{-1};
    }

    [[nodiscard]] bool is_no_wait() const noexcept {
        return timeout_ == std::chrono::milliseconds::zero();
    }

    [[nodiscard]] bool is_infinite() const noexcept {
        return timeout_ == std::chrono::milliseconds{-1};
    }

    [[nodiscard]] sms_status check() const noexcept {
        if (cancellation_ != nullptr && cancellation_->is_canceled()) {
            return SMS_STATUS_OPERATION_CANCELED;
        }
        if (!valid()) return SMS_STATUS_UNKNOWN_FAILURE;
        if (is_infinite() || is_no_wait()) return SMS_STATUS_SUCCESS;
        return clock::now() - started_ >= timeout_
            ? SMS_STATUS_STORE_BUSY
            : SMS_STATUS_SUCCESS;
    }

    [[nodiscard]] sms_status check_periodic(std::int32_t iteration) const noexcept {
        if (iteration < 0) return SMS_STATUS_UNKNOWN_FAILURE;
        if ((iteration & probe_mask) != 0) return SMS_STATUS_SUCCESS;
        const auto status = check();
        if (status != SMS_STATUS_SUCCESS) return status;
        return is_no_wait() && !full_structural_scan_ && iteration != 0
            ? SMS_STATUS_STORE_BUSY
            : SMS_STATUS_SUCCESS;
    }

    [[nodiscard]] bool try_continue_after_contention(
        std::int32_t attempt,
        sms_status& terminal_status) const noexcept {
        terminal_status = check();
        if (terminal_status != SMS_STATUS_SUCCESS) return false;
        if (is_no_wait()) {
            terminal_status = SMS_STATUS_STORE_BUSY;
            return false;
        }

        const auto nonnegative_attempt = attempt < 0 ? 0 : attempt;
        const auto bounded_attempt = nonnegative_attempt > 10 ? 10 : nonnegative_attempt;
        const auto spin_count = static_cast<std::uint32_t>(4U << bounded_attempt);
        for (std::uint32_t index = 0; index < spin_count; ++index) {
            std::atomic_signal_fence(std::memory_order_seq_cst);
        }
        if ((nonnegative_attempt & probe_mask) == probe_mask) {
            std::this_thread::yield();
        }
        return true;
    }

private:
    static constexpr std::int32_t probe_mask = 63;

    OperationBudget(
        std::chrono::milliseconds timeout,
        clock::time_point started,
        const CancellationFlag* cancellation,
        bool full_structural_scan) noexcept
        : timeout_(timeout),
          started_(started),
          cancellation_(cancellation),
          full_structural_scan_(full_structural_scan) {}

    std::chrono::milliseconds timeout_{};
    clock::time_point started_{};
    const CancellationFlag* cancellation_{};
    bool full_structural_scan_{};
};

} // namespace sms::detail
