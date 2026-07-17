#include "store_control.hpp"

#include <cstring>

namespace sms::detail {

StoreControlV2::StoreControlV2(
    std::uint8_t* mapping_base,
    std::size_t mapping_length,
    const LayoutV2& layout) noexcept
    : mapping_base_(mapping_base),
      mapping_length_(mapping_length),
      layout_(layout) {}

bool StoreControlV2::valid_mapping() const noexcept {
    return mapping_base_ != nullptr && layout_.required_bytes > 0 &&
        layout_.total_bytes > 0 && layout_.fits_within_total_bytes() &&
        static_cast<std::uint64_t>(layout_.total_bytes) <= mapping_length_ &&
        static_cast<std::uint64_t>(layout_.required_bytes) <= mapping_length_ &&
        MappedAtomic64::is_aligned(mapping_base_);
}

StoreHeaderV2* StoreControlV2::header() const noexcept {
    return valid_mapping()
        ? reinterpret_cast<StoreHeaderV2*>(mapping_base_)
        : nullptr;
}

bool StoreControlV2::initialize_participant_records(
    const OperationBudget& budget) noexcept {
    std::uint64_t free_control{};
    if (!ParticipantControl::try_encode(0, 1, 0, free_control)) return false;
    for (std::int32_t index = 0;
         index < layout_.participant_record_count;
         ++index) {
        if (budget.check_periodic(index) != SMS_STATUS_SUCCESS) return false;
        const auto offset = layout_.participant_offset +
            static_cast<std::int64_t>(index) * layout_.participant_stride;
        if (offset < 0 || static_cast<std::uint64_t>(offset) >
                mapping_length_ - sizeof(ParticipantRecordV2)) {
            return false;
        }
        auto& current = *reinterpret_cast<ParticipantRecordV2*>(
            mapping_base_ + offset);
        current.IdentityKind = 0;
        current.Reserved = 0;
        current.ProcessStartValue = 0;
        current.OpenSequence = 0;
        current.PidNamespaceId = 0;
        std::memset(current.ReservedBytes, 0, sizeof(current.ReservedBytes));
        MappedAtomic64::store_release(current.Control, free_control);
    }
    return true;
}

bool StoreControlV2::initialize_lease_records(
    const OperationBudget& budget) noexcept {
    std::uint64_t free_control{};
    if (!LeaseControl::try_encode(0, 1, 0, free_control)) return false;
    for (std::int32_t index = 0; index < layout_.lease_record_count; ++index) {
        if (budget.check_periodic(index) != SMS_STATUS_SUCCESS) return false;
        const auto offset = layout_.lease_registry_offset +
            static_cast<std::int64_t>(index) * layout_.lease_stride;
        if (offset < 0 || static_cast<std::uint64_t>(offset) >
                mapping_length_ - sizeof(LeaseRecordV2)) {
            return false;
        }
        auto& current = *reinterpret_cast<LeaseRecordV2*>(mapping_base_ + offset);
        current.SlotBinding = 0;
        current.AcquireSequence = 0;
        MappedAtomic64::store_release(current.Control, free_control);
    }
    return true;
}

bool StoreControlV2::initialize_slot_records(
    const OperationBudget& budget) noexcept {
    std::uint64_t free_control{};
    if (!SlotControl::try_encode(0, 1, 0, free_control)) return false;
    for (std::int32_t index = 0; index < layout_.slot_count; ++index) {
        if (budget.check_periodic(index) != SMS_STATUS_SUCCESS) return false;
        const auto offset = layout_.slot_metadata_offset +
            static_cast<std::int64_t>(index) * layout_.slot_metadata_stride;
        if (offset < 0 || static_cast<std::uint64_t>(offset) >
                mapping_length_ - sizeof(ValueSlotMetadataV2)) {
            return false;
        }
        auto& current = *reinterpret_cast<ValueSlotMetadataV2*>(mapping_base_ + offset);
        current.DirectoryBinding = 0;
        current.DirectoryLocation = 0;
        current.DirectoryOperation = 0;
        current.KeyHash = 0;
        current.KeyLength = 0;
        current.DescriptorLength = 0;
        current.ValueLength = 0;
        current.PublicationIntent = 0;
        current.BytesAdvanced = 0;
        current.CommitSequence = 0;
        current.KeyOffset = layout_.key_storage_offset +
            static_cast<std::int64_t>(index) * layout_.key_stride;
        current.DescriptorOffset = layout_.descriptor_storage_offset +
            static_cast<std::int64_t>(index) * layout_.descriptor_stride;
        current.PayloadOffset = layout_.payload_storage_offset +
            static_cast<std::int64_t>(index) * layout_.payload_stride;
        MappedAtomic64::store_release(current.Control, free_control);
    }
    return true;
}

bool StoreControlV2::initialize_creator(
    std::uint64_t store_id,
    std::uint64_t pid_namespace_id,
    std::uint64_t pid_namespace_mode,
    const OperationBudget& budget) noexcept {
    if (!valid_mapping() || store_id == 0 ||
        (pid_namespace_mode != sms2_pid_namespace_recovery_enabled &&
         pid_namespace_mode != sms2_pid_namespace_recovery_mixed)) {
        return false;
    }
    std::memset(mapping_base_, 0, static_cast<std::size_t>(layout_.required_bytes));
    auto& value = *reinterpret_cast<StoreHeaderV2*>(mapping_base_);
    // Magic is the final creator publication. Existing openers that observe
    // zero must never interpret any partially initialized record topology.
    value.Magic = 0;
    value.LayoutMajorVersion = static_cast<std::uint16_t>(sms2_layout_major);
    value.LayoutMinorVersion = static_cast<std::uint16_t>(sms2_layout_minor);
    value.HeaderLength = layout_.header_length;
    value.ResourceProtocolVersion = sms2_resource_protocol;
    value.RequiredFeatures = sms2_required_features;
    value.OptionalFeatures = sms2_optional_features;
    value.TotalBytes = layout_.total_bytes;
    value.StoreId = store_id;
    value.Control = sms2_store_initializing;
    value.Sequence = 0;
    value.SlotCount = layout_.slot_count;
    value.LeaseRecordCount = layout_.lease_record_count;
    value.ParticipantRecordCount = layout_.participant_record_count;
    value.MaxKeyBytes = layout_.max_key_bytes;
    value.MaxDescriptorBytes = layout_.max_descriptor_bytes;
    value.MaxValueBytes = layout_.max_value_bytes;
    value.ParticipantIndexBits = layout_.participant_index_bits;
    value.ParticipantGenerationBits = layout_.participant_generation_bits;
    value.ParticipantOffset = layout_.participant_offset;
    value.ParticipantLength = layout_.participant_length;
    value.ParticipantStride = layout_.participant_stride;
    value.PrimaryLaneCount = layout_.primary_lane_count;
    value.PrimaryBucketCount = layout_.primary_bucket_count;
    value.PrimaryBucketStride = layout_.primary_bucket_stride;
    value.PrimaryDirectoryOffset = layout_.primary_directory_offset;
    value.PrimaryDirectoryLength = layout_.primary_directory_length;
    value.OverflowDirectoryOffset = layout_.overflow_directory_offset;
    value.OverflowDirectoryLength = layout_.overflow_directory_length;
    value.OverflowStride = layout_.overflow_stride;
    value.LeaseStride = layout_.lease_stride;
    value.LeaseRegistryOffset = layout_.lease_registry_offset;
    value.LeaseRegistryLength = layout_.lease_registry_length;
    value.SlotMetadataStride = layout_.slot_metadata_stride;
    value.KeyStride = layout_.key_stride;
    value.SlotMetadataOffset = layout_.slot_metadata_offset;
    value.SlotMetadataLength = layout_.slot_metadata_length;
    value.KeyStorageOffset = layout_.key_storage_offset;
    value.KeyStorageLength = layout_.key_storage_length;
    value.DescriptorStride = layout_.descriptor_stride;
    value.PayloadStride = layout_.payload_stride;
    value.DescriptorStorageOffset = layout_.descriptor_storage_offset;
    value.DescriptorStorageLength = layout_.descriptor_storage_length;
    value.PayloadStorageOffset = layout_.payload_storage_offset;
    value.PayloadStorageLength = layout_.payload_storage_length;
    value.PidNamespaceId = pid_namespace_id;
    value.PidNamespaceMode = pid_namespace_mode;

    if (!initialize_participant_records(budget) ||
        !initialize_lease_records(budget) ||
        !initialize_slot_records(budget)) {
        return false;
    }
    MappedAtomic64::store_release(value.Control, sms2_store_ready);
    std::atomic_ref<std::uint32_t>(value.Magic).store(
        sms2_magic, std::memory_order_release);
    return true;
}

StoreControlStatus StoreControlV2::validate_existing() const noexcept {
    const auto* value = header();
    if (value == nullptr) return StoreControlStatus::incompatible_layout;
    if (value->Magic == 0) return StoreControlStatus::store_busy;
    if (!layout_.matches(*value) ||
        (value->PidNamespaceMode != sms2_pid_namespace_recovery_enabled &&
         value->PidNamespaceMode != sms2_pid_namespace_recovery_mixed)) {
        return StoreControlStatus::incompatible_layout;
    }
    switch (MappedAtomic64::load_acquire(
        const_cast<std::uint64_t&>(value->Control))) {
    case sms2_store_ready: return StoreControlStatus::success;
    case sms2_store_initializing: return StoreControlStatus::store_busy;
    case sms2_store_corrupt: return StoreControlStatus::corrupt_store;
    case sms2_store_unsupported: return StoreControlStatus::unsupported_platform;
    default: return StoreControlStatus::incompatible_layout;
    }
}

sms_status StoreControlV2::ensure_ready() const noexcept {
    switch (validate_existing()) {
    case StoreControlStatus::success: return SMS_STATUS_SUCCESS;
    case StoreControlStatus::store_busy: return SMS_STATUS_STORE_BUSY;
    case StoreControlStatus::corrupt_store: return SMS_STATUS_CORRUPT_STORE;
    case StoreControlStatus::unsupported_platform: return SMS_STATUS_UNSUPPORTED_PLATFORM;
    default: return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

bool StoreControlV2::latch_corrupt() noexcept {
    auto* value = header();
    if (value == nullptr) return false;
    auto expected = sms2_store_ready;
    if (MappedAtomic64::compare_exchange(
            value->Control, expected, sms2_store_corrupt)) {
        return true;
    }
    return expected == sms2_store_corrupt;
}

} // namespace sms::detail
