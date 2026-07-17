#include <shared_memory_store/store.hpp>

#include <array>
#include <cstddef>
#include <cstring>
#include <string>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
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
        64,
        open_mode::create_new);
    memory_store store;
    if (memory_store::try_create_or_open(options, store) != open_status::success) return 1;
    if (store.protocol() != protocol_info{2, 0, 2, 7, 0}) return 2;
    const std::array<std::byte, 1> key{std::byte{1}};
    const std::array<std::byte, 3> payload{std::byte{2}, std::byte{0}, std::byte{3}};
    if (store.try_publish(key, payload) != status::success) return 3;
    value_lease lease;
    if (store.try_acquire(key, lease) != status::success) return 4;
    if (lease.value().size() != payload.size() ||
        std::memcmp(lease.value().data(), payload.data(), payload.size()) != 0) return 5;
    if (lease.release() != status::success) return 6;

    diagnostics_snapshot diagnostics;
    if (store.try_get_diagnostics(diagnostics) != status::success ||
        diagnostics.protocol() != protocol_info{2, 0, 2, 7, 0} ||
        diagnostics.participant_record_count() != 64 ||
        diagnostics.active_participant_count() != 1 ||
        diagnostics.published_slot_count() != 1) {
        return 7;
    }

    recovery_report report{};
    if (store.try_recover_leases(false, report) != status::unsupported_platform) {
        // Recovery is disabled for this handle and must fail deterministically.
        return 8;
    }

    cancellation_source cancellation;
    if (cancellation.signal() != status::success ||
        store.try_remove(key, wait_options::infinite(cancellation.token())) !=
            status::operation_canceled) {
        return 9;
    }
    return 0;
}
