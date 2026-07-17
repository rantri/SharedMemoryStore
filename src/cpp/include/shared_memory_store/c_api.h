#ifndef SHARED_MEMORY_STORE_C_API_H
#define SHARED_MEMORY_STORE_C_API_H

#include <stddef.h>
#include <stdint.h>

#if defined(SMS_STATIC)
#  define SMS_API
#  define SMS_CALL
#elif defined(_WIN32)
#  if defined(SMS_BUILDING_LIBRARY)
#    define SMS_API __declspec(dllexport)
#  else
#    define SMS_API __declspec(dllimport)
#  endif
#  define SMS_CALL __cdecl
#else
#  if defined(SMS_BUILDING_LIBRARY)
#    define SMS_API __attribute__((visibility("default")))
#  else
#    define SMS_API
#  endif
#  define SMS_CALL
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define SMS_C_ABI_VERSION 0x00020000u
#define SMS_LAYOUT_MAJOR_VERSION 2
#define SMS_LAYOUT_MINOR_VERSION 0
#define SMS_RESOURCE_PROTOCOL_VERSION 2
#define SMS_REQUIRED_FEATURES UINT64_C(7)
#define SMS_OPTIONAL_FEATURES UINT64_C(0)
#define SMS_WAIT_INFINITE (-1LL)
#define SMS_STATUS_COUNT 23

#define SMS_STORE_HEADER_SIZE 512
#define SMS_PARTICIPANT_RECORD_SIZE 64
#define SMS_PRIMARY_DIRECTORY_BUCKET_SIZE 128
#define SMS_OVERFLOW_BINDING_SIZE 8
#define SMS_LEASE_RECORD_SIZE 64
#define SMS_VALUE_SLOT_SIZE 128

typedef int32_t sms_open_mode;
enum sms_open_mode_values {
    SMS_OPEN_MODE_CREATE_NEW = 0,
    SMS_OPEN_MODE_OPEN_EXISTING = 1,
    SMS_OPEN_MODE_CREATE_OR_OPEN = 2
};

typedef int32_t sms_open_status;
enum sms_open_status_values {
    SMS_OPEN_SUCCESS = 0,
    SMS_OPEN_ALREADY_EXISTS = 1,
    SMS_OPEN_NOT_FOUND = 2,
    SMS_OPEN_INVALID_OPTIONS = 3,
    SMS_OPEN_INCOMPATIBLE_LAYOUT = 4,
    SMS_OPEN_UNSUPPORTED_PLATFORM = 5,
    SMS_OPEN_INSUFFICIENT_CAPACITY = 6,
    SMS_OPEN_ACCESS_DENIED = 7,
    SMS_OPEN_MAPPING_FAILED = 8,
    SMS_OPEN_STORE_BUSY = 9,
    SMS_OPEN_OPERATION_CANCELED = 10,
    SMS_OPEN_PARTICIPANT_TABLE_FULL = 11
};

typedef int32_t sms_status;
enum sms_status_values {
    SMS_STATUS_SUCCESS = 0,
    SMS_STATUS_DUPLICATE_KEY = 1,
    SMS_STATUS_NOT_FOUND = 2,
    SMS_STATUS_KEY_TOO_LARGE = 3,
    SMS_STATUS_VALUE_TOO_LARGE = 4,
    SMS_STATUS_DESCRIPTOR_TOO_LARGE = 5,
    SMS_STATUS_STORE_FULL = 6,
    SMS_STATUS_LEASE_TABLE_FULL = 7,
    SMS_STATUS_INVALID_LEASE = 8,
    SMS_STATUS_LEASE_ALREADY_RELEASED = 9,
    SMS_STATUS_REMOVE_PENDING = 10,
    SMS_STATUS_UNSUPPORTED_PLATFORM = 11,
    SMS_STATUS_STORE_DISPOSED = 12,
    SMS_STATUS_CORRUPT_STORE = 13,
    SMS_STATUS_ACCESS_DENIED = 14,
    SMS_STATUS_UNKNOWN_FAILURE = 15,
    SMS_STATUS_INVALID_RESERVATION = 16,
    SMS_STATUS_RESERVATION_INCOMPLETE = 17,
    SMS_STATUS_RESERVATION_ALREADY_COMPLETED = 18,
    SMS_STATUS_RESERVATION_WRITE_OUT_OF_RANGE = 19,
    SMS_STATUS_INVALID_KEY = 20,
    SMS_STATUS_STORE_BUSY = 21,
    SMS_STATUS_OPERATION_CANCELED = 22
};

