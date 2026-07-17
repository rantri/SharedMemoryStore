#include "shared_memory_store/c_api.h"
#include "test_support.hpp"

#include <array>
#include <atomic>
#include <cstddef>
#include <cstring>
#include <cstdint>
#include <iostream>
#include <thread>
#include <type_traits>
#include <vector>

#define SMS_ABI_CHECK(expression) do { \
    if (!(expression)) { \
        std::cerr << __FILE__ << ':' << __LINE__ \
                  << ": check failed: " #expression << '\n'; \
        return 1; \
    } \
} while (false)

#ifdef SMS_RESOURCE_NAMING_VERSION
#  error "ABI 2 must not expose the retired SMS_RESOURCE_NAMING_VERSION macro."
#endif

template <class T>
concept has_v1_resource_naming_version = requires(T value) {
    value.resource_naming_version;
};

template <class T>
concept has_v1_index_entry_header_size = requires(T value) {
    value.index_entry_header_size;
};

template <class T>
concept has_v1_index_entry_count = requires(T value) {
    value.index_entry_count;
};

template <class T>
concept has_v1_index_entry_size = requires(T value) {
    value.index_entry_size;
};

template <class T>
concept has_v1_index_offset = requires(T value) {
    value.index_offset;
};

template <class T>
concept has_v1_index_length = requires(T value) {
    value.index_length;
};

template <class T>
concept has_tombstone_index_entries = requires(T value) {
    value.tombstone_index_entry_count;
};

template <class T>
concept has_index_compaction_count = requires(T value) {
    value.index_compaction_count;
};

