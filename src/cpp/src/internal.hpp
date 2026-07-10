#pragma once

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

constexpr std::int32_t magic = 0x31534D53;
constexpr std::int32_t alignment = 8;

constexpr std::int32_t store_initializing = 0;
constexpr std::int32_t store_ready = 1;
constexpr std::int32_t store_disposing = 2;
constexpr std::int32_t store_corrupt = 3;
constexpr std::int32_t store_unsupported = 4;

constexpr std::int32_t index_empty = 0;
constexpr std::int32_t index_occupied = 1;
constexpr std::int32_t index_tombstone = 2;

constexpr std::int32_t slot_free = 0;
constexpr std::int32_t slot_publishing = 1;
constexpr std::int32_t slot_published = 2;
constexpr std::int32_t slot_remove_requested = 3;
constexpr std::int32_t slot_reclaiming = 4;

constexpr std::int32_t lease_free = 0;
constexpr std::int32_t lease_active = 1;
constexpr std::int32_t lease_released = 2;
constexpr std::int32_t lease_abandoned = 3;

#pragma pack(push, 8)
struct StoreHeader {
    std::int32_t Magic;
    std::int32_t LayoutMajorVersion;
    std::int32_t LayoutMinorVersion;
    std::int32_t HeaderLength;
    std::int64_t TotalBytes;
    std::int32_t SlotCount;
    std::int32_t LeaseRecordCount;
    std::int32_t MaxKeyBytes;
    std::int32_t MaxDescriptorBytes;
    std::int32_t MaxValueBytes;
    std::int32_t IndexEntryCount;
    std::int32_t IndexEntrySize;
    std::int64_t IndexOffset;
    std::int64_t IndexLength;
    std::int64_t LeaseRegistryOffset;
    std::int64_t LeaseRegistryLength;
    std::int64_t SlotMetadataOffset;
    std::int64_t SlotMetadataLength;
    std::int64_t DescriptorStorageOffset;
    std::int64_t DescriptorStorageLength;
    std::int64_t PayloadStorageOffset;
    std::int64_t PayloadStorageLength;
    std::int64_t StoreId;
    std::int32_t StoreState;
    std::int32_t Reserved;
    std::int64_t Sequence;
};

struct IndexEntryHeader {
    std::int32_t State;
    std::int32_t KeyLength;
    std::uint64_t KeyHash;
    std::int32_t SlotIndex;
    std::int32_t SlotGeneration;
    std::int64_t SlotReuseEpoch;
};

struct SlotMetadata {
    std::int32_t State;
    std::int32_t Generation;
    std::int64_t ReuseEpoch;
    std::int32_t UsageCount;
    std::int32_t KeyLength;
    std::int32_t DescriptorLength;
    std::int32_t ValueLength;
    std::int32_t PublisherProcessId;
    std::int32_t Reserved;
    std::uint64_t KeyHash;
    std::int64_t DescriptorOffset;
    std::int64_t PayloadOffset;
    std::int64_t CommittedSequence;
};

struct LeaseRecord {
    std::int32_t State;
    std::int32_t LeaseRecordId;
    std::int32_t SlotIndex;
    std::int32_t SlotGeneration;
    std::int64_t SlotReuseEpoch;
    std::int32_t OwnerProcessId;
    std::int32_t Reserved;
    std::int64_t AcquireSequence;
};
#pragma pack(pop)

static_assert(sizeof(StoreHeader) == 160);
static_assert(offsetof(StoreHeader, IndexOffset) == 56);
static_assert(offsetof(StoreHeader, StoreId) == 136);
static_assert(offsetof(StoreHeader, StoreState) == 144);
static_assert(offsetof(StoreHeader, Sequence) == 152);
static_assert(sizeof(IndexEntryHeader) == 32);
static_assert(offsetof(IndexEntryHeader, KeyHash) == 8);
static_assert(offsetof(IndexEntryHeader, SlotReuseEpoch) == 24);
static_assert(sizeof(SlotMetadata) == 72);
static_assert(offsetof(SlotMetadata, ReuseEpoch) == 8);
static_assert(offsetof(SlotMetadata, UsageCount) == 16);
static_assert(offsetof(SlotMetadata, PublisherProcessId) == 32);
static_assert(offsetof(SlotMetadata, KeyHash) == 40);
static_assert(offsetof(SlotMetadata, CommittedSequence) == 64);
static_assert(sizeof(LeaseRecord) == 40);
static_assert(offsetof(LeaseRecord, SlotReuseEpoch) == 16);
static_assert(offsetof(LeaseRecord, OwnerProcessId) == 24);
static_assert(offsetof(LeaseRecord, AcquireSequence) == 32);

struct Layout {
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t lease_record_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t header_length{};
    std::int32_t index_entry_count{};
    std::int32_t index_entry_size{};
    std::int64_t index_offset{};
    std::int64_t index_length{};
    std::int64_t lease_registry_offset{};
    std::int64_t lease_registry_length{};
    std::int64_t slot_metadata_offset{};
    std::int64_t slot_metadata_length{};
    std::int32_t descriptor_stride{};
    std::int64_t descriptor_storage_offset{};
    std::int64_t descriptor_storage_length{};
    std::int32_t payload_stride{};
    std::int64_t payload_storage_offset{};
    std::int64_t payload_storage_length{};
    std::int64_t required_bytes{};

