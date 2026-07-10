#include <shared_memory_store/store.hpp>

#include <array>
#include <cstddef>
#include <iostream>
#include <string>

#if defined(_WIN32)
#  define NOMINMAX
#  include <windows.h>
#else
#  include <unistd.h>
#endif

int main() {
#if defined(_WIN32)
    const auto pid = GetCurrentProcessId();
#else
    const auto pid = getpid();
#endif
    using namespace shared_memory_store;
    auto options = store_options::create(
        "sms-cpp-sample-" + std::to_string(pid), 2, 64, 16, 16, 4,
        open_mode::create_new);
    memory_store store;
    if (const auto opened = memory_store::try_create_or_open(options, store);
        opened != open_status::success) {
        std::cerr << "open failed: " << static_cast<int>(opened) << '\n';
        return 1;
    }
    const std::array<std::byte, 3> key{std::byte{1}, std::byte{2}, std::byte{3}};
    const std::array<std::byte, 3> payload{std::byte{7}, std::byte{8}, std::byte{9}};
    if (store.try_publish(key, payload) != status::success) return 2;
    value_lease lease;
    if (store.try_acquire(key, lease) != status::success) return 3;
    std::cout << "value bytes: " << lease.value().size() << '\n';
    return lease.release() == status::success ? 0 : 4;
}
