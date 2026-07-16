#include "internal.hpp"

#include <algorithm>
#include <chrono>
#include <new>

namespace sms::detail {
namespace {

sms_open_status to_open_status(sms_status status) noexcept {
    switch (status) {
        case SMS_STATUS_SUCCESS: return SMS_OPEN_SUCCESS;
        case SMS_STATUS_STORE_BUSY: return SMS_OPEN_STORE_BUSY;
        case SMS_STATUS_OPERATION_CANCELED: return SMS_OPEN_OPERATION_CANCELED;
        case SMS_STATUS_ACCESS_DENIED: return SMS_OPEN_ACCESS_DENIED;
        case SMS_STATUS_UNSUPPORTED_PLATFORM: return SMS_OPEN_UNSUPPORTED_PLATFORM;
        default: return SMS_OPEN_MAPPING_FAILED;
    }
}

Wait remaining_wait(const Wait& wait, std::chrono::steady_clock::time_point started) noexcept {
    if (wait.infinite()) return wait;
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started).count();
    return Wait{std::max<std::int64_t>(0, wait.milliseconds - elapsed)};
}

} // namespace

Store::Store(std::unique_ptr<MappedRegion> region, std::unique_ptr<SharedLock> lock,
             Layout layout, bool recovery_enabled) noexcept
    : region_(std::move(region)), lock_(std::move(lock)), layout_(layout), recovery_enabled_(recovery_enabled) {
    local_diagnostics_.total_bytes = layout.total_bytes;
    local_diagnostics_.slot_count = layout.slot_count;
    local_diagnostics_.index_entries = layout.index_entry_count;
}

Store::~Store() { close(); }

sms_open_status Store::open(const Options& options, const Wait& wait, std::shared_ptr<Store>& result) noexcept {
    result.reset();
    try {
        if (!wait.valid() || utf8_whitespace_only(options.name) || options.name.find('\0') != std::string::npos ||
            !valid_utf8(options.name) || utf16_length(options.name) > 240 ||
            options.open_mode < SMS_OPEN_MODE_CREATE_NEW || options.open_mode > SMS_OPEN_MODE_CREATE_OR_OPEN ||
            options.total_bytes <= 0) {
            return SMS_OPEN_INVALID_OPTIONS;
        }
        Layout layout{};
        if (!Layout::calculate(options.total_bytes, options.slot_count, options.max_value_bytes,
                               options.max_descriptor_bytes, options.max_key_bytes,
                               options.lease_record_count, layout)) {
            return SMS_OPEN_INVALID_OPTIONS;
        }
        if (options.total_bytes < layout.required_bytes) return SMS_OPEN_INSUFFICIENT_CAPACITY;
        ResourceName resource{};
        if (!make_resource_name(options.name, resource)) return SMS_OPEN_INVALID_OPTIONS;

        const auto started = std::chrono::steady_clock::now();
        auto platform = platform_open(resource, options, wait);
        if (platform.status != SMS_OPEN_SUCCESS || !platform.region || !platform.lock) return platform.status;
        auto candidate = std::shared_ptr<Store>(new Store(std::move(platform.region), std::move(platform.lock),
                                                         layout, options.enable_lease_recovery));
        const auto left = remaining_wait(wait, started);
        sms_open_status initialize{};
        {
            Guard guard(*candidate, left);
            if (!guard.acquired()) {
                return to_open_status(guard.status());
            }
            initialize = candidate->initialize_or_validate(options);
        }
        if (initialize != SMS_OPEN_SUCCESS) {
            candidate->close();
            return initialize;
        }
        result = std::move(candidate);
        return SMS_OPEN_SUCCESS;
    } catch (const std::bad_alloc&) {
        return SMS_OPEN_MAPPING_FAILED;
    } catch (...) {
        return SMS_OPEN_MAPPING_FAILED;
    }
}

Store::Guard::Guard(Store& store, const Wait& wait) noexcept : store_(store) {
    if (!wait.valid()) { status_ = SMS_STATUS_UNKNOWN_FAILURE; return; }
    if (store_.closed_.load(std::memory_order_acquire)) { status_ = SMS_STATUS_STORE_DISPOSED; return; }
    const auto started = std::chrono::steady_clock::now();
    if (wait.infinite()) {
        store_.gate_.lock();
        local_acquired_ = true;
    } else if (wait.milliseconds == 0) {
        local_acquired_ = store_.gate_.try_lock();
    } else {
        local_acquired_ = store_.gate_.try_lock_for(std::chrono::milliseconds(wait.milliseconds));
    }
    if (!local_acquired_) { status_ = SMS_STATUS_STORE_BUSY; return; }
    if (store_.closed_.load(std::memory_order_acquire) || !store_.lock_) {
        store_.gate_.unlock();
        local_acquired_ = false;
        status_ = SMS_STATUS_STORE_DISPOSED;
        return;
    }
    status_ = store_.lock_->acquire(remaining_wait(wait, started));
    if (status_ != SMS_STATUS_SUCCESS) {
        store_.gate_.unlock();
        local_acquired_ = false;
        return;
    }
    acquired_ = true;
}

Store::Guard::~Guard() {
    if (acquired_ && store_.lock_) store_.lock_->release();
    if (local_acquired_) store_.gate_.unlock();
}

