#pragma once

#include "lifecycle_gate.hpp"
#include "layout_v2.hpp"
#include "operation_budget.hpp"
#include "shared_memory_store/c_api.h"

#include <array>
#include <atomic>
#include <bit>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace sms::detail {

static_assert(sizeof(void*) == 8, "SharedMemoryStore native ABI requires a 64-bit target.");
static_assert(
    std::endian::native == std::endian::little,
    "SharedMemoryStore mapped layout requires a little-endian target.");

struct ResourceName {
    std::string public_name;
    std::string fragment;
    std::string linux_region_path;
    std::string linux_lock_path;
    std::string linux_owners_path;
    std::string linux_lifecycle_path;
#if defined(_WIN32)
    std::wstring windows_region_name;
    std::wstring windows_lock_name;
#endif
};

// Canonical protocol utilities shared by the SMS2 layout and store engine.
bool checked_add_nonnegative(
    std::int64_t left,
    std::int64_t right,
    std::int64_t& result) noexcept;
bool checked_multiply_nonnegative(
    std::int64_t left,
    std::int64_t right,
    std::int64_t& result) noexcept;
bool checked_align_up_nonnegative(
    std::int64_t value,
    std::int64_t alignment,
    std::int64_t& result) noexcept;
bool exact_bytes_equal(
    std::span<const std::uint8_t> left,
    std::span<const std::uint8_t> right) noexcept;
std::uint64_t hash_key(std::span<const std::uint8_t> key) noexcept;
std::array<std::uint8_t, 32> sha256(std::span<const std::uint8_t> data);
bool valid_utf8(std::string_view value) noexcept;
std::size_t utf16_length(std::string_view value) noexcept;
bool utf8_whitespace_only(std::string_view value) noexcept;
bool make_resource_name(std::string_view public_name, ResourceName& result);

struct Options {
    std::string name;
    sms_open_mode open_mode{SMS_OPEN_MODE_CREATE_OR_OPEN};
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t lease_record_count{};
    std::int32_t participant_record_count{};
    bool enable_lease_recovery{};
};

class CancellationFlag;

struct Wait {
    std::int64_t milliseconds{1000};
    const CancellationFlag* cancellation{};
    bool valid() const noexcept { return milliseconds >= -1; }
    bool infinite() const noexcept { return milliseconds == -1; }
};

struct LifecycleId {
    std::uint64_t store_id{};
    std::uint64_t slot_binding{};
    std::uint64_t resource_binding{};
    std::uint32_t participant_token{};
    std::int32_t payload_length{};

    [[nodiscard]] bool reservation_valid() const noexcept {
        return store_id != 0 && slot_binding != 0 &&
            resource_binding == 0 && participant_token != 0 &&
            payload_length >= 0;
    }

    [[nodiscard]] bool lease_valid() const noexcept {
        return store_id != 0 && slot_binding != 0 &&
            resource_binding != 0 && participant_token != 0;
    }
};

std::int32_t current_process_id() noexcept;

template <class T>
inline T load_acquire(T& value) noexcept {
    return std::atomic_ref<T>(value).load(std::memory_order_acquire);
}

template <class T>
inline void store_release(T& target, T value) noexcept {
    std::atomic_ref<T>(target).store(value, std::memory_order_release);
}

template <class T>
inline T increment(T& target) noexcept {
    return std::atomic_ref<T>(target).fetch_add(static_cast<T>(1), std::memory_order_acq_rel) + static_cast<T>(1);
}

template <class T>
inline T decrement(T& target) noexcept {
    return std::atomic_ref<T>(target).fetch_sub(static_cast<T>(1), std::memory_order_acq_rel) - static_cast<T>(1);
}

class MappedRegion {
public:
    virtual ~MappedRegion() = default;
    virtual std::uint8_t* data() noexcept = 0;
    virtual std::int64_t size() const noexcept = 0;
    virtual void close() noexcept = 0;
    // Failed-open cleanup still owns every cold gate. Platforms with owner
    // metadata may override this path to finalize it directly rather than
    // attempting to reacquire a gate already retained by the caller.
    virtual void close_while_cold_locked() noexcept { close(); }
};

class SharedLock {
public:
    virtual ~SharedLock() = default;
    virtual sms_status acquire(const Wait& wait) noexcept = 0;
    virtual void release() noexcept = 0;
};

