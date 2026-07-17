#include "internal.hpp"
#include "operation_budget.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <new>

using sms::detail::Diagnostics;
using sms::detail::CancellationFlag;
using sms::detail::LayoutV2;
using sms::detail::LifecycleId;
using sms::detail::Options;
using sms::detail::RecoveryReport;
using sms::detail::Store;
using sms::detail::Wait;

struct sms_store {
    enum class close_state { open, closing, closed };

    sms_store(std::shared_ptr<Store> value, LayoutV2 layout)
        : implementation(std::move(value)), public_layout(layout) {}

    // Hot calls take only an atomic shared snapshot. Close coordination never
    // enters a publish/read/remove path.
    std::atomic<std::shared_ptr<Store>> implementation;
    const LayoutV2 public_layout;
    std::mutex close_mutex;
    std::condition_variable close_completed;
    std::atomic<close_state> state{close_state::open};
};
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
struct sms_cancellation { CancellationFlag flag; };

static_assert(sizeof(sms_open_mode) == 4);
static_assert(sizeof(sms_open_status) == 4);
static_assert(sizeof(sms_status) == 4);
static_assert(sizeof(sms_bytes) == 16 && offsetof(sms_bytes, length) == 8);
static_assert(sizeof(sms_mutable_bytes) == 16 && offsetof(sms_mutable_bytes, length) == 8);
static_assert(sizeof(sms_segment) == 16 && offsetof(sms_segment, length) == 8);
static_assert(sizeof(sms_wait_options) == 24 && offsetof(sms_wait_options, cancellation) == 16);
static_assert(sizeof(sms_store_options) == 72 && offsetof(sms_store_options, participant_record_count) == 60);
static_assert(sizeof(sms_recovery_report) == 32 && offsetof(sms_recovery_report, scanned_count) == 8);
static_assert(sizeof(sms_diagnostics) == 560);
static_assert(offsetof(sms_diagnostics, failure_counts) == 376);
static_assert(sizeof(sms_protocol_info) == 64 && offsetof(sms_protocol_info, required_features) == 24);
static_assert(sizeof(sms_store_layout) == 240 && offsetof(sms_store_layout, required_bytes) == 232);

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
    result.cancellation = input->cancellation == nullptr
        ? nullptr
        : &input->cancellation->flag;
    return true;
}