sms_open_status Store::initialize_or_validate(const Options& options) noexcept {
    auto& h = header();
    if (options.open_mode == SMS_OPEN_MODE_CREATE_NEW || h.Magic == 0) {
        if (options.open_mode == SMS_OPEN_MODE_OPEN_EXISTING) return SMS_OPEN_INCOMPATIBLE_LAYOUT;
        initialize_header();
        return SMS_OPEN_SUCCESS;
    }
    // Native ABI 1.0 is intentionally layout-v1.2-only. Recognize SMS2 solely
    // to reject it before any v1 directory, slot, lease, descriptor, or payload
    // address is calculated.
    if (h.Magic == lock_free_magic) return SMS_OPEN_INCOMPATIBLE_LAYOUT;
    if (h.Magic != magic || h.LayoutMajorVersion != SMS_LAYOUT_MAJOR_VERSION ||
        !layout_.matches(h) || !layout_.bounds_valid(h)) {
        return SMS_OPEN_INCOMPATIBLE_LAYOUT;
    }
    return load_acquire(h.StoreState) == store_unsupported ? SMS_OPEN_UNSUPPORTED_PLATFORM : SMS_OPEN_SUCCESS;
}

void Store::initialize_header() noexcept {
    std::memset(region_->data(), 0, static_cast<std::size_t>(layout_.required_bytes));
    auto& h = header();
    h.Magic = magic;
    h.LayoutMajorVersion = SMS_LAYOUT_MAJOR_VERSION;
    h.LayoutMinorVersion = SMS_LAYOUT_MINOR_VERSION;
    h.HeaderLength = layout_.header_length;
    h.TotalBytes = layout_.total_bytes;
    h.SlotCount = layout_.slot_count;
    h.LeaseRecordCount = layout_.lease_record_count;
    h.MaxKeyBytes = layout_.max_key_bytes;
    h.MaxDescriptorBytes = layout_.max_descriptor_bytes;
    h.MaxValueBytes = layout_.max_value_bytes;
    h.IndexEntryCount = layout_.index_entry_count;
    h.IndexEntrySize = layout_.index_entry_size;
    h.IndexOffset = layout_.index_offset;
    h.IndexLength = layout_.index_length;
    h.LeaseRegistryOffset = layout_.lease_registry_offset;
    h.LeaseRegistryLength = layout_.lease_registry_length;
    h.SlotMetadataOffset = layout_.slot_metadata_offset;
    h.SlotMetadataLength = layout_.slot_metadata_length;
    h.DescriptorStorageOffset = layout_.descriptor_storage_offset;
    h.DescriptorStorageLength = layout_.descriptor_storage_length;
    h.PayloadStorageOffset = layout_.payload_storage_offset;
    h.PayloadStorageLength = layout_.payload_storage_length;
    h.StoreId = static_cast<std::int64_t>(std::chrono::system_clock::now().time_since_epoch().count()) ^ current_process_id();
    h.Sequence = 0;

    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        auto& value = slot(index);
        value.State = slot_free;
        value.Generation = 1;
        value.ReuseEpoch = 0;
        value.DescriptorOffset = layout_.descriptor_storage_offset + static_cast<std::int64_t>(index) * layout_.descriptor_stride;
        value.PayloadOffset = layout_.payload_storage_offset + static_cast<std::int64_t>(index) * layout_.payload_stride;
    }
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        auto& value = lease(index);
        value.State = lease_free;
        value.LeaseRecordId = index;
        value.SlotIndex = -1;
    }
    store_release(h.StoreState, store_ready);
}