static_assert(SMS_C_ABI_VERSION == 0x0002'0000u);
static_assert(SMS_LAYOUT_MAJOR_VERSION == 2);
static_assert(SMS_LAYOUT_MINOR_VERSION == 0);
static_assert(SMS_OPEN_PARTICIPANT_TABLE_FULL == 11);

static_assert(std::is_standard_layout_v<sms_wait_options>);
static_assert(sizeof(sms_wait_options) == 24);
static_assert(offsetof(sms_wait_options, struct_size) == 0);
static_assert(offsetof(sms_wait_options, abi_version) == 4);
static_assert(offsetof(sms_wait_options, timeout_milliseconds) == 8);
static_assert(offsetof(sms_wait_options, cancellation) == 16);

static_assert(std::is_standard_layout_v<sms_store_options>);
static_assert(sizeof(sms_store_options) == 72);
static_assert(offsetof(sms_store_options, struct_size) == 0);
static_assert(offsetof(sms_store_options, abi_version) == 4);
static_assert(offsetof(sms_store_options, name_utf8) == 8);
static_assert(offsetof(sms_store_options, name_length) == 16);
static_assert(offsetof(sms_store_options, open_mode) == 24);
static_assert(offsetof(sms_store_options, total_bytes) == 32);
static_assert(offsetof(sms_store_options, slot_count) == 40);
static_assert(offsetof(sms_store_options, max_value_bytes) == 44);
static_assert(offsetof(sms_store_options, max_descriptor_bytes) == 48);
static_assert(offsetof(sms_store_options, max_key_bytes) == 52);
static_assert(offsetof(sms_store_options, lease_record_count) == 56);
static_assert(offsetof(sms_store_options, participant_record_count) == 60);
static_assert(offsetof(sms_store_options, enable_lease_recovery) == 64);
static_assert(offsetof(sms_store_options, reserved) == 65);

static_assert(std::is_standard_layout_v<sms_protocol_info>);
static_assert(sizeof(sms_protocol_info) == 64);
static_assert(offsetof(sms_protocol_info, struct_size) == 0);
static_assert(offsetof(sms_protocol_info, abi_version) == 4);
static_assert(offsetof(sms_protocol_info, layout_major) == 8);
static_assert(offsetof(sms_protocol_info, layout_minor) == 12);
static_assert(offsetof(sms_protocol_info, resource_protocol) == 16);
static_assert(offsetof(sms_protocol_info, reserved) == 20);
static_assert(offsetof(sms_protocol_info, required_features) == 24);
static_assert(offsetof(sms_protocol_info, optional_features) == 32);
static_assert(offsetof(sms_protocol_info, store_header_size) == 40);
static_assert(offsetof(sms_protocol_info, participant_record_size) == 44);
static_assert(offsetof(sms_protocol_info, primary_directory_bucket_size) == 48);
static_assert(offsetof(sms_protocol_info, overflow_binding_size) == 52);
static_assert(offsetof(sms_protocol_info, lease_record_size) == 56);
static_assert(offsetof(sms_protocol_info, value_slot_size) == 60);
static_assert(!has_v1_resource_naming_version<sms_protocol_info>);
static_assert(!has_v1_index_entry_header_size<sms_protocol_info>);

static_assert(std::is_standard_layout_v<sms_store_layout>);
static_assert(sizeof(sms_store_layout) == 240);
static_assert(offsetof(sms_store_layout, struct_size) == 0);
static_assert(offsetof(sms_store_layout, abi_version) == 4);
static_assert(offsetof(sms_store_layout, total_bytes) == 8);
static_assert(offsetof(sms_store_layout, slot_count) == 16);
static_assert(offsetof(sms_store_layout, lease_record_count) == 20);
static_assert(offsetof(sms_store_layout, participant_record_count) == 24);
static_assert(offsetof(sms_store_layout, max_value_bytes) == 28);
static_assert(offsetof(sms_store_layout, max_descriptor_bytes) == 32);
static_assert(offsetof(sms_store_layout, max_key_bytes) == 36);
static_assert(offsetof(sms_store_layout, header_length) == 40);
static_assert(offsetof(sms_store_layout, participant_index_bits) == 44);
static_assert(offsetof(sms_store_layout, participant_generation_bits) == 48);
static_assert(offsetof(sms_store_layout, participant_stride) == 52);
static_assert(offsetof(sms_store_layout, participant_offset) == 56);
static_assert(offsetof(sms_store_layout, participant_length) == 64);
static_assert(offsetof(sms_store_layout, primary_lane_count) == 72);
static_assert(offsetof(sms_store_layout, primary_bucket_count) == 76);
static_assert(offsetof(sms_store_layout, primary_bucket_stride) == 80);
static_assert(offsetof(sms_store_layout, primary_directory_offset) == 88);
static_assert(offsetof(sms_store_layout, primary_directory_length) == 96);
static_assert(offsetof(sms_store_layout, overflow_stride) == 104);
static_assert(offsetof(sms_store_layout, overflow_directory_offset) == 112);
static_assert(offsetof(sms_store_layout, overflow_directory_length) == 120);
static_assert(offsetof(sms_store_layout, lease_stride) == 128);
static_assert(offsetof(sms_store_layout, lease_registry_offset) == 136);
static_assert(offsetof(sms_store_layout, lease_registry_length) == 144);
static_assert(offsetof(sms_store_layout, slot_metadata_stride) == 152);
static_assert(offsetof(sms_store_layout, key_stride) == 156);
static_assert(offsetof(sms_store_layout, slot_metadata_offset) == 160);
static_assert(offsetof(sms_store_layout, slot_metadata_length) == 168);
static_assert(offsetof(sms_store_layout, key_storage_offset) == 176);
static_assert(offsetof(sms_store_layout, key_storage_length) == 184);
static_assert(offsetof(sms_store_layout, descriptor_stride) == 192);
static_assert(offsetof(sms_store_layout, payload_stride) == 196);
static_assert(offsetof(sms_store_layout, descriptor_storage_offset) == 200);
static_assert(offsetof(sms_store_layout, descriptor_storage_length) == 208);
static_assert(offsetof(sms_store_layout, payload_storage_offset) == 216);
static_assert(offsetof(sms_store_layout, payload_storage_length) == 224);
static_assert(offsetof(sms_store_layout, required_bytes) == 232);
static_assert(!has_v1_index_entry_count<sms_store_layout>);
static_assert(!has_v1_index_entry_size<sms_store_layout>);
static_assert(!has_v1_index_offset<sms_store_layout>);
static_assert(!has_v1_index_length<sms_store_layout>);

static_assert(std::is_standard_layout_v<sms_diagnostics>);
static_assert(sizeof(sms_diagnostics) == 560);
static_assert(offsetof(sms_diagnostics, layout_major) == 8);
static_assert(offsetof(sms_diagnostics, required_features) == 24);
static_assert(offsetof(sms_diagnostics, total_bytes) == 40);
static_assert(offsetof(sms_diagnostics, participant_record_count) == 104);
static_assert(offsetof(sms_diagnostics, failure_counts) == 376);
static_assert(!has_tombstone_index_entries<sms_diagnostics>);
static_assert(!has_index_compaction_count<sms_diagnostics>);

namespace {

struct expected_field {
    std::int32_t id;
    std::uint32_t offset;
};

constexpr std::array header_fields{
    expected_field{0, 0}, expected_field{1, 4}, expected_field{2, 6},
    expected_field{3, 8}, expected_field{4, 12}, expected_field{5, 16},
    expected_field{6, 24}, expected_field{7, 32}, expected_field{8, 40},
    expected_field{9, 48}, expected_field{10, 56}, expected_field{11, 64},
    expected_field{12, 68}, expected_field{13, 72}, expected_field{14, 76},
    expected_field{15, 80}, expected_field{16, 84}, expected_field{17, 88},
    expected_field{18, 92}, expected_field{19, 96}, expected_field{20, 104},
    expected_field{21, 112}, expected_field{22, 116}, expected_field{23, 120},
    expected_field{24, 124}, expected_field{25, 128}, expected_field{26, 136},
    expected_field{27, 144}, expected_field{28, 152}, expected_field{29, 160},
    expected_field{30, 164}, expected_field{31, 168}, expected_field{32, 176},
    expected_field{33, 184}, expected_field{34, 188}, expected_field{35, 192},
    expected_field{36, 200}, expected_field{37, 208}, expected_field{38, 216},
    expected_field{39, 224}, expected_field{40, 228}, expected_field{41, 232},
    expected_field{42, 240}, expected_field{43, 248}, expected_field{44, 256},
    expected_field{45, 264}, expected_field{46, 272},
};

constexpr std::array participant_fields{
    expected_field{100, 0}, expected_field{101, 8}, expected_field{102, 12},
    expected_field{103, 16}, expected_field{104, 24}, expected_field{105, 32},
};

constexpr std::array primary_fields{
    expected_field{200, 0}, expected_field{201, 8}, expected_field{202, 16},
};

constexpr std::array overflow_fields{expected_field{300, 0}};

constexpr std::array lease_fields{
    expected_field{400, 0}, expected_field{401, 8}, expected_field{402, 16},
};

constexpr std::array slot_fields{
    expected_field{500, 0}, expected_field{501, 8}, expected_field{502, 16},
    expected_field{503, 24}, expected_field{504, 32}, expected_field{505, 40},
    expected_field{506, 44}, expected_field{507, 48}, expected_field{508, 52},
    expected_field{509, 56}, expected_field{510, 64}, expected_field{511, 72},
    expected_field{512, 80}, expected_field{513, 88},
};

template <std::size_t Size>
bool offsets_match(const std::array<expected_field, Size>& expected) {
    for (const auto [id, expected_offset] : expected) {
        std::uint32_t actual_offset{};
        if (sms_get_layout_field_offset(
                static_cast<sms_layout_field>(id), &actual_offset) != SMS_STATUS_SUCCESS ||
            actual_offset != expected_offset) {
            return false;
        }
    }
    return true;
}

} // namespace

int main() {
    SMS_ABI_CHECK(sms_abi_version() == SMS_C_ABI_VERSION);

    sms_protocol_info protocol{};
    protocol.struct_size = sizeof(protocol);
    protocol.abi_version = SMS_C_ABI_VERSION;
    SMS_ABI_CHECK(sms_get_protocol_info(&protocol) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(protocol.layout_major == 2);
    SMS_ABI_CHECK(protocol.layout_minor == 0);
    SMS_ABI_CHECK(protocol.resource_protocol == 2);
    SMS_ABI_CHECK(protocol.required_features == 7);
    SMS_ABI_CHECK(protocol.optional_features == 0);
    SMS_ABI_CHECK(protocol.store_header_size == 512);
    SMS_ABI_CHECK(protocol.participant_record_size == 64);
    SMS_ABI_CHECK(protocol.primary_directory_bucket_size == 128);
    SMS_ABI_CHECK(protocol.overflow_binding_size == 8);
    SMS_ABI_CHECK(protocol.lease_record_size == 64);
    SMS_ABI_CHECK(protocol.value_slot_size == 128);

    SMS_ABI_CHECK(offsets_match(header_fields));
    SMS_ABI_CHECK(offsets_match(participant_fields));
    SMS_ABI_CHECK(offsets_match(primary_fields));
    SMS_ABI_CHECK(offsets_match(overflow_fields));
    SMS_ABI_CHECK(offsets_match(lease_fields));
    SMS_ABI_CHECK(offsets_match(slot_fields));

    std::int64_t required_bytes{};
    SMS_ABI_CHECK(sms_calculate_required_bytes(
        3, 17, 5, 9, 4, 4, &required_bytes) == SMS_OPEN_SUCCESS);
    SMS_ABI_CHECK(required_bytes == 2128);
    const auto representative_required_bytes = required_bytes;
    SMS_ABI_CHECK(sms_calculate_required_bytes(
        3, 17, 5, 9, 4, 0, &required_bytes) == SMS_OPEN_INVALID_OPTIONS);

    const auto name = sms_test_name("c-api-abi2");
    sms_store_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = SMS_C_ABI_VERSION;
    options.name_utf8 = name.data();
    options.name_length = name.size();
    options.open_mode = SMS_OPEN_MODE_CREATE_NEW;
    options.total_bytes = representative_required_bytes;
    options.slot_count = 3;
    options.max_value_bytes = 17;
    options.max_descriptor_bytes = 5;
    options.max_key_bytes = 9;
    options.lease_record_count = 4;
    options.participant_record_count = 4;
    options.enable_lease_recovery = 1;
    sms_wait_options wait{sizeof(wait), SMS_C_ABI_VERSION, 1000, nullptr};
    sms_store* store{};
    SMS_ABI_CHECK(sms_open_store(&options, &wait, &store) == SMS_OPEN_SUCCESS);
    SMS_ABI_CHECK(store != nullptr);

    sms_store_layout layout{};
    layout.struct_size = sizeof(layout);
    layout.abi_version = SMS_C_ABI_VERSION;
    SMS_ABI_CHECK(sms_get_store_layout(store, &wait, &layout) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(layout.total_bytes == representative_required_bytes);
    SMS_ABI_CHECK(layout.required_bytes == representative_required_bytes);
    SMS_ABI_CHECK(layout.slot_count == 3);
    SMS_ABI_CHECK(layout.lease_record_count == 4);
    SMS_ABI_CHECK(layout.participant_record_count == 4);
    SMS_ABI_CHECK(layout.header_length == SMS_STORE_HEADER_SIZE);
    SMS_ABI_CHECK(layout.participant_offset == SMS_STORE_HEADER_SIZE);

    constexpr std::array<std::uint8_t, 2> key{1, 0};
    constexpr std::array<std::uint8_t, 3> value{2, 0, 3};
    SMS_ABI_CHECK(sms_publish(
        store, {key.data(), key.size()}, {value.data(), value.size()}, {}, &wait) ==
        SMS_STATUS_SUCCESS);
    sms_lease* lease{};
    SMS_ABI_CHECK(sms_acquire(
        store, {key.data(), key.size()}, &wait, &lease) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(lease != nullptr && sms_lease_is_valid(lease) == 1);
    const auto view = sms_lease_value(lease);
    SMS_ABI_CHECK(view.length == value.size());
    SMS_ABI_CHECK(std::memcmp(view.data, value.data(), value.size()) == 0);
    SMS_ABI_CHECK(sms_release_lease(lease, &wait) == SMS_STATUS_SUCCESS);
    sms_destroy_lease(lease);

    sms_diagnostics diagnostics{};
    diagnostics.struct_size = sizeof(diagnostics);
    diagnostics.abi_version = SMS_C_ABI_VERSION;
    SMS_ABI_CHECK(sms_get_diagnostics(store, &wait, &diagnostics) ==
                  SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(diagnostics.layout_major == 2);
    SMS_ABI_CHECK(diagnostics.layout_minor == 0);
    SMS_ABI_CHECK(diagnostics.resource_protocol == 2);
    SMS_ABI_CHECK(diagnostics.required_features == 7);
    SMS_ABI_CHECK(diagnostics.optional_features == 0);
    SMS_ABI_CHECK(diagnostics.slot_count == 3);
    SMS_ABI_CHECK(diagnostics.published_slot_count == 1);
    SMS_ABI_CHECK(diagnostics.free_slot_count == 2);
    SMS_ABI_CHECK(diagnostics.active_participant_count == 1);
    SMS_ABI_CHECK(diagnostics.participant_record_count == 4);
    SMS_ABI_CHECK(diagnostics.active_lease_count == 0);

    sms_cancellation* cancellation{};
    SMS_ABI_CHECK(sms_create_cancellation(&cancellation) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(cancellation != nullptr);
    SMS_ABI_CHECK(sms_cancellation_is_signaled(cancellation) == 0);
    SMS_ABI_CHECK(sms_signal_cancellation(cancellation) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(sms_signal_cancellation(cancellation) == SMS_STATUS_SUCCESS);
    SMS_ABI_CHECK(sms_cancellation_is_signaled(cancellation) == 1);
    sms_store_layout canceled_layout{};
    canceled_layout.struct_size = sizeof(canceled_layout);
    canceled_layout.abi_version = SMS_C_ABI_VERSION;
    const sms_wait_options canceled_wait{
        sizeof(canceled_wait), SMS_C_ABI_VERSION, SMS_WAIT_INFINITE, cancellation};
    SMS_ABI_CHECK(sms_get_store_layout(store, &canceled_wait, &canceled_layout) ==
                  SMS_STATUS_OPERATION_CANCELED);
    sms_destroy_cancellation(cancellation);

    std::atomic<bool> start_close_race{};
    std::atomic<bool> stop_workers{};
    std::atomic<std::int32_t> entered_calls{};
    std::atomic<std::int32_t> unexpected_statuses{};
    std::vector<std::thread> workers;
    for (int index = 0; index < 8; ++index) {
        workers.emplace_back([&] {
            while (!start_close_race.load(std::memory_order_acquire)) {
                std::this_thread::yield();
            }
            while (!stop_workers.load(std::memory_order_acquire)) {
                sms_diagnostics snapshot{};
                snapshot.struct_size = sizeof(snapshot);
                snapshot.abi_version = SMS_C_ABI_VERSION;
                entered_calls.fetch_add(1, std::memory_order_release);
                const auto status = sms_get_diagnostics(store, &wait, &snapshot);
                if (status == SMS_STATUS_STORE_DISPOSED) break;
                if (status != SMS_STATUS_SUCCESS) {
                    unexpected_statuses.fetch_add(1, std::memory_order_relaxed);
                    break;
                }
            }
        });
    }
    start_close_race.store(true, std::memory_order_release);
    while (entered_calls.load(std::memory_order_acquire) < 8) {
        std::this_thread::yield();
    }
    std::thread first_close([&] { sms_close_store(store); });
    std::thread second_close([&] { sms_close_store(store); });
    first_close.join();
    second_close.join();
    stop_workers.store(true, std::memory_order_release);
    for (auto& worker : workers) worker.join();
    SMS_ABI_CHECK(unexpected_statuses.load(std::memory_order_acquire) == 0);

    sms_diagnostics after_close{};
    after_close.struct_size = sizeof(after_close);
    after_close.abi_version = SMS_C_ABI_VERSION;
    SMS_ABI_CHECK(sms_get_diagnostics(store, &wait, &after_close) ==
                  SMS_STATUS_STORE_DISPOSED);
    sms_close_store(store);
    sms_destroy_store(store);

    // Repeated logical close plus caller-synchronized destroy releases every
    // outer handle and physical resource; the same create-new name can churn.
    for (int iteration = 0; iteration < 64; ++iteration) {
        sms_store* reopened{};
        SMS_ABI_CHECK(sms_open_store(&options, &wait, &reopened) ==
                      SMS_OPEN_SUCCESS);
        SMS_ABI_CHECK(reopened != nullptr);
        sms_close_store(reopened);
        sms_close_store(reopened);
        sms_destroy_store(reopened);
    }
    return 0;
}