struct PlatformOpenResult {
    sms_open_status status{SMS_OPEN_MAPPING_FAILED};
    std::unique_ptr<MappedRegion> region;
    // Cold lifecycle gates are returned held. Store::open releases them only
    // after SMS2 validation and participant registration have completed.
    // They are never consulted by a hot key/value operation.
    std::unique_ptr<SharedLock> lifecycle_lock;
    std::unique_ptr<SharedLock> cold_lock;
    bool physical_creator{};
};

PlatformOpenResult platform_open(const ResourceName& resource, const Options& options, const Wait& wait) noexcept;
enum class OwnerKind { current, live, stale, unsupported };
OwnerKind classify_process(std::int32_t process_id) noexcept;

struct RecoveryReport {
    std::int32_t scanned{};
    std::int32_t recovered{};
    std::int32_t active{};
    std::int32_t unsupported{};
    std::int32_t failed{};
};

struct Diagnostics {
    std::int32_t layout_major{sms2_layout_major};
    std::int32_t layout_minor{sms2_layout_minor};
    std::int32_t resource_protocol{sms2_resource_protocol};
    std::uint64_t required_features{sms2_required_features};
    std::uint64_t optional_features{sms2_optional_features};
    std::int64_t total_bytes{};
    std::uint64_t store_control{};

    std::int32_t slot_count{};
    std::int32_t free_slots{};
    std::int32_t initializing_slots{};
    std::int32_t reserved_slots{};
    std::int32_t published_slots{};
    std::int32_t pending_removal{};
    std::int32_t reclaiming_slots{};
    std::int32_t retired_slots{};
    std::int32_t active_reservations{};

    std::int32_t lease_record_count{};
    std::int32_t free_leases{};
    std::int32_t claiming_leases{};
    std::int32_t active_leases{};
    std::int32_t recovering_leases{};
    std::int32_t retired_leases{};

    std::int32_t participant_record_count{};
    std::int32_t free_participants{};
    std::int32_t registering_participants{};
    std::int32_t active_participants{};
    std::int32_t closing_participants{};
    std::int32_t recovering_participants{};
    std::int32_t reclaiming_participants{};
    std::int32_t retired_participants{};

    std::int32_t index_entries{};
    std::int32_t occupied_index_entries{};
    std::int32_t empty_index_entries{};
    std::int32_t usable_index_capacity{};
    std::int32_t primary_directory_occupancy{};
    std::int32_t spilled_bucket_count{};
    std::int32_t overflow_directory_occupancy{};

    std::int32_t last_probe{};
    std::int32_t max_probe{};
    std::int32_t max_overflow_scan{};
    sms_status last_failure{SMS_STATUS_SUCCESS};

    std::int64_t aborted_reservations{};
    std::int64_t recovered_leases{};
    std::int64_t active_lease_recoveries{};
    std::int64_t unsupported_lease_recoveries{};
    std::int64_t failed_lease_recoveries{};
    std::int64_t recovered_reservations{};
    std::int64_t active_reservation_recoveries{};
    std::int64_t unsupported_reservation_recoveries{};
    std::int64_t failed_reservation_recoveries{};
    std::int64_t capacity_pressure{};
    std::int64_t overflow_scans{};
    std::int64_t cas_retries{};
    std::int64_t helped_transitions{};
    std::int64_t contention_exhaustions{};
    std::int64_t invalid_tokens{};
    std::int64_t stale_tokens{};
    std::int64_t recovery_attempts{};
    std::int64_t recovered_transitions{};
    std::int64_t current_owner_classifications{};
    std::int64_t live_owner_classifications{};
    std::int64_t stale_owner_classifications{};
    std::int64_t unsupported_owner_classifications{};
    std::int64_t inconsistent_owner_classifications{};
    std::int64_t changing_owner_classifications{};
    std::array<std::int64_t, SMS_STATUS_COUNT> failures{};
};

struct ReservationToken;
struct LeaseToken;
enum class SlotPublicationIntent : std::int32_t;

class Store : public std::enable_shared_from_this<Store> {
public:
    static sms_open_status open(const Options& options, const Wait& wait, std::shared_ptr<Store>& result) noexcept;
    ~Store();

    Store(const Store&) = delete;
    Store& operator=(const Store&) = delete;

