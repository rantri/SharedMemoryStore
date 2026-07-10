#include "internal.hpp"

#include <algorithm>
#include <array>
#include <filesystem>
#include <iomanip>
#include <sstream>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <unistd.h>
#endif

namespace sms::detail {
namespace {

constexpr std::uint64_t fnv_offset = 14695981039346656037ULL;
constexpr std::uint64_t fnv_prime = 1099511628211ULL;

bool checked_add(std::int64_t a, std::int64_t b, std::int64_t& result) noexcept {
    if (a < 0 || b < 0 || a > std::numeric_limits<std::int64_t>::max() - b) {
        return false;
    }
    result = a + b;
    return true;
}

bool checked_mul(std::int64_t a, std::int64_t b, std::int64_t& result) noexcept {
    if (a < 0 || b < 0 || (a != 0 && b > std::numeric_limits<std::int64_t>::max() / a)) {
        return false;
    }
    result = a * b;
    return true;
}

bool align8(std::int64_t value, std::int64_t& result) noexcept {
    std::int64_t expanded{};
    if (!checked_add(value, alignment - 1, expanded)) {
        return false;
    }
    result = expanded & ~static_cast<std::int64_t>(alignment - 1);
    return true;
}

bool next_power_of_two(std::int64_t value, std::int32_t& result) noexcept {
    if (value <= 0 || value > (1LL << 30)) {
        return false;
    }
    std::int32_t current = 1;
    while (current < value) {
        current <<= 1;
    }
    result = current;
    return true;
}

bool next_utf8(std::string_view text, std::size_t& index, std::uint32_t& cp) noexcept {
    if (index >= text.size()) {
        return false;
    }
    const auto first = static_cast<std::uint8_t>(text[index++]);
    if (first < 0x80) {
        cp = first;
        return true;
    }
    int continuation{};
    std::uint32_t value{};
    std::uint32_t minimum{};
    if ((first & 0xE0) == 0xC0) {
        continuation = 1;
        value = first & 0x1F;
        minimum = 0x80;
    } else if ((first & 0xF0) == 0xE0) {
        continuation = 2;
        value = first & 0x0F;
        minimum = 0x800;
    } else if ((first & 0xF8) == 0xF0) {
        continuation = 3;
        value = first & 0x07;
        minimum = 0x10000;
    } else {
        return false;
    }
    if (index + static_cast<std::size_t>(continuation) > text.size()) {
        return false;
    }
    for (int i = 0; i < continuation; ++i) {
        const auto part = static_cast<std::uint8_t>(text[index++]);
        if ((part & 0xC0) != 0x80) {
            return false;
        }
        value = (value << 6) | (part & 0x3F);
    }
    if (value < minimum || value > 0x10FFFF || (value >= 0xD800 && value <= 0xDFFF)) {
        return false;
    }
    cp = value;
    return true;
}

std::string lowercase_hex(std::span<const std::uint8_t> bytes) {
    std::ostringstream output;
    output << std::hex << std::setfill('0');
    for (auto byte : bytes) {
        output << std::setw(2) << static_cast<unsigned>(byte);
    }
    return output.str();
}

constexpr std::array<std::uint32_t, 64> sha_k{
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
};

constexpr std::uint32_t rotr(std::uint32_t value, int bits) noexcept {
    return (value >> bits) | (value << (32 - bits));
}

#if defined(_WIN32)
bool utf8_to_wide(std::string_view value, std::wstring& result) noexcept {
    if (value.empty()) {
        result.clear();
        return true;
    }
    if (value.size() > static_cast<std::size_t>(std::numeric_limits<int>::max())) {
        return false;
    }
    const auto needed = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                                            static_cast<int>(value.size()), nullptr, 0);
    if (needed <= 0) {
        return false;
    }
    result.resize(static_cast<std::size_t>(needed));
    return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, value.data(),
                               static_cast<int>(value.size()), result.data(), needed) == needed;
}

bool starts_global(const std::wstring& value) noexcept {
    constexpr wchar_t prefix[] = L"Global\\";
    if (value.size() < 7) {
        return false;
    }
    return CompareStringOrdinal(value.data(), 7, prefix, 7, TRUE) == CSTR_EQUAL;
}
#endif

} // namespace

