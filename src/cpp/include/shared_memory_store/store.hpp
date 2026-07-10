#pragma once

#include "c_api.h"

#include <cstddef>
#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace shared_memory_store {

enum class open_mode : std::int32_t {
    create_new = SMS_OPEN_MODE_CREATE_NEW,
    open_existing = SMS_OPEN_MODE_OPEN_EXISTING,
    create_or_open = SMS_OPEN_MODE_CREATE_OR_OPEN
};

enum class open_status : std::int32_t {
    success = SMS_OPEN_SUCCESS,
    already_exists = SMS_OPEN_ALREADY_EXISTS,
    not_found = SMS_OPEN_NOT_FOUND,
    invalid_options = SMS_OPEN_INVALID_OPTIONS,
    incompatible_layout = SMS_OPEN_INCOMPATIBLE_LAYOUT,
    unsupported_platform = SMS_OPEN_UNSUPPORTED_PLATFORM,
    insufficient_capacity = SMS_OPEN_INSUFFICIENT_CAPACITY,
    access_denied = SMS_OPEN_ACCESS_DENIED,
    mapping_failed = SMS_OPEN_MAPPING_FAILED,
    store_busy = SMS_OPEN_STORE_BUSY,
    operation_canceled = SMS_OPEN_OPERATION_CANCELED
};

enum class status : std::int32_t {
    success = SMS_STATUS_SUCCESS,
    duplicate_key = SMS_STATUS_DUPLICATE_KEY,
    not_found = SMS_STATUS_NOT_FOUND,
    key_too_large = SMS_STATUS_KEY_TOO_LARGE,
    value_too_large = SMS_STATUS_VALUE_TOO_LARGE,
    descriptor_too_large = SMS_STATUS_DESCRIPTOR_TOO_LARGE,
    store_full = SMS_STATUS_STORE_FULL,
    lease_table_full = SMS_STATUS_LEASE_TABLE_FULL,
    invalid_lease = SMS_STATUS_INVALID_LEASE,
    lease_already_released = SMS_STATUS_LEASE_ALREADY_RELEASED,
    remove_pending = SMS_STATUS_REMOVE_PENDING,
    unsupported_platform = SMS_STATUS_UNSUPPORTED_PLATFORM,
    store_disposed = SMS_STATUS_STORE_DISPOSED,
    corrupt_store = SMS_STATUS_CORRUPT_STORE,
    access_denied = SMS_STATUS_ACCESS_DENIED,
    unknown_failure = SMS_STATUS_UNKNOWN_FAILURE,
    invalid_reservation = SMS_STATUS_INVALID_RESERVATION,
    reservation_incomplete = SMS_STATUS_RESERVATION_INCOMPLETE,
    reservation_already_completed = SMS_STATUS_RESERVATION_ALREADY_COMPLETED,
    reservation_write_out_of_range = SMS_STATUS_RESERVATION_WRITE_OUT_OF_RANGE,
    invalid_key = SMS_STATUS_INVALID_KEY,
    store_busy = SMS_STATUS_STORE_BUSY,
    operation_canceled = SMS_STATUS_OPERATION_CANCELED
};

struct wait_options {
    std::int64_t timeout_milliseconds{1000};
    static constexpr wait_options defaults() noexcept { return {1000}; }
    static constexpr wait_options no_wait() noexcept { return {0}; }
    static constexpr wait_options infinite() noexcept { return {SMS_WAIT_INFINITE}; }
};

struct store_options {
    std::string name;
    open_mode mode{open_mode::create_or_open};
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t lease_record_count{};
    bool enable_lease_recovery{};

    static std::int64_t calculate_required_bytes(std::int32_t slots, std::int32_t max_value,
                                                 std::int32_t max_descriptor, std::int32_t max_key,
                                                 std::int32_t leases) {
        std::int64_t required{};
        if (sms_calculate_required_bytes(slots, max_value, max_descriptor, max_key, leases, &required) != SMS_OPEN_SUCCESS)
            throw std::invalid_argument("SharedMemoryStore capacities are invalid.");
        return required;
    }

