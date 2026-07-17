#pragma once

#include <atomic>
#include <bit>
#include <cassert>
#include <cstddef>
#include <cstdint>
#include <limits>

namespace sms::detail {

#if defined(_M_X64) || defined(__x86_64__) || defined(__amd64__)
inline constexpr bool sms2_qualified_architecture = true;
#else
inline constexpr bool sms2_qualified_architecture = false;
#endif

struct MappedAtomic64 {
    static constexpr std::size_t required_alignment = 8;
    static constexpr bool is_always_lock_free =
        std::atomic_ref<std::uint64_t>::is_always_lock_free;

    [[nodiscard]] static bool supported() noexcept {
        if constexpr (!sms2_qualified_architecture || sizeof(void*) != 8 ||
                      std::endian::native != std::endian::little ||
                      !is_always_lock_free ||
                      std::atomic_ref<std::uint64_t>::required_alignment >
                          required_alignment) {
            return false;
        } else {
            alignas(required_alignment) std::uint64_t probe{};
            return std::atomic_ref<std::uint64_t>(probe).is_lock_free();
        }
    }

    [[nodiscard]] static bool is_aligned(const void* address) noexcept {
        return address != nullptr &&
            (reinterpret_cast<std::uintptr_t>(address) & (required_alignment - 1U)) == 0;
    }

    [[nodiscard]] static bool is_addressable(
        const void* mapping_base,
        std::size_t mapping_length,
        const void* address) noexcept {
        if (mapping_base == nullptr || !is_aligned(address) ||
            mapping_length < sizeof(std::uint64_t)) {
            return false;
        }
        const auto base = reinterpret_cast<std::uintptr_t>(mapping_base);
        const auto target = reinterpret_cast<std::uintptr_t>(address);
        if (base > std::numeric_limits<std::uintptr_t>::max() - mapping_length ||
            target < base) {
            return false;
        }
        return target - base <= mapping_length - sizeof(std::uint64_t);
    }

    [[nodiscard]] static std::uint64_t load_acquire(std::uint64_t& location) noexcept {
        assert(is_aligned(&location));
        return std::atomic_ref<std::uint64_t>(location).load(std::memory_order_acquire);
    }

    static void store_release(
        std::uint64_t& location,
        std::uint64_t value) noexcept {
        assert(is_aligned(&location));
        std::atomic_ref<std::uint64_t>(location).store(value, std::memory_order_release);
    }

    [[nodiscard]] static bool compare_exchange(
        std::uint64_t& location,
        std::uint64_t& expected,
        std::uint64_t desired) noexcept {
        assert(is_aligned(&location));
        return std::atomic_ref<std::uint64_t>(location).compare_exchange_strong(
            expected,
            desired,
            std::memory_order_seq_cst,
            std::memory_order_seq_cst);
    }

    [[nodiscard]] static std::uint64_t exchange(
        std::uint64_t& location,
        std::uint64_t desired) noexcept {
        assert(is_aligned(&location));
        return std::atomic_ref<std::uint64_t>(location).exchange(
            desired, std::memory_order_seq_cst);
    }

    [[nodiscard]] static std::uint64_t fetch_add(
        std::uint64_t& location,
        std::uint64_t increment) noexcept {
        assert(is_aligned(&location));
        return std::atomic_ref<std::uint64_t>(location).fetch_add(
            increment, std::memory_order_seq_cst);
    }
};

static_assert(
    MappedAtomic64::is_always_lock_free,
    "SharedMemoryStore requires an always-lock-free 64-bit atomic_ref implementation.");
static_assert(
    std::atomic_ref<std::uint64_t>::required_alignment <=
        MappedAtomic64::required_alignment,
    "SharedMemoryStore requires 64-bit atomic_ref alignment of at most eight bytes.");

} // namespace sms::detail