bool wait_canceled(const sms_wait_options* input) noexcept {
    return input && input->cancellation && input->cancellation->flag.is_canceled();
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

constexpr std::array<std::uint32_t, 47> header_offsets{
    offsetof(sms::detail::StoreHeaderV2, Magic),
    offsetof(sms::detail::StoreHeaderV2, LayoutMajorVersion),
    offsetof(sms::detail::StoreHeaderV2, LayoutMinorVersion),
    offsetof(sms::detail::StoreHeaderV2, HeaderLength),
    offsetof(sms::detail::StoreHeaderV2, ResourceProtocolVersion),
    offsetof(sms::detail::StoreHeaderV2, RequiredFeatures),
    offsetof(sms::detail::StoreHeaderV2, OptionalFeatures),
    offsetof(sms::detail::StoreHeaderV2, TotalBytes),
    offsetof(sms::detail::StoreHeaderV2, StoreId),
    offsetof(sms::detail::StoreHeaderV2, Control),
    offsetof(sms::detail::StoreHeaderV2, Sequence),
    offsetof(sms::detail::StoreHeaderV2, SlotCount),
    offsetof(sms::detail::StoreHeaderV2, LeaseRecordCount),
    offsetof(sms::detail::StoreHeaderV2, ParticipantRecordCount),
    offsetof(sms::detail::StoreHeaderV2, MaxKeyBytes),
    offsetof(sms::detail::StoreHeaderV2, MaxDescriptorBytes),
    offsetof(sms::detail::StoreHeaderV2, MaxValueBytes),
    offsetof(sms::detail::StoreHeaderV2, ParticipantIndexBits),
    offsetof(sms::detail::StoreHeaderV2, ParticipantGenerationBits),
    offsetof(sms::detail::StoreHeaderV2, ParticipantOffset),
    offsetof(sms::detail::StoreHeaderV2, ParticipantLength),
    offsetof(sms::detail::StoreHeaderV2, ParticipantStride),
    offsetof(sms::detail::StoreHeaderV2, PrimaryLaneCount),
    offsetof(sms::detail::StoreHeaderV2, PrimaryBucketCount),
    offsetof(sms::detail::StoreHeaderV2, PrimaryBucketStride),
    offsetof(sms::detail::StoreHeaderV2, PrimaryDirectoryOffset),
    offsetof(sms::detail::StoreHeaderV2, PrimaryDirectoryLength),
    offsetof(sms::detail::StoreHeaderV2, OverflowDirectoryOffset),
    offsetof(sms::detail::StoreHeaderV2, OverflowDirectoryLength),
    offsetof(sms::detail::StoreHeaderV2, OverflowStride),
    offsetof(sms::detail::StoreHeaderV2, LeaseStride),
    offsetof(sms::detail::StoreHeaderV2, LeaseRegistryOffset),
    offsetof(sms::detail::StoreHeaderV2, LeaseRegistryLength),
    offsetof(sms::detail::StoreHeaderV2, SlotMetadataStride),
    offsetof(sms::detail::StoreHeaderV2, KeyStride),
    offsetof(sms::detail::StoreHeaderV2, SlotMetadataOffset),
    offsetof(sms::detail::StoreHeaderV2, SlotMetadataLength),
    offsetof(sms::detail::StoreHeaderV2, KeyStorageOffset),
    offsetof(sms::detail::StoreHeaderV2, KeyStorageLength),
    offsetof(sms::detail::StoreHeaderV2, DescriptorStride),
    offsetof(sms::detail::StoreHeaderV2, PayloadStride),
    offsetof(sms::detail::StoreHeaderV2, DescriptorStorageOffset),
    offsetof(sms::detail::StoreHeaderV2, DescriptorStorageLength),
    offsetof(sms::detail::StoreHeaderV2, PayloadStorageOffset),
    offsetof(sms::detail::StoreHeaderV2, PayloadStorageLength),
    offsetof(sms::detail::StoreHeaderV2, PidNamespaceId),
    offsetof(sms::detail::StoreHeaderV2, PidNamespaceMode),
};

constexpr std::array<std::uint32_t, 6> participant_offsets{
    offsetof(sms::detail::ParticipantRecordV2, Control),
    offsetof(sms::detail::ParticipantRecordV2, IdentityKind),
    offsetof(sms::detail::ParticipantRecordV2, Reserved),
    offsetof(sms::detail::ParticipantRecordV2, ProcessStartValue),
    offsetof(sms::detail::ParticipantRecordV2, OpenSequence),
    offsetof(sms::detail::ParticipantRecordV2, PidNamespaceId),
};

constexpr std::array<std::uint32_t, 3> primary_offsets{
    offsetof(sms::detail::PrimaryDirectoryBucketV2, SpillSummary),
    offsetof(sms::detail::PrimaryDirectoryBucketV2, Mutation),
    offsetof(sms::detail::PrimaryDirectoryBucketV2, Lanes),
};

constexpr std::array<std::uint32_t, 3> lease_offsets{
    offsetof(sms::detail::LeaseRecordV2, Control),
    offsetof(sms::detail::LeaseRecordV2, SlotBinding),
    offsetof(sms::detail::LeaseRecordV2, AcquireSequence),
};

constexpr std::array<std::uint32_t, 14> slot_offsets{
    offsetof(sms::detail::ValueSlotMetadataV2, Control),
    offsetof(sms::detail::ValueSlotMetadataV2, DirectoryBinding),
    offsetof(sms::detail::ValueSlotMetadataV2, DirectoryLocation),
    offsetof(sms::detail::ValueSlotMetadataV2, DirectoryOperation),
    offsetof(sms::detail::ValueSlotMetadataV2, KeyHash),
    offsetof(sms::detail::ValueSlotMetadataV2, KeyLength),
    offsetof(sms::detail::ValueSlotMetadataV2, DescriptorLength),
    offsetof(sms::detail::ValueSlotMetadataV2, ValueLength),
    offsetof(sms::detail::ValueSlotMetadataV2, PublicationIntent),
    offsetof(sms::detail::ValueSlotMetadataV2, BytesAdvanced),
    offsetof(sms::detail::ValueSlotMetadataV2, CommitSequence),
    offsetof(sms::detail::ValueSlotMetadataV2, KeyOffset),
    offsetof(sms::detail::ValueSlotMetadataV2, DescriptorOffset),
    offsetof(sms::detail::ValueSlotMetadataV2, PayloadOffset),
};

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
    info->resource_protocol = SMS_RESOURCE_PROTOCOL_VERSION;
    info->required_features = SMS_REQUIRED_FEATURES;
    info->optional_features = SMS_OPTIONAL_FEATURES;
    info->store_header_size = SMS_STORE_HEADER_SIZE;
    info->participant_record_size = SMS_PARTICIPANT_RECORD_SIZE;
    info->primary_directory_bucket_size = SMS_PRIMARY_DIRECTORY_BUCKET_SIZE;
    info->overflow_binding_size = SMS_OVERFLOW_BINDING_SIZE;
    info->lease_record_size = SMS_LEASE_RECORD_SIZE;
    info->value_slot_size = SMS_VALUE_SLOT_SIZE;
    return SMS_STATUS_SUCCESS;
}

