#pragma once

#include "shared_memory_store/store.hpp"

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <span>
#include <string>
#include <string_view>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <unistd.h>
#endif

#define SMS_CHECK(expression) do { \
    if (!(expression)) { \
        std::cerr << __FILE__ << ':' << __LINE__ << ": check failed: " #expression << '\n'; \
        return 1; \
    } \
} while (false)

inline std::span<const std::byte> sms_test_bytes(std::string_view value) noexcept {
    return {reinterpret_cast<const std::byte*>(value.data()), value.size()};
}

template <std::size_t N>
inline std::span<const std::byte> sms_test_bytes(const std::array<std::uint8_t, N>& value) noexcept {
    return {reinterpret_cast<const std::byte*>(value.data()), value.size()};
}

inline std::string sms_test_name(std::string_view suffix) {
#if defined(_WIN32)
    const auto pid = GetCurrentProcessId();
#else
    const auto pid = getpid();
#endif
    return "sms-native-" + std::to_string(pid) + "-" +
        std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()) + "-" + std::string(suffix);
}

inline shared_memory_store::store_options sms_test_options(std::string suffix, std::int32_t slots = 4,
                                                            std::int32_t leases = 8) {
    return shared_memory_store::store_options::create(
        sms_test_name(suffix), slots, 128, 32, 32, leases,
        64,
        shared_memory_store::open_mode::create_new, true);
}