typedef struct sms_store sms_store;
typedef struct sms_lease sms_lease;
typedef struct sms_reservation sms_reservation;
typedef struct sms_cancellation sms_cancellation;

typedef struct sms_bytes {
    const uint8_t* data;
    uint64_t length;
} sms_bytes;

typedef struct sms_mutable_bytes {
    uint8_t* data;
    uint64_t length;
} sms_mutable_bytes;

typedef struct sms_wait_options {
    uint32_t struct_size;
    uint32_t abi_version;
    int64_t timeout_milliseconds;
    const sms_cancellation* cancellation;
} sms_wait_options;

typedef struct sms_store_options {
    uint32_t struct_size;
    uint32_t abi_version;
    const char* name_utf8;
    uint64_t name_length;
    int32_t open_mode;
    int64_t total_bytes;
    int32_t slot_count;
    int32_t max_value_bytes;
    int32_t max_descriptor_bytes;
    int32_t max_key_bytes;
    int32_t lease_record_count;
    int32_t participant_record_count;
    uint8_t enable_lease_recovery;
    uint8_t reserved[7];
} sms_store_options;

typedef struct sms_segment {
    const uint8_t* data;
    uint64_t length;
} sms_segment;

typedef struct sms_recovery_report {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t scanned_count;
    int32_t recovered_count;
    int32_t active_count;
    int32_t unsupported_count;
    int32_t failed_count;
    int32_t reserved;
} sms_recovery_report;

typedef struct sms_diagnostics {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t layout_major;
    int32_t layout_minor;
    int32_t resource_protocol;
    int32_t reserved;
    uint64_t required_features;
    uint64_t optional_features;
    int64_t total_bytes;
    int32_t slot_count;
    int32_t free_slot_count;
    int32_t initializing_slot_count;
    int32_t reserved_slot_count;
    int32_t published_slot_count;
    int32_t pending_removal_count;
    int32_t reclaiming_slot_count;
    int32_t retired_slot_count;
    int32_t active_reservation_count;

    int32_t active_lease_count;
    int32_t claiming_lease_count;
    int32_t recovering_lease_count;
    int32_t free_lease_count;
    int32_t retired_lease_count;

    int32_t participant_record_count;
    int32_t free_participant_count;
    int32_t registering_participant_count;
    int32_t active_participant_count;
    int32_t closing_participant_count;
    int32_t recovering_participant_count;
    int32_t reclaiming_participant_count;
    int32_t retired_participant_count;

    int32_t index_entry_count;
    int32_t occupied_index_entry_count;
    int32_t empty_index_entry_count;
    int32_t usable_index_capacity;
    int32_t primary_directory_occupancy;
    int32_t spilled_bucket_count;
    int32_t overflow_directory_occupancy;

    int32_t last_observed_probe_length;
    int32_t max_observed_probe_length;
    int32_t max_observed_overflow_scan_length;
    int32_t last_failure_status;

    int64_t aborted_reservation_count;
    int64_t recovered_lease_count;
    int64_t active_lease_recovery_count;
    int64_t unsupported_lease_recovery_count;
    int64_t failed_lease_recovery_count;
    int64_t recovered_reservation_count;
    int64_t active_reservation_recovery_count;
    int64_t unsupported_reservation_recovery_count;
    int64_t failed_reservation_recovery_count;
    int64_t capacity_pressure_count;
    int64_t overflow_scan_count;
    int64_t cas_retry_count;
    int64_t helped_transition_count;
    int64_t contention_budget_exhaustion_count;
    int64_t invalid_token_count;
    int64_t stale_token_count;
    int64_t recovery_attempt_count;
    int64_t recovered_transition_count;
    int64_t current_owner_classification_count;
    int64_t live_owner_classification_count;
    int64_t stale_owner_classification_count;
    int64_t unsupported_owner_classification_count;
    int64_t inconsistent_owner_classification_count;
    int64_t changing_owner_classification_count;
    int64_t failure_counts[SMS_STATUS_COUNT];
} sms_diagnostics;

typedef struct sms_protocol_info {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t layout_major;
    int32_t layout_minor;
    int32_t resource_protocol;
    int32_t reserved;
    uint64_t required_features;
    uint64_t optional_features;
    int32_t store_header_size;
    int32_t participant_record_size;
    int32_t primary_directory_bucket_size;
    int32_t overflow_binding_size;
    int32_t lease_record_size;
    int32_t value_slot_size;
} sms_protocol_info;

