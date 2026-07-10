#include "internal.hpp"

#include <algorithm>
#include <new>

using sms::detail::Diagnostics;
using sms::detail::Layout;
using sms::detail::LifecycleId;
using sms::detail::Options;
using sms::detail::RecoveryReport;
using sms::detail::Store;
using sms::detail::Wait;

struct sms_store { std::shared_ptr<Store> implementation; };
struct sms_lease {
    std::shared_ptr<Store> store;
    std::int32_t slot{-1};
    LifecycleId lifecycle{};
    std::int32_t lease_id{-1};
};
struct sms_reservation {
    std::shared_ptr<Store> store;
    std::int32_t slot{-1};
    LifecycleId lifecycle{};
};

static_assert(sizeof(sms_open_mode) == 4);
static_assert(sizeof(sms_open_status) == 4);
static_assert(sizeof(sms_status) == 4);
static_assert(sizeof(sms_bytes) == 16 && offsetof(sms_bytes, length) == 8);
static_assert(sizeof(sms_mutable_bytes) == 16 && offsetof(sms_mutable_bytes, length) == 8);
static_assert(sizeof(sms_segment) == 16 && offsetof(sms_segment, length) == 8);
static_assert(sizeof(sms_wait_options) == 16 && offsetof(sms_wait_options, timeout_milliseconds) == 8);
static_assert(sizeof(sms_store_options) == 72 && offsetof(sms_store_options, total_bytes) == 32);
static_assert(sizeof(sms_recovery_report) == 32 && offsetof(sms_recovery_report, scanned_count) == 8);
static_assert(sizeof(sms_diagnostics) == 344 && offsetof(sms_diagnostics, failure_counts) == 160);
static_assert(sizeof(sms_protocol_info) == 36);
static_assert(sizeof(sms_store_layout) == 144 && offsetof(sms_store_layout, required_bytes) == 136);

namespace {

bool abi_compatible(std::uint32_t version) noexcept {
    return (version >> 16) == (SMS_C_ABI_VERSION >> 16);
}

bool read_wait(const sms_wait_options* input, Wait& result) noexcept {
    result = Wait{1000};
    if (!input) return true;
    if (input->struct_size < sizeof(sms_wait_options) || !abi_compatible(input->abi_version) ||
        input->timeout_milliseconds < SMS_WAIT_INFINITE) return false;
    result.milliseconds = input->timeout_milliseconds;
    return true;
}

bool valid_bytes(sms_bytes value) noexcept {
    return value.length <= std::numeric_limits<std::size_t>::max() &&
        (value.length == 0 || value.data != nullptr);
}

std::span<const std::uint8_t> as_span(sms_bytes value) noexcept {
    return {value.data, static_cast<std::size_t>(value.length)};
}

void fill_report(sms_recovery_report& destination, const RecoveryReport& source) noexcept {
    destination.struct_size = sizeof(destination);
    destination.abi_version = SMS_C_ABI_VERSION;
    destination.scanned_count = source.scanned;
    destination.recovered_count = source.recovered;
    destination.active_count = source.active;
    destination.unsupported_count = source.unsupported;
    destination.failed_count = source.failed;
    destination.reserved = 0;
}

} // namespace

