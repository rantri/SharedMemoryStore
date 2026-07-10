#include "shared_memory_store/c_api.h"
#include "test_support.hpp"

#include <array>
#include <cstring>

int main() {
    SMS_CHECK(sms_abi_version() == SMS_C_ABI_VERSION);
    sms_protocol_info protocol{};
    protocol.struct_size = sizeof(protocol);
    protocol.abi_version = SMS_C_ABI_VERSION;
    SMS_CHECK(sms_get_protocol_info(&protocol) == SMS_STATUS_SUCCESS);
    SMS_CHECK(protocol.store_header_size == 160);
    SMS_CHECK(protocol.slot_metadata_size == 72);
    SMS_CHECK(sms_get_protocol_info(nullptr) == SMS_STATUS_UNKNOWN_FAILURE);
    std::uint32_t field_offset{};
    SMS_CHECK(sms_get_layout_field_offset(SMS_LAYOUT_FIELD_HEADER_SEQUENCE, &field_offset) == SMS_STATUS_SUCCESS);
    SMS_CHECK(field_offset == 152);
    SMS_CHECK(sms_get_layout_field_offset(SMS_LAYOUT_FIELD_SLOT_KEY_HASH, &field_offset) == SMS_STATUS_SUCCESS);
    SMS_CHECK(field_offset == 40);

    std::int64_t required{};
    SMS_CHECK(sms_calculate_required_bytes(2, 64, 8, 16, 4, &required) == SMS_OPEN_SUCCESS);
    SMS_CHECK(required > 0);
    const auto name = sms_test_name("c-api");
    sms_store_options options{};
    options.struct_size = sizeof(options);
    options.abi_version = SMS_C_ABI_VERSION;
    options.name_utf8 = name.data();
    options.name_length = name.size();
    options.open_mode = SMS_OPEN_MODE_CREATE_NEW;
    options.total_bytes = required;
    options.slot_count = 2;
    options.max_value_bytes = 64;
    options.max_descriptor_bytes = 8;
    options.max_key_bytes = 16;
    options.lease_record_count = 4;
    options.enable_lease_recovery = 1;
    sms_wait_options wait{sizeof(wait), SMS_C_ABI_VERSION, 1000};
    sms_store* store{};
    SMS_CHECK(sms_open_store(&options, &wait, &store) == SMS_OPEN_SUCCESS);
    SMS_CHECK(store != nullptr);
    sms_store_layout layout{};
    layout.struct_size = sizeof(layout);
    layout.abi_version = SMS_C_ABI_VERSION;
    SMS_CHECK(sms_get_store_layout(store, &wait, &layout) == SMS_STATUS_SUCCESS);
    SMS_CHECK(layout.total_bytes == required);
    SMS_CHECK(layout.slot_count == 2);
    const std::array<std::uint8_t, 2> key{1, 0};
    const std::array<std::uint8_t, 3> value{2, 0, 3};
    SMS_CHECK(sms_publish(store, {key.data(), key.size()}, {value.data(), value.size()}, {}, &wait) == SMS_STATUS_SUCCESS);
    sms_lease* lease{};
    SMS_CHECK(sms_acquire(store, {key.data(), key.size()}, &wait, &lease) == SMS_STATUS_SUCCESS);
    SMS_CHECK(sms_lease_is_valid(lease) == 1);
    const auto view = sms_lease_value(lease);
    SMS_CHECK(view.length == value.size());
    SMS_CHECK(std::memcmp(view.data, value.data(), value.size()) == 0);
    SMS_CHECK(sms_release_lease(lease, &wait) == SMS_STATUS_SUCCESS);
    SMS_CHECK(sms_release_lease(lease, &wait) == SMS_STATUS_LEASE_ALREADY_RELEASED);
    sms_destroy_lease(lease);
    sms_close_store(store);
    SMS_CHECK(sms_publish(nullptr, {}, {}, {}, &wait) == SMS_STATUS_STORE_DISPOSED);
    return 0;
}