typedef int32_t sms_layout_field;
enum sms_layout_field_values {
    SMS_LAYOUT_FIELD_HEADER_MAGIC = 0,
    SMS_LAYOUT_FIELD_HEADER_LAYOUT_MAJOR_VERSION = 1,
    SMS_LAYOUT_FIELD_HEADER_LAYOUT_MINOR_VERSION = 2,
    SMS_LAYOUT_FIELD_HEADER_HEADER_LENGTH = 3,
    SMS_LAYOUT_FIELD_HEADER_RESOURCE_PROTOCOL_VERSION = 4,
    SMS_LAYOUT_FIELD_HEADER_REQUIRED_FEATURES = 5,
    SMS_LAYOUT_FIELD_HEADER_OPTIONAL_FEATURES = 6,
    SMS_LAYOUT_FIELD_HEADER_TOTAL_BYTES = 7,
    SMS_LAYOUT_FIELD_HEADER_STORE_ID = 8,
    SMS_LAYOUT_FIELD_HEADER_CONTROL = 9,
    SMS_LAYOUT_FIELD_HEADER_SEQUENCE = 10,
    SMS_LAYOUT_FIELD_HEADER_SLOT_COUNT = 11,
    SMS_LAYOUT_FIELD_HEADER_LEASE_RECORD_COUNT = 12,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_RECORD_COUNT = 13,
    SMS_LAYOUT_FIELD_HEADER_MAX_KEY_BYTES = 14,
    SMS_LAYOUT_FIELD_HEADER_MAX_DESCRIPTOR_BYTES = 15,
    SMS_LAYOUT_FIELD_HEADER_MAX_VALUE_BYTES = 16,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_INDEX_BITS = 17,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_GENERATION_BITS = 18,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_OFFSET = 19,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_LENGTH = 20,
    SMS_LAYOUT_FIELD_HEADER_PARTICIPANT_STRIDE = 21,
    SMS_LAYOUT_FIELD_HEADER_PRIMARY_LANE_COUNT = 22,
    SMS_LAYOUT_FIELD_HEADER_PRIMARY_BUCKET_COUNT = 23,
    SMS_LAYOUT_FIELD_HEADER_PRIMARY_BUCKET_STRIDE = 24,
    SMS_LAYOUT_FIELD_HEADER_PRIMARY_DIRECTORY_OFFSET = 25,
    SMS_LAYOUT_FIELD_HEADER_PRIMARY_DIRECTORY_LENGTH = 26,
    SMS_LAYOUT_FIELD_HEADER_OVERFLOW_DIRECTORY_OFFSET = 27,
    SMS_LAYOUT_FIELD_HEADER_OVERFLOW_DIRECTORY_LENGTH = 28,
    SMS_LAYOUT_FIELD_HEADER_OVERFLOW_STRIDE = 29,
    SMS_LAYOUT_FIELD_HEADER_LEASE_STRIDE = 30,
    SMS_LAYOUT_FIELD_HEADER_LEASE_REGISTRY_OFFSET = 31,
    SMS_LAYOUT_FIELD_HEADER_LEASE_REGISTRY_LENGTH = 32,
    SMS_LAYOUT_FIELD_HEADER_SLOT_METADATA_STRIDE = 33,
    SMS_LAYOUT_FIELD_HEADER_KEY_STRIDE = 34,
    SMS_LAYOUT_FIELD_HEADER_SLOT_METADATA_OFFSET = 35,
    SMS_LAYOUT_FIELD_HEADER_SLOT_METADATA_LENGTH = 36,
    SMS_LAYOUT_FIELD_HEADER_KEY_STORAGE_OFFSET = 37,
    SMS_LAYOUT_FIELD_HEADER_KEY_STORAGE_LENGTH = 38,
    SMS_LAYOUT_FIELD_HEADER_DESCRIPTOR_STRIDE = 39,
    SMS_LAYOUT_FIELD_HEADER_PAYLOAD_STRIDE = 40,
    SMS_LAYOUT_FIELD_HEADER_DESCRIPTOR_STORAGE_OFFSET = 41,
    SMS_LAYOUT_FIELD_HEADER_DESCRIPTOR_STORAGE_LENGTH = 42,
    SMS_LAYOUT_FIELD_HEADER_PAYLOAD_STORAGE_OFFSET = 43,
    SMS_LAYOUT_FIELD_HEADER_PAYLOAD_STORAGE_LENGTH = 44,
    SMS_LAYOUT_FIELD_HEADER_PID_NAMESPACE_ID = 45,
    SMS_LAYOUT_FIELD_HEADER_PID_NAMESPACE_MODE = 46,