extern "C" {

uint32_t SMS_CALL sms_abi_version(void) { return SMS_C_ABI_VERSION; }

sms_status SMS_CALL sms_get_protocol_info(sms_protocol_info* info) {
    if (!info || info->struct_size < sizeof(*info) || !abi_compatible(info->abi_version))
        return SMS_STATUS_UNKNOWN_FAILURE;
    *info = {};
    info->struct_size = sizeof(*info);
    info->abi_version = SMS_C_ABI_VERSION;
    info->layout_major = SMS_LAYOUT_MAJOR_VERSION;
    info->layout_minor = SMS_LAYOUT_MINOR_VERSION;
    info->resource_naming_version = SMS_RESOURCE_NAMING_VERSION;
    info->store_header_size = sizeof(sms::detail::StoreHeader);
    info->index_entry_header_size = sizeof(sms::detail::IndexEntryHeader);
    info->slot_metadata_size = sizeof(sms::detail::SlotMetadata);
    info->lease_record_size = sizeof(sms::detail::LeaseRecord);
    return SMS_STATUS_SUCCESS;
}

sms_status SMS_CALL sms_get_layout_field_offset(sms_layout_field field, uint32_t* offset) {
    if (!offset) return SMS_STATUS_UNKNOWN_FAILURE;
    switch (field) {
        case SMS_LAYOUT_FIELD_HEADER_MAGIC: *offset = offsetof(sms::detail::StoreHeader, Magic); break;
        case SMS_LAYOUT_FIELD_HEADER_INDEX_OFFSET: *offset = offsetof(sms::detail::StoreHeader, IndexOffset); break;
        case SMS_LAYOUT_FIELD_HEADER_STORE_STATE: *offset = offsetof(sms::detail::StoreHeader, StoreState); break;
        case SMS_LAYOUT_FIELD_HEADER_SEQUENCE: *offset = offsetof(sms::detail::StoreHeader, Sequence); break;
        case SMS_LAYOUT_FIELD_INDEX_STATE: *offset = offsetof(sms::detail::IndexEntryHeader, State); break;
        case SMS_LAYOUT_FIELD_INDEX_KEY_HASH: *offset = offsetof(sms::detail::IndexEntryHeader, KeyHash); break;
        case SMS_LAYOUT_FIELD_INDEX_REUSE_EPOCH: *offset = offsetof(sms::detail::IndexEntryHeader, SlotReuseEpoch); break;
        case SMS_LAYOUT_FIELD_SLOT_STATE: *offset = offsetof(sms::detail::SlotMetadata, State); break;
        case SMS_LAYOUT_FIELD_SLOT_REUSE_EPOCH: *offset = offsetof(sms::detail::SlotMetadata, ReuseEpoch); break;
        case SMS_LAYOUT_FIELD_SLOT_USAGE_COUNT: *offset = offsetof(sms::detail::SlotMetadata, UsageCount); break;
        case SMS_LAYOUT_FIELD_SLOT_KEY_HASH: *offset = offsetof(sms::detail::SlotMetadata, KeyHash); break;
        case SMS_LAYOUT_FIELD_SLOT_COMMITTED_SEQUENCE: *offset = offsetof(sms::detail::SlotMetadata, CommittedSequence); break;
        case SMS_LAYOUT_FIELD_LEASE_STATE: *offset = offsetof(sms::detail::LeaseRecord, State); break;
        case SMS_LAYOUT_FIELD_LEASE_REUSE_EPOCH: *offset = offsetof(sms::detail::LeaseRecord, SlotReuseEpoch); break;
        case SMS_LAYOUT_FIELD_LEASE_OWNER_PROCESS_ID: *offset = offsetof(sms::detail::LeaseRecord, OwnerProcessId); break;
        case SMS_LAYOUT_FIELD_LEASE_ACQUIRE_SEQUENCE: *offset = offsetof(sms::detail::LeaseRecord, AcquireSequence); break;
        default: *offset = 0; return SMS_STATUS_UNKNOWN_FAILURE;
    }
    return SMS_STATUS_SUCCESS;
}

sms_open_status SMS_CALL sms_calculate_required_bytes(
    int32_t slot_count, int32_t max_value_bytes, int32_t max_descriptor_bytes,
    int32_t max_key_bytes, int32_t lease_record_count, int64_t* required_bytes) {
    if (!required_bytes) return SMS_OPEN_INVALID_OPTIONS;
    *required_bytes = 0;
    Layout layout{};
    if (!Layout::calculate(0, slot_count, max_value_bytes, max_descriptor_bytes,
                           max_key_bytes, lease_record_count, layout)) return SMS_OPEN_INVALID_OPTIONS;
    *required_bytes = layout.required_bytes;
    return SMS_OPEN_SUCCESS;
}

sms_open_status SMS_CALL sms_open_store(const sms_store_options* options,
                                        const sms_wait_options* wait_options,
                                        sms_store** store) {
    if (!store) return SMS_OPEN_INVALID_OPTIONS;
    *store = nullptr;
    Wait wait{};
    if (!options || options->struct_size < sizeof(*options) || !abi_compatible(options->abi_version) ||
        !read_wait(wait_options, wait) || options->name_length > std::numeric_limits<std::size_t>::max() ||
        (options->name_length > 0 && !options->name_utf8))
        return SMS_OPEN_INVALID_OPTIONS;
    try {
        Options native{};
        native.name.assign(options->name_utf8 ? options->name_utf8 : "",
                           static_cast<std::size_t>(options->name_length));
        native.open_mode = static_cast<sms_open_mode>(options->open_mode);
        native.total_bytes = options->total_bytes;
        native.slot_count = options->slot_count;
        native.max_value_bytes = options->max_value_bytes;
        native.max_descriptor_bytes = options->max_descriptor_bytes;
        native.max_key_bytes = options->max_key_bytes;
        native.lease_record_count = options->lease_record_count;
        native.enable_lease_recovery = options->enable_lease_recovery != 0;
        std::shared_ptr<Store> implementation;
        const auto status = Store::open(native, wait, implementation);
        if (status != SMS_OPEN_SUCCESS) return status;
        auto* handle = new (std::nothrow) sms_store{implementation};
        if (!handle) { implementation->close(); return SMS_OPEN_MAPPING_FAILED; }
        *store = handle;
        return SMS_OPEN_SUCCESS;
    } catch (...) {
        return SMS_OPEN_MAPPING_FAILED;
    }
}

void SMS_CALL sms_close_store(sms_store* store) {
    if (!store) return;
    if (store->implementation) store->implementation->close();
    delete store;
}

sms_status SMS_CALL sms_get_store_layout(sms_store* store, const sms_wait_options* wait_options,
                                         sms_store_layout* layout) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!layout || layout->struct_size < sizeof(*layout) || !abi_compatible(layout->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    Layout native{};
    const auto status = store->implementation->get_layout(wait, native);
    if (status != SMS_STATUS_SUCCESS) return status;
    *layout = {};
    layout->struct_size = sizeof(*layout);
    layout->abi_version = SMS_C_ABI_VERSION;
    layout->total_bytes = native.total_bytes;
    layout->slot_count = native.slot_count;
    layout->lease_record_count = native.lease_record_count;
    layout->max_value_bytes = native.max_value_bytes;
    layout->max_descriptor_bytes = native.max_descriptor_bytes;
    layout->max_key_bytes = native.max_key_bytes;
    layout->header_length = native.header_length;
    layout->index_entry_count = native.index_entry_count;
    layout->index_entry_size = native.index_entry_size;
    layout->index_offset = native.index_offset;
    layout->index_length = native.index_length;
    layout->lease_registry_offset = native.lease_registry_offset;
    layout->lease_registry_length = native.lease_registry_length;
    layout->slot_metadata_offset = native.slot_metadata_offset;
    layout->slot_metadata_length = native.slot_metadata_length;
    layout->descriptor_stride = native.descriptor_stride;
    layout->payload_stride = native.payload_stride;
    layout->descriptor_storage_offset = native.descriptor_storage_offset;
    layout->descriptor_storage_length = native.descriptor_storage_length;
    layout->payload_storage_offset = native.payload_storage_offset;
    layout->payload_storage_length = native.payload_storage_length;
    layout->required_bytes = native.required_bytes;
    return SMS_STATUS_SUCCESS;
}

sms_status SMS_CALL sms_publish(sms_store* store, sms_bytes key, sms_bytes value,
                                sms_bytes descriptor, const sms_wait_options* wait_options) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(value) || !valid_bytes(descriptor))
        return SMS_STATUS_UNKNOWN_FAILURE;
    return store->implementation->publish(as_span(key), as_span(value), as_span(descriptor), wait);
}

