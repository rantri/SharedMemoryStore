#include "layout_v2.hpp"
#include "test_support.hpp"
#include "test_support_v2.hpp"

#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <limits>
#include <string_view>
#include <type_traits>

namespace {

using sms::detail::LayoutV2;
using sms::detail::StoreHeaderV2;

StoreHeaderV2 make_header(const LayoutV2& layout) {
    StoreHeaderV2 header{};
    header.Magic = sms::detail::sms2_magic;
    header.LayoutMajorVersion = sms::detail::sms2_layout_major;
    header.LayoutMinorVersion = sms::detail::sms2_layout_minor;
    header.HeaderLength = layout.header_length;
    header.ResourceProtocolVersion = sms::detail::sms2_resource_protocol;
    header.RequiredFeatures = sms::detail::sms2_required_features;
    header.OptionalFeatures = sms::detail::sms2_optional_features;
    header.TotalBytes = layout.total_bytes;
    header.StoreId = 0x0102'0304'0506'0708ULL;
    header.Control = sms::detail::sms2_store_ready;
    header.Sequence = 19;
    header.SlotCount = layout.slot_count;
    header.LeaseRecordCount = layout.lease_record_count;
    header.ParticipantRecordCount = layout.participant_record_count;
    header.MaxKeyBytes = layout.max_key_bytes;
    header.MaxDescriptorBytes = layout.max_descriptor_bytes;
    header.MaxValueBytes = layout.max_value_bytes;
    header.ParticipantIndexBits = layout.participant_index_bits;
    header.ParticipantGenerationBits = layout.participant_generation_bits;
    header.ParticipantOffset = layout.participant_offset;
    header.ParticipantLength = layout.participant_length;
    header.ParticipantStride = layout.participant_stride;
    header.PrimaryLaneCount = layout.primary_lane_count;
    header.PrimaryBucketCount = layout.primary_bucket_count;
    header.PrimaryBucketStride = layout.primary_bucket_stride;
    header.PrimaryDirectoryOffset = layout.primary_directory_offset;
    header.PrimaryDirectoryLength = layout.primary_directory_length;
    header.OverflowDirectoryOffset = layout.overflow_directory_offset;
    header.OverflowDirectoryLength = layout.overflow_directory_length;
    header.OverflowStride = layout.overflow_stride;
    header.LeaseStride = layout.lease_stride;
    header.LeaseRegistryOffset = layout.lease_registry_offset;
    header.LeaseRegistryLength = layout.lease_registry_length;
    header.SlotMetadataStride = layout.slot_metadata_stride;
    header.KeyStride = layout.key_stride;
    header.SlotMetadataOffset = layout.slot_metadata_offset;
    header.SlotMetadataLength = layout.slot_metadata_length;
    header.KeyStorageOffset = layout.key_storage_offset;
    header.KeyStorageLength = layout.key_storage_length;
    header.DescriptorStride = layout.descriptor_stride;
    header.PayloadStride = layout.payload_stride;
    header.DescriptorStorageOffset = layout.descriptor_storage_offset;
    header.DescriptorStorageLength = layout.descriptor_storage_length;
    header.PayloadStorageOffset = layout.payload_storage_offset;
    header.PayloadStorageLength = layout.payload_storage_length;
    header.PidNamespaceId = 0x8899'aabb'ccdd'eeffULL;
    header.PidNamespaceMode = sms::detail::sms2_pid_namespace_recovery_enabled;
    return header;
}

bool is_aligned(std::int64_t value, std::int64_t alignment) {
    return value >= 0 && (value % alignment) == 0;
}

} // namespace