sms_status Store::ensure_ready() const noexcept {
    if (closed_.load(std::memory_order_acquire) || !region_) return SMS_STATUS_STORE_DISPOSED;
    const auto& h = *reinterpret_cast<StoreHeader*>(region_->data());
    switch (load_acquire(const_cast<std::int32_t&>(h.StoreState))) {
        case store_ready: return SMS_STATUS_SUCCESS;
        case store_unsupported: return SMS_STATUS_UNSUPPORTED_PLATFORM;
        case store_corrupt: return SMS_STATUS_CORRUPT_STORE;
        default: return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status Store::validate_key(std::span<const std::uint8_t> key) const noexcept {
    if (key.empty()) return SMS_STATUS_INVALID_KEY;
    return key.size() > static_cast<std::size_t>(layout_.max_key_bytes)
        ? SMS_STATUS_KEY_TOO_LARGE : SMS_STATUS_SUCCESS;
}

sms_status Store::validate_value(std::span<const std::uint8_t> key, std::size_t value_length,
                                 std::size_t descriptor_length, bool) const noexcept {
    const auto key_status = validate_key(key);
    if (key_status != SMS_STATUS_SUCCESS) return key_status;
    if (value_length > static_cast<std::size_t>(layout_.max_value_bytes)) return SMS_STATUS_VALUE_TOO_LARGE;
    if (descriptor_length > static_cast<std::size_t>(layout_.max_descriptor_bytes)) return SMS_STATUS_DESCRIPTOR_TOO_LARGE;
    return SMS_STATUS_SUCCESS;
}

sms_status Store::record(sms_status status) noexcept {
    if (status == SMS_STATUS_SUCCESS) return status;
    if (status == SMS_STATUS_CORRUPT_STORE && region_) store_release(header().StoreState, store_corrupt);
    std::lock_guard guard(diagnostics_gate_);
    const auto index = static_cast<std::int32_t>(status);
    if (index >= 0 && index < static_cast<std::int32_t>(local_diagnostics_.failures.size())) {
        ++local_diagnostics_.failures[static_cast<std::size_t>(index)];
    }
    local_diagnostics_.last_failure = status;
    if (status == SMS_STATUS_STORE_FULL || status == SMS_STATUS_LEASE_TABLE_FULL) ++local_diagnostics_.capacity_pressure;
    return status;
}

StoreHeader& Store::header() noexcept { return *reinterpret_cast<StoreHeader*>(region_->data()); }
IndexEntryHeader& Store::index_entry(std::int32_t index) noexcept {
    return *reinterpret_cast<IndexEntryHeader*>(region_->data() + layout_.index_offset +
                                                static_cast<std::int64_t>(index) * layout_.index_entry_size);
}
std::uint8_t* Store::index_key(std::int32_t index) noexcept {
    return region_->data() + layout_.index_offset + static_cast<std::int64_t>(index) * layout_.index_entry_size + sizeof(IndexEntryHeader);
}
SlotMetadata& Store::slot(std::int32_t index) noexcept {
    return *reinterpret_cast<SlotMetadata*>(region_->data() + layout_.slot_metadata_offset +
                                           static_cast<std::int64_t>(index) * sizeof(SlotMetadata));
}
LeaseRecord& Store::lease(std::int32_t index) noexcept {
    return *reinterpret_cast<LeaseRecord*>(region_->data() + layout_.lease_registry_offset +
                                          static_cast<std::int64_t>(index) * sizeof(LeaseRecord));
}

void Store::record_probe(std::int32_t probes) noexcept {
    last_probe_.store(probes, std::memory_order_release);
    auto current = max_probe_.load(std::memory_order_acquire);
    while (probes > current && !max_probe_.compare_exchange_weak(current, probes, std::memory_order_acq_rel)) {}
}

bool Store::index_find(std::span<const std::uint8_t> key, std::uint64_t hash,
                       std::int32_t& slot_index, LifecycleId& lifecycle) noexcept {
    slot_index = -1;
    lifecycle = {};
    const auto mask = layout_.index_entry_count - 1;
    const auto start = static_cast<std::int32_t>(hash & static_cast<std::uint64_t>(mask));
    std::int32_t probes{};
    for (std::int32_t step = 0; step < layout_.index_entry_count; ++step) {
        ++probes;
        const auto index = (start + step) & mask;
        auto& entry = index_entry(index);
        const auto state = load_acquire(entry.State);
        if (state == index_empty) { record_probe(probes); return false; }
        if (state == index_occupied && entry.KeyHash == hash && entry.KeyLength >= 0 &&
            entry.KeyLength <= layout_.max_key_bytes && static_cast<std::size_t>(entry.KeyLength) == key.size() &&
            std::memcmp(index_key(index), key.data(), key.size()) == 0) {
            slot_index = entry.SlotIndex;
            lifecycle = {entry.SlotGeneration, entry.SlotReuseEpoch};
            record_probe(probes);
            return true;
        }
    }
    record_probe(probes);
    return false;
}

void Store::write_index(std::int32_t index, std::span<const std::uint8_t> key, std::uint64_t hash,
                        std::int32_t slot_index, LifecycleId lifecycle) noexcept {
    auto& entry = index_entry(index);
    store_release(entry.State, index_tombstone);
    entry.KeyLength = static_cast<std::int32_t>(key.size());
    entry.KeyHash = hash;
    entry.SlotIndex = slot_index;
    entry.SlotGeneration = lifecycle.generation;
    entry.SlotReuseEpoch = lifecycle.reuse_epoch;
    std::memset(index_key(index), 0, static_cast<std::size_t>(layout_.max_key_bytes));
    if (!key.empty()) std::memcpy(index_key(index), key.data(), key.size());
    store_release(entry.State, index_occupied);
}

bool Store::index_insert(std::span<const std::uint8_t> key, std::uint64_t hash,
                         std::int32_t slot_index, LifecycleId lifecycle) noexcept {
    const auto mask = layout_.index_entry_count - 1;
    const auto start = static_cast<std::int32_t>(hash & static_cast<std::uint64_t>(mask));
    std::int32_t tombstone = -1, probes{};
    for (std::int32_t step = 0; step < layout_.index_entry_count; ++step) {
        ++probes;
        const auto index = (start + step) & mask;
        auto& entry = index_entry(index);
        const auto state = load_acquire(entry.State);
        if (state == index_occupied) {
            if (entry.KeyHash == hash && entry.KeyLength >= 0 &&
                static_cast<std::size_t>(entry.KeyLength) == key.size() &&
                std::memcmp(index_key(index), key.data(), key.size()) == 0) {
                record_probe(probes); return false;
            }
        } else if (state == index_tombstone) {
            if (tombstone < 0) tombstone = index;
        } else {
            write_index(tombstone >= 0 ? tombstone : index, key, hash, slot_index, lifecycle);
            record_probe(probes); return true;
        }
    }
    if (tombstone >= 0) {
        write_index(tombstone, key, hash, slot_index, lifecycle);
        record_probe(probes); return true;
    }
    record_probe(probes);
    return false;
}

bool Store::index_remove_slot(std::int32_t slot_index, LifecycleId lifecycle, std::uint64_t hash) noexcept {
    const auto mask = layout_.index_entry_count - 1;
    const auto start = static_cast<std::int32_t>(hash & static_cast<std::uint64_t>(mask));
    bool removed{};
    std::int32_t probes{};
    for (std::int32_t step = 0; step < layout_.index_entry_count; ++step) {
        ++probes;
        const auto index = (start + step) & mask;
        auto& entry = index_entry(index);
        const auto state = load_acquire(entry.State);
        if (state == index_empty) { record_probe(probes); return removed; }
        if (state == index_occupied && entry.KeyHash == hash && entry.SlotIndex == slot_index &&
            lifecycle.matches(entry.SlotGeneration, entry.SlotReuseEpoch)) {
            store_release(entry.State, index_tombstone);
            removed = true;
        }
    }
    record_probe(probes);
    return removed;
}

bool Store::reserve_slot(std::int32_t& slot_index) noexcept {
    const auto start = ++next_slot_;
    for (std::int32_t step = 0; step < layout_.slot_count; ++step) {
        const auto candidate = static_cast<std::int32_t>((start + static_cast<std::uint32_t>(step)) %
                                                         static_cast<std::uint32_t>(layout_.slot_count));
        auto& value = slot(candidate);
        if (load_acquire(value.State) != slot_free) continue;
        store_release(value.State, slot_publishing);
        value.UsageCount = 0;
        value.PublisherProcessId = current_process_id();
        value.Reserved = 0;
        value.KeyHash = 0;
        value.KeyLength = 0;
        value.DescriptorLength = 0;
        value.ValueLength = 0;
        value.CommittedSequence = 0;
        slot_index = candidate;
        return true;
    }
    slot_index = -1;
    return false;
}

void Store::abort_slot(std::int32_t index) noexcept {
    auto& value = slot(index);
    value.KeyHash = 0;
    value.KeyLength = 0;
    value.ValueLength = 0;
    value.DescriptorLength = 0;
    value.UsageCount = 0;
    value.PublisherProcessId = 0;
    value.Reserved = 0;
    value.CommittedSequence = 0;
    store_release(value.State, slot_free);
}

bool Store::activate_lease(std::int32_t slot_index, LifecycleId lifecycle,
                           std::int64_t sequence, std::int32_t& lease_id) noexcept {
    const auto start = ++next_lease_;
    for (std::int32_t step = 0; step < layout_.lease_record_count; ++step) {
        const auto candidate = static_cast<std::int32_t>((start + static_cast<std::uint32_t>(step)) %
                                                         static_cast<std::uint32_t>(layout_.lease_record_count));
        auto& record = lease(candidate);
        if (load_acquire(record.State) == lease_active) continue;
        record.LeaseRecordId = candidate;
        record.SlotIndex = slot_index;
        record.SlotGeneration = lifecycle.generation;
        record.SlotReuseEpoch = lifecycle.reuse_epoch;
        record.OwnerProcessId = current_process_id();
        record.AcquireSequence = sequence;
        store_release(record.State, lease_active);
        lease_id = candidate;
        return true;
    }
    lease_id = -1;
    return false;
}

sms_status Store::publish(std::span<const std::uint8_t> key, std::span<const std::uint8_t> value,
                          std::span<const std::uint8_t> descriptor, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_value(key, value.size(), descriptor.size(), false);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto hash = hash_key(key);
    std::int32_t existing{}; LifecycleId ignored{};
    if (index_find(key, hash, existing, ignored) && existing >= 0 && existing < layout_.slot_count) {
        const auto state = load_acquire(slot(existing).State);
        if (state == slot_published || state == slot_publishing || state == slot_remove_requested)
            return record(SMS_STATUS_DUPLICATE_KEY);
    }
    std::int32_t index{};
    if (!reserve_slot(index)) return record(SMS_STATUS_STORE_FULL);
    auto& target = slot(index);
    const LifecycleId lifecycle{target.Generation, target.ReuseEpoch};
    if (!descriptor.empty()) std::memcpy(region_->data() + target.DescriptorOffset, descriptor.data(), descriptor.size());
    if (!value.empty()) std::memcpy(region_->data() + target.PayloadOffset, value.data(), value.size());
    if (!index_insert(key, hash, index, lifecycle)) {
        abort_slot(index);
        return record(SMS_STATUS_DUPLICATE_KEY);
    }
    target.KeyHash = hash;
    target.KeyLength = static_cast<std::int32_t>(key.size());
    target.DescriptorLength = static_cast<std::int32_t>(descriptor.size());
    target.ValueLength = static_cast<std::int32_t>(value.size());
    target.PublisherProcessId = current_process_id();
    target.Reserved = 0;
    target.CommittedSequence = increment(header().Sequence);
    store_release(target.State, slot_published);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::publish_segments(std::span<const std::uint8_t> key, std::span<const sms_segment> segments,
                                   std::span<const std::uint8_t> descriptor, const Wait& wait,
                                   std::int64_t& copied) noexcept {
    copied = 0;
    std::size_t total{};
    for (const auto& segment : segments) {
        if (segment.length > 0 && !segment.data) return record(SMS_STATUS_UNKNOWN_FAILURE);
        if (segment.length > std::numeric_limits<std::size_t>::max() - total) return record(SMS_STATUS_VALUE_TOO_LARGE);
        total += segment.length;
    }
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_value(key, total, descriptor.size(), false);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto hash = hash_key(key);
    std::int32_t existing{}; LifecycleId ignored{};
    if (index_find(key, hash, existing, ignored) && existing >= 0 && existing < layout_.slot_count) {
        const auto state = load_acquire(slot(existing).State);
        if (state == slot_published || state == slot_publishing || state == slot_remove_requested)
            return record(SMS_STATUS_DUPLICATE_KEY);
    }
    std::int32_t index{};
    if (!reserve_slot(index)) return record(SMS_STATUS_STORE_FULL);
    auto& target = slot(index);
    const LifecycleId lifecycle{target.Generation, target.ReuseEpoch};
    if (!descriptor.empty()) std::memcpy(region_->data() + target.DescriptorOffset, descriptor.data(), descriptor.size());
    auto* output = region_->data() + target.PayloadOffset;
    for (const auto& segment : segments) {
        if (segment.length) std::memcpy(output + copied, segment.data, segment.length);
        copied += static_cast<std::int64_t>(segment.length);
    }
    if (!index_insert(key, hash, index, lifecycle)) { abort_slot(index); return record(SMS_STATUS_DUPLICATE_KEY); }
    target.KeyHash = hash;
    target.KeyLength = static_cast<std::int32_t>(key.size());
    target.DescriptorLength = static_cast<std::int32_t>(descriptor.size());
    target.ValueLength = static_cast<std::int32_t>(total);
    target.PublisherProcessId = current_process_id();
    target.Reserved = 0;
    target.CommittedSequence = increment(header().Sequence);
    store_release(target.State, slot_published);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::acquire(std::span<const std::uint8_t> key, const Wait& wait,
                          std::int32_t& slot_index, LifecycleId& lifecycle, std::int32_t& lease_id) noexcept {
    slot_index = -1; lifecycle = {}; lease_id = -1;
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_key(key);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto hash = hash_key(key);
    if (!index_find(key, hash, slot_index, lifecycle) || slot_index < 0 || slot_index >= layout_.slot_count)
        return record(SMS_STATUS_NOT_FOUND);
    auto& target = slot(slot_index);
    if (load_acquire(target.State) != slot_published || !lifecycle.matches(target.Generation, target.ReuseEpoch))
        return record(SMS_STATUS_NOT_FOUND);
    const auto sequence = increment(header().Sequence);
    if (!activate_lease(slot_index, lifecycle, sequence, lease_id)) return record(SMS_STATUS_LEASE_TABLE_FULL);
    increment(target.UsageCount);
    if (load_acquire(target.State) != slot_published || !lifecycle.matches(target.Generation, target.ReuseEpoch)) {
        auto& activated = lease(lease_id);
        store_release(activated.State, lease_released);
        decrement(target.UsageCount);
        return record(SMS_STATUS_NOT_FOUND);
    }
    return SMS_STATUS_SUCCESS;
}

bool Store::lease_valid(std::int32_t slot_index, LifecycleId lifecycle, std::int32_t lease_id) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || lease_id < 0 || lease_id >= layout_.lease_record_count ||
        slot_index < 0 || slot_index >= layout_.slot_count) return false;
    auto& record = lease(lease_id);
    auto& target = slot(slot_index);
    return load_acquire(record.State) == lease_active && record.SlotIndex == slot_index &&
        lifecycle.matches(record.SlotGeneration, record.SlotReuseEpoch) &&
        lifecycle.matches(target.Generation, target.ReuseEpoch) &&
        (load_acquire(target.State) == slot_published || load_acquire(target.State) == slot_remove_requested);
}

std::span<const std::uint8_t> Store::lease_value(std::int32_t slot_index, LifecycleId lifecycle,
                                                std::int32_t lease_id) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || lease_id < 0 || lease_id >= layout_.lease_record_count ||
        slot_index < 0 || slot_index >= layout_.slot_count) return {};
    auto& record = lease(lease_id); auto& target = slot(slot_index);
    const auto state = load_acquire(target.State);
    if (load_acquire(record.State) != lease_active || record.SlotIndex != slot_index ||
        !lifecycle.matches(record.SlotGeneration, record.SlotReuseEpoch) ||
        !lifecycle.matches(target.Generation, target.ReuseEpoch) ||
        (state != slot_published && state != slot_remove_requested) || target.ValueLength < 0) return {};
    return {region_->data() + target.PayloadOffset, static_cast<std::size_t>(target.ValueLength)};
}