    sms_status publish(std::span<const std::uint8_t> key,
                       std::span<const std::uint8_t> value,
                       std::span<const std::uint8_t> descriptor,
                       const Wait& wait) noexcept;
    sms_status publish_segments(std::span<const std::uint8_t> key,
                                std::span<const sms_segment> segments,
                                std::span<const std::uint8_t> descriptor,
                                const Wait& wait,
                                std::int64_t& copied) noexcept;
    sms_status acquire(std::span<const std::uint8_t> key,
                       const Wait& wait,
                       std::int32_t& slot,
                       LifecycleId& lifecycle,
                       std::int32_t& lease_id) noexcept;
    sms_status release_lease(std::int32_t slot, LifecycleId lifecycle,
                             std::int32_t lease_id, const Wait& wait) noexcept;
    bool lease_valid(std::int32_t slot, LifecycleId lifecycle, std::int32_t lease_id) noexcept;
    std::span<const std::uint8_t> lease_value(std::int32_t slot, LifecycleId lifecycle,
                                             std::int32_t lease_id) noexcept;
    std::span<const std::uint8_t> lease_descriptor(std::int32_t slot, LifecycleId lifecycle,
                                                  std::int32_t lease_id) noexcept;
    sms_status remove(std::span<const std::uint8_t> key, const Wait& wait) noexcept;

    sms_status reserve(std::span<const std::uint8_t> key, std::int32_t payload_length,
                       std::span<const std::uint8_t> descriptor, const Wait& wait,
                       std::int32_t& slot, LifecycleId& lifecycle) noexcept;
    bool reservation_valid(std::int32_t slot, LifecycleId lifecycle) noexcept;
    std::int32_t reservation_payload_length(std::int32_t slot, LifecycleId lifecycle) noexcept;
    std::int32_t reservation_bytes_written(std::int32_t slot, LifecycleId lifecycle) noexcept;
    std::span<std::uint8_t> reservation_buffer(std::int32_t slot, LifecycleId lifecycle,
                                               std::int32_t size_hint) noexcept;
    sms_status advance_reservation(std::int32_t slot, LifecycleId lifecycle,
                                   std::int32_t count, const Wait& wait) noexcept;
    sms_status commit_reservation(std::int32_t slot, LifecycleId lifecycle,
                                  const Wait& wait) noexcept;
    sms_status abort_reservation(std::int32_t slot, LifecycleId lifecycle,
                                 bool count_abort, const Wait& wait) noexcept;
    sms_status recover_leases(bool recover_current, const Wait& wait, RecoveryReport& report) noexcept;
    sms_status recover_reservations(bool recover_current, const Wait& wait, RecoveryReport& report) noexcept;
    sms_status diagnostics(const Wait& wait, Diagnostics& result) noexcept;
    void close() noexcept;

private:
    struct State;

    explicit Store(std::unique_ptr<State> state) noexcept;

    [[nodiscard]] sms_status enter(
        const Wait& wait,
        LifecycleGate::Operation& operation) noexcept;
    [[nodiscard]] sms_status ensure_ready() const noexcept;
    [[nodiscard]] sms_status validate_key(
        std::span<const std::uint8_t> key) const noexcept;
    [[nodiscard]] sms_status validate_value(
        std::span<const std::uint8_t> key,
        std::size_t value_length,
        std::size_t descriptor_length) const noexcept;
    [[nodiscard]] sms_status record(sms_status status) noexcept;

    [[nodiscard]] sms_status reserve_core(
        std::span<const std::uint8_t> key,
        std::int32_t payload_length,
        std::span<const std::uint8_t> descriptor,
        SlotPublicationIntent intent,
        const OperationBudget& budget,
        ReservationToken& reservation) noexcept;
    [[nodiscard]] sms_status abort_core(
        const ReservationToken& reservation,
        const OperationBudget& budget) noexcept;
    [[nodiscard]] bool project_lease(
        const LeaseToken& lease,
        ValueSlotMetadataV2*& slot,
        std::int32_t& value_length,
        std::int32_t& descriptor_length) noexcept;
    void cleanup_owned_resources() noexcept;

    static ReservationToken to_reservation(
        const LifecycleId& lifecycle) noexcept;
    static LeaseToken to_lease(const LifecycleId& lifecycle) noexcept;
    static LifecycleId from_reservation(
        const ReservationToken& reservation) noexcept;
    static LifecycleId from_lease(const LeaseToken& lease) noexcept;

    std::unique_ptr<State> state_;
    LifecycleGate lifecycle_;
};

} // namespace sms::detail