static_assert(std::endian::native == std::endian::little);
static_assert(std::is_standard_layout_v<StoreHeaderV2>);
static_assert(sizeof(StoreHeaderV2) == 512);
static_assert(alignof(StoreHeaderV2) == 64);
static_assert(offsetof(StoreHeaderV2, Magic) == 0);
static_assert(offsetof(StoreHeaderV2, LayoutMajorVersion) == 4);
static_assert(offsetof(StoreHeaderV2, LayoutMinorVersion) == 6);
static_assert(offsetof(StoreHeaderV2, HeaderLength) == 8);
static_assert(offsetof(StoreHeaderV2, ResourceProtocolVersion) == 12);
static_assert(offsetof(StoreHeaderV2, RequiredFeatures) == 16);
static_assert(offsetof(StoreHeaderV2, OptionalFeatures) == 24);
static_assert(offsetof(StoreHeaderV2, TotalBytes) == 32);
static_assert(offsetof(StoreHeaderV2, StoreId) == 40);
static_assert(offsetof(StoreHeaderV2, Control) == 48);
static_assert(offsetof(StoreHeaderV2, Sequence) == 56);
static_assert(offsetof(StoreHeaderV2, SlotCount) == 64);
static_assert(offsetof(StoreHeaderV2, LeaseRecordCount) == 68);
static_assert(offsetof(StoreHeaderV2, ParticipantRecordCount) == 72);
static_assert(offsetof(StoreHeaderV2, MaxKeyBytes) == 76);
static_assert(offsetof(StoreHeaderV2, MaxDescriptorBytes) == 80);
static_assert(offsetof(StoreHeaderV2, MaxValueBytes) == 84);
static_assert(offsetof(StoreHeaderV2, ParticipantIndexBits) == 88);
static_assert(offsetof(StoreHeaderV2, ParticipantGenerationBits) == 92);
static_assert(offsetof(StoreHeaderV2, ParticipantOffset) == 96);
static_assert(offsetof(StoreHeaderV2, ParticipantLength) == 104);
static_assert(offsetof(StoreHeaderV2, ParticipantStride) == 112);
static_assert(offsetof(StoreHeaderV2, PrimaryLaneCount) == 116);
static_assert(offsetof(StoreHeaderV2, PrimaryBucketCount) == 120);
static_assert(offsetof(StoreHeaderV2, PrimaryBucketStride) == 124);
static_assert(offsetof(StoreHeaderV2, PrimaryDirectoryOffset) == 128);
static_assert(offsetof(StoreHeaderV2, PrimaryDirectoryLength) == 136);
static_assert(offsetof(StoreHeaderV2, OverflowDirectoryOffset) == 144);
static_assert(offsetof(StoreHeaderV2, OverflowDirectoryLength) == 152);
static_assert(offsetof(StoreHeaderV2, OverflowStride) == 160);
static_assert(offsetof(StoreHeaderV2, LeaseStride) == 164);
static_assert(offsetof(StoreHeaderV2, LeaseRegistryOffset) == 168);
static_assert(offsetof(StoreHeaderV2, LeaseRegistryLength) == 176);
static_assert(offsetof(StoreHeaderV2, SlotMetadataStride) == 184);
static_assert(offsetof(StoreHeaderV2, KeyStride) == 188);
static_assert(offsetof(StoreHeaderV2, SlotMetadataOffset) == 192);
static_assert(offsetof(StoreHeaderV2, SlotMetadataLength) == 200);
static_assert(offsetof(StoreHeaderV2, KeyStorageOffset) == 208);
static_assert(offsetof(StoreHeaderV2, KeyStorageLength) == 216);
static_assert(offsetof(StoreHeaderV2, DescriptorStride) == 224);
static_assert(offsetof(StoreHeaderV2, PayloadStride) == 228);
static_assert(offsetof(StoreHeaderV2, DescriptorStorageOffset) == 232);
static_assert(offsetof(StoreHeaderV2, DescriptorStorageLength) == 240);
static_assert(offsetof(StoreHeaderV2, PayloadStorageOffset) == 248);
static_assert(offsetof(StoreHeaderV2, PayloadStorageLength) == 256);
static_assert(offsetof(StoreHeaderV2, PidNamespaceId) == 264);
static_assert(offsetof(StoreHeaderV2, PidNamespaceMode) == 272);