    static store_options create(std::string name, std::int32_t slots, std::int32_t max_value,
                                std::int32_t max_descriptor, std::int32_t max_key,
                                std::int32_t leases, open_mode mode = open_mode::create_or_open,
                                bool recovery = false) {
        store_options result{std::move(name), mode, 0, slots, max_value, max_descriptor, max_key, leases, recovery};
        result.total_bytes = calculate_required_bytes(slots, max_value, max_descriptor, max_key, leases);
        return result;
    }
};

struct recovery_report {
    std::int32_t scanned_count{};
    std::int32_t recovered_count{};
    std::int32_t active_count{};
    std::int32_t unsupported_count{};
    std::int32_t failed_count{};
};

class diagnostics_snapshot {
public:
    std::int64_t total_bytes() const noexcept { return value_.total_bytes; }
    std::int32_t slot_count() const noexcept { return value_.slot_count; }
    std::int32_t free_slot_count() const noexcept { return value_.free_slot_count; }
    std::int32_t published_slot_count() const noexcept { return value_.published_slot_count; }
    std::int32_t pending_removal_count() const noexcept { return value_.pending_removal_count; }
    std::int32_t active_lease_count() const noexcept { return value_.active_lease_count; }
    std::int32_t active_reservation_count() const noexcept { return value_.active_reservation_count; }
    std::int32_t index_entry_count() const noexcept { return value_.index_entry_count; }
    std::int32_t occupied_index_entry_count() const noexcept { return value_.occupied_index_entry_count; }
    std::int32_t tombstone_index_entry_count() const noexcept { return value_.tombstone_index_entry_count; }
    std::int32_t usable_index_capacity() const noexcept { return value_.usable_index_capacity; }
    status last_failure_status() const noexcept { return static_cast<status>(value_.last_failure_status); }
    std::int64_t failure_count(status value) const noexcept {
        const auto index = static_cast<std::int32_t>(value);
        return index >= 0 && index < 23 ? value_.failure_counts[index] : 0;
    }
    const sms_diagnostics& native() const noexcept { return value_; }
private:
    friend class memory_store;
    sms_diagnostics value_{};
};

namespace detail {
inline sms_wait_options native_wait(wait_options value) noexcept {
    return {sizeof(sms_wait_options), SMS_C_ABI_VERSION, value.timeout_milliseconds};
}
inline sms_bytes bytes(std::span<const std::byte> value) noexcept {
    return {reinterpret_cast<const std::uint8_t*>(value.data()), static_cast<std::uint64_t>(value.size())};
}
inline std::span<const std::byte> byte_span(sms_bytes value) noexcept {
    return {reinterpret_cast<const std::byte*>(value.data), static_cast<std::size_t>(value.length)};
}
} // namespace detail

class value_lease {
public:
    value_lease() noexcept = default;
    ~value_lease() { reset(); }
    value_lease(const value_lease&) = delete;
    value_lease& operator=(const value_lease&) = delete;
    value_lease(value_lease&& other) noexcept : handle_(std::exchange(other.handle_, nullptr)) {}
    value_lease& operator=(value_lease&& other) noexcept {
        if (this != &other) { reset(); handle_ = std::exchange(other.handle_, nullptr); }
        return *this;
    }
    bool valid() const noexcept { return handle_ && sms_lease_is_valid(handle_) != 0; }
    std::span<const std::byte> value() const noexcept {
        return handle_ ? detail::byte_span(sms_lease_value(handle_)) : std::span<const std::byte>{};
    }
    std::span<const std::byte> descriptor() const noexcept {
        return handle_ ? detail::byte_span(sms_lease_descriptor(handle_)) : std::span<const std::byte>{};
    }
    status release(wait_options wait = wait_options::defaults()) noexcept {
        if (!handle_) return status::invalid_lease;
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_release_lease(handle_, &native));
    }
    void reset() noexcept { if (handle_) sms_destroy_lease(std::exchange(handle_, nullptr)); }
