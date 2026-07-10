#include <shared_memory_store/store.hpp>

#include <array>
#include <cstddef>
#include <cstring>
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
        "sms-installed-consumer-" + std::to_string(pid), 2, 32, 8, 8, 4,
        open_mode::create_new);
    memory_store store;
    if (memory_store::try_create_or_open(options, store) != open_status::success) return 1;
    const std::array<std::byte, 1> key{std::byte{1}};
    const std::array<std::byte, 3> payload{std::byte{2}, std::byte{0}, std::byte{3}};
    if (store.try_publish(key, payload) != status::success) return 2;
    value_lease lease;
    if (store.try_acquire(key, lease) != status::success) return 3;
    if (lease.value().size() != payload.size() ||
        std::memcmp(lease.value().data(), payload.data(), payload.size()) != 0) return 4;
    return lease.release() == status::success ? 0 : 5;
}