std::span<const std::uint8_t> Store::lease_descriptor(std::int32_t slot_index, LifecycleId lifecycle,
                                                     std::int32_t lease_id) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || lease_id < 0 || lease_id >= layout_.lease_record_count ||
        slot_index < 0 || slot_index >= layout_.slot_count) return {};
    auto& record = lease(lease_id); auto& target = slot(slot_index);
    const auto state = load_acquire(target.State);
    if (load_acquire(record.State) != lease_active || record.SlotIndex != slot_index ||
        !lifecycle.matches(record.SlotGeneration, record.SlotReuseEpoch) ||
        !lifecycle.matches(target.Generation, target.ReuseEpoch) ||
        (state != slot_published && state != slot_remove_requested) || target.DescriptorLength < 0) return {};
    return {region_->data() + target.DescriptorOffset, static_cast<std::size_t>(target.DescriptorLength)};
}

sms_status Store::release_lease(std::int32_t slot_index, LifecycleId lifecycle,
                                std::int32_t lease_id, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    if (lease_id < 0 || lease_id >= layout_.lease_record_count) return record(SMS_STATUS_INVALID_LEASE);
    auto& record_value = lease(lease_id);
    const auto state = load_acquire(record_value.State);
    if (state == lease_released || state == lease_abandoned) return record(SMS_STATUS_LEASE_ALREADY_RELEASED);
    if (state != lease_active || record_value.SlotIndex != slot_index ||
        !lifecycle.matches(record_value.SlotGeneration, record_value.SlotReuseEpoch) ||
        slot_index < 0 || slot_index >= layout_.slot_count) return record(SMS_STATUS_INVALID_LEASE);
    auto& target = slot(slot_index);
    if (!lifecycle.matches(target.Generation, target.ReuseEpoch)) return record(SMS_STATUS_INVALID_LEASE);
    store_release(record_value.State, lease_released);
    const auto remaining = decrement(target.UsageCount);
    if (remaining < 0) { store_release(target.State, slot_free); return record(SMS_STATUS_CORRUPT_STORE); }
    const auto result = remaining == 0 ? reclaim_after_release(slot_index, lifecycle) : SMS_STATUS_SUCCESS;
    if (result == SMS_STATUS_SUCCESS) maybe_compact_index();
    return result == SMS_STATUS_SUCCESS ? result : record(result);
}

