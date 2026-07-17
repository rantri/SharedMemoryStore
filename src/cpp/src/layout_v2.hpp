#pragma once

#include <cstddef>
#include <cstdint>
#include <type_traits>

namespace sms::detail {

inline constexpr std::uint32_t sms2_magic = 0x3253'4d53U;
inline constexpr std::int32_t sms2_layout_major = 2;
inline constexpr std::int32_t sms2_layout_minor = 0;
inline constexpr std::int32_t sms2_resource_protocol = 2;
inline constexpr std::uint64_t sms2_required_features = 7;
inline constexpr std::uint64_t sms2_optional_features = 0;

inline constexpr std::int32_t sms2_header_length = 512;
inline constexpr std::int32_t sms2_atomic_alignment = 8;
inline constexpr std::int32_t sms2_cache_line_size = 64;
inline constexpr std::int32_t sms2_participant_stride = 64;
inline constexpr std::int32_t sms2_primary_bucket_stride = 128;
inline constexpr std::int32_t sms2_primary_lanes_per_bucket = 8;
inline constexpr std::int32_t sms2_overflow_stride = 8;
inline constexpr std::int32_t sms2_lease_stride = 64;
inline constexpr std::int32_t sms2_slot_metadata_stride = 128;
inline constexpr std::int32_t sms2_maximum_slot_count = 1'048'575;
inline constexpr std::int32_t sms2_maximum_participant_count = 1'048'575;
inline constexpr std::int32_t sms2_slot_generation_bits = 33;
inline constexpr std::int32_t sms2_participant_token_bits = 28;

inline constexpr std::uint64_t sms2_feature_versioned_empty_spill_summary = 1ULL << 0;
inline constexpr std::uint64_t sms2_feature_publication_intent = 1ULL << 1;
inline constexpr std::uint64_t sms2_feature_pid_namespace_identity = 1ULL << 2;

inline constexpr std::uint64_t sms2_store_initializing = 1;
inline constexpr std::uint64_t sms2_store_ready = 2;
inline constexpr std::uint64_t sms2_store_corrupt = 3;
inline constexpr std::uint64_t sms2_store_unsupported = 4;
inline constexpr std::uint64_t sms2_pid_namespace_recovery_enabled = 1;
inline constexpr std::uint64_t sms2_pid_namespace_recovery_mixed = 2;

struct alignas(64) StoreHeaderV2 {
    std::uint32_t Magic{};
    std::uint16_t LayoutMajorVersion{};
    std::uint16_t LayoutMinorVersion{};
    std::int32_t HeaderLength{};
    std::int32_t ResourceProtocolVersion{};
    std::uint64_t RequiredFeatures{};
    std::uint64_t OptionalFeatures{};
    std::int64_t TotalBytes{};
    std::uint64_t StoreId{};
    std::uint64_t Control{};
    std::uint64_t Sequence{};
    std::int32_t SlotCount{};
    std::int32_t LeaseRecordCount{};
    std::int32_t ParticipantRecordCount{};
    std::int32_t MaxKeyBytes{};
    std::int32_t MaxDescriptorBytes{};
    std::int32_t MaxValueBytes{};
    std::int32_t ParticipantIndexBits{};
    std::int32_t ParticipantGenerationBits{};
    std::int64_t ParticipantOffset{};
    std::int64_t ParticipantLength{};
    std::int32_t ParticipantStride{};
    std::int32_t PrimaryLaneCount{};
    std::int32_t PrimaryBucketCount{};
    std::int32_t PrimaryBucketStride{};
    std::int64_t PrimaryDirectoryOffset{};
    std::int64_t PrimaryDirectoryLength{};
    std::int64_t OverflowDirectoryOffset{};
    std::int64_t OverflowDirectoryLength{};
    std::int32_t OverflowStride{};
    std::int32_t LeaseStride{};
    std::int64_t LeaseRegistryOffset{};
    std::int64_t LeaseRegistryLength{};
    std::int32_t SlotMetadataStride{};
    std::int32_t KeyStride{};
    std::int64_t SlotMetadataOffset{};
    std::int64_t SlotMetadataLength{};
    std::int64_t KeyStorageOffset{};
    std::int64_t KeyStorageLength{};
    std::int32_t DescriptorStride{};
    std::int32_t PayloadStride{};
    std::int64_t DescriptorStorageOffset{};
    std::int64_t DescriptorStorageLength{};
    std::int64_t PayloadStorageOffset{};
    std::int64_t PayloadStorageLength{};
    std::uint64_t PidNamespaceId{};
    std::uint64_t PidNamespaceMode{};
    std::byte ReservedBytes[232]{};
};

struct ParticipantRecordV2 {
    std::uint64_t Control{};
    std::int32_t IdentityKind{};
    std::int32_t Reserved{};
    std::int64_t ProcessStartValue{};
    std::int64_t OpenSequence{};
    std::uint64_t PidNamespaceId{};
    std::byte ReservedBytes[24]{};
};

struct PrimaryDirectoryBucketV2 {
    std::uint64_t SpillSummary{};
    std::uint64_t Mutation{};
    std::uint64_t Lanes[sms2_primary_lanes_per_bucket]{};
    std::byte ReservedBytes[48]{};
};

struct LeaseRecordV2 {
    std::uint64_t Control{};
    std::uint64_t SlotBinding{};
    std::int64_t AcquireSequence{};
    std::byte ReservedBytes[40]{};
};

struct ValueSlotMetadataV2 {
    std::uint64_t Control{};
    std::uint64_t DirectoryBinding{};
    std::uint64_t DirectoryLocation{};
    std::uint64_t DirectoryOperation{};
    std::uint64_t KeyHash{};
    std::int32_t KeyLength{};
    std::int32_t DescriptorLength{};
    std::int32_t ValueLength{};
    std::int32_t PublicationIntent{};
    std::uint64_t BytesAdvanced{};
    std::int64_t CommitSequence{};
    std::int64_t KeyOffset{};
    std::int64_t DescriptorOffset{};
    std::int64_t PayloadOffset{};
    std::byte ReservedBytes[32]{};
};

static_assert(std::is_standard_layout_v<StoreHeaderV2>);
static_assert(sizeof(StoreHeaderV2) == sms2_header_length);
static_assert(alignof(StoreHeaderV2) == sms2_cache_line_size);
static_assert(offsetof(StoreHeaderV2, RequiredFeatures) == 16);
static_assert(offsetof(StoreHeaderV2, ParticipantOffset) == 96);
static_assert(offsetof(StoreHeaderV2, PrimaryDirectoryOffset) == 128);
static_assert(offsetof(StoreHeaderV2, LeaseRegistryOffset) == 168);
static_assert(offsetof(StoreHeaderV2, SlotMetadataOffset) == 192);
static_assert(offsetof(StoreHeaderV2, DescriptorStorageOffset) == 232);
static_assert(offsetof(StoreHeaderV2, PidNamespaceId) == 264);
static_assert(offsetof(StoreHeaderV2, PidNamespaceMode) == 272);

static_assert(std::is_standard_layout_v<ParticipantRecordV2>);
static_assert(sizeof(ParticipantRecordV2) == sms2_participant_stride);
static_assert(alignof(ParticipantRecordV2) >= sms2_atomic_alignment);
static_assert(offsetof(ParticipantRecordV2, PidNamespaceId) == 32);

static_assert(std::is_standard_layout_v<PrimaryDirectoryBucketV2>);
static_assert(sizeof(PrimaryDirectoryBucketV2) == sms2_primary_bucket_stride);
static_assert(alignof(PrimaryDirectoryBucketV2) >= sms2_atomic_alignment);
static_assert(offsetof(PrimaryDirectoryBucketV2, Lanes) == 16);

static_assert(std::is_standard_layout_v<LeaseRecordV2>);
static_assert(sizeof(LeaseRecordV2) == sms2_lease_stride);
static_assert(alignof(LeaseRecordV2) >= sms2_atomic_alignment);
static_assert(offsetof(LeaseRecordV2, AcquireSequence) == 16);

static_assert(std::is_standard_layout_v<ValueSlotMetadataV2>);
static_assert(sizeof(ValueSlotMetadataV2) == sms2_slot_metadata_stride);
static_assert(alignof(ValueSlotMetadataV2) >= sms2_atomic_alignment);
static_assert(offsetof(ValueSlotMetadataV2, PublicationIntent) == 52);
static_assert(offsetof(ValueSlotMetadataV2, BytesAdvanced) == 56);
static_assert(offsetof(ValueSlotMetadataV2, PayloadOffset) == 88);

struct LayoutV2 {
    std::int64_t total_bytes{};
    std::int32_t slot_count{};
    std::int32_t lease_record_count{};
    std::int32_t participant_record_count{};
    std::int32_t max_value_bytes{};
    std::int32_t max_descriptor_bytes{};
    std::int32_t max_key_bytes{};
    std::int32_t header_length{};

