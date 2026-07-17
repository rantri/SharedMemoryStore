#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <limits>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace sms::test::v2 {

inline const std::filesystem::path& repository_root() {
#if defined(SMS_REPOSITORY_ROOT)
    static const auto root = [] {
        auto value = std::filesystem::path(SMS_REPOSITORY_ROOT).lexically_normal();
        if (!value.is_absolute()) {
            throw std::runtime_error("SMS_REPOSITORY_ROOT must be an absolute path.");
        }
        return value;
    }();
    return root;
#else
    throw std::runtime_error("SMS_REPOSITORY_ROOT is not defined for this native test target.");
#endif
}

inline const std::filesystem::path& fixture_root() {
    static const auto root = repository_root() / "protocol" / "fixtures" / "v2.0";
    return root;
}

inline std::filesystem::path fixture_path(std::string_view relative_name) {
    if (relative_name.empty()) {
        throw std::invalid_argument("A fixture-relative path is required.");
    }

    const auto relative = std::filesystem::path(relative_name).lexically_normal();
    if (relative.is_absolute() || relative.has_root_name() || relative.has_root_directory()) {
        throw std::invalid_argument("Fixture paths must be relative to protocol/fixtures/v2.0.");
    }
    for (const auto& component : relative) {
        if (component == "..") {
            throw std::invalid_argument("Fixture paths must not escape protocol/fixtures/v2.0.");
        }
    }

    return (fixture_root() / relative).lexically_normal();
}

inline std::vector<std::byte> load_exact_bytes(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary | std::ios::ate);
    if (!input) {
        throw std::runtime_error("Could not open fixture: " + path.string());
    }

    const auto end = input.tellg();
    const auto length = static_cast<std::streamoff>(end);
    if (length < 0 || static_cast<std::uintmax_t>(length) >
            static_cast<std::uintmax_t>(std::numeric_limits<std::size_t>::max()) ||
        static_cast<std::uintmax_t>(length) >
            static_cast<std::uintmax_t>(std::numeric_limits<std::streamsize>::max())) {
        throw std::runtime_error("Fixture size cannot be represented: " + path.string());
    }

    const auto size = static_cast<std::size_t>(length);
    std::vector<std::byte> result(size);
    input.seekg(0, std::ios::beg);
    if (size != 0) {
        input.read(reinterpret_cast<char*>(result.data()), static_cast<std::streamsize>(size));
        if (!input || static_cast<std::size_t>(input.gcount()) != size) {
            throw std::runtime_error("Fixture could not be read exactly: " + path.string());
        }
    }

    char unexpected{};
    if (input.get(unexpected)) {
        throw std::runtime_error("Fixture changed while it was being read: " + path.string());
    }
    return result;
}

inline std::vector<std::byte> load_fixture_bytes(std::string_view relative_name) {
    return load_exact_bytes(fixture_path(relative_name));
}

inline std::string load_exact_text(const std::filesystem::path& path) {
    const auto bytes = load_exact_bytes(path);
    if (bytes.empty()) return {};
    return std::string(
        reinterpret_cast<const char*>(bytes.data()),
        bytes.size());
}

struct manifest_document {
    std::filesystem::path path;
    std::string json;
};

inline manifest_document load_manifest() {
    auto path = fixture_path("manifest.json");
    auto json = load_exact_text(path);
    if (json.empty() || json.find('\0') != std::string::npos) {
        throw std::runtime_error("The SMS2 manifest must be non-empty UTF-8 JSON without NUL bytes.");
    }
    if (json.size() >= 3 &&
        static_cast<unsigned char>(json[0]) == 0xEF &&
        static_cast<unsigned char>(json[1]) == 0xBB &&
        static_cast<unsigned char>(json[2]) == 0xBF) {
        throw std::runtime_error("The SMS2 manifest must not contain a UTF-8 BOM.");
    }
    return {std::move(path), std::move(json)};
}

inline std::size_t require_unique_json_fragment(
    std::string_view json,
    std::string_view exact_fragment) {
    if (exact_fragment.empty()) {
        throw std::invalid_argument("An exact JSON fragment is required.");
    }
    const auto position = json.find(exact_fragment);
    if (position == std::string_view::npos) {
        throw std::runtime_error("The required JSON fragment is absent.");
    }
    if (json.find(exact_fragment, position + exact_fragment.size()) != std::string_view::npos) {
        throw std::runtime_error("The required JSON fragment is not unique.");
    }
    return position;
}

inline std::vector<std::byte> decode_hex_exact(std::string_view value) {
    if ((value.size() & 1U) != 0) {
        throw std::invalid_argument("Hex fixture text must contain complete bytes.");
    }

    auto nibble = [](char current) -> std::uint8_t {
        if (current >= '0' && current <= '9') return static_cast<std::uint8_t>(current - '0');
        if (current >= 'a' && current <= 'f') return static_cast<std::uint8_t>(current - 'a' + 10);
        if (current >= 'A' && current <= 'F') return static_cast<std::uint8_t>(current - 'A' + 10);
        throw std::invalid_argument("Hex fixture text contains a non-hexadecimal character.");
    };

    std::vector<std::byte> result(value.size() / 2);
    for (std::size_t index = 0; index < result.size(); ++index) {
        result[index] = static_cast<std::byte>(
            static_cast<std::uint8_t>((nibble(value[index * 2]) << 4U) |
                                      nibble(value[index * 2 + 1])));
    }
    return result;
}

inline bool bytes_equal(std::span<const std::byte> left, std::span<const std::byte> right) noexcept {
    if (left.size() != right.size()) return false;
    for (std::size_t index = 0; index < left.size(); ++index) {
        if (left[index] != right[index]) return false;
    }
    return true;
}

} // namespace sms::test::v2
