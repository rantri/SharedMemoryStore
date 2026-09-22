#pragma once

#include <atomic>
#include <cstdint>
#include <memory>
#include <utility>

namespace shared_memory_store::detail {

// Process-local ownership for a public wrapper. A snapshot enters with bounded
// lock-free atomics; only close/move wait. T::close() stops native operations
// before the last snapshot is drained and T can be destroyed.
template<class T>
class local_handle {
    enum class state : std::uint32_t { closed, open, closing };
    static_assert(std::atomic<state>::is_always_lock_free);
    static_assert(std::atomic<std::uint64_t>::is_always_lock_free);

public:
    class snapshot {
    public:
        snapshot() noexcept = default;
        snapshot(const snapshot&) = delete;
        snapshot& operator=(const snapshot&) = delete;
        snapshot(snapshot&& other) noexcept
            : owner_(std::exchange(other.owner_, nullptr)), value_(other.value_) {}
        ~snapshot() { if (owner_) owner_->leave(); }
        explicit operator bool() const noexcept { return owner_ != nullptr; }
        T* operator->() const noexcept { return value_; }
    private:
        friend class local_handle;
        snapshot(const local_handle* owner, T* value) noexcept
            : owner_(owner), value_(value) {}
        const local_handle* owner_{};
        T* value_{};
    };

    local_handle() noexcept = default;
    local_handle(const local_handle&) = delete;
    local_handle& operator=(const local_handle&) = delete;
    local_handle(local_handle&& other) noexcept { reset(other.take()); }
    local_handle& operator=(local_handle&& other) noexcept {
        if (this != &other) reset(other.take());
        return *this;
    }
    ~local_handle() { close(); }

    [[nodiscard]] snapshot pin() const noexcept {
        if (state_.load(std::memory_order_acquire) != state::open) return {};
        // The sequentially consistent increment and recheck pair with close's
        // state transition and drain. A late entrant either contributes to the
        // drain or observes closing, so it cannot dereference a detached value.
        active_.fetch_add(1, std::memory_order_seq_cst);
        if (state_.load(std::memory_order_seq_cst) != state::open) {
            leave();
            return {};
        }
        return snapshot(this, value_.get());
    }

    [[nodiscard]] bool is_open() const noexcept {
        return state_.load(std::memory_order_acquire) == state::open;
    }

    void close() noexcept {
        if (!begin_close()) return;
        if (value_) value_->close();
        drain();
        value_.reset();
        finish_close();
    }

    // Replacement, like construction, requires the caller to synchronize
    // concurrent replacements. Ordinary calls and close use pin()/close().
    void reset(std::unique_ptr<T> value) noexcept {
        close();
        value_ = std::move(value);
        state_.store(value_ ? state::open : state::closed, std::memory_order_release);
    }

private:
    bool begin_close() noexcept {
        auto expected = state::open;
        if (state_.compare_exchange_strong(expected, state::closing,
                std::memory_order_seq_cst, std::memory_order_seq_cst)) return true;
        while (expected == state::closing) {
            state_.wait(expected, std::memory_order_acquire);
            expected = state_.load(std::memory_order_acquire);
        }
        return false;
    }

    void drain() const noexcept {
        auto count = active_.load(std::memory_order_seq_cst);
        while (count != 0) {
            active_.wait(count, std::memory_order_acquire);
            count = active_.load(std::memory_order_seq_cst);
        }
    }

    void finish_close() noexcept {
        state_.store(state::closed, std::memory_order_release);
        state_.notify_all();
    }

    void leave() const noexcept {
        if (active_.fetch_sub(1, std::memory_order_seq_cst) == 1 &&
            state_.load(std::memory_order_seq_cst) != state::open) {
            // With the sequentially consistent close/drain ordering, either
            // close sees zero readers or this check sees closing and wakes it.
            // Open-handle operations never enter the atomic-wait adapter.
            active_.notify_all();
        }
    }

    std::unique_ptr<T> take() noexcept {
        if (!begin_close()) return {};
        drain();
        auto value = std::move(value_);
        finish_close();
        return value;
    }

    std::unique_ptr<T> value_;
    std::atomic<state> state_{state::closed};
    mutable std::atomic<std::uint64_t> active_{};
};
} // namespace shared_memory_store::detail
