#include "internal.hpp"
#include "test_support.hpp"

#include <array>
#include <cstddef>
#include <limits>
#include <span>
#include <string_view>
#include <utility>

static_assert(!noexcept(sms::detail::sha256(
    std::span<const std::uint8_t>{})));
static_assert(!noexcept(sms::detail::make_resource_name(
    std::string_view{},
    std::declval<sms::detail::ResourceName&>())));

int main() {
    using namespace sms::detail;
    std::int64_t arithmetic{};
    SMS_CHECK(checked_add_nonnegative(5, 7, arithmetic) && arithmetic == 12);
    SMS_CHECK(!checked_add_nonnegative(-1, 7, arithmetic));
    SMS_CHECK(!checked_add_nonnegative(
        std::numeric_limits<std::int64_t>::max(), 1, arithmetic));
    SMS_CHECK(checked_multiply_nonnegative(5, 7, arithmetic) && arithmetic == 35);
    SMS_CHECK(!checked_multiply_nonnegative(
        std::numeric_limits<std::int64_t>::max(), 2, arithmetic));
    SMS_CHECK(checked_align_up_nonnegative(65, 64, arithmetic) && arithmetic == 128);
    SMS_CHECK(!checked_align_up_nonnegative(65, 3, arithmetic));

    constexpr std::array<std::uint8_t, 5> hello{'h', 'e', 'l', 'l', 'o'};
    SMS_CHECK(hash_key(hello) == 0xa430d84680aabd0bULL);
    constexpr std::array<std::uint8_t, 4> binary{0x00, 0x01, 0xff, 0x80};
    SMS_CHECK(hash_key(binary) == 0x4653dd7f9a76930dULL);
    constexpr std::array<std::uint8_t, 4> same_binary{0x00, 0x01, 0xff, 0x80};
    constexpr std::array<std::uint8_t, 4> other_binary{0x00, 0x01, 0xff, 0x81};
    SMS_CHECK(exact_bytes_equal(binary, same_binary));
    SMS_CHECK(!exact_bytes_equal(binary, other_binary));
    SMS_CHECK(!exact_bytes_equal(binary, hello));

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
    SMS_CHECK(!valid_utf8("\xED\xA0\x80"));
    SMS_CHECK(!valid_utf8("\xF4\x90\x80\x80"));
    return 0;
}