private:
    friend class memory_store;
    explicit value_lease(sms_lease* handle) noexcept : handle_(handle) {}
    sms_lease* handle_{};
};

class value_reservation {
public:
    value_reservation() noexcept = default;
    ~value_reservation() { reset(); }
    value_reservation(const value_reservation&) = delete;
    value_reservation& operator=(const value_reservation&) = delete;
    value_reservation(value_reservation&& other) noexcept : handle_(std::exchange(other.handle_, nullptr)) {}
    value_reservation& operator=(value_reservation&& other) noexcept {
        if (this != &other) { reset(); handle_ = std::exchange(other.handle_, nullptr); }
        return *this;
    }
    bool valid() const noexcept { return handle_ && sms_reservation_is_valid(handle_) != 0; }
    std::int32_t payload_length() const noexcept { return handle_ ? sms_reservation_payload_length(handle_) : 0; }
    std::int32_t bytes_written() const noexcept { return handle_ ? sms_reservation_bytes_written(handle_) : 0; }
    std::int32_t remaining_bytes() const noexcept {
        return handle_ ? sms_reservation_remaining_bytes(handle_) : 0;
    }
    std::span<std::byte> buffer(std::int32_t size_hint = 0) noexcept {
        if (!handle_) return {};
        const auto value = sms_reservation_buffer(handle_, size_hint);
        return {reinterpret_cast<std::byte*>(value.data), static_cast<std::size_t>(value.length)};
    }
    status advance(std::int32_t count, wait_options wait = wait_options::defaults()) noexcept {
        if (!handle_) return status::invalid_reservation;
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_advance_reservation(handle_, count, &native));
    }
    status commit(wait_options wait = wait_options::defaults()) noexcept {
        if (!handle_) return status::invalid_reservation;
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_commit_reservation(handle_, &native));
    }
    status abort(wait_options wait = wait_options::defaults()) noexcept {
        if (!handle_) return status::invalid_reservation;
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_abort_reservation(handle_, &native));
    }
    void reset() noexcept { if (handle_) sms_destroy_reservation(std::exchange(handle_, nullptr)); }
private:
    friend class memory_store;
    explicit value_reservation(sms_reservation* handle) noexcept : handle_(handle) {}
    sms_reservation* handle_{};
};

class memory_store {
public:
    memory_store() noexcept = default;
    ~memory_store() { close(); }
    memory_store(const memory_store&) = delete;
    memory_store& operator=(const memory_store&) = delete;
    memory_store(memory_store&& other) noexcept : handle_(std::exchange(other.handle_, nullptr)) {}
    memory_store& operator=(memory_store&& other) noexcept {
        if (this != &other) { close(); handle_ = std::exchange(other.handle_, nullptr); }
        return *this;
    }

    static open_status try_create_or_open(const store_options& options, memory_store& result,
                                          wait_options wait = wait_options::defaults()) noexcept {
        result.close();
        sms_store_options native{};
        native.struct_size = sizeof(native);
        native.abi_version = SMS_C_ABI_VERSION;
        native.name_utf8 = options.name.data();
        native.name_length = options.name.size();
        native.open_mode = static_cast<std::int32_t>(options.mode);
        native.total_bytes = options.total_bytes;
        native.slot_count = options.slot_count;
        native.max_value_bytes = options.max_value_bytes;
        native.max_descriptor_bytes = options.max_descriptor_bytes;
        native.max_key_bytes = options.max_key_bytes;
        native.lease_record_count = options.lease_record_count;
        native.enable_lease_recovery = options.enable_lease_recovery ? 1 : 0;
        const auto native_wait = detail::native_wait(wait);
        return static_cast<open_status>(sms_open_store(&native, &native_wait, &result.handle_));
    }

    bool valid() const noexcept { return handle_ != nullptr; }
    void close() noexcept { if (handle_) sms_close_store(std::exchange(handle_, nullptr)); }