bool Layout::calculate(std::int64_t total, std::int32_t slots, std::int32_t max_value,
                       std::int32_t max_descriptor, std::int32_t max_key,
                       std::int32_t leases, Layout& result) noexcept {
    if (slots <= 0 || max_value <= 0 || max_descriptor < 0 || max_key <= 0 || leases <= 0) {
        return false;
    }
    Layout value{};
    value.total_bytes = total;
    value.slot_count = slots;
    value.lease_record_count = leases;
    value.max_value_bytes = max_value;
    value.max_descriptor_bytes = max_descriptor;
    value.max_key_bytes = max_key;

    std::int64_t temp{};
    if (!align8(sizeof(StoreHeader), temp) || temp > std::numeric_limits<std::int32_t>::max()) return false;
    value.header_length = static_cast<std::int32_t>(temp);
    if (!next_power_of_two(std::max<std::int64_t>(4, static_cast<std::int64_t>(slots) * 2), value.index_entry_count)) return false;
    if (!checked_add(sizeof(IndexEntryHeader), max_key, temp) || !align8(temp, temp) ||
        temp > std::numeric_limits<std::int32_t>::max()) return false;
    value.index_entry_size = static_cast<std::int32_t>(temp);
    value.index_offset = value.header_length;
    if (!checked_mul(value.index_entry_count, value.index_entry_size, value.index_length) ||
        !checked_add(value.index_offset, value.index_length, temp) || !align8(temp, value.lease_registry_offset) ||
        !checked_mul(leases, sizeof(LeaseRecord), value.lease_registry_length) ||
        !checked_add(value.lease_registry_offset, value.lease_registry_length, temp) || !align8(temp, value.slot_metadata_offset) ||
        !checked_mul(slots, sizeof(SlotMetadata), value.slot_metadata_length) ||
        !align8(std::max<std::int64_t>(1, max_descriptor), temp) || temp > std::numeric_limits<std::int32_t>::max()) return false;
    value.descriptor_stride = static_cast<std::int32_t>(temp);
    if (!checked_add(value.slot_metadata_offset, value.slot_metadata_length, temp) || !align8(temp, value.descriptor_storage_offset) ||
        !checked_mul(slots, value.descriptor_stride, value.descriptor_storage_length) ||
        !align8(std::max<std::int64_t>(1, max_value), temp) || temp > std::numeric_limits<std::int32_t>::max()) return false;
    value.payload_stride = static_cast<std::int32_t>(temp);
    if (!checked_add(value.descriptor_storage_offset, value.descriptor_storage_length, temp) || !align8(temp, value.payload_storage_offset) ||
        !checked_mul(slots, value.payload_stride, value.payload_storage_length) ||
        !checked_add(value.payload_storage_offset, value.payload_storage_length, temp) || !align8(temp, value.required_bytes)) return false;
    result = value;
    return true;
}

bool Layout::matches(const StoreHeader& h) const noexcept {
    return h.HeaderLength == header_length && h.TotalBytes == total_bytes && h.SlotCount == slot_count &&
        h.LeaseRecordCount == lease_record_count && h.MaxKeyBytes == max_key_bytes &&
        h.MaxDescriptorBytes == max_descriptor_bytes && h.MaxValueBytes == max_value_bytes &&
        h.IndexEntryCount == index_entry_count && h.IndexEntrySize == index_entry_size &&
        h.IndexOffset == index_offset && h.IndexLength == index_length &&
        h.LeaseRegistryOffset == lease_registry_offset && h.LeaseRegistryLength == lease_registry_length &&
        h.SlotMetadataOffset == slot_metadata_offset && h.SlotMetadataLength == slot_metadata_length &&
        h.DescriptorStorageOffset == descriptor_storage_offset && h.DescriptorStorageLength == descriptor_storage_length &&
        h.PayloadStorageOffset == payload_storage_offset && h.PayloadStorageLength == payload_storage_length;
}