sms_status Store::reclaim_after_release(std::int32_t slot_index, LifecycleId lifecycle) noexcept {
    auto& target = slot(slot_index);
    if (!lifecycle.matches(target.Generation, target.ReuseEpoch)) return SMS_STATUS_INVALID_LEASE;
    if (load_acquire(target.State) == slot_remove_requested && load_acquire(target.UsageCount) == 0) {
        if (!index_remove_slot(slot_index, lifecycle, target.KeyHash)) return SMS_STATUS_CORRUPT_STORE;
        return reclaim(slot_index);
    }
    return SMS_STATUS_SUCCESS;
}

sms_status Store::reclaim(std::int32_t slot_index) noexcept {
    auto& target = slot(slot_index);
    store_release(target.State, slot_reclaiming);
    LifecycleId next{};
    if (!LifecycleId{target.Generation, target.ReuseEpoch}.advance(next)) return SMS_STATUS_CORRUPT_STORE;
    target.KeyHash = 0; target.KeyLength = 0; target.ValueLength = 0; target.DescriptorLength = 0;
    target.PublisherProcessId = 0; target.UsageCount = 0; target.Reserved = 0; target.CommittedSequence = 0;
    target.Generation = next.generation; target.ReuseEpoch = next.reuse_epoch;
    store_release(target.State, slot_free);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::request_remove(std::int32_t slot_index, LifecycleId lifecycle) noexcept {
    auto& target = slot(slot_index);
    const auto state = load_acquire(target.State);
    if (state == slot_remove_requested) return SMS_STATUS_REMOVE_PENDING;
    if (state != slot_published || !lifecycle.matches(target.Generation, target.ReuseEpoch)) return SMS_STATUS_NOT_FOUND;
    if (load_acquire(target.UsageCount) > 0) {
        store_release(target.State, slot_remove_requested);
        return SMS_STATUS_REMOVE_PENDING;
    }
    if (!index_remove_slot(slot_index, lifecycle, target.KeyHash)) return SMS_STATUS_CORRUPT_STORE;
    return reclaim(slot_index);
}

sms_status Store::remove(std::span<const std::uint8_t> key, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_key(key);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    std::int32_t index{}; LifecycleId lifecycle{};
    if (!index_find(key, hash_key(key), index, lifecycle) || index < 0 || index >= layout_.slot_count)
        return record(SMS_STATUS_NOT_FOUND);
    status = request_remove(index, lifecycle);
    if (status == SMS_STATUS_SUCCESS) maybe_compact_index();
    return status == SMS_STATUS_SUCCESS ? status : record(status);
}

sms_status Store::reserve(std::span<const std::uint8_t> key, std::int32_t payload_length,
                          std::span<const std::uint8_t> descriptor, const Wait& wait,
                          std::int32_t& slot_index, LifecycleId& lifecycle) noexcept {
    slot_index = -1; lifecycle = {};
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (payload_length < 0) return record(SMS_STATUS_VALUE_TOO_LARGE);
    status = validate_value(key, static_cast<std::size_t>(payload_length), descriptor.size(), true);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto hash = hash_key(key);
    std::int32_t existing{}; LifecycleId ignored{};
    if (index_find(key, hash, existing, ignored) && existing >= 0 && existing < layout_.slot_count) {
        const auto state = load_acquire(slot(existing).State);
        if (state == slot_published || state == slot_publishing || state == slot_remove_requested)
            return record(SMS_STATUS_DUPLICATE_KEY);
    }
    if (!reserve_slot(slot_index)) return record(SMS_STATUS_STORE_FULL);
    auto& target = slot(slot_index);
    lifecycle = {target.Generation, target.ReuseEpoch};
    target.KeyHash = hash;
    target.KeyLength = static_cast<std::int32_t>(key.size());
    target.DescriptorLength = static_cast<std::int32_t>(descriptor.size());
    target.ValueLength = payload_length;
    target.PublisherProcessId = current_process_id();
    target.Reserved = 0;
    target.CommittedSequence = 0;
    if (!descriptor.empty()) std::memcpy(region_->data() + target.DescriptorOffset, descriptor.data(), descriptor.size());
    if (!index_insert(key, hash, slot_index, lifecycle)) {
        abort_slot(slot_index); slot_index = -1; lifecycle = {};
        return record(SMS_STATUS_DUPLICATE_KEY);
    }
    return SMS_STATUS_SUCCESS;
}

bool Store::reservation_valid(std::int32_t index, LifecycleId lifecycle) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || index < 0 || index >= layout_.slot_count) return false;
    auto& target = slot(index);
    return load_acquire(target.State) == slot_publishing && lifecycle.matches(target.Generation, target.ReuseEpoch);
}