    status try_publish(std::span<const std::byte> key, std::span<const std::byte> value,
                       std::span<const std::byte> descriptor = {},
                       wait_options wait = wait_options::defaults()) noexcept {
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_publish(handle_, detail::bytes(key), detail::bytes(value),
                                               detail::bytes(descriptor), &native));
    }

    status try_publish_segments(std::span<const std::byte> key,
                                std::span<const std::span<const std::byte>> segments,
                                std::span<const std::byte> descriptor, std::int64_t& copied,
                                wait_options wait = wait_options::defaults()) noexcept {
        try {
            std::vector<sms_segment> native_segments;
            native_segments.reserve(segments.size());
            for (const auto value : segments)
                native_segments.push_back({reinterpret_cast<const std::uint8_t*>(value.data()),
                                           static_cast<std::uint64_t>(value.size())});
            const auto native = detail::native_wait(wait);
            return static_cast<status>(sms_publish_segments(handle_, detail::bytes(key), native_segments.data(),
                                                            native_segments.size(), detail::bytes(descriptor),
                                                            &native, &copied));
        } catch (...) {
            copied = 0;
            return status::unknown_failure;
        }
    }

    status try_acquire(std::span<const std::byte> key, value_lease& lease,
                       wait_options wait = wait_options::defaults()) noexcept {
        lease.reset();
        sms_lease* handle{};
        const auto native = detail::native_wait(wait);
        const auto result = static_cast<status>(sms_acquire(handle_, detail::bytes(key), &native, &handle));
        if (result == status::success) lease.handle_ = handle;
        return result;
    }

    status try_remove(std::span<const std::byte> key,
                      wait_options wait = wait_options::defaults()) noexcept {
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_remove(handle_, detail::bytes(key), &native));
    }

    status try_reserve(std::span<const std::byte> key, std::int32_t payload_length,
                       std::span<const std::byte> descriptor, value_reservation& reservation,
                       wait_options wait = wait_options::defaults()) noexcept {
        reservation.reset();
        sms_reservation* handle{};
        const auto native = detail::native_wait(wait);
        const auto result = static_cast<status>(sms_reserve(handle_, detail::bytes(key), payload_length,
                                                            detail::bytes(descriptor), &native, &handle));
        if (result == status::success) reservation.handle_ = handle;
        return result;
    }

    status try_recover_leases(bool recover_current_process, recovery_report& report,
                              wait_options wait = wait_options::defaults()) noexcept {
        sms_recovery_report native{};
        native.struct_size = sizeof(native);
        native.abi_version = SMS_C_ABI_VERSION;
        const auto native_wait = detail::native_wait(wait);
        const auto result = static_cast<status>(sms_recover_leases(handle_, recover_current_process ? 1 : 0,
                                                                  &native_wait, &native));
        report = {native.scanned_count, native.recovered_count, native.active_count,
                  native.unsupported_count, native.failed_count};
        return result;
    }

    status try_recover_reservations(bool recover_current_process, recovery_report& report,
                                    wait_options wait = wait_options::defaults()) noexcept {
        sms_recovery_report native{};
        native.struct_size = sizeof(native);
        native.abi_version = SMS_C_ABI_VERSION;
        const auto native_wait = detail::native_wait(wait);
        const auto result = static_cast<status>(sms_recover_reservations(handle_, recover_current_process ? 1 : 0,
                                                                        &native_wait, &native));
        report = {native.scanned_count, native.recovered_count, native.active_count,
                  native.unsupported_count, native.failed_count};
        return result;
    }

    status try_get_diagnostics(diagnostics_snapshot& snapshot,
                               wait_options wait = wait_options::defaults()) noexcept {
        snapshot.value_ = {};
        snapshot.value_.struct_size = sizeof(snapshot.value_);
        snapshot.value_.abi_version = SMS_C_ABI_VERSION;
        const auto native = detail::native_wait(wait);
        return static_cast<status>(sms_get_diagnostics(handle_, &native, &snapshot.value_));
    }

private:
    sms_store* handle_{};
};

} // namespace shared_memory_store