bool Layout::bounds_valid(const StoreHeader& h) const noexcept {
    auto within = [&](std::int64_t offset, std::int64_t length, std::int64_t minimum) {
        std::int64_t end{};
        return offset >= minimum && checked_add(offset, length, end) && end <= h.TotalBytes;
    };
    std::int64_t index_end{}, lease_end{}, slot_end{}, descriptor_end{};
    return within(h.IndexOffset, h.IndexLength, h.HeaderLength) &&
        checked_add(h.IndexOffset, h.IndexLength, index_end) &&
        within(h.LeaseRegistryOffset, h.LeaseRegistryLength, index_end) &&
        checked_add(h.LeaseRegistryOffset, h.LeaseRegistryLength, lease_end) &&
        within(h.SlotMetadataOffset, h.SlotMetadataLength, lease_end) &&
        checked_add(h.SlotMetadataOffset, h.SlotMetadataLength, slot_end) &&
        within(h.DescriptorStorageOffset, h.DescriptorStorageLength, slot_end) &&
        checked_add(h.DescriptorStorageOffset, h.DescriptorStorageLength, descriptor_end) &&
        within(h.PayloadStorageOffset, h.PayloadStorageLength, descriptor_end);
}

bool LifecycleId::advance(LifecycleId& next) const noexcept {
    if (generation == std::numeric_limits<std::int32_t>::max()) {
        if (reuse_epoch == std::numeric_limits<std::int64_t>::max()) return false;
        next = {1, reuse_epoch + 1};
    } else {
        next = {generation + 1, reuse_epoch};
    }
    return true;
}

std::uint64_t hash_key(std::span<const std::uint8_t> key) noexcept {
    auto hash = fnv_offset;
    for (auto byte : key) {
        hash ^= byte;
        hash *= fnv_prime;
    }
    return hash;
}

std::array<std::uint8_t, 32> sha256(std::span<const std::uint8_t> input) noexcept {
    std::array<std::uint32_t, 8> state{0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a,
                                      0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19};
    const auto total = input.size() + 1 + 8;
    const auto padded = ((total + 63) / 64) * 64;
    std::vector<std::uint8_t> bytes(padded, 0);
    std::copy(input.begin(), input.end(), bytes.begin());
    bytes[input.size()] = 0x80;
    const auto bits = static_cast<std::uint64_t>(input.size()) * 8;
    for (int i = 0; i < 8; ++i) bytes[padded - 1 - i] = static_cast<std::uint8_t>(bits >> (i * 8));

    for (std::size_t offset = 0; offset < padded; offset += 64) {
        std::array<std::uint32_t, 64> w{};
        for (int i = 0; i < 16; ++i) {
            const auto p = offset + static_cast<std::size_t>(i) * 4;
            w[i] = (static_cast<std::uint32_t>(bytes[p]) << 24) |
                   (static_cast<std::uint32_t>(bytes[p + 1]) << 16) |
                   (static_cast<std::uint32_t>(bytes[p + 2]) << 8) | bytes[p + 3];
        }
        for (int i = 16; i < 64; ++i) {
            const auto s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
            const auto s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16] + s0 + w[i - 7] + s1;
        }
        auto a = state[0], b = state[1], c = state[2], d = state[3];
        auto e = state[4], f = state[5], g = state[6], h = state[7];
        for (int i = 0; i < 64; ++i) {
            const auto s1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
            const auto choose = (e & f) ^ (~e & g);
            const auto temp1 = h + s1 + choose + sha_k[i] + w[i];
            const auto s0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
            const auto majority = (a & b) ^ (a & c) ^ (b & c);
            const auto temp2 = s0 + majority;
            h = g; g = f; f = e; e = d + temp1;
            d = c; c = b; b = a; a = temp1 + temp2;
        }
        state[0] += a; state[1] += b; state[2] += c; state[3] += d;
        state[4] += e; state[5] += f; state[6] += g; state[7] += h;
    }
    std::array<std::uint8_t, 32> result{};
    for (std::size_t i = 0; i < state.size(); ++i) {
        result[i * 4] = static_cast<std::uint8_t>(state[i] >> 24);
        result[i * 4 + 1] = static_cast<std::uint8_t>(state[i] >> 16);
        result[i * 4 + 2] = static_cast<std::uint8_t>(state[i] >> 8);
        result[i * 4 + 3] = static_cast<std::uint8_t>(state[i]);
    }
    return result;
}

