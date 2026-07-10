#include "internal.hpp"
#include "test_support.hpp"

#include <array>
#include <cstddef>

int main() {
    using namespace sms::detail;
    Layout layout{};
    SMS_CHECK(Layout::calculate(0, 3, 17, 5, 9, 4, layout));
    SMS_CHECK(layout.header_length == 160);
    SMS_CHECK(layout.index_entry_count == 8);
    SMS_CHECK(layout.index_entry_size == 48);
    SMS_CHECK(layout.index_offset == 160);
    SMS_CHECK(layout.index_length == 384);
    SMS_CHECK(layout.lease_registry_offset == 544);
    SMS_CHECK(layout.slot_metadata_offset == 704);
    SMS_CHECK(layout.descriptor_storage_offset == 920);
    SMS_CHECK(layout.payload_storage_offset == 944);
    SMS_CHECK(layout.required_bytes == 1016);
    SMS_CHECK(!Layout::calculate(0, 0, 1, 0, 1, 1, layout));
    SMS_CHECK(!Layout::calculate(0, 1, 1, 0, INT32_MAX, 1, layout));

    constexpr std::array<std::uint8_t, 5> hello{'h', 'e', 'l', 'l', 'o'};
    SMS_CHECK(hash_key(hello) == 0xa430d84680aabd0bULL);
    constexpr std::array<std::uint8_t, 4> binary{0x00, 0x01, 0xff, 0x80};
    SMS_CHECK(hash_key(binary) == 0x4653dd7f9a76930dULL);

    ResourceName simple{};
    SMS_CHECK(make_resource_name("sms.compatibility", simple));
    SMS_CHECK(simple.fragment == "sms-sms.compatibility-251220bcba0b63e6");
#if defined(_WIN32)
    SMS_CHECK(simple.windows_region_name == L"sms.compatibility");
    SMS_CHECK(simple.windows_lock_name == L"Local\\SharedMemoryStore-sms_compatibility");
#endif
    ResourceName separator{};
    SMS_CHECK(make_resource_name("store/name", separator));
    SMS_CHECK(separator.fragment == "sms-store_name-549a0c43d4d76e02");
    ResourceName collision{};
    SMS_CHECK(make_resource_name("store?name", collision));
    SMS_CHECK(collision.fragment == "sms-store_name-0f08216b745495cd");
    ResourceName unicode{};
    SMS_CHECK(make_resource_name("caf\xC3\xA9/\xE5\x85\xB1\xE4\xBA\xAB/\xF0\x9F\x98\x80", unicode));
    SMS_CHECK(unicode.fragment == "sms-caf-0f903cf0f516f93b");
#if defined(_WIN32)
    SMS_CHECK(unicode.windows_lock_name == L"Local\\SharedMemoryStore-caf\u00e9_\u5171\u4eab___");
#endif
    SMS_CHECK(utf16_length("\xF0\x9F\x98\x80") == 2);
    SMS_CHECK(!valid_utf8("\xC0\x80"));
    return 0;
}