std::int32_t Store::reservation_payload_length(std::int32_t index, LifecycleId lifecycle) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || index < 0 || index >= layout_.slot_count) return 0;
    auto& target = slot(index);
    return load_acquire(target.State) == slot_publishing && lifecycle.matches(target.Generation, target.ReuseEpoch)
        ? target.ValueLength : 0;
}

std::int32_t Store::reservation_bytes_written(std::int32_t index, LifecycleId lifecycle) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || index < 0 || index >= layout_.slot_count) return 0;
    auto& target = slot(index);
    return load_acquire(target.State) == slot_publishing && lifecycle.matches(target.Generation, target.ReuseEpoch)
        ? target.Reserved : 0;
}

std::span<std::uint8_t> Store::reservation_buffer(std::int32_t index, LifecycleId lifecycle,
                                                  std::int32_t size_hint) noexcept {
    Guard guard(*this, Wait{1000});
    if (!guard.acquired() || index < 0 || index >= layout_.slot_count) return {};
    auto& target = slot(index);
    if (load_acquire(target.State) != slot_publishing || !lifecycle.matches(target.Generation, target.ReuseEpoch)) return {};
    const auto remaining = target.ValueLength - target.Reserved;
    if (remaining <= 0 || size_hint < 0 || size_hint > remaining) return {};
    return {region_->data() + target.PayloadOffset + target.Reserved, static_cast<std::size_t>(remaining)};
}