static_assert(std::is_standard_layout_v<sms::detail::ParticipantRecordV2>);
static_assert(sizeof(sms::detail::ParticipantRecordV2) == 64);
static_assert(alignof(sms::detail::ParticipantRecordV2) >= 8);
static_assert(offsetof(sms::detail::ParticipantRecordV2, Control) == 0);
static_assert(offsetof(sms::detail::ParticipantRecordV2, IdentityKind) == 8);
static_assert(offsetof(sms::detail::ParticipantRecordV2, Reserved) == 12);
static_assert(offsetof(sms::detail::ParticipantRecordV2, ProcessStartValue) == 16);
static_assert(offsetof(sms::detail::ParticipantRecordV2, OpenSequence) == 24);
static_assert(offsetof(sms::detail::ParticipantRecordV2, PidNamespaceId) == 32);

static_assert(std::is_standard_layout_v<sms::detail::PrimaryDirectoryBucketV2>);
static_assert(sizeof(sms::detail::PrimaryDirectoryBucketV2) == 128);
static_assert(alignof(sms::detail::PrimaryDirectoryBucketV2) >= 8);
static_assert(offsetof(sms::detail::PrimaryDirectoryBucketV2, SpillSummary) == 0);
static_assert(offsetof(sms::detail::PrimaryDirectoryBucketV2, Mutation) == 8);
static_assert(offsetof(sms::detail::PrimaryDirectoryBucketV2, Lanes) == 16);

static_assert(std::is_standard_layout_v<sms::detail::LeaseRecordV2>);
static_assert(sizeof(sms::detail::LeaseRecordV2) == 64);
static_assert(alignof(sms::detail::LeaseRecordV2) >= 8);
static_assert(offsetof(sms::detail::LeaseRecordV2, Control) == 0);
static_assert(offsetof(sms::detail::LeaseRecordV2, SlotBinding) == 8);
static_assert(offsetof(sms::detail::LeaseRecordV2, AcquireSequence) == 16);

static_assert(std::is_standard_layout_v<sms::detail::ValueSlotMetadataV2>);
static_assert(sizeof(sms::detail::ValueSlotMetadataV2) == 128);
static_assert(alignof(sms::detail::ValueSlotMetadataV2) >= 8);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, Control) == 0);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, DirectoryBinding) == 8);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, DirectoryLocation) == 16);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, DirectoryOperation) == 24);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, KeyHash) == 32);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, KeyLength) == 40);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, DescriptorLength) == 44);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, ValueLength) == 48);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, PublicationIntent) == 52);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, BytesAdvanced) == 56);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, CommitSequence) == 64);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, KeyOffset) == 72);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, DescriptorOffset) == 80);
static_assert(offsetof(sms::detail::ValueSlotMetadataV2, PayloadOffset) == 88);