    SMS_LAYOUT_FIELD_PARTICIPANT_CONTROL = 100,
    SMS_LAYOUT_FIELD_PARTICIPANT_IDENTITY_KIND = 101,
    SMS_LAYOUT_FIELD_PARTICIPANT_RESERVED = 102,
    SMS_LAYOUT_FIELD_PARTICIPANT_PROCESS_START_VALUE = 103,
    SMS_LAYOUT_FIELD_PARTICIPANT_OPEN_SEQUENCE = 104,
    SMS_LAYOUT_FIELD_PARTICIPANT_PID_NAMESPACE_ID = 105,

    SMS_LAYOUT_FIELD_PRIMARY_BUCKET_SPILL_SUMMARY = 200,
    SMS_LAYOUT_FIELD_PRIMARY_BUCKET_MUTATION = 201,
    SMS_LAYOUT_FIELD_PRIMARY_BUCKET_LANES = 202,

    SMS_LAYOUT_FIELD_OVERFLOW_BINDING = 300,

    SMS_LAYOUT_FIELD_LEASE_CONTROL = 400,
    SMS_LAYOUT_FIELD_LEASE_SLOT_BINDING = 401,
    SMS_LAYOUT_FIELD_LEASE_ACQUIRE_SEQUENCE = 402,

    SMS_LAYOUT_FIELD_VALUE_SLOT_CONTROL = 500,
    SMS_LAYOUT_FIELD_VALUE_SLOT_DIRECTORY_BINDING = 501,
    SMS_LAYOUT_FIELD_VALUE_SLOT_DIRECTORY_LOCATION = 502,
    SMS_LAYOUT_FIELD_VALUE_SLOT_DIRECTORY_OPERATION = 503,
    SMS_LAYOUT_FIELD_VALUE_SLOT_KEY_HASH = 504,
    SMS_LAYOUT_FIELD_VALUE_SLOT_KEY_LENGTH = 505,
    SMS_LAYOUT_FIELD_VALUE_SLOT_DESCRIPTOR_LENGTH = 506,
    SMS_LAYOUT_FIELD_VALUE_SLOT_VALUE_LENGTH = 507,
    SMS_LAYOUT_FIELD_VALUE_SLOT_PUBLICATION_INTENT = 508,
    SMS_LAYOUT_FIELD_VALUE_SLOT_BYTES_ADVANCED = 509,
    SMS_LAYOUT_FIELD_VALUE_SLOT_COMMIT_SEQUENCE = 510,
    SMS_LAYOUT_FIELD_VALUE_SLOT_KEY_OFFSET = 511,
    SMS_LAYOUT_FIELD_VALUE_SLOT_DESCRIPTOR_OFFSET = 512,
    SMS_LAYOUT_FIELD_VALUE_SLOT_PAYLOAD_OFFSET = 513
};

typedef struct sms_store_layout {
    uint32_t struct_size;
    uint32_t abi_version;
    int64_t total_bytes;
    int32_t slot_count;
    int32_t lease_record_count;
    int32_t participant_record_count;
    int32_t max_value_bytes;
    int32_t max_descriptor_bytes;
    int32_t max_key_bytes;
    int32_t header_length;
    int32_t participant_index_bits;
    int32_t participant_generation_bits;
    int32_t participant_stride;
    int64_t participant_offset;
    int64_t participant_length;
    int32_t primary_lane_count;
    int32_t primary_bucket_count;
    int32_t primary_bucket_stride;
    int64_t primary_directory_offset;
    int64_t primary_directory_length;
    int32_t overflow_stride;
    int64_t overflow_directory_offset;
    int64_t overflow_directory_length;
    int32_t lease_stride;
    int64_t lease_registry_offset;
    int64_t lease_registry_length;
    int32_t slot_metadata_stride;
    int32_t key_stride;
    int64_t slot_metadata_offset;
    int64_t slot_metadata_length;
    int64_t key_storage_offset;
    int64_t key_storage_length;
    int32_t descriptor_stride;
    int32_t payload_stride;
    int64_t descriptor_storage_offset;
    int64_t descriptor_storage_length;
    int64_t payload_storage_offset;
    int64_t payload_storage_length;
    int64_t required_bytes;
} sms_store_layout;

SMS_API uint32_t SMS_CALL sms_abi_version(void);
SMS_API sms_status SMS_CALL sms_get_protocol_info(sms_protocol_info* info);
SMS_API sms_status SMS_CALL sms_get_layout_field_offset(sms_layout_field field, uint32_t* offset);

SMS_API sms_status SMS_CALL sms_create_cancellation(sms_cancellation** cancellation);
SMS_API sms_status SMS_CALL sms_signal_cancellation(sms_cancellation* cancellation);
SMS_API int32_t SMS_CALL sms_cancellation_is_signaled(const sms_cancellation* cancellation);
SMS_API void SMS_CALL sms_destroy_cancellation(sms_cancellation* cancellation);