bool valid_utf8(std::string_view value) noexcept {
    std::size_t index{};
    std::uint32_t cp{};
    while (index < value.size()) if (!next_utf8(value, index, cp)) return false;
    return true;
}

std::size_t utf16_length(std::string_view value) noexcept {
    std::size_t index{}, count{};
    std::uint32_t cp{};
    while (index < value.size()) {
        if (!next_utf8(value, index, cp)) return std::numeric_limits<std::size_t>::max();
        count += cp > 0xFFFF ? 2 : 1;
    }
    return count;
}

bool utf8_whitespace_only(std::string_view value) noexcept {
    if (value.empty()) return true;
    std::size_t index{};
    std::uint32_t cp{};
    while (index < value.size()) {
        if (!next_utf8(value, index, cp)) return false;
        const bool whitespace = (cp >= 0x09 && cp <= 0x0D) || cp == 0x20 || cp == 0x85 ||
            cp == 0xA0 || cp == 0x1680 || (cp >= 0x2000 && cp <= 0x200A) ||
            cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000;
        if (!whitespace) return false;
    }
    return true;
}

bool make_resource_name(std::string_view name, ResourceName& result) noexcept {
    if (!valid_utf8(name)) return false;
    std::string readable;
    readable.reserve(name.size());
    std::size_t index{};
    std::uint32_t cp{};
    while (index < name.size()) {
        next_utf8(name, index, cp);
        if (cp < 128 && ((cp >= 'A' && cp <= 'Z') || (cp >= 'a' && cp <= 'z') ||
                         (cp >= '0' && cp <= '9') || cp == '-' || cp == '_' || cp == '.')) {
            readable.push_back(static_cast<char>(cp));
        } else {
            readable.push_back('_');
            if (cp > 0xFFFF) readable.push_back('_');
        }
    }
    const auto first = readable.find_first_not_of("_.");
    if (first == std::string::npos) {
        readable = "store";
    } else {
        const auto last = readable.find_last_not_of("_.");
        readable = readable.substr(first, last - first + 1);
    }
    if (readable.size() > 80) readable.resize(80);
    const auto digest = sha256(std::span<const std::uint8_t>(
        reinterpret_cast<const std::uint8_t*>(name.data()), name.size()));
    const auto fragment = "sms-" + readable + "-" + lowercase_hex(std::span<const std::uint8_t>(digest.data(), 8));

    std::filesystem::path root;
    std::error_code error;
    if (std::filesystem::is_directory(std::filesystem::path("/dev/shm"), error)) {
        root = "/dev/shm";
    } else {
        root = std::filesystem::temp_directory_path(error);
        if (error) root = std::filesystem::path("/tmp");
    }
    const auto directory = root / "SharedMemoryStore";
    result.public_name = std::string(name);
    result.fragment = fragment;
    result.linux_region_path = (directory / (fragment + ".region")).string();
    result.linux_lock_path = (directory / (fragment + ".lock")).string();
    result.linux_owners_path = (directory / (fragment + ".owners")).string();
    result.linux_lifecycle_path = (directory / (fragment + ".lifecycle")).string();
#if defined(_WIN32)
    if (!utf8_to_wide(name, result.windows_region_name)) return false;
    std::wstring sanitized;
    sanitized.reserve(result.windows_region_name.size());
    for (const auto ch : result.windows_region_name) {
        WORD type{};
        const bool alpha_numeric = GetStringTypeW(CT_CTYPE1, &ch, 1, &type) != 0 &&
                                    (type & (C1_ALPHA | C1_DIGIT)) != 0;
        sanitized.push_back(alpha_numeric || ch == L'-' || ch == L'_' ? ch : L'_');
    }
    result.windows_lock_name = (starts_global(result.windows_region_name) ? L"Global\\" : L"Local\\") +
                               std::wstring(L"SharedMemoryStore-") + sanitized;
#endif
    return true;
}

std::int32_t current_process_id() noexcept {
#if defined(_WIN32)
    return static_cast<std::int32_t>(GetCurrentProcessId());
#else
    return static_cast<std::int32_t>(getpid());
#endif
}

} // namespace sms::detail