sms_status SMS_CALL sms_get_layout_field_offset(sms_layout_field field, uint32_t* offset) {
    if (!offset) return SMS_STATUS_UNKNOWN_FAILURE;
    if (field >= 0 && field < static_cast<sms_layout_field>(header_offsets.size()))
        *offset = header_offsets[static_cast<std::size_t>(field)];
    else if (field >= 100 && field < 100 + static_cast<sms_layout_field>(participant_offsets.size()))
        *offset = participant_offsets[static_cast<std::size_t>(field - 100)];
    else if (field >= 200 && field < 200 + static_cast<sms_layout_field>(primary_offsets.size()))
        *offset = primary_offsets[static_cast<std::size_t>(field - 200)];
    else if (field == 300)
        *offset = 0;
    else if (field >= 400 && field < 400 + static_cast<sms_layout_field>(lease_offsets.size()))
        *offset = lease_offsets[static_cast<std::size_t>(field - 400)];
    else if (field >= 500 && field < 500 + static_cast<sms_layout_field>(slot_offsets.size()))
        *offset = slot_offsets[static_cast<std::size_t>(field - 500)];
    else {
        *offset = 0;
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
    return SMS_STATUS_SUCCESS;
}

sms_status SMS_CALL sms_create_cancellation(sms_cancellation** cancellation) {
    if (!cancellation) return SMS_STATUS_UNKNOWN_FAILURE;
    *cancellation = new (std::nothrow) sms_cancellation{};
    return *cancellation ? SMS_STATUS_SUCCESS : SMS_STATUS_UNKNOWN_FAILURE;
}

sms_status SMS_CALL sms_signal_cancellation(sms_cancellation* cancellation) {
    if (!cancellation) return SMS_STATUS_UNKNOWN_FAILURE;
    cancellation->flag.cancel();
    return SMS_STATUS_SUCCESS;
}

int32_t SMS_CALL sms_cancellation_is_signaled(const sms_cancellation* cancellation) {
    return cancellation && cancellation->flag.is_canceled() ? 1 : 0;
}

void SMS_CALL sms_destroy_cancellation(sms_cancellation* cancellation) {
    delete cancellation;
}

sms_open_status SMS_CALL sms_calculate_required_bytes(
    int32_t slot_count, int32_t max_value_bytes, int32_t max_descriptor_bytes,
    int32_t max_key_bytes, int32_t lease_record_count,
    int32_t participant_record_count, int64_t* required_bytes) {
    if (!required_bytes) return SMS_OPEN_INVALID_OPTIONS;
    *required_bytes = 0;
    LayoutV2 layout{};
    if (!LayoutV2::calculate(0, slot_count, max_value_bytes, max_descriptor_bytes,
                             max_key_bytes, lease_record_count,
                             participant_record_count, layout)) return SMS_OPEN_INVALID_OPTIONS;
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
    if (wait_canceled(wait_options)) return SMS_OPEN_OPERATION_CANCELED;
    try {
        LayoutV2 public_layout{};
        if (!LayoutV2::calculate(
                options->total_bytes, options->slot_count, options->max_value_bytes,
                options->max_descriptor_bytes, options->max_key_bytes,
                options->lease_record_count, options->participant_record_count,
                public_layout)) {
            return SMS_OPEN_INVALID_OPTIONS;
        }
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
        native.participant_record_count = options->participant_record_count;
        native.enable_lease_recovery = options->enable_lease_recovery != 0;
        std::shared_ptr<Store> implementation;
        const auto status = Store::open(native, wait, implementation);
        if (status != SMS_OPEN_SUCCESS) return status;
        auto* handle = new (std::nothrow) sms_store{
            implementation, public_layout};
        if (!handle) {
            implementation->close();
            return SMS_OPEN_MAPPING_FAILED;
        }
        *store = handle;
        return SMS_OPEN_SUCCESS;
    } catch (...) {
        return SMS_OPEN_MAPPING_FAILED;
    }
}

void SMS_CALL sms_close_store(sms_store* store) {
    if (!store) return;
    try {
        auto observed = store->state.load(std::memory_order_acquire);
        for (;;) {
            if (observed == sms_store::close_state::closed) return;
            if (observed == sms_store::close_state::closing) {
                std::unique_lock lock(store->close_mutex);
                store->close_completed.wait(lock, [store] {
                    return store->state.load(std::memory_order_acquire) ==
                        sms_store::close_state::closed;
                });
                return;
            }
            if (store->state.compare_exchange_weak(
                    observed,
                    sms_store::close_state::closing,
                    std::memory_order_acq_rel,
                    std::memory_order_acquire)) {
                break;
            }
        }

        auto implementation = store->implementation.exchange(
            {}, std::memory_order_acq_rel);
        if (implementation) implementation->close();
        store->state.store(
            sms_store::close_state::closed, std::memory_order_release);
        store->close_completed.notify_all();
    } catch (...) {
        // No C++ exception may cross the C ABI. A synchronization-adapter
        // failure is not shared corruption and cannot justify termination.
        auto implementation = store->implementation.exchange(
            {}, std::memory_order_acq_rel);
        if (implementation) implementation->close();
        store->state.store(
            sms_store::close_state::closed, std::memory_order_release);
        try {
            store->close_completed.notify_all();
        } catch (...) {
            // notify_all is non-throwing in supported standard libraries.
        }
    }
}

void SMS_CALL sms_destroy_store(sms_store* store) {
    if (!store) return;
    // Caller-synchronized lifetime end: no thread may enter any store ABI with
    // this pointer once destruction begins.
    sms_close_store(store);
    delete store;
}

sms_status SMS_CALL sms_get_store_layout(sms_store* store, const sms_wait_options* wait_options,
                                         sms_store_layout* layout) {
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!layout || layout->struct_size < sizeof(*layout) || !abi_compatible(layout->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    const auto& native = store->public_layout;
    *layout = {};
    layout->struct_size = sizeof(*layout);
    layout->abi_version = SMS_C_ABI_VERSION;
    layout->total_bytes = native.total_bytes;
    layout->slot_count = native.slot_count;
    layout->lease_record_count = native.lease_record_count;
    layout->participant_record_count = native.participant_record_count;
    layout->max_value_bytes = native.max_value_bytes;
    layout->max_descriptor_bytes = native.max_descriptor_bytes;
    layout->max_key_bytes = native.max_key_bytes;
    layout->header_length = native.header_length;
    layout->participant_index_bits = native.participant_index_bits;
    layout->participant_generation_bits = native.participant_generation_bits;
    layout->participant_stride = native.participant_stride;
    layout->participant_offset = native.participant_offset;
    layout->participant_length = native.participant_length;
    layout->primary_lane_count = native.primary_lane_count;
    layout->primary_bucket_count = native.primary_bucket_count;
    layout->primary_bucket_stride = native.primary_bucket_stride;
    layout->primary_directory_offset = native.primary_directory_offset;
    layout->primary_directory_length = native.primary_directory_length;
    layout->overflow_stride = native.overflow_stride;
    layout->overflow_directory_offset = native.overflow_directory_offset;
    layout->overflow_directory_length = native.overflow_directory_length;
    layout->lease_stride = native.lease_stride;
    layout->lease_registry_offset = native.lease_registry_offset;
    layout->lease_registry_length = native.lease_registry_length;
    layout->slot_metadata_stride = native.slot_metadata_stride;
    layout->key_stride = native.key_stride;
    layout->slot_metadata_offset = native.slot_metadata_offset;
    layout->slot_metadata_length = native.slot_metadata_length;
    layout->key_storage_offset = native.key_storage_offset;
    layout->key_storage_length = native.key_storage_length;
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
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(value) || !valid_bytes(descriptor))
        return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    return implementation->publish(
        as_span(key), as_span(value), as_span(descriptor), wait);
}

sms_status SMS_CALL sms_publish_segments(sms_store* store, sms_bytes key,
                                         const sms_segment* segments, uint64_t segment_count,
                                         sms_bytes descriptor, const sms_wait_options* wait_options,
                                         int64_t* copied_bytes) {
    if (copied_bytes) *copied_bytes = 0;
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!copied_bytes || !read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(descriptor) ||
        segment_count > std::numeric_limits<std::size_t>::max() ||
        (segment_count > 0 && !segments)) return SMS_STATUS_UNKNOWN_FAILURE;
    const auto count = static_cast<std::size_t>(segment_count);
    for (std::size_t index = 0; index < count; ++index)
        if (segments[index].length > std::numeric_limits<std::size_t>::max() ||
            (segments[index].length > 0 && !segments[index].data)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    return implementation->publish_segments(
        as_span(key), {segments, count}, as_span(descriptor),
        wait, *copied_bytes);
}

sms_status SMS_CALL sms_acquire(sms_store* store, sms_bytes key,
                                const sms_wait_options* wait_options, sms_lease** lease) {
    if (!lease) return SMS_STATUS_INVALID_LEASE;
    *lease = nullptr;
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    std::int32_t slot{}, lease_id{}; LifecycleId lifecycle{};
    const auto status = implementation->acquire(
        as_span(key), wait, slot, lifecycle, lease_id);
    if (status != SMS_STATUS_SUCCESS) return status;
    auto* handle = new (std::nothrow) sms_lease{
        implementation, slot, lifecycle, lease_id};
    if (!handle) {
        implementation->release_lease(
            slot, lifecycle, lease_id, Wait{1000});
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
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
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
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    return implementation->remove(as_span(key), wait);
}

sms_status SMS_CALL sms_reserve(sms_store* store, sms_bytes key, int32_t payload_length,
                                sms_bytes descriptor, const sms_wait_options* wait_options,
                                sms_reservation** reservation) {
    if (!reservation) return SMS_STATUS_INVALID_RESERVATION;
    *reservation = nullptr;
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!read_wait(wait_options, wait) || !valid_bytes(key) || !valid_bytes(descriptor))
        return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    std::int32_t slot{}; LifecycleId lifecycle{};
    const auto status = implementation->reserve(
        as_span(key), payload_length, as_span(descriptor),
        wait, slot, lifecycle);
    if (status != SMS_STATUS_SUCCESS) return status;
    auto* handle = new (std::nothrow) sms_reservation{
        implementation, slot, lifecycle};
    if (!handle) {
        implementation->abort_reservation(
            slot, lifecycle, false, Wait{1000});
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
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    return reservation->store->advance_reservation(reservation->slot, reservation->lifecycle, byte_count, wait);
}

sms_status SMS_CALL sms_commit_reservation(sms_reservation* reservation,
                                           const sms_wait_options* wait_options) {
    Wait wait{};
    if (!reservation || !reservation->store) return SMS_STATUS_INVALID_RESERVATION;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    return reservation->store->commit_reservation(reservation->slot, reservation->lifecycle, wait);
}

sms_status SMS_CALL sms_abort_reservation(sms_reservation* reservation,
                                          const sms_wait_options* wait_options) {
    Wait wait{};
    if (!reservation || !reservation->store) return SMS_STATUS_INVALID_RESERVATION;
    if (!read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
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
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!report || report->struct_size < sizeof(*report) || !abi_compatible(report->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    RecoveryReport native{};
    const auto status = implementation->recover_leases(
        recover_current_process != 0, wait, native);
    fill_report(*report, native);
    return status;
}

sms_status SMS_CALL sms_recover_reservations(sms_store* store, int32_t recover_current_process,
                                             const sms_wait_options* wait_options,
                                             sms_recovery_report* report) {
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!report || report->struct_size < sizeof(*report) || !abi_compatible(report->abi_version) ||
        !read_wait(wait_options, wait)) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    RecoveryReport native{};
    const auto status = implementation->recover_reservations(
        recover_current_process != 0, wait, native);
    fill_report(*report, native);
    return status;
}

sms_status SMS_CALL sms_get_diagnostics(sms_store* store, const sms_wait_options* wait_options,
                                        sms_diagnostics* diagnostics) {
    Wait wait{};
    auto implementation = store
        ? store->implementation.load(std::memory_order_acquire)
        : std::shared_ptr<Store>{};
    if (!implementation) return SMS_STATUS_STORE_DISPOSED;
    if (!diagnostics || diagnostics->struct_size < sizeof(*diagnostics) ||
        !abi_compatible(diagnostics->abi_version) || !read_wait(wait_options, wait))
        return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait_canceled(wait_options)) return SMS_STATUS_OPERATION_CANCELED;
    Diagnostics native{};
    const auto status = implementation->diagnostics(wait, native);
    if (status != SMS_STATUS_SUCCESS) return status;
    *diagnostics = {};
    diagnostics->struct_size = sizeof(*diagnostics);
    diagnostics->abi_version = SMS_C_ABI_VERSION;
    diagnostics->layout_major = native.layout_major;
    diagnostics->layout_minor = native.layout_minor;
    diagnostics->resource_protocol = native.resource_protocol;
    diagnostics->required_features = native.required_features;
    diagnostics->optional_features = native.optional_features;
    diagnostics->total_bytes = native.total_bytes;
    diagnostics->slot_count = native.slot_count;
    diagnostics->free_slot_count = native.free_slots;
    diagnostics->initializing_slot_count = native.initializing_slots;
    diagnostics->reserved_slot_count = native.reserved_slots;
    diagnostics->published_slot_count = native.published_slots;
    diagnostics->pending_removal_count = native.pending_removal;
    diagnostics->reclaiming_slot_count = native.reclaiming_slots;
    diagnostics->retired_slot_count = native.retired_slots;
    diagnostics->active_reservation_count = native.active_reservations;
    diagnostics->active_lease_count = native.active_leases;
    diagnostics->claiming_lease_count = native.claiming_leases;
    diagnostics->recovering_lease_count = native.recovering_leases;
    diagnostics->free_lease_count = native.free_leases;
    diagnostics->retired_lease_count = native.retired_leases;
    diagnostics->participant_record_count = native.participant_record_count;
    diagnostics->free_participant_count = native.free_participants;
    diagnostics->registering_participant_count =
        native.registering_participants;
    diagnostics->active_participant_count = native.active_participants;
    diagnostics->closing_participant_count = native.closing_participants;
    diagnostics->recovering_participant_count = native.recovering_participants;
    diagnostics->reclaiming_participant_count =
        native.reclaiming_participants;
    diagnostics->retired_participant_count = native.retired_participants;
    diagnostics->index_entry_count = native.index_entries;
    diagnostics->occupied_index_entry_count = native.occupied_index_entries;
    diagnostics->empty_index_entry_count = native.empty_index_entries;
    diagnostics->usable_index_capacity = native.usable_index_capacity;
    diagnostics->primary_directory_occupancy =
        native.primary_directory_occupancy;
    diagnostics->spilled_bucket_count = native.spilled_bucket_count;
    diagnostics->overflow_directory_occupancy =
        native.overflow_directory_occupancy;
    diagnostics->last_observed_probe_length = native.last_probe;
    diagnostics->max_observed_probe_length = native.max_probe;
    diagnostics->max_observed_overflow_scan_length = native.max_overflow_scan;
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
    diagnostics->overflow_scan_count = native.overflow_scans;
    diagnostics->cas_retry_count = native.cas_retries;
    diagnostics->helped_transition_count = native.helped_transitions;
    diagnostics->contention_budget_exhaustion_count =
        native.contention_exhaustions;
    diagnostics->invalid_token_count = native.invalid_tokens;
    diagnostics->stale_token_count = native.stale_tokens;
    diagnostics->recovery_attempt_count = native.recovery_attempts;
    diagnostics->recovered_transition_count = native.recovered_transitions;
    diagnostics->current_owner_classification_count =
        native.current_owner_classifications;
    diagnostics->live_owner_classification_count =
        native.live_owner_classifications;
    diagnostics->stale_owner_classification_count =
        native.stale_owner_classifications;
    diagnostics->unsupported_owner_classification_count =
        native.unsupported_owner_classifications;
    diagnostics->inconsistent_owner_classification_count =
        native.inconsistent_owner_classifications;
    diagnostics->changing_owner_classification_count =
        native.changing_owner_classifications;
    for (std::size_t index = 0; index < native.failures.size(); ++index)
        diagnostics->failure_counts[index] = native.failures[index];
    return SMS_STATUS_SUCCESS;
}

} // extern "C"