sms_status Store::advance_reservation(std::int32_t index, LifecycleId lifecycle,
                                      std::int32_t count, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready(); if (status != SMS_STATUS_SUCCESS) return record(status);
    if (index < 0 || index >= layout_.slot_count) return record(SMS_STATUS_INVALID_RESERVATION);
    auto& target = slot(index);
    if (!lifecycle.matches(target.Generation, target.ReuseEpoch)) return record(SMS_STATUS_INVALID_RESERVATION);
    if (load_acquire(target.State) != slot_publishing) return record(SMS_STATUS_RESERVATION_ALREADY_COMPLETED);
    const auto remaining = target.ValueLength - target.Reserved;
    if (count < 0 || count > remaining) return record(SMS_STATUS_RESERVATION_WRITE_OUT_OF_RANGE);
    target.Reserved += count;
    return SMS_STATUS_SUCCESS;
}

sms_status Store::commit_reservation(std::int32_t index, LifecycleId lifecycle, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready(); if (status != SMS_STATUS_SUCCESS) return record(status);
    if (index < 0 || index >= layout_.slot_count) return record(SMS_STATUS_INVALID_RESERVATION);
    auto& target = slot(index);
    if (!lifecycle.matches(target.Generation, target.ReuseEpoch)) return record(SMS_STATUS_INVALID_RESERVATION);
    if (load_acquire(target.State) != slot_publishing) return record(SMS_STATUS_RESERVATION_ALREADY_COMPLETED);
    if (target.Reserved != target.ValueLength) return record(SMS_STATUS_RESERVATION_INCOMPLETE);
    target.PublisherProcessId = current_process_id();
    target.Reserved = 0;
    target.CommittedSequence = increment(header().Sequence);
    store_release(target.State, slot_published);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::abort_reservation(std::int32_t index, LifecycleId lifecycle,
                                    bool count_abort, const Wait& wait) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready(); if (status != SMS_STATUS_SUCCESS) return record(status);
    if (index < 0 || index >= layout_.slot_count) return record(SMS_STATUS_INVALID_RESERVATION);
    auto& target = slot(index);
    if (!lifecycle.matches(target.Generation, target.ReuseEpoch)) return record(SMS_STATUS_INVALID_RESERVATION);
    if (load_acquire(target.State) != slot_publishing) return record(SMS_STATUS_RESERVATION_ALREADY_COMPLETED);
    if (!index_remove_slot(index, lifecycle, target.KeyHash)) return record(SMS_STATUS_CORRUPT_STORE);
    status = reclaim(index);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (count_abort) { std::lock_guard local(diagnostics_gate_); ++local_diagnostics_.aborted_reservations; }
    maybe_compact_index();
    return SMS_STATUS_SUCCESS;
}

sms_status Store::recover_leases(bool recover_current, const Wait& wait, RecoveryReport& report) noexcept {
    report = {};
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready(); if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!recovery_enabled_) {
        report.scanned = layout_.lease_record_count;
        report.unsupported = layout_.lease_record_count;
        return record(SMS_STATUS_UNSUPPORTED_PLATFORM);
    }
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        ++report.scanned;
        auto& value = lease(index);
        if (load_acquire(value.State) != lease_active) continue;
        if (value.SlotIndex < 0 || value.SlotIndex >= layout_.slot_count) { ++report.failed; continue; }
        const auto owner = classify_process(value.OwnerProcessId);
        if (owner == OwnerKind::unsupported) { ++report.unsupported; continue; }
        if (owner == OwnerKind::live || (owner == OwnerKind::current && !recover_current)) { ++report.active; continue; }
        auto& target = slot(value.SlotIndex);
        const LifecycleId lifecycle{value.SlotGeneration, value.SlotReuseEpoch};
        if (!lifecycle.valid() || !lifecycle.matches(target.Generation, target.ReuseEpoch) ||
            load_acquire(target.UsageCount) <= 0) { ++report.failed; continue; }
        store_release(value.State, lease_abandoned);
        const auto remaining = decrement(target.UsageCount);
        if (remaining == 0 && reclaim_after_release(value.SlotIndex, lifecycle) != SMS_STATUS_SUCCESS) {
            ++report.failed; continue;
        }
        ++report.recovered;
    }
    if (report.recovered > 0) maybe_compact_index();
    {
        std::lock_guard local(diagnostics_gate_);
        local_diagnostics_.recovered_leases += report.recovered;
        local_diagnostics_.active_lease_recoveries += report.active;
        local_diagnostics_.unsupported_lease_recoveries += report.unsupported;
        local_diagnostics_.failed_lease_recoveries += report.failed;
    }
    return SMS_STATUS_SUCCESS;
}

