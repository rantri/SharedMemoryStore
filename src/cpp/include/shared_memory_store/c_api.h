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

#define SMS_C_ABI_VERSION 0x00010000u
#define SMS_LAYOUT_MAJOR_VERSION 1
#define SMS_LAYOUT_MINOR_VERSION 2
#define SMS_RESOURCE_NAMING_VERSION 1
#define SMS_WAIT_INFINITE (-1LL)

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
    SMS_OPEN_OPERATION_CANCELED = 10
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
    int64_t total_bytes;
    int32_t slot_count;
    int32_t free_slot_count;
    int32_t published_slot_count;
    int32_t pending_removal_count;
    int32_t active_lease_count;
    int32_t active_reservation_count;
    int32_t index_entry_count;
    int32_t occupied_index_entry_count;
    int32_t tombstone_index_entry_count;
    int32_t empty_index_entry_count;
    int32_t usable_index_capacity;
    int32_t last_observed_probe_length;
    int32_t max_observed_probe_length;
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
    int64_t index_compaction_count;
    int64_t failure_counts[23];
} sms_diagnostics;

typedef struct sms_protocol_info {
    uint32_t struct_size;
    uint32_t abi_version;
    int32_t layout_major;
    int32_t layout_minor;
    int32_t resource_naming_version;
    int32_t store_header_size;
    int32_t index_entry_header_size;
    int32_t slot_metadata_size;
    int32_t lease_record_size;
} sms_protocol_info;

typedef int32_t sms_layout_field;
enum sms_layout_field_values {
    SMS_LAYOUT_FIELD_HEADER_MAGIC = 0,
    SMS_LAYOUT_FIELD_HEADER_INDEX_OFFSET = 1,
    SMS_LAYOUT_FIELD_HEADER_STORE_STATE = 2,
    SMS_LAYOUT_FIELD_HEADER_SEQUENCE = 3,
    SMS_LAYOUT_FIELD_INDEX_STATE = 100,
    SMS_LAYOUT_FIELD_INDEX_KEY_HASH = 101,
    SMS_LAYOUT_FIELD_INDEX_REUSE_EPOCH = 102,
    SMS_LAYOUT_FIELD_SLOT_STATE = 200,
    SMS_LAYOUT_FIELD_SLOT_REUSE_EPOCH = 201,
    SMS_LAYOUT_FIELD_SLOT_USAGE_COUNT = 202,
    SMS_LAYOUT_FIELD_SLOT_KEY_HASH = 203,
    SMS_LAYOUT_FIELD_SLOT_COMMITTED_SEQUENCE = 204,
    SMS_LAYOUT_FIELD_LEASE_STATE = 300,
    SMS_LAYOUT_FIELD_LEASE_REUSE_EPOCH = 301,
    SMS_LAYOUT_FIELD_LEASE_OWNER_PROCESS_ID = 302,
    SMS_LAYOUT_FIELD_LEASE_ACQUIRE_SEQUENCE = 303
};

typedef struct sms_store_layout {
    uint32_t struct_size;
    uint32_t abi_version;
    int64_t total_bytes;
    int32_t slot_count;
    int32_t lease_record_count;
    int32_t max_value_bytes;
    int32_t max_descriptor_bytes;
    int32_t max_key_bytes;
    int32_t header_length;
    int32_t index_entry_count;
    int32_t index_entry_size;
    int64_t index_offset;
    int64_t index_length;
    int64_t lease_registry_offset;
    int64_t lease_registry_length;
    int64_t slot_metadata_offset;
    int64_t slot_metadata_length;
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

SMS_API sms_open_status SMS_CALL sms_calculate_required_bytes(
    int32_t slot_count,
    int32_t max_value_bytes,
    int32_t max_descriptor_bytes,
    int32_t max_key_bytes,
    int32_t lease_record_count,
    int64_t* required_bytes);

SMS_API sms_open_status SMS_CALL sms_open_store(
    const sms_store_options* options,
    const sms_wait_options* wait_options,
    sms_store** store);

SMS_API void SMS_CALL sms_close_store(sms_store* store);
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