    static bool calculate(
        std::int64_t total_bytes,
        std::int32_t slot_count,
        std::int32_t max_value_bytes,
        std::int32_t max_descriptor_bytes,
        std::int32_t max_key_bytes,
        std::int32_t lease_record_count,
        Layout& result) noexcept;

    bool matches(const StoreHeader& header) const noexcept;
    bool bounds_valid(const StoreHeader& header) const noexcept;
};

struct Options {
    std::string name;
    sms_open_mode open_mode{SMS_OPEN_MODE_CREATE_OR_OPEN};
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t lease_record_count{};
    bool enable_lease_recovery{};
};

struct Wait {
    std::int64_t milliseconds{1000};
    bool valid() const noexcept { return milliseconds >= -1; }
    bool infinite() const noexcept { return milliseconds == -1; }
};

struct LifecycleId {
    std::int32_t generation{};
    std::int64_t reuse_epoch{};

    bool valid() const noexcept { return generation > 0 && reuse_epoch >= 0; }
    bool matches(std::int32_t g, std::int64_t e) const noexcept {
        return generation == g && reuse_epoch == e;
    }
    bool advance(LifecycleId& next) const noexcept;
};

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

std::uint64_t hash_key(std::span<const std::uint8_t> key) noexcept;
std::array<std::uint8_t, 32> sha256(std::span<const std::uint8_t> data) noexcept;
bool valid_utf8(std::string_view value) noexcept;
std::size_t utf16_length(std::string_view value) noexcept;
bool utf8_whitespace_only(std::string_view value) noexcept;
bool make_resource_name(std::string_view public_name, ResourceName& result) noexcept;
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
    std::unique_ptr<SharedLock> lock;
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
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t free_slots{};
    std::int32_t published_slots{};
    std::int32_t pending_removal{};
    std::int32_t active_leases{};
    std::int32_t active_reservations{};
    std::int32_t index_entries{};
    std::int32_t occupied_index_entries{};
    std::int32_t tombstone_index_entries{};
    std::int32_t empty_index_entries{};
    std::int32_t last_probe{};
    std::int32_t max_probe{};
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
    std::int64_t index_compactions{};
    std::array<std::int64_t, 23> failures{};
};

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
    sms_status get_layout(const Wait& wait, Layout& result) noexcept;
    void close() noexcept;

private:
    Store(std::unique_ptr<MappedRegion> region, std::unique_ptr<SharedLock> lock,
          Layout layout, bool recovery_enabled) noexcept;

    class Guard {
    public:
        Guard(Store& store, const Wait& wait) noexcept;
        ~Guard();
        bool acquired() const noexcept { return acquired_; }
        sms_status status() const noexcept { return status_; }
    private:
        Store& store_;
        bool local_acquired_{};
        bool acquired_{};
        sms_status status_{SMS_STATUS_UNKNOWN_FAILURE};
    };

    sms_open_status initialize_or_validate(const Options& options) noexcept;
    void initialize_header() noexcept;
    sms_status ensure_ready() const noexcept;
    sms_status validate_key(std::span<const std::uint8_t> key) const noexcept;
    sms_status validate_value(std::span<const std::uint8_t> key, std::size_t value_length,
                              std::size_t descriptor_length, bool reservation) const noexcept;
    sms_status record(sms_status status) noexcept;

    StoreHeader& header() noexcept;
    IndexEntryHeader& index_entry(std::int32_t index) noexcept;
    std::uint8_t* index_key(std::int32_t index) noexcept;
    SlotMetadata& slot(std::int32_t index) noexcept;
    LeaseRecord& lease(std::int32_t index) noexcept;

    bool index_find(std::span<const std::uint8_t> key, std::uint64_t hash,
                    std::int32_t& slot_index, LifecycleId& lifecycle) noexcept;
    bool index_insert(std::span<const std::uint8_t> key, std::uint64_t hash,
                      std::int32_t slot_index, LifecycleId lifecycle) noexcept;
    bool index_remove_slot(std::int32_t slot_index, LifecycleId lifecycle,
                           std::uint64_t hash) noexcept;
    void write_index(std::int32_t index, std::span<const std::uint8_t> key,
                     std::uint64_t hash, std::int32_t slot_index, LifecycleId lifecycle) noexcept;
    void record_probe(std::int32_t probes) noexcept;
    bool reserve_slot(std::int32_t& slot_index) noexcept;
    bool activate_lease(std::int32_t slot_index, LifecycleId lifecycle,
                        std::int64_t sequence, std::int32_t& lease_id) noexcept;
    sms_status request_remove(std::int32_t slot_index, LifecycleId lifecycle) noexcept;
    sms_status reclaim(std::int32_t slot_index) noexcept;
    sms_status reclaim_after_release(std::int32_t slot_index, LifecycleId lifecycle) noexcept;
    void abort_slot(std::int32_t slot_index) noexcept;
    void maybe_compact_index() noexcept;
    bool compact_index() noexcept;

    std::unique_ptr<MappedRegion> region_;
    std::unique_ptr<SharedLock> lock_;
    Layout layout_;
    bool recovery_enabled_{};
    std::timed_mutex gate_;
    std::atomic<bool> closed_{false};
    std::atomic<std::uint32_t> next_slot_{0};
    std::atomic<std::uint32_t> next_lease_{0};
    std::atomic<std::int32_t> last_probe_{0};
    std::atomic<std::int32_t> max_probe_{0};
    std::mutex diagnostics_gate_;
    Diagnostics local_diagnostics_{};
};

} // namespace sms::detail