int main() {
    using namespace sms::detail;

    SMS_CHECK(sms2_magic == 0x3253'4d53U);
    SMS_CHECK(sms2_layout_major == 2);
    SMS_CHECK(sms2_layout_minor == 0);
    SMS_CHECK(sms2_resource_protocol == 2);
    SMS_CHECK(sms2_required_features == 7);
    SMS_CHECK(sms2_optional_features == 0);
    SMS_CHECK(sms2_atomic_alignment == 8);
    SMS_CHECK(sms2_maximum_slot_count == 1'048'575);
    SMS_CHECK(sms2_maximum_participant_count == 1'048'575);

    const auto manifest = sms::test::v2::load_manifest();
    SMS_CHECK(sms::test::v2::require_unique_json_fragment(
                  manifest.json, "\"magic_integer_hex\": \"32534d53\"") !=
              std::string_view::npos);
    SMS_CHECK(manifest.json.find("\"required_features\": 7") != std::string_view::npos);

    LayoutV2 smallest{};
    SMS_CHECK(LayoutV2::calculate(1'368, 1, 1, 0, 1, 1, 1, smallest));
    SMS_CHECK(smallest.header_length == 512);
    SMS_CHECK(smallest.participant_index_bits == 1);
    SMS_CHECK(smallest.participant_generation_bits == 27);
    SMS_CHECK(smallest.participant_offset == 512);
    SMS_CHECK(smallest.participant_length == 64);
    SMS_CHECK(smallest.primary_lane_count == 32);
    SMS_CHECK(smallest.primary_bucket_count == 4);
    SMS_CHECK(smallest.primary_directory_offset == 576);
    SMS_CHECK(smallest.primary_directory_length == 512);
    SMS_CHECK(smallest.overflow_directory_offset == 1'088);
    SMS_CHECK(smallest.overflow_directory_length == 8);
    SMS_CHECK(smallest.lease_registry_offset == 1'152);
    SMS_CHECK(smallest.lease_registry_length == 64);
    SMS_CHECK(smallest.slot_metadata_offset == 1'216);
    SMS_CHECK(smallest.slot_metadata_length == 128);
    SMS_CHECK(smallest.key_stride == 8);
    SMS_CHECK(smallest.key_storage_offset == 1'344);
    SMS_CHECK(smallest.key_storage_length == 8);
    SMS_CHECK(smallest.descriptor_stride == 8);
    SMS_CHECK(smallest.descriptor_storage_offset == 1'352);
    SMS_CHECK(smallest.descriptor_storage_length == 8);
    SMS_CHECK(smallest.payload_stride == 8);
    SMS_CHECK(smallest.payload_storage_offset == 1'360);
    SMS_CHECK(smallest.payload_storage_length == 8);
    SMS_CHECK(smallest.required_bytes == 1'368);
    SMS_CHECK(smallest.fits_within_total_bytes());

    LayoutV2 representative{};
    SMS_CHECK(LayoutV2::calculate(2'128, 3, 17, 5, 9, 4, 4, representative));
    SMS_CHECK(representative.participant_index_bits == 3);
    SMS_CHECK(representative.participant_generation_bits == 25);
    SMS_CHECK(representative.participant_offset == 512);
    SMS_CHECK(representative.participant_length == 256);
    SMS_CHECK(representative.primary_lane_count == 32);
    SMS_CHECK(representative.primary_bucket_count == 4);
    SMS_CHECK(representative.primary_directory_offset == 768);
    SMS_CHECK(representative.primary_directory_length == 512);
    SMS_CHECK(representative.overflow_directory_offset == 1'280);
    SMS_CHECK(representative.overflow_directory_length == 24);
    SMS_CHECK(representative.lease_registry_offset == 1'344);
    SMS_CHECK(representative.lease_registry_length == 256);
    SMS_CHECK(representative.slot_metadata_offset == 1'600);
    SMS_CHECK(representative.slot_metadata_length == 384);
    SMS_CHECK(representative.key_stride == 16);
    SMS_CHECK(representative.key_storage_offset == 1'984);
    SMS_CHECK(representative.key_storage_length == 48);
    SMS_CHECK(representative.descriptor_stride == 8);
    SMS_CHECK(representative.descriptor_storage_offset == 2'032);
    SMS_CHECK(representative.descriptor_storage_length == 24);
    SMS_CHECK(representative.payload_stride == 24);
    SMS_CHECK(representative.payload_storage_offset == 2'056);
    SMS_CHECK(representative.payload_storage_length == 72);
    SMS_CHECK(representative.required_bytes == 2'128);

    LayoutV2 aligned{};
    SMS_CHECK(LayoutV2::calculate(10'624, 4, 1'024, 16, 64, 8, 64, aligned));
    SMS_CHECK(aligned.participant_index_bits == 7);
    SMS_CHECK(aligned.participant_generation_bits == 21);
    SMS_CHECK(aligned.participant_offset == 512);
    SMS_CHECK(aligned.participant_length == 4'096);
    SMS_CHECK(aligned.primary_directory_offset == 4'608);
    SMS_CHECK(aligned.primary_directory_length == 512);
    SMS_CHECK(aligned.overflow_directory_offset == 5'120);
    SMS_CHECK(aligned.overflow_directory_length == 32);
    SMS_CHECK(aligned.lease_registry_offset == 5'184);
    SMS_CHECK(aligned.lease_registry_length == 512);
    SMS_CHECK(aligned.slot_metadata_offset == 5'696);
    SMS_CHECK(aligned.slot_metadata_length == 512);
    SMS_CHECK(aligned.key_storage_offset == 6'208);
    SMS_CHECK(aligned.descriptor_storage_offset == 6'464);
    SMS_CHECK(aligned.payload_storage_offset == 6'528);
    SMS_CHECK(aligned.payload_storage_length == 4'096);
    SMS_CHECK(aligned.required_bytes == 10'624);

    SMS_CHECK(is_aligned(aligned.participant_offset, 64));
    SMS_CHECK(is_aligned(aligned.primary_directory_offset, 64));
    SMS_CHECK(is_aligned(aligned.overflow_directory_offset, 8));
    SMS_CHECK(is_aligned(aligned.lease_registry_offset, 64));
    SMS_CHECK(is_aligned(aligned.slot_metadata_offset, 64));
    SMS_CHECK(is_aligned(aligned.key_storage_offset, 8));
    SMS_CHECK(is_aligned(aligned.descriptor_storage_offset, 8));
    SMS_CHECK(is_aligned(aligned.payload_storage_offset, 8));

    LayoutV2 insufficient{};
    SMS_CHECK(LayoutV2::calculate(2'127, 3, 17, 5, 9, 4, 4, insufficient));
    SMS_CHECK(insufficient.required_bytes == 2'128);
    SMS_CHECK(!insufficient.fits_within_total_bytes());

    LayoutV2 invalid{};
    SMS_CHECK(!LayoutV2::calculate(0, 0, 1, 0, 1, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1'048'576, 1, 0, 1, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 1, 0, 1, 0, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 1, 0, 1, 1, 0, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 1, 0, 1, 1, 1'048'576, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 1, 0, 0, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 1, -1, 1, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(0, 1, 0, 0, 1, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(
        0, 1, 1, 0, std::numeric_limits<std::int32_t>::max(), 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(
        0, 1, 1, std::numeric_limits<std::int32_t>::max(), 1, 1, 1, invalid));
    SMS_CHECK(!LayoutV2::calculate(
        0, 1, std::numeric_limits<std::int32_t>::max(), 0, 1, 1, 1, invalid));

    auto header = make_header(representative);
    SMS_CHECK(representative.matches(header));
    SMS_CHECK(representative.bounds_valid(header));

    std::array<std::byte, sizeof(StoreHeaderV2)> header_bytes{};
    std::memcpy(header_bytes.data(), &header, sizeof(header));
    SMS_CHECK(header_bytes[0] == std::byte{0x53});
    SMS_CHECK(header_bytes[1] == std::byte{0x4d});
    SMS_CHECK(header_bytes[2] == std::byte{0x53});
    SMS_CHECK(header_bytes[3] == std::byte{0x32});
    SMS_CHECK(header_bytes[16] == std::byte{0x07});
    for (std::size_t index = 17; index < 24; ++index) {
        SMS_CHECK(header_bytes[index] == std::byte{0x00});
    }

    for (const std::uint64_t incompatible : {0ULL, 1ULL, 3ULL, 15ULL}) {
        auto changed = header;
        changed.RequiredFeatures = incompatible;
        SMS_CHECK(!representative.matches(changed));
    }
    auto optional_changed = header;
    optional_changed.OptionalFeatures = 1;
    SMS_CHECK(representative.matches(optional_changed));

    auto misaligned = header;
    ++misaligned.LeaseRegistryOffset;
    SMS_CHECK(!representative.matches(misaligned));
    SMS_CHECK(!representative.bounds_valid(misaligned));

    auto out_of_bounds = header;
    out_of_bounds.PayloadStorageLength = out_of_bounds.TotalBytes;
    SMS_CHECK(!representative.matches(out_of_bounds));
    SMS_CHECK(!representative.bounds_valid(out_of_bounds));

    auto zero_store_id = header;
    zero_store_id.StoreId = 0;
    SMS_CHECK(!representative.matches(zero_store_id));
    return 0;
}