SMS_API sms_open_status SMS_CALL sms_calculate_required_bytes(
    int32_t slot_count,
    int32_t max_value_bytes,
    int32_t max_descriptor_bytes,
    int32_t max_key_bytes,
    int32_t lease_record_count,
    int32_t participant_record_count,
    int64_t* required_bytes);

SMS_API sms_open_status SMS_CALL sms_open_store(
    const sms_store_options* options,
    const sms_wait_options* wait_options,
    sms_store** store);

/*
 * Logically closes a store. This operation is thread-safe and idempotent:
 * concurrent close callers wait for the same teardown, and entered operations
 * complete or observe StoreDisposed. The opaque handle remains valid only for
 * close/status calls until sms_destroy_store.
 */
SMS_API void SMS_CALL sms_close_store(sms_store* store);
/*
 * Releases the opaque handle allocation after ensuring logical close.
 * The caller must guarantee that no other thread can enter any API with this
 * pointer, including sms_close_store, once destruction begins.
 */
SMS_API void SMS_CALL sms_destroy_store(sms_store* store);
SMS_API sms_status SMS_CALL sms_get_store_layout(
    sms_store* store,
    const sms_wait_options* wait_options,
    sms_store_layout* layout);

SMS_API sms_status SMS_CALL sms_publish(
    sms_store* store,
    sms_bytes key,
    sms_bytes value,
    sms_bytes descriptor,
    const sms_wait_options* wait_options);

SMS_API sms_status SMS_CALL sms_publish_segments(
    sms_store* store,
    sms_bytes key,
    const sms_segment* segments,
    uint64_t segment_count,
    sms_bytes descriptor,
    const sms_wait_options* wait_options,
    int64_t* copied_bytes);

SMS_API sms_status SMS_CALL sms_acquire(
    sms_store* store,
    sms_bytes key,
    const sms_wait_options* wait_options,
    sms_lease** lease);

SMS_API int32_t SMS_CALL sms_lease_is_valid(const sms_lease* lease);
SMS_API sms_bytes SMS_CALL sms_lease_value(const sms_lease* lease);
SMS_API sms_bytes SMS_CALL sms_lease_descriptor(const sms_lease* lease);
SMS_API sms_status SMS_CALL sms_release_lease(
    sms_lease* lease,
    const sms_wait_options* wait_options);
SMS_API void SMS_CALL sms_destroy_lease(sms_lease* lease);

SMS_API sms_status SMS_CALL sms_remove(
    sms_store* store,
    sms_bytes key,
    const sms_wait_options* wait_options);

SMS_API sms_status SMS_CALL sms_reserve(
    sms_store* store,
    sms_bytes key,
    int32_t payload_length,
    sms_bytes descriptor,
    const sms_wait_options* wait_options,
    sms_reservation** reservation);

SMS_API int32_t SMS_CALL sms_reservation_is_valid(const sms_reservation* reservation);
SMS_API int32_t SMS_CALL sms_reservation_payload_length(const sms_reservation* reservation);
SMS_API int32_t SMS_CALL sms_reservation_bytes_written(const sms_reservation* reservation);
SMS_API int32_t SMS_CALL sms_reservation_remaining_bytes(const sms_reservation* reservation);
SMS_API sms_mutable_bytes SMS_CALL sms_reservation_buffer(sms_reservation* reservation, int32_t size_hint);
SMS_API sms_status SMS_CALL sms_advance_reservation(
    sms_reservation* reservation,
    int32_t byte_count,
    const sms_wait_options* wait_options);
SMS_API sms_status SMS_CALL sms_commit_reservation(
    sms_reservation* reservation,
    const sms_wait_options* wait_options);
SMS_API sms_status SMS_CALL sms_abort_reservation(
    sms_reservation* reservation,
    const sms_wait_options* wait_options);
SMS_API void SMS_CALL sms_destroy_reservation(sms_reservation* reservation);

SMS_API sms_status SMS_CALL sms_recover_leases(
    sms_store* store,
    int32_t recover_current_process,
    const sms_wait_options* wait_options,
    sms_recovery_report* report);

SMS_API sms_status SMS_CALL sms_recover_reservations(
    sms_store* store,
    int32_t recover_current_process,
    const sms_wait_options* wait_options,
    sms_recovery_report* report);

SMS_API sms_status SMS_CALL sms_get_diagnostics(
    sms_store* store,
    const sms_wait_options* wait_options,
    sms_diagnostics* diagnostics);

#ifdef __cplusplus
}
#endif

#endif