sms_status SMS_CALL sms_publish_segments(sms_store* store, sms_bytes key,
                                         const sms_segment* segments, uint64_t segment_count,
                                         sms_bytes descriptor, const sms_wait_options* wait_options,
                                         int64_t* copied_bytes) {
    if (copied_bytes) *copied_bytes = 0;
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!copied_bytes || !read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(descriptor) ||
        segment_count > std::numeric_limits<std::size_t>::max() ||
        (segment_count > 0 && !segments)) return SMS_STATUS_UNKNOWN_FAILURE;
    const auto count = static_cast<std::size_t>(segment_count);
    for (std::size_t index = 0; index < count; ++index)
        if (segments[index].length > std::numeric_limits<std::size_t>::max() ||
            (segments[index].length > 0 && !segments[index].data)) return SMS_STATUS_UNKNOWN_FAILURE;
    return store->implementation->publish_segments(as_span(key), {segments, count}, as_span(descriptor),
                                                   wait, *copied_bytes);
}

sms_status SMS_CALL sms_acquire(sms_store* store, sms_bytes key,
                                const sms_wait_options* wait_options, sms_lease** lease) {
    if (!lease) return SMS_STATUS_INVALID_LEASE;
    *lease = nullptr;
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key)) return SMS_STATUS_UNKNOWN_FAILURE;
    std::int32_t slot{}, lease_id{}; LifecycleId lifecycle{};
    const auto status = store->implementation->acquire(as_span(key), wait, slot, lifecycle, lease_id);
    if (status != SMS_STATUS_SUCCESS) return status;
    auto* handle = new (std::nothrow) sms_lease{store->implementation, slot, lifecycle, lease_id};
    if (!handle) {
        store->implementation->release_lease(slot, lifecycle, lease_id, Wait{1000});
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
    *lease = handle;
    return SMS_STATUS_SUCCESS;
}