    std::int32_t participant_index_bits{};
    std::int32_t participant_generation_bits{};
    std::int32_t participant_index_mask{};
    std::int32_t participant_generation_mask{};
    std::int32_t participant_stride{};
    std::int64_t participant_offset{};
    std::int64_t participant_length{};

    std::int32_t primary_lane_count{};
    std::int32_t primary_bucket_count{};
    std::int32_t primary_bucket_stride{};
    std::int64_t primary_directory_offset{};
    std::int64_t primary_directory_length{};

    std::int32_t overflow_stride{};
    std::int64_t overflow_directory_offset{};
    std::int64_t overflow_directory_length{};

    std::int32_t lease_stride{};
    std::int64_t lease_registry_offset{};
    std::int64_t lease_registry_length{};

    std::int32_t slot_metadata_stride{};
    std::int64_t slot_metadata_offset{};
    std::int64_t slot_metadata_length{};

    std::int32_t key_stride{};
    std::int64_t key_storage_offset{};
    std::int64_t key_storage_length{};

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
        std::int32_t participant_record_count,
        LayoutV2& result) noexcept;

    [[nodiscard]] bool fits_within_total_bytes() const noexcept;
    [[nodiscard]] bool matches(const StoreHeaderV2& header) const noexcept;
    [[nodiscard]] bool bounds_valid(const StoreHeaderV2& header) const noexcept;
};

} // namespace sms::detail
