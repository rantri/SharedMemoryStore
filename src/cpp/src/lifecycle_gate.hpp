#pragma once

#include "shared_memory_store/c_api.h"

#include <atomic>
#include <cstdint>

namespace sms::detail {

// Process-local lifetime gate. Entry and exit are atomic-only hot paths; only
// close callers may park through C++20 atomic wait while entered calls drain.
// No object from this class is placed in shared memory.
class LifecycleGate {
public:
    class Operation {
    public:
        Operation() noexcept = default;
        Operation(const Operation&) = delete;
        Operation& operator=(const Operation&) = delete;

        Operation(Operation&& other) noexcept
            : owner_(other.owner_) {
            other.owner_ = nullptr;
        }

        Operation& operator=(Operation&& other) noexcept {
            if (this != &other) {
                reset();
                owner_ = other.owner_;
                other.owner_ = nullptr;
            }
            return *this;
        }

        ~Operation() { reset(); }

        [[nodiscard]] explicit operator bool() const noexcept {
            return owner_ != nullptr;
        }

        void reset() noexcept {
            if (owner_ != nullptr) {
                owner_->leave();
                owner_ = nullptr;
            }
        }

    private:
        friend class LifecycleGate;
        explicit Operation(LifecycleGate* owner) noexcept : owner_(owner) {}
        LifecycleGate* owner_{};
    };

    LifecycleGate() noexcept = default;
    LifecycleGate(const LifecycleGate&) = delete;
    LifecycleGate& operator=(const LifecycleGate&) = delete;

    [[nodiscard]] sms_status try_enter(Operation& operation) noexcept {
        operation.reset();
        if (state_.load(std::memory_order_acquire) != open_state) {
            return SMS_STATUS_STORE_DISPOSED;
        }
        active_.fetch_add(1, std::memory_order_acq_rel);
        if (state_.load(std::memory_order_acquire) != open_state) {
            leave();
            return SMS_STATUS_STORE_DISPOSED;
        }
        operation = Operation(this);
        return SMS_STATUS_SUCCESS;
    }

    // Exactly one closer performs teardown; concurrent close calls wait for
    // its completion. After this returns no operation can still hold a mapped
    // projection and all later entry attempts return StoreDisposed.
    [[nodiscard]] bool begin_close_and_drain() noexcept {
        auto expected = open_state;
        const bool owner = state_.compare_exchange_strong(
            expected, closing_state,
            std::memory_order_acq_rel,
            std::memory_order_acquire);
        if (!owner) {
            for (;;) {
                const auto observed = state_.load(std::memory_order_acquire);
                if (observed == closed_state) return false;
                state_.wait(observed, std::memory_order_acquire);
            }
        }

        for (;;) {
            const auto observed = active_.load(std::memory_order_acquire);
            if (observed == 0) return true;
            active_.wait(observed, std::memory_order_acquire);
        }
    }

    void complete_close() noexcept {
        state_.store(closed_state, std::memory_order_release);
        state_.notify_all();
    }

    [[nodiscard]] bool is_open() const noexcept {
        return state_.load(std::memory_order_acquire) == open_state;
    }

private:
    void leave() noexcept {
        if (active_.fetch_sub(1, std::memory_order_acq_rel) == 1) {
            active_.notify_all();
        }
    }

    static constexpr std::uint32_t open_state = 0;
    static constexpr std::uint32_t closing_state = 1;
    static constexpr std::uint32_t closed_state = 2;

    std::atomic<std::uint32_t> state_{open_state};
    std::atomic<std::uint32_t> active_{};
};

} // namespace sms::detail