int32_t SMS_CALL sms_lease_is_valid(const sms_lease* lease) {
    return lease && lease->store && lease->store->lease_valid(lease->slot, lease->lifecycle, lease->lease_id) ? 1 : 0;
}

sms_bytes SMS_CALL sms_lease_value(const sms_lease* lease) {
    if (!lease || !lease->store) return {};
    const auto value = lease->store->lease_value(lease->slot, lease->lifecycle, lease->lease_id);
    return {value.data(), value.size()};
}

sms_bytes SMS_CALL sms_lease_descriptor(const sms_lease* lease) {
    if (!lease || !lease->store) return {};
    const auto value = lease->store->lease_descriptor(lease->slot, lease->lifecycle, lease->lease_id);
    return {value.data(), value.size()};
}

sms_status SMS_CALL sms_release_lease(sms_lease* lease, const sms_wait_options* wait_options) {
    Wait wait{};
    if (!lease || !lease->store) return SMS_STATUS_INVALID_LEASE;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    return lease->store->release_lease(lease->slot, lease->lifecycle, lease->lease_id, wait);
}

void SMS_CALL sms_destroy_lease(sms_lease* lease) {
    if (!lease) return;
    if (lease->store && lease->store->lease_valid(lease->slot, lease->lifecycle, lease->lease_id))
        lease->store->release_lease(lease->slot, lease->lifecycle, lease->lease_id, Wait{1000});
    delete lease;
}

sms_status SMS_CALL sms_remove(sms_store* store, sms_bytes key, const sms_wait_options* wait_options) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key)) return SMS_STATUS_UNKNOWN_FAILURE;
    return store->implementation->remove(as_span(key), wait);
}

sms_status SMS_CALL sms_reserve(sms_store* store, sms_bytes key, int32_t payload_length,
                                sms_bytes descriptor, const sms_wait_options* wait_options,
                                sms_reservation** reservation) {
    if (!reservation) return SMS_STATUS_INVALID_RESERVATION;
    *reservation = nullptr;
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(descriptor))
        return SMS_STATUS_UNKNOWN_FAILURE;
    std::int32_t slot{}; LifecycleId lifecycle{};
    const auto status = store->implementation->reserve(as_span(key), payload_length, as_span(descriptor),
                                                       wait, slot, lifecycle);
    if (status != SMS_STATUS_SUCCESS) return status;
    auto* handle = new (std::nothrow) sms_reservation{store->implementation, slot, lifecycle};
    if (!handle) {
        store->implementation->abort_reservation(slot, lifecycle, false, Wait{1000});
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
    *reservation = handle;
    return SMS_STATUS_SUCCESS;
}

int32_t SMS_CALL sms_reservation_is_valid(const sms_reservation* reservation) {
    return reservation && reservation->store &&
        reservation->store->reservation_valid(reservation->slot, reservation->lifecycle) ? 1 : 0;
}

int32_t SMS_CALL sms_reservation_payload_length(const sms_reservation* reservation) {
    return reservation && reservation->store
        ? reservation->store->reservation_payload_length(reservation->slot, reservation->lifecycle) : 0;
}

int32_t SMS_CALL sms_reservation_bytes_written(const sms_reservation* reservation) {
    return reservation && reservation->store
        ? reservation->store->reservation_bytes_written(reservation->slot, reservation->lifecycle) : 0;
}

int32_t SMS_CALL sms_reservation_remaining_bytes(const sms_reservation* reservation) {
    if (!reservation || !reservation->store) return 0;
    const auto total = reservation->store->reservation_payload_length(reservation->slot, reservation->lifecycle);
    const auto written = reservation->store->reservation_bytes_written(reservation->slot, reservation->lifecycle);
    return std::max(0, total - written);
}

sms_mutable_bytes SMS_CALL sms_reservation_buffer(sms_reservation* reservation, int32_t size_hint) {
    if (!reservation || !reservation->store) return {};
    const auto value = reservation->store->reservation_buffer(reservation->slot, reservation->lifecycle, size_hint);
    return {value.data(), value.size()};
}

sms_status SMS_CALL sms_advance_reservation(sms_reservation* reservation, int32_t byte_count,
                                            const sms_wait_options* wait_options) {
    Wait wait{};
    if (!reservation || !reservation->store) return SMS_STATUS_INVALID_RESERVATION;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    return reservation->store->advance_reservation(reservation->slot, reservation->lifecycle, byte_count, wait);
}

