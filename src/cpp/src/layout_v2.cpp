#include "internal.hpp"

#include <algorithm>
#include <limits>

namespace sms::detail {
namespace {

bool aligned_stride(std::int32_t maximum_bytes, std::int32_t& result) noexcept {
    const auto minimum_one = std::max<std::int64_t>(1, maximum_bytes);
    std::int64_t aligned{};
    if (!checked_align_up_nonnegative(minimum_one, sms2_atomic_alignment, aligned) ||
        aligned > std::numeric_limits<std::int32_t>::max()) {
        return false;
    }
    result = static_cast<std::int32_t>(aligned);
    return true;
}

std::int32_t required_bits(std::uint32_t distinct_values) noexcept {
    std::int32_t bits = 0;
    std::uint32_t value = distinct_values - 1;
    do {
        ++bits;
        value >>= 1;
    } while (value != 0);
    return bits;
}

bool next_power_of_two(std::int64_t value, std::int32_t& result) noexcept {
    if (value <= 0 || value > (1LL << 30)) return false;
    std::int64_t next = 1;
    while (next < value) next <<= 1;
    if (next > std::numeric_limits<std::int32_t>::max()) return false;
    result = static_cast<std::int32_t>(next);
    return true;
}

bool range_fits(
    std::int64_t offset,
    std::int64_t length,
    std::int64_t total_bytes) noexcept {
    return offset >= 0 && length >= 0 && total_bytes >= 0 &&
        offset <= total_bytes && length <= total_bytes - offset;
}

bool nonoverlapping(
    std::int64_t offset,
    std::int64_t length,
    std::int64_t next_offset) noexcept {
    return offset >= 0 && length >= 0 && next_offset >= offset &&
        length <= next_offset - offset;
}

bool aligned(std::int64_t value, std::int64_t alignment) noexcept {
    return value >= 0 && (value & (alignment - 1)) == 0;
}

} // namespace

bool LayoutV2::calculate(
    std::int64_t total_bytes_value,
    std::int32_t slot_count_value,
    std::int32_t max_value_bytes_value,
    std::int32_t max_descriptor_bytes_value,
    std::int32_t max_key_bytes_value,
    std::int32_t lease_record_count_value,
    std::int32_t participant_record_count_value,
    LayoutV2& result) noexcept {
    if (slot_count_value < 1 || slot_count_value > sms2_maximum_slot_count ||
        lease_record_count_value < 1 || participant_record_count_value < 1 ||
        participant_record_count_value > sms2_maximum_participant_count ||
        max_key_bytes_value < 1 || max_descriptor_bytes_value < 0 ||
        max_value_bytes_value < 1) {
        return false;
    }

    LayoutV2 calculated{};
    calculated.total_bytes = total_bytes_value;
    calculated.slot_count = slot_count_value;
    calculated.lease_record_count = lease_record_count_value;
    calculated.participant_record_count = participant_record_count_value;
    calculated.max_value_bytes = max_value_bytes_value;
    calculated.max_descriptor_bytes = max_descriptor_bytes_value;
    calculated.max_key_bytes = max_key_bytes_value;
    calculated.header_length = sms2_header_length;

    calculated.participant_index_bits = required_bits(
        static_cast<std::uint32_t>(participant_record_count_value) + 1U);
    calculated.participant_generation_bits =
        sms2_participant_token_bits - calculated.participant_index_bits;
    if (calculated.participant_generation_bits < 8) return false;
    calculated.participant_index_mask = static_cast<std::int32_t>(
        (1U << calculated.participant_index_bits) - 1U);
    calculated.participant_generation_mask = static_cast<std::int32_t>(
        (1U << calculated.participant_generation_bits) - 1U);

    calculated.participant_stride = sms2_participant_stride;
    calculated.participant_offset = calculated.header_length;
    if (!checked_multiply_nonnegative(
            participant_record_count_value,
            calculated.participant_stride,
            calculated.participant_length)) {
        return false;
    }

    std::int64_t slot_lanes{};
    if (!checked_multiply_nonnegative(slot_count_value, 4, slot_lanes) ||
        !next_power_of_two(std::max<std::int64_t>(32, slot_lanes),
                           calculated.primary_lane_count)) {
        return false;
    }
    calculated.primary_bucket_count =
        calculated.primary_lane_count / sms2_primary_lanes_per_bucket;
    calculated.primary_bucket_stride = sms2_primary_bucket_stride;

    std::int64_t section_end{};
    if (!checked_add_nonnegative(
            calculated.participant_offset,
            calculated.participant_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_cache_line_size, calculated.primary_directory_offset) ||
        !checked_multiply_nonnegative(
            calculated.primary_bucket_count,
            calculated.primary_bucket_stride,
            calculated.primary_directory_length)) {
        return false;
    }

    calculated.overflow_stride = sms2_overflow_stride;
    if (!checked_add_nonnegative(
            calculated.primary_directory_offset,
            calculated.primary_directory_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_atomic_alignment, calculated.overflow_directory_offset) ||
        !checked_multiply_nonnegative(
            slot_count_value,
            calculated.overflow_stride,
            calculated.overflow_directory_length)) {
        return false;
    }

    calculated.lease_stride = sms2_lease_stride;
    if (!checked_add_nonnegative(
            calculated.overflow_directory_offset,
            calculated.overflow_directory_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_cache_line_size, calculated.lease_registry_offset) ||
        !checked_multiply_nonnegative(
            lease_record_count_value,
            calculated.lease_stride,
            calculated.lease_registry_length)) {
        return false;
    }

    calculated.slot_metadata_stride = sms2_slot_metadata_stride;
    if (!checked_add_nonnegative(
            calculated.lease_registry_offset,
            calculated.lease_registry_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_cache_line_size, calculated.slot_metadata_offset) ||
        !checked_multiply_nonnegative(
            slot_count_value,
            calculated.slot_metadata_stride,
            calculated.slot_metadata_length)) {
        return false;
    }

    if (!aligned_stride(max_key_bytes_value, calculated.key_stride) ||
        !checked_add_nonnegative(
            calculated.slot_metadata_offset,
            calculated.slot_metadata_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_atomic_alignment, calculated.key_storage_offset) ||
        !checked_multiply_nonnegative(
            slot_count_value,
            calculated.key_stride,
            calculated.key_storage_length)) {
        return false;
    }

    if (!aligned_stride(max_descriptor_bytes_value, calculated.descriptor_stride) ||
        !checked_add_nonnegative(
            calculated.key_storage_offset,
            calculated.key_storage_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_atomic_alignment, calculated.descriptor_storage_offset) ||
        !checked_multiply_nonnegative(
            slot_count_value,
            calculated.descriptor_stride,
            calculated.descriptor_storage_length)) {
        return false;
    }

    if (!aligned_stride(max_value_bytes_value, calculated.payload_stride) ||
        !checked_add_nonnegative(
            calculated.descriptor_storage_offset,
            calculated.descriptor_storage_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_atomic_alignment, calculated.payload_storage_offset) ||
        !checked_multiply_nonnegative(
            slot_count_value,
            calculated.payload_stride,
            calculated.payload_storage_length) ||
        !checked_add_nonnegative(
            calculated.payload_storage_offset,
            calculated.payload_storage_length,
            section_end) ||
        !checked_align_up_nonnegative(section_end, sms2_atomic_alignment, calculated.required_bytes)) {
        return false;
    }

    result = calculated;
    return true;
}

bool LayoutV2::fits_within_total_bytes() const noexcept {
    return total_bytes > 0 && required_bytes > 0 && total_bytes >= required_bytes;
}

bool LayoutV2::matches(const StoreHeaderV2& header) const noexcept {
    return header.Magic == sms2_magic &&
        header.LayoutMajorVersion == static_cast<std::uint16_t>(sms2_layout_major) &&
        header.LayoutMinorVersion == static_cast<std::uint16_t>(sms2_layout_minor) &&
        header.HeaderLength == header_length &&
        header.ResourceProtocolVersion == sms2_resource_protocol &&
        header.RequiredFeatures == sms2_required_features &&
        header.TotalBytes == total_bytes && header.StoreId != 0 &&
        header.SlotCount == slot_count &&
        header.LeaseRecordCount == lease_record_count &&
        header.ParticipantRecordCount == participant_record_count &&
        header.MaxKeyBytes == max_key_bytes &&
        header.MaxDescriptorBytes == max_descriptor_bytes &&
        header.MaxValueBytes == max_value_bytes &&
        header.ParticipantIndexBits == participant_index_bits &&
        header.ParticipantGenerationBits == participant_generation_bits &&
        header.ParticipantOffset == participant_offset &&
        header.ParticipantLength == participant_length &&
        header.ParticipantStride == participant_stride &&
        header.PrimaryLaneCount == primary_lane_count &&
        header.PrimaryBucketCount == primary_bucket_count &&
        header.PrimaryBucketStride == primary_bucket_stride &&
        header.PrimaryDirectoryOffset == primary_directory_offset &&
        header.PrimaryDirectoryLength == primary_directory_length &&
        header.OverflowDirectoryOffset == overflow_directory_offset &&
        header.OverflowDirectoryLength == overflow_directory_length &&
        header.OverflowStride == overflow_stride &&
        header.LeaseStride == lease_stride &&
        header.LeaseRegistryOffset == lease_registry_offset &&
        header.LeaseRegistryLength == lease_registry_length &&
        header.SlotMetadataStride == slot_metadata_stride &&
        header.KeyStride == key_stride &&
        header.SlotMetadataOffset == slot_metadata_offset &&
        header.SlotMetadataLength == slot_metadata_length &&
        header.KeyStorageOffset == key_storage_offset &&
        header.KeyStorageLength == key_storage_length &&
        header.DescriptorStride == descriptor_stride &&
        header.PayloadStride == payload_stride &&
        header.DescriptorStorageOffset == descriptor_storage_offset &&
        header.DescriptorStorageLength == descriptor_storage_length &&
        header.PayloadStorageOffset == payload_storage_offset &&
        header.PayloadStorageLength == payload_storage_length &&
        bounds_valid(header);
}

bool LayoutV2::bounds_valid(const StoreHeaderV2& header) const noexcept {
    if (header.TotalBytes != total_bytes || !fits_within_total_bytes() ||
        header.ParticipantOffset != participant_offset ||
        header.ParticipantLength != participant_length ||
        header.PrimaryDirectoryOffset != primary_directory_offset ||
        header.PrimaryDirectoryLength != primary_directory_length ||
        header.OverflowDirectoryOffset != overflow_directory_offset ||
        header.OverflowDirectoryLength != overflow_directory_length ||
        header.LeaseRegistryOffset != lease_registry_offset ||
        header.LeaseRegistryLength != lease_registry_length ||
        header.SlotMetadataOffset != slot_metadata_offset ||
        header.SlotMetadataLength != slot_metadata_length ||
        header.KeyStorageOffset != key_storage_offset ||
        header.KeyStorageLength != key_storage_length ||
        header.DescriptorStorageOffset != descriptor_storage_offset ||
        header.DescriptorStorageLength != descriptor_storage_length ||
        header.PayloadStorageOffset != payload_storage_offset ||
        header.PayloadStorageLength != payload_storage_length) {
        return false;
    }

    if (!aligned(header.ParticipantOffset, sms2_cache_line_size) ||
        !aligned(header.PrimaryDirectoryOffset, sms2_cache_line_size) ||
        !aligned(header.OverflowDirectoryOffset, sms2_atomic_alignment) ||
        !aligned(header.LeaseRegistryOffset, sms2_cache_line_size) ||
        !aligned(header.SlotMetadataOffset, sms2_cache_line_size) ||
        !aligned(header.KeyStorageOffset, sms2_atomic_alignment) ||
        !aligned(header.DescriptorStorageOffset, sms2_atomic_alignment) ||
        !aligned(header.PayloadStorageOffset, sms2_atomic_alignment)) {
        return false;
    }

    const auto total = header.TotalBytes;
    return range_fits(header.ParticipantOffset, header.ParticipantLength, total) &&
        range_fits(header.PrimaryDirectoryOffset, header.PrimaryDirectoryLength, total) &&
        range_fits(header.OverflowDirectoryOffset, header.OverflowDirectoryLength, total) &&
        range_fits(header.LeaseRegistryOffset, header.LeaseRegistryLength, total) &&
        range_fits(header.SlotMetadataOffset, header.SlotMetadataLength, total) &&
        range_fits(header.KeyStorageOffset, header.KeyStorageLength, total) &&
        range_fits(header.DescriptorStorageOffset, header.DescriptorStorageLength, total) &&
        range_fits(header.PayloadStorageOffset, header.PayloadStorageLength, total) &&
        nonoverlapping(header.ParticipantOffset, header.ParticipantLength,
                       header.PrimaryDirectoryOffset) &&
        nonoverlapping(header.PrimaryDirectoryOffset, header.PrimaryDirectoryLength,
                       header.OverflowDirectoryOffset) &&
        nonoverlapping(header.OverflowDirectoryOffset, header.OverflowDirectoryLength,
                       header.LeaseRegistryOffset) &&
        nonoverlapping(header.LeaseRegistryOffset, header.LeaseRegistryLength,
                       header.SlotMetadataOffset) &&
        nonoverlapping(header.SlotMetadataOffset, header.SlotMetadataLength,
                       header.KeyStorageOffset) &&
        nonoverlapping(header.KeyStorageOffset, header.KeyStorageLength,
                       header.DescriptorStorageOffset) &&
        nonoverlapping(header.DescriptorStorageOffset, header.DescriptorStorageLength,
                       header.PayloadStorageOffset);
}

} // namespace sms::detail