sms_status Store::recover_reservations(bool recover_current, const Wait& wait, RecoveryReport& report) noexcept {
    report = {};
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    auto status = ensure_ready(); if (status != SMS_STATUS_SUCCESS) return record(status);
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        auto& target = slot(index);
        if (load_acquire(target.State) != slot_publishing) continue;
        ++report.scanned;
        const auto owner = classify_process(target.PublisherProcessId);
        if (owner == OwnerKind::unsupported) { ++report.unsupported; continue; }
        if (owner == OwnerKind::live || (owner == OwnerKind::current && !recover_current)) { ++report.active; continue; }
        const LifecycleId lifecycle{target.Generation, target.ReuseEpoch};
        if (!index_remove_slot(index, lifecycle, target.KeyHash) || reclaim(index) != SMS_STATUS_SUCCESS) {
            ++report.failed; continue;
        }
        ++report.recovered;
    }
    if (report.recovered > 0) maybe_compact_index();
    {
        std::lock_guard local(diagnostics_gate_);
        local_diagnostics_.recovered_reservations += report.recovered;
        local_diagnostics_.active_reservation_recoveries += report.active;
        local_diagnostics_.unsupported_reservation_recoveries += report.unsupported;
        local_diagnostics_.failed_reservation_recoveries += report.failed;
    }
    return SMS_STATUS_SUCCESS;
}

bool Store::compact_index() noexcept {
    bool compacted{};
    const auto mask = layout_.index_entry_count - 1;
    auto clear = [&](std::int32_t index) {
        auto& entry = index_entry(index);
        store_release(entry.State, index_empty);
        entry.KeyLength = 0; entry.KeyHash = 0; entry.SlotIndex = -1;
        entry.SlotGeneration = 0; entry.SlotReuseEpoch = 0;
        std::memset(index_key(index), 0, static_cast<std::size_t>(layout_.max_key_bytes));
    };
    for (std::int32_t pass = 0; pass < layout_.index_entry_count; ++pass) {
        bool changed{};
        for (std::int32_t initial = 0; initial < layout_.index_entry_count; ++initial) {
            if (load_acquire(index_entry(initial).State) != index_tombstone) continue;
            auto hole = initial;
            auto scan = (hole + 1) & mask;
            bool closed{};
            for (std::int32_t step = 0; step < layout_.index_entry_count; ++step) {
                auto& candidate = index_entry(scan);
                const auto state = load_acquire(candidate.State);
                if (state == index_empty) {
                    clear(hole); changed = compacted = closed = true; break;
                }
                if (state == index_occupied && candidate.KeyLength >= 0 && candidate.KeyLength <= layout_.max_key_bytes) {
                    const auto home = static_cast<std::int32_t>(candidate.KeyHash & static_cast<std::uint64_t>(mask));
                    const auto distance_hole = (hole - home) & mask;
                    const auto distance_candidate = (scan - home) & mask;
                    if (distance_hole < distance_candidate) {
                        const auto key = std::span<const std::uint8_t>(
                            index_key(scan), static_cast<std::size_t>(candidate.KeyLength));
                        write_index(hole, key, candidate.KeyHash, candidate.SlotIndex,
                                    {candidate.SlotGeneration, candidate.SlotReuseEpoch});
                        store_release(candidate.State, index_tombstone);
                        hole = scan;
                    }
                }
                scan = (scan + 1) & mask;
            }
            (void)closed;
        }
        if (!changed) break;
    }
    return compacted;
}

void Store::maybe_compact_index() noexcept {
    std::int32_t tombstones{}, empty{};
    for (std::int32_t index = 0; index < layout_.index_entry_count; ++index) {
        switch (load_acquire(index_entry(index).State)) {
            case index_occupied: break;
            case index_tombstone: ++tombstones; break;
            default: ++empty; break;
        }
    }
    if (tombstones == 0) return;
    const auto tombstone_pressure = static_cast<double>(tombstones) / layout_.index_entry_count >= 0.35 || empty == 0;
    const auto probe_pressure = max_probe_.load(std::memory_order_acquire) >= std::max(1, (layout_.index_entry_count * 3) / 4);
    if ((tombstone_pressure || probe_pressure) && compact_index()) {
        std::lock_guard local(diagnostics_gate_);
        ++local_diagnostics_.index_compactions;
    }
}

sms_status Store::diagnostics(const Wait& wait, Diagnostics& result) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    result = {};
    {
        std::lock_guard local(diagnostics_gate_);
        result = local_diagnostics_;
    }
    result.total_bytes = layout_.total_bytes;
    result.slot_count = layout_.slot_count;
    result.index_entries = layout_.index_entry_count;
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        switch (load_acquire(slot(index).State)) {
            case slot_free: ++result.free_slots; break;
            case slot_published: ++result.published_slots; break;
            case slot_remove_requested: ++result.pending_removal; break;
            case slot_publishing: ++result.active_reservations; break;
        }
    }
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index)
        if (load_acquire(lease(index).State) == lease_active) ++result.active_leases;
    for (std::int32_t index = 0; index < layout_.index_entry_count; ++index) {
        switch (load_acquire(index_entry(index).State)) {
            case index_occupied: ++result.occupied_index_entries; break;
            case index_tombstone: ++result.tombstone_index_entries; break;
            default: ++result.empty_index_entries; break;
        }
    }
    result.last_probe = last_probe_.load(std::memory_order_acquire);
    result.max_probe = max_probe_.load(std::memory_order_acquire);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::get_layout(const Wait& wait, Layout& result) noexcept {
    Guard guard(*this, wait);
    if (!guard.acquired()) return record(guard.status());
    const auto status = ensure_ready();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    result = layout_;
    return SMS_STATUS_SUCCESS;
}

void Store::close() noexcept {
    if (closed_.exchange(true, std::memory_order_acq_rel)) return;
    gate_.lock();
    // Linux region close can enter lifecycle cleanup. Retire the ordinary
    // lock descriptor before that cleanup can observe a final owner.
    lock_.reset();
    if (region_) region_->close();
    region_.reset();
    gate_.unlock();
}

} // namespace sms::detail