sms_status SMS_CALL sms_commit_reservation(sms_reservation* reservation,
                                           const sms_wait_options* wait_options) {
    Wait wait{};
    if (!reservation || !reservation->store) return SMS_STATUS_INVALID_RESERVATION;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    return reservation->store->commit_reservation(reservation->slot, reservation->lifecycle, wait);
}

sms_status SMS_CALL sms_abort_reservation(sms_reservation* reservation,
                                          const sms_wait_options* wait_options) {
    Wait wait{};
    if (!reservation || !reservation->store) return SMS_STATUS_INVALID_RESERVATION;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    return reservation->store->abort_reservation(reservation->slot, reservation->lifecycle, true, wait);
}

void SMS_CALL sms_destroy_reservation(sms_reservation* reservation) {
    if (!reservation) return;
    if (reservation->store && reservation->store->reservation_valid(reservation->slot, reservation->lifecycle))
        reservation->store->abort_reservation(reservation->slot, reservation->lifecycle, false, Wait{1000});
    delete reservation;
}

sms_status SMS_CALL sms_recover_leases(sms_store* store, int32_t recover_current_process,
                                       const sms_wait_options* wait_options, sms_recovery_report* report) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!report || report->struct_size < sizeof(*report) || !abi_compatible(report->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    RecoveryReport native{};
    const auto status = store->implementation->recover_leases(recover_current_process != 0, wait, native);
    fill_report(*report, native);
    return status;
}

sms_status SMS_CALL sms_recover_reservations(sms_store* store, int32_t recover_current_process,
                                             const sms_wait_options* wait_options,
                                             sms_recovery_report* report) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!report || report->struct_size < sizeof(*report) || !abi_compatible(report->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    RecoveryReport native{};
    const auto status = store->implementation->recover_reservations(recover_current_process != 0, wait, native);
    fill_report(*report, native);
    return status;
}

sms_status SMS_CALL sms_get_diagnostics(sms_store* store, const sms_wait_options* wait_options,
                                        sms_diagnostics* diagnostics) {
    Wait wait{};
    if (!store || !store->implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!diagnostics || diagnostics->struct_size < sizeof(*diagnostics) ||
        !abi_compatible(diagnostics->abi_version) || !read_wait(wait_options, wait))
        return SMS_STATUS_UNKNOWN_FAILURE;
    Diagnostics native{};
    const auto status = store->implementation->diagnostics(wait, native);
    if (status != SMS_STATUS_SUCCESS) return status;
    *diagnostics = {};
    diagnostics->struct_size = sizeof(*diagnostics);
    diagnostics->abi_version = SMS_C_ABI_VERSION;
    diagnostics->total_bytes = native.total_bytes;
    diagnostics->slot_count = native.slot_count;
    diagnostics->free_slot_count = native.free_slots;
    diagnostics->published_slot_count = native.published_slots;
    diagnostics->pending_removal_count = native.pending_removal;
    diagnostics->active_lease_count = native.active_leases;
    diagnostics->active_reservation_count = native.active_reservations;
    diagnostics->index_entry_count = native.index_entries;
    diagnostics->occupied_index_entry_count = native.occupied_index_entries;
    diagnostics->tombstone_index_entry_count = native.tombstone_index_entries;
    diagnostics->empty_index_entry_count = native.empty_index_entries;
    diagnostics->usable_index_capacity = native.empty_index_entries + native.tombstone_index_entries;
    diagnostics->last_observed_probe_length = native.last_probe;
    diagnostics->max_observed_probe_length = native.max_probe;
    diagnostics->last_failure_status = native.last_failure;
    diagnostics->aborted_reservation_count = native.aborted_reservations;
    diagnostics->recovered_lease_count = native.recovered_leases;
    diagnostics->active_lease_recovery_count = native.active_lease_recoveries;
    diagnostics->unsupported_lease_recovery_count = native.unsupported_lease_recoveries;
    diagnostics->failed_lease_recovery_count = native.failed_lease_recoveries;
    diagnostics->recovered_reservation_count = native.recovered_reservations;
    diagnostics->active_reservation_recovery_count = native.active_reservation_recoveries;
    diagnostics->unsupported_reservation_recovery_count = native.unsupported_reservation_recoveries;
    diagnostics->failed_reservation_recovery_count = native.failed_reservation_recoveries;
    diagnostics->capacity_pressure_count = native.capacity_pressure;
    diagnostics->index_compaction_count = native.index_compactions;
    for (std::size_t index = 0; index < native.failures.size(); ++index)
        diagnostics->failure_counts[index] = native.failures[index];
    return SMS_STATUS_SUCCESS;
}

} // extern "C"
