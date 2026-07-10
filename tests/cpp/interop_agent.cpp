#include <shared_memory_store/store.hpp>

#include <algorithm>
#include <array>
#include <charconv>
#include <cctype>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <iostream>
#include <limits>
#include <map>
#include <span>
#include <stdexcept>
#include <string>
#include <string_view>
#include <type_traits>
#include <unordered_map>
#include <utility>
#include <variant>
#include <vector>

namespace {

namespace json {

struct value {
    using array = std::vector<value>;
    using object = std::map<std::string, value, std::less<>>;
    using storage = std::variant<std::nullptr_t, bool, std::int64_t, double, std::string, array, object>;

    value() : data(nullptr) {}
    value(std::nullptr_t) : data(nullptr) {}
    value(bool input) : data(input) {}
    value(int input) : data(static_cast<std::int64_t>(input)) {}
    value(std::int64_t input) : data(input) {}
    value(double input) : data(input) {}
    value(const char* input) : data(std::string(input)) {}
    value(std::string input) : data(std::move(input)) {}
    value(array input) : data(std::move(input)) {}
    value(object input) : data(std::move(input)) {}

    storage data;
};

void append_utf8(std::string& destination, std::uint32_t code_point) {
    if (code_point <= 0x7fu) {
        destination.push_back(static_cast<char>(code_point));
    } else if (code_point <= 0x7ffu) {
        destination.push_back(static_cast<char>(0xc0u | (code_point >> 6u)));
        destination.push_back(static_cast<char>(0x80u | (code_point & 0x3fu)));
    } else if (code_point <= 0xffffu) {
        destination.push_back(static_cast<char>(0xe0u | (code_point >> 12u)));
        destination.push_back(static_cast<char>(0x80u | ((code_point >> 6u) & 0x3fu)));
        destination.push_back(static_cast<char>(0x80u | (code_point & 0x3fu)));
    } else if (code_point <= 0x10ffffu) {
        destination.push_back(static_cast<char>(0xf0u | (code_point >> 18u)));
        destination.push_back(static_cast<char>(0x80u | ((code_point >> 12u) & 0x3fu)));
        destination.push_back(static_cast<char>(0x80u | ((code_point >> 6u) & 0x3fu)));
        destination.push_back(static_cast<char>(0x80u | (code_point & 0x3fu)));
    } else {
        throw std::runtime_error("JSON string contains an invalid Unicode scalar value.");
    }
}

class parser {
public:
    explicit parser(std::string_view input) : input_(input) {}

    value parse() {
        skip_space();
        auto result = parse_value();
        skip_space();
        if (position_ != input_.size()) fail("Unexpected characters after the JSON value.");
        return result;
    }

private:
    [[noreturn]] void fail(const char* message) const {
        throw std::runtime_error(std::string(message) + " At byte " + std::to_string(position_) + '.');
    }

    void skip_space() {
        while (position_ < input_.size()) {
            const auto current = input_[position_];
            if (current != ' ' && current != '\t' && current != '\r' && current != '\n') break;
            ++position_;
        }
    }

    char take() {
        if (position_ == input_.size()) fail("Unexpected end of JSON input.");
        return input_[position_++];
    }

    bool consume(char expected) {
        if (position_ < input_.size() && input_[position_] == expected) {
            ++position_;
            return true;
        }
        return false;
    }

    void expect_literal(std::string_view expected) {
        if (input_.substr(position_, expected.size()) != expected) fail("Invalid JSON literal.");
        position_ += expected.size();
    }

    value parse_value() {
        if (position_ == input_.size()) fail("A JSON value is required.");
        switch (input_[position_]) {
        case 'n': expect_literal("null"); return nullptr;
        case 't': expect_literal("true"); return true;
        case 'f': expect_literal("false"); return false;
        case '"': return parse_string();
        case '[': return parse_array();
        case '{': return parse_object();
        default:
            if (input_[position_] == '-' || (input_[position_] >= '0' && input_[position_] <= '9'))
                return parse_number();
            fail("Invalid JSON value.");
        }
    }

    std::uint32_t parse_hex_quad() {
        std::uint32_t result{};
        for (int index = 0; index < 4; ++index) {
            const auto current = take();
            result <<= 4u;
            if (current >= '0' && current <= '9') result |= static_cast<std::uint32_t>(current - '0');
            else if (current >= 'a' && current <= 'f') result |= static_cast<std::uint32_t>(current - 'a' + 10);
            else if (current >= 'A' && current <= 'F') result |= static_cast<std::uint32_t>(current - 'A' + 10);
            else fail("Invalid hexadecimal JSON escape.");
        }
        return result;
    }

    std::string parse_string() {
        if (take() != '"') fail("A JSON string was expected.");
        std::string result;
        while (true) {
            const auto current = static_cast<unsigned char>(take());
            if (current == '"') return result;
            if (current < 0x20u) fail("Unescaped control character in JSON string.");
            if (current != '\\') {
                result.push_back(static_cast<char>(current));
                continue;
            }

            switch (take()) {
            case '"': result.push_back('"'); break;
            case '\\': result.push_back('\\'); break;
            case '/': result.push_back('/'); break;
            case 'b': result.push_back('\b'); break;
            case 'f': result.push_back('\f'); break;
            case 'n': result.push_back('\n'); break;
            case 'r': result.push_back('\r'); break;
            case 't': result.push_back('\t'); break;
            case 'u': {
                auto scalar = parse_hex_quad();
                if (scalar >= 0xd800u && scalar <= 0xdbffu) {
                    if (take() != '\\' || take() != 'u') fail("A high surrogate must be followed by a low surrogate.");
                    const auto low = parse_hex_quad();
                    if (low < 0xdc00u || low > 0xdfffu) fail("Invalid low surrogate in JSON string.");
                    scalar = 0x10000u + ((scalar - 0xd800u) << 10u) + (low - 0xdc00u);
                } else if (scalar >= 0xdc00u && scalar <= 0xdfffu) {
                    fail("Unexpected low surrogate in JSON string.");
                }
                append_utf8(result, scalar);
                break;
            }
            default: fail("Invalid JSON string escape.");
            }
        }
    }

    value parse_number() {
        const auto start = position_;
        consume('-');
        if (consume('0')) {
            if (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9')
                fail("A JSON number cannot contain a leading zero.");
        } else {
            if (position_ == input_.size() || input_[position_] < '1' || input_[position_] > '9')
                fail("Invalid JSON number.");
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
        }
        bool floating = false;
        if (consume('.')) {
            floating = true;
            if (position_ == input_.size() || input_[position_] < '0' || input_[position_] > '9')
                fail("A JSON fraction requires at least one digit.");
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
        }
        if (position_ < input_.size() && (input_[position_] == 'e' || input_[position_] == 'E')) {
            floating = true;
            ++position_;
            if (position_ < input_.size() && (input_[position_] == '+' || input_[position_] == '-')) ++position_;
            if (position_ == input_.size() || input_[position_] < '0' || input_[position_] > '9')
                fail("A JSON exponent requires at least one digit.");
            while (position_ < input_.size() && input_[position_] >= '0' && input_[position_] <= '9') ++position_;
        }
        const auto token = input_.substr(start, position_ - start);
        if (!floating) {
            std::int64_t result{};
            const auto parsed = std::from_chars(token.data(), token.data() + token.size(), result);
            if (parsed.ec != std::errc{} || parsed.ptr != token.data() + token.size()) fail("JSON integer is out of range.");
            return result;
        }
        std::string owned(token);
        char* end{};
        const auto result = std::strtod(owned.c_str(), &end);
        if (end != owned.c_str() + owned.size()) fail("Invalid JSON floating-point number.");
        return result;
    }

    value::array parse_array() {
        take();
        skip_space();
        value::array result;
        if (consume(']')) return result;
        while (true) {
            skip_space();
            result.push_back(parse_value());
            skip_space();
            if (consume(']')) return result;
            if (!consume(',')) fail("A comma was expected in the JSON array.");
        }
    }

    value::object parse_object() {
        take();
        skip_space();
        value::object result;
        if (consume('}')) return result;
        while (true) {
            skip_space();
            if (position_ == input_.size() || input_[position_] != '"') fail("A JSON object key must be a string.");
            auto key = parse_string();
            skip_space();
            if (!consume(':')) fail("A colon was expected after the JSON object key.");
            skip_space();
            auto [iterator, inserted] = result.emplace(std::move(key), parse_value());
            if (!inserted) fail("Duplicate JSON object keys are not supported.");
            skip_space();
            if (consume('}')) return result;
            if (!consume(',')) fail("A comma was expected in the JSON object.");
        }
    }

    std::string_view input_;
    std::size_t position_{};
};

void append_escaped(std::string& output, std::string_view input) {
    static constexpr char hex[] = "0123456789abcdef";
    output.push_back('"');
    for (const auto current : input) {
        const auto byte = static_cast<unsigned char>(current);
        switch (byte) {
        case '"': output += "\\\""; break;
        case '\\': output += "\\\\"; break;
        case '\b': output += "\\b"; break;
        case '\f': output += "\\f"; break;
        case '\n': output += "\\n"; break;
        case '\r': output += "\\r"; break;
        case '\t': output += "\\t"; break;
        default:
            if (byte < 0x20u) {
                output += "\\u00";
                output.push_back(hex[byte >> 4u]);
                output.push_back(hex[byte & 0x0fu]);
            } else {
                output.push_back(current);
            }
        }
    }
    output.push_back('"');
}

void append_json(std::string& output, const value& input) {
    std::visit([&output](const auto& current) {
        using type = std::decay_t<decltype(current)>;
        if constexpr (std::is_same_v<type, std::nullptr_t>) output += "null";
        else if constexpr (std::is_same_v<type, bool>) output += current ? "true" : "false";
        else if constexpr (std::is_same_v<type, std::int64_t>) output += std::to_string(current);
        else if constexpr (std::is_same_v<type, double>) output += std::to_string(current);
        else if constexpr (std::is_same_v<type, std::string>) append_escaped(output, current);
        else if constexpr (std::is_same_v<type, value::array>) {
            output.push_back('[');
            bool first = true;
            for (const auto& item : current) {
                if (!first) output.push_back(',');
                first = false;
                append_json(output, item);
            }
            output.push_back(']');
        } else {
            output.push_back('{');
            bool first = true;
            for (const auto& [key, item] : current) {
                if (!first) output.push_back(',');
                first = false;
                append_escaped(output, key);
                output.push_back(':');
                append_json(output, item);
            }
            output.push_back('}');
        }
    }, input.data);
}

std::string dump(const value& input) {
    std::string result;
    result.reserve(256);
    append_json(result, input);
    return result;
}

const value::object& object_value(const value& input, std::string_view description) {
    const auto* result = std::get_if<value::object>(&input.data);
    if (!result) throw std::runtime_error(std::string(description) + " must be a JSON object.");
    return *result;
}

const value::array& array_value(const value& input, std::string_view description) {
    const auto* result = std::get_if<value::array>(&input.data);
    if (!result) throw std::runtime_error(std::string(description) + " must be a JSON array.");
    return *result;
}

const std::string& string_value(const value& input, std::string_view description) {
    const auto* result = std::get_if<std::string>(&input.data);
    if (!result) throw std::runtime_error(std::string(description) + " must be a JSON string.");
    return *result;
}

} // namespace json

class protocol_error : public std::runtime_error {
public:
    protocol_error(std::string code, std::string message)
        : std::runtime_error(std::move(message)), code_(std::move(code)) {}
    const std::string& code() const noexcept { return code_; }
private:
    std::string code_;
};

const json::value* find(const json::value::object& object, std::string_view key) {
    const auto iterator = object.find(key);
    return iterator == object.end() ? nullptr : &iterator->second;
}

const json::value& require(const json::value::object& object, std::string_view key) {
    const auto* result = find(object, key);
    if (!result) throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument is required.");
    return *result;
}

std::string string_argument(const json::value::object& object, std::string_view key,
                            std::string default_value = {}) {
    const auto* input = find(object, key);
    if (!input) return default_value;
    try { return json::string_value(*input, std::string("The '") + std::string(key) + "' argument"); }
    catch (const std::exception& exception) { throw protocol_error("invalid_arguments", exception.what()); }
}

std::string required_string(const json::value::object& object, std::string_view key) {
    const auto result = string_argument(object, key);
    if (result.empty()) throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument is required.");
    return result;
}

std::int64_t integer_argument(const json::value::object& object, std::string_view key,
                              std::int64_t default_value) {
    const auto* input = find(object, key);
    if (!input || std::holds_alternative<std::nullptr_t>(input->data)) return default_value;
    if (const auto* result = std::get_if<std::int64_t>(&input->data)) return *result;
    throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument must be an integer.");
}

std::int32_t int32_argument(const json::value::object& object, std::string_view key,
                            std::int32_t default_value) {
    const auto result = integer_argument(object, key, default_value);
    if (result < std::numeric_limits<std::int32_t>::min() || result > std::numeric_limits<std::int32_t>::max())
        throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument is outside the Int32 range.");
    return static_cast<std::int32_t>(result);
}

std::int32_t required_int32(const json::value::object& object, std::string_view key) {
    const auto* input = find(object, key);
    if (!input || std::holds_alternative<std::nullptr_t>(input->data))
        throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument is required.");
    return int32_argument(object, key, 0);
}

bool bool_argument(const json::value::object& object, std::string_view key, bool default_value) {
    const auto* input = find(object, key);
    if (!input || std::holds_alternative<std::nullptr_t>(input->data)) return default_value;
    if (const auto* result = std::get_if<bool>(&input->data)) return *result;
    throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument must be a Boolean.");
}

std::vector<std::byte> decode_base64(std::string_view input) {
    if (input.empty()) return {};
    if (input.size() % 4u != 0u)
        throw protocol_error("invalid_base64", "A base64 field has an invalid length.");

    const auto decode_character = [](char current) -> int {
        if (current >= 'A' && current <= 'Z') return current - 'A';
        if (current >= 'a' && current <= 'z') return current - 'a' + 26;
        if (current >= '0' && current <= '9') return current - '0' + 52;
        if (current == '+') return 62;
        if (current == '/') return 63;
        return -1;
    };

    std::vector<std::byte> result;
    result.reserve((input.size() / 4u) * 3u);
    for (std::size_t offset = 0; offset < input.size(); offset += 4u) {
        const bool final_group = offset + 4u == input.size();
        const bool pad2 = input[offset + 2u] == '=';
        const bool pad3 = input[offset + 3u] == '=';
        if ((!final_group && (pad2 || pad3)) || (pad2 && !pad3))
            throw protocol_error("invalid_base64", "A base64 field has invalid padding.");
        const auto a = decode_character(input[offset]);
        const auto b = decode_character(input[offset + 1u]);
        const auto c = pad2 ? 0 : decode_character(input[offset + 2u]);
        const auto d = pad3 ? 0 : decode_character(input[offset + 3u]);
        if (a < 0 || b < 0 || c < 0 || d < 0)
            throw protocol_error("invalid_base64", "A base64 field contains an invalid character.");
        const auto bits = (static_cast<std::uint32_t>(a) << 18u) |
                          (static_cast<std::uint32_t>(b) << 12u) |
                          (static_cast<std::uint32_t>(c) << 6u) |
                          static_cast<std::uint32_t>(d);
        result.push_back(static_cast<std::byte>((bits >> 16u) & 0xffu));
        if (!pad2) result.push_back(static_cast<std::byte>((bits >> 8u) & 0xffu));
        if (!pad3) result.push_back(static_cast<std::byte>(bits & 0xffu));
    }
    return result;
}

std::string encode_base64(std::span<const std::byte> input) {
    static constexpr char alphabet[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string result;
    result.reserve(((input.size() + 2u) / 3u) * 4u);
    for (std::size_t offset = 0; offset < input.size(); offset += 3u) {
        const auto remaining = input.size() - offset;
        const auto a = std::to_integer<std::uint32_t>(input[offset]);
        const auto b = remaining > 1u ? std::to_integer<std::uint32_t>(input[offset + 1u]) : 0u;
        const auto c = remaining > 2u ? std::to_integer<std::uint32_t>(input[offset + 2u]) : 0u;
        const auto bits = (a << 16u) | (b << 8u) | c;
        result.push_back(alphabet[(bits >> 18u) & 0x3fu]);
        result.push_back(alphabet[(bits >> 12u) & 0x3fu]);
        result.push_back(remaining > 1u ? alphabet[(bits >> 6u) & 0x3fu] : '=');
        result.push_back(remaining > 2u ? alphabet[bits & 0x3fu] : '=');
    }
    return result;
}

std::vector<std::byte> bytes_argument(const json::value::object& object, std::string_view key,
                                      bool required_field = false) {
    const auto* input = find(object, key);
    if (!input) {
        if (required_field) throw protocol_error("invalid_arguments", "The '" + std::string(key) + "' argument is required.");
        return {};
    }
    if (!required_field && std::holds_alternative<std::nullptr_t>(input->data)) return {};
    try { return decode_base64(json::string_value(*input, std::string("The '") + std::string(key) + "' argument")); }
    catch (const protocol_error&) { throw; }
    catch (const std::exception& exception) { throw protocol_error("invalid_arguments", exception.what()); }
}

shared_memory_store::wait_options wait_argument(const json::value::object& arguments) {
    return {integer_argument(arguments, "timeoutMs", 1000)};
}

shared_memory_store::open_mode open_mode_argument(const json::value::object& arguments) {
    const auto* input = find(arguments, "openMode");
    if (!input) return shared_memory_store::open_mode::create_or_open;
    if (const auto* number = std::get_if<std::int64_t>(&input->data)) {
        if (*number >= std::numeric_limits<std::int32_t>::min() &&
            *number <= std::numeric_limits<std::int32_t>::max())
            return static_cast<shared_memory_store::open_mode>(static_cast<std::int32_t>(*number));
    } else if (const auto* text = std::get_if<std::string>(&input->data)) {
        std::string normalized;
        normalized.reserve(text->size());
        for (const auto current : *text) {
            if (current != '_' && current != '-' && current != '/')
                normalized.push_back(static_cast<char>(std::tolower(static_cast<unsigned char>(current))));
        }
        if (normalized == "createnew" || normalized == "create") return shared_memory_store::open_mode::create_new;
        if (normalized == "openexisting" || normalized == "open") return shared_memory_store::open_mode::open_existing;
        if (normalized == "createoropen") return shared_memory_store::open_mode::create_or_open;
    }
    throw protocol_error("invalid_arguments", "The 'openMode' argument is invalid.");
}

const char* open_status_name(shared_memory_store::open_status input) noexcept {
    using enum shared_memory_store::open_status;
    switch (input) {
    case success: return "Success";
    case already_exists: return "AlreadyExists";
    case not_found: return "NotFound";
    case invalid_options: return "InvalidOptions";
    case incompatible_layout: return "IncompatibleLayout";
    case unsupported_platform: return "UnsupportedPlatform";
    case insufficient_capacity: return "InsufficientCapacity";
    case access_denied: return "AccessDenied";
    case mapping_failed: return "MappingFailed";
    case store_busy: return "StoreBusy";
    case operation_canceled: return "OperationCanceled";
    }
    return "UnknownOpenStatus";
}

const char* status_name(shared_memory_store::status input) noexcept {
    using enum shared_memory_store::status;
    switch (input) {
    case success: return "Success";
    case duplicate_key: return "DuplicateKey";
    case not_found: return "NotFound";
    case key_too_large: return "KeyTooLarge";
    case value_too_large: return "ValueTooLarge";
    case descriptor_too_large: return "DescriptorTooLarge";
    case store_full: return "StoreFull";
    case lease_table_full: return "LeaseTableFull";
    case invalid_lease: return "InvalidLease";
    case lease_already_released: return "LeaseAlreadyReleased";
    case remove_pending: return "RemovePending";
    case unsupported_platform: return "UnsupportedPlatform";
    case store_disposed: return "StoreDisposed";
    case corrupt_store: return "CorruptStore";
    case access_denied: return "AccessDenied";
    case unknown_failure: return "UnknownFailure";
    case invalid_reservation: return "InvalidReservation";
    case reservation_incomplete: return "ReservationIncomplete";
    case reservation_already_completed: return "ReservationAlreadyCompleted";
    case reservation_write_out_of_range: return "ReservationWriteOutOfRange";
    case invalid_key: return "InvalidKey";
    case store_busy: return "StoreBusy";
    case operation_canceled: return "OperationCanceled";
    }
    return "UnknownStatus";
}

json::value status_json(std::int32_t code, const char* name) {
    return json::value::object{{"code", code}, {"name", name}};
}

json::value response(std::string id, std::int32_t code, const char* name, json::value result = nullptr) {
    json::value::object output{{"id", std::move(id)}, {"ok", true}, {"status", status_json(code, name)}};
    if (!std::holds_alternative<std::nullptr_t>(result.data)) output.emplace("result", std::move(result));
    return output;
}

json::value response(std::string id, shared_memory_store::status operation_status,
                     json::value result = nullptr) {
    return response(std::move(id), static_cast<std::int32_t>(operation_status),
                    status_name(operation_status), std::move(result));
}

json::value response(std::string id, shared_memory_store::open_status operation_status,
                     json::value result = nullptr) {
    return response(std::move(id), static_cast<std::int32_t>(operation_status),
                    open_status_name(operation_status), std::move(result));
}

json::value failure(std::string id, std::int32_t status_code, const char* status_name_value,
                    std::string error_code, std::string message) {
    return json::value::object{
        {"error", json::value::object{{"code", std::move(error_code)}, {"message", std::move(message)}}},
        {"id", std::move(id)},
        {"ok", false},
        {"status", status_json(status_code, status_name_value)}};
}

struct lease_entry {
    std::string store_id;
    shared_memory_store::value_lease lease;
};

struct reservation_entry {
    std::string store_id;
    shared_memory_store::value_reservation reservation;
};

class agent {
public:
    json::value handle(const json::value& request) {
        const auto& root = json::object_value(request, "The request");
        const auto id = required_request_string(root, "id");
        const auto command = required_request_string(root, "command");
        static const json::value::object empty_arguments;
        const auto* arguments_value = find(root, "arguments");
        const auto& arguments = arguments_value
            ? checked_object(*arguments_value, "Request arguments")
            : empty_arguments;

        if (command == "ping") {
            return response(id, shared_memory_store::status::success,
                            json::value::object{{"protocolVersion", 1}, {"runtime", "cpp"}});
        }
        if (command == "open" || command == "create" || command == "open/create")
            return open(id, arguments, command);
        if (command == "close") return close(id, arguments);
        if (command == "publish") return publish(id, arguments);
        if (command == "publishSegments" || command == "publishSegmented") return publish_segments(id, arguments);
        if (command == "acquire") return acquire(id, arguments);
        if (command == "read") return read(id, arguments);
        if (command == "release") return release(id, arguments);
        if (command == "remove") return remove(id, arguments);
        if (command == "reserve") return reserve(id, arguments);
        if (command == "reservationWrite" || command == "write") return reservation_write(id, arguments);
        if (command == "advance") return advance(id, arguments);
        if (command == "commit") return commit(id, arguments);
        if (command == "abort") return abort(id, arguments);
        if (command == "recoverLeases") return recover(id, arguments, false);
        if (command == "recoverReservations") return recover(id, arguments, true);
        if (command == "diagnostics") return diagnostics(id, arguments);
        if (command == "crash") {
            const auto exit_code = int32_argument(arguments, "exitCode", 97);
            std::_Exit(exit_code);
        }
        throw protocol_error("unsupported_command", "The command '" + command + "' is not implemented by this agent.");
    }

private:
    static const json::value::object& checked_object(const json::value& input, std::string_view description) {
        try { return json::object_value(input, description); }
        catch (const std::exception& exception) { throw protocol_error("invalid_request", exception.what()); }
    }

    static std::string required_request_string(const json::value::object& root, std::string_view key) {
        const auto* input = find(root, key);
        if (!input) throw protocol_error("invalid_request", "The request '" + std::string(key) + "' is required.");
        try {
            const auto& result = json::string_value(*input, std::string("The request '") + std::string(key) + "'");
            if (result.empty()) throw std::runtime_error("The request '" + std::string(key) + "' cannot be empty.");
            return result;
        } catch (const std::exception& exception) {
            throw protocol_error("invalid_request", exception.what());
        }
    }

    shared_memory_store::memory_store& store(const json::value::object& arguments, std::string& id) {
        id = required_string(arguments, "storeId");
        const auto iterator = stores_.find(id);
        if (iterator == stores_.end()) throw protocol_error("invalid_arguments", "The store handle '" + id + "' is unknown.");
        return iterator->second;
    }

    lease_entry& lease(const json::value::object& arguments, std::string& id) {
        id = required_string(arguments, "leaseId");
        const auto iterator = leases_.find(id);
        if (iterator == leases_.end()) throw protocol_error("invalid_arguments", "The lease handle '" + id + "' is unknown.");
        return iterator->second;
    }

    reservation_entry& reservation(const json::value::object& arguments, std::string& id) {
        id = required_string(arguments, "reservationId");
        const auto iterator = reservations_.find(id);
        if (iterator == reservations_.end())
            throw protocol_error("invalid_arguments", "The reservation handle '" + id + "' is unknown.");
        return iterator->second;
    }

    json::value open(const std::string& request_id, const json::value::object& arguments,
                     std::string_view command) {
        const auto handle_id = required_string(arguments, "storeId");
        stores_.erase(handle_id);

        shared_memory_store::store_options options;
        options.name = required_string(arguments, "name");
        options.mode = command == "create" ? shared_memory_store::open_mode::create_new : open_mode_argument(arguments);
        options.slot_count = required_int32(arguments, "slotCount");
        options.max_value_bytes = required_int32(arguments, "maxValueBytes");
        options.max_descriptor_bytes = required_int32(arguments, "maxDescriptorBytes");
        options.max_key_bytes = required_int32(arguments, "maxKeyBytes");
        options.lease_record_count = required_int32(arguments, "leaseRecordCount");
        options.enable_lease_recovery = bool_argument(arguments, "enableLeaseRecovery", false);
        const auto* total_bytes = find(arguments, "totalBytes");
        if (!total_bytes || std::holds_alternative<std::nullptr_t>(total_bytes->data)) {
            try {
                options.total_bytes = shared_memory_store::store_options::calculate_required_bytes(
                    options.slot_count, options.max_value_bytes, options.max_descriptor_bytes,
                    options.max_key_bytes, options.lease_record_count);
            } catch (const std::exception&) {
                return response(request_id, shared_memory_store::open_status::invalid_options,
                                json::value::object{{"storeId", handle_id}, {"totalBytes", 0}});
            }
        } else {
            options.total_bytes = integer_argument(arguments, "totalBytes", 0);
        }

        shared_memory_store::memory_store opened;
        const auto result = shared_memory_store::memory_store::try_create_or_open(options, opened, wait_argument(arguments));
        if (result == shared_memory_store::open_status::success)
            stores_.emplace(handle_id, std::move(opened));
        return response(request_id, result,
                        json::value::object{{"storeId", handle_id}, {"totalBytes", options.total_bytes}});
    }

    json::value close(const std::string& request_id, const json::value::object& arguments) {
        const auto handle_id = required_string(arguments, "storeId");
        const auto iterator = stores_.find(handle_id);
        if (iterator != stores_.end()) stores_.erase(iterator);
        return response(request_id, shared_memory_store::status::success,
                        json::value::object{{"closed", true}, {"storeId", handle_id}});
    }

    json::value publish(const std::string& request_id, const json::value::object& arguments) {
        std::string handle_id;
        auto& target = store(arguments, handle_id);
        const auto key = bytes_argument(arguments, "key", true);
        const auto value = bytes_argument(arguments, "value", true);
        const auto descriptor = bytes_argument(arguments, "descriptor");
        const auto result = target.try_publish(key, value, descriptor, wait_argument(arguments));
        return response(request_id, result,
                        json::value::object{{"storeId", handle_id}, {"valueLength", static_cast<std::int64_t>(value.size())}});
    }

    json::value publish_segments(const std::string& request_id, const json::value::object& arguments) {
        std::string handle_id;
        auto& target = store(arguments, handle_id);
        const auto key = bytes_argument(arguments, "key", true);
        const auto descriptor = bytes_argument(arguments, "descriptor");
        const auto& inputs = json::array_value(require(arguments, "segments"), "The 'segments' argument");
        std::vector<std::vector<std::byte>> owned;
        std::vector<std::span<const std::byte>> segments;
        owned.reserve(inputs.size());
        segments.reserve(inputs.size());
        for (const auto& item : inputs) {
            owned.push_back(decode_base64(json::string_value(item, "A segment")));
            segments.emplace_back(owned.back());
        }
        std::int64_t copied{};
        const auto result = target.try_publish_segments(key, segments, descriptor, copied, wait_argument(arguments));
        return response(request_id, result,
                        json::value::object{{"copiedBytes", copied}, {"storeId", handle_id}});
    }

    json::value acquire(const std::string& request_id, const json::value::object& arguments) {
        std::string store_id;
        auto& target = store(arguments, store_id);
        const auto lease_id = required_string(arguments, "leaseId");
        leases_.erase(lease_id);
        const auto key = bytes_argument(arguments, "key", true);
        shared_memory_store::value_lease acquired;
        const auto result = target.try_acquire(key, acquired, wait_argument(arguments));
        json::value::object output{{"leaseId", lease_id}, {"storeId", store_id}};
        if (result == shared_memory_store::status::success) {
            output.emplace("descriptor", encode_base64(acquired.descriptor()));
            output.emplace("value", encode_base64(acquired.value()));
            leases_.emplace(lease_id, lease_entry{store_id, std::move(acquired)});
        }
        return response(request_id, result, std::move(output));
    }

    json::value read(const std::string& request_id, const json::value::object& arguments) {
        std::string lease_id;
        auto& entry = lease(arguments, lease_id);
        if (!entry.lease.valid())
            return response(request_id, shared_memory_store::status::invalid_lease,
                            json::value::object{{"leaseId", lease_id}});
        return response(request_id, shared_memory_store::status::success,
                        json::value::object{{"descriptor", encode_base64(entry.lease.descriptor())},
                                            {"leaseId", lease_id},
                                            {"value", encode_base64(entry.lease.value())}});
    }

    json::value release(const std::string& request_id, const json::value::object& arguments) {
        const auto lease_id = required_string(arguments, "leaseId");
        const auto iterator = leases_.find(lease_id);
        if (iterator == leases_.end())
            return response(request_id, shared_memory_store::status::invalid_lease);
        auto& entry = iterator->second;
        const auto result = entry.lease.release(wait_argument(arguments));
        return response(request_id, result,
                        json::value::object{{"leaseId", lease_id}, {"valid", entry.lease.valid()}});
    }

    json::value remove(const std::string& request_id, const json::value::object& arguments) {
        std::string store_id;
        auto& target = store(arguments, store_id);
        const auto key = bytes_argument(arguments, "key", true);
        const auto result = target.try_remove(key, wait_argument(arguments));
        return response(request_id, result, json::value::object{{"storeId", store_id}});
    }

    json::value reserve(const std::string& request_id, const json::value::object& arguments) {
        std::string store_id;
        auto& target = store(arguments, store_id);
        const auto reservation_id = required_string(arguments, "reservationId");
        reservations_.erase(reservation_id);
        const auto key = bytes_argument(arguments, "key", true);
        const auto descriptor = bytes_argument(arguments, "descriptor");
        const auto payload_length = required_int32(arguments, "payloadLength");
        shared_memory_store::value_reservation reserved;
        const auto result = target.try_reserve(key, payload_length, descriptor, reserved, wait_argument(arguments));
        json::value::object output{{"payloadLength", payload_length},
                                   {"reservationId", reservation_id},
                                   {"storeId", store_id}};
        if (result == shared_memory_store::status::success) {
            output.emplace("bytesWritten", reserved.bytes_written());
            output.emplace("remainingBytes", reserved.remaining_bytes());
            reservations_.emplace(reservation_id, reservation_entry{store_id, std::move(reserved)});
        }
        return response(request_id, result, std::move(output));
    }

    json::value reservation_write(const std::string& request_id, const json::value::object& arguments) {
        const auto reservation_id = required_string(arguments, "reservationId");
        const auto iterator = reservations_.find(reservation_id);
        if (iterator == reservations_.end())
            return response(request_id, shared_memory_store::status::invalid_reservation);
        auto& entry = iterator->second;
        const auto data = bytes_argument(arguments, "data", true);
        if (!entry.reservation.valid())
            return response(request_id, shared_memory_store::status::invalid_reservation,
                            reservation_result(reservation_id, entry.reservation, 0));
        if (data.size() > static_cast<std::size_t>(std::max(0, entry.reservation.remaining_bytes())))
            return response(request_id, shared_memory_store::status::reservation_write_out_of_range,
                            reservation_result(reservation_id, entry.reservation, 0));
        if (!data.empty()) {
            const auto buffer = entry.reservation.buffer(static_cast<std::int32_t>(data.size()));
            if (buffer.size() < data.size())
                return response(request_id, shared_memory_store::status::invalid_reservation,
                                reservation_result(reservation_id, entry.reservation, 0));
            std::memcpy(buffer.data(), data.data(), data.size());
        }
        return response(request_id, shared_memory_store::status::success,
                        reservation_result(reservation_id, entry.reservation,
                                           static_cast<std::int64_t>(data.size())));
    }

    json::value advance(const std::string& request_id, const json::value::object& arguments) {
        std::string reservation_id;
        auto& entry = reservation(arguments, reservation_id);
        const auto result = entry.reservation.advance(required_int32(arguments, "byteCount"), wait_argument(arguments));
        return response(request_id, result, reservation_result(reservation_id, entry.reservation, 0));
    }

    json::value commit(const std::string& request_id, const json::value::object& arguments) {
        std::string reservation_id;
        auto& entry = reservation(arguments, reservation_id);
        const auto result = entry.reservation.commit(wait_argument(arguments));
        return response(request_id, result, reservation_result(reservation_id, entry.reservation, 0));
    }

    json::value abort(const std::string& request_id, const json::value::object& arguments) {
        std::string reservation_id;
        auto& entry = reservation(arguments, reservation_id);
        const auto result = entry.reservation.abort(wait_argument(arguments));
        return response(request_id, result, reservation_result(reservation_id, entry.reservation, 0));
    }

    static json::value reservation_result(const std::string& reservation_id,
                                          const shared_memory_store::value_reservation& reservation,
                                          std::int64_t bytes_copied) {
        return json::value::object{{"bytesCopied", bytes_copied},
                                   {"bytesWritten", reservation.bytes_written()},
                                   {"payloadLength", reservation.payload_length()},
                                   {"remainingBytes", reservation.remaining_bytes()},
                                   {"reservationId", reservation_id},
                                   {"written", bytes_copied},
                                   {"valid", reservation.valid()}};
    }

    json::value recover(const std::string& request_id, const json::value::object& arguments,
                        bool reservations) {
        std::string store_id;
        auto& target = store(arguments, store_id);
        shared_memory_store::recovery_report report{};
        const auto current = bool_argument(arguments, "recoverCurrentProcess", false);
        const auto result = reservations
            ? target.try_recover_reservations(current, report, wait_argument(arguments))
            : target.try_recover_leases(current, report, wait_argument(arguments));
        json::value::object output{{"activeCount", report.active_count},
                                   {"failedCount", report.failed_count},
                                   {"failedRecoveryCount", report.failed_count},
                                   {"recoveredCount", report.recovered_count},
                                   {"scannedCount", report.scanned_count},
                                   {"storeId", store_id},
                                   {"unsupportedCount", report.unsupported_count}};
        if (reservations) {
            output.emplace("activeReservationCount", report.active_count);
            output.emplace("recoveredReservationCount", report.recovered_count);
            output.emplace("scannedReservationCount", report.scanned_count);
            output.emplace("unsupportedReservationCount", report.unsupported_count);
        } else {
            output.emplace("activeLeaseCount", report.active_count);
            output.emplace("recoveredLeaseCount", report.recovered_count);
            output.emplace("scannedRecordCount", report.scanned_count);
            output.emplace("unsupportedLeaseCount", report.unsupported_count);
        }
        return response(request_id, result, std::move(output));
    }

    json::value diagnostics(const std::string& request_id, const json::value::object& arguments) {
        std::string store_id;
        auto& target = store(arguments, store_id);
        shared_memory_store::diagnostics_snapshot snapshot;
        const auto result = target.try_get_diagnostics(snapshot, wait_argument(arguments));
        json::value::object output{{"storeId", store_id}};
        if (result == shared_memory_store::status::success) {
            const auto& native = snapshot.native();
            json::value::array failures;
            failures.reserve(23);
            for (int index = 0; index < 23; ++index) failures.emplace_back(native.failure_counts[index]);
            output.insert({
                {"abortedReservationCount", native.aborted_reservation_count},
                {"activeLeaseCount", native.active_lease_count},
                {"activeLeaseRecoveryCount", native.active_lease_recovery_count},
                {"activeReservationCount", native.active_reservation_count},
                {"activeReservationRecoveryCount", native.active_reservation_recovery_count},
                {"capacityPressureCount", native.capacity_pressure_count},
                {"emptyIndexEntryCount", native.empty_index_entry_count},
                {"failedLeaseRecoveryCount", native.failed_lease_recovery_count},
                {"failedReservationRecoveryCount", native.failed_reservation_recovery_count},
                {"failureCounts", std::move(failures)},
                {"freeSlotCount", native.free_slot_count},
                {"indexCompactionCount", native.index_compaction_count},
                {"indexEntryCount", native.index_entry_count},
                {"lastFailureStatus", native.last_failure_status},
                {"lastObservedProbeLength", native.last_observed_probe_length},
                {"maxObservedProbeLength", native.max_observed_probe_length},
                {"occupiedIndexEntryCount", native.occupied_index_entry_count},
                {"pendingRemovalCount", native.pending_removal_count},
                {"publishedSlotCount", native.published_slot_count},
                {"recoveredLeaseCount", native.recovered_lease_count},
                {"recoveredReservationCount", native.recovered_reservation_count},
                {"slotCount", native.slot_count},
                {"tombstoneIndexEntryCount", native.tombstone_index_entry_count},
                {"tombstonePressureRatio", native.index_entry_count == 0
                    ? 0.0
                    : static_cast<double>(native.tombstone_index_entry_count) /
                      static_cast<double>(native.index_entry_count)},
                {"totalBytes", native.total_bytes},
                {"unsupportedLeaseRecoveryCount", native.unsupported_lease_recovery_count},
                {"unsupportedReservationRecoveryCount", native.unsupported_reservation_recovery_count},
                {"usableIndexCapacity", native.usable_index_capacity}
            });
        }
        return response(request_id, result, std::move(output));
    }

    std::unordered_map<std::string, shared_memory_store::memory_store> stores_;
    std::unordered_map<std::string, lease_entry> leases_;
    std::unordered_map<std::string, reservation_entry> reservations_;
};

} // namespace

int main() {
    std::ios::sync_with_stdio(false);
    std::cin.tie(nullptr);

    agent participant;
    std::string line;
    while (std::getline(std::cin, line)) {
        std::string request_id;
        json::value output;
        try {
            const auto request = json::parser(line).parse();
            if (const auto* root = std::get_if<json::value::object>(&request.data)) {
                if (const auto* id = find(*root, "id")) {
                    if (const auto* text = std::get_if<std::string>(&id->data)) request_id = *text;
                }
            }
            output = participant.handle(request);
        } catch (const protocol_error& exception) {
            const bool unsupported = exception.code() == "unsupported_command";
            output = failure(request_id, unsupported ? -2 : -1,
                             unsupported ? "UnsupportedCommand" : "ProtocolError",
                             exception.code(), exception.what());
        } catch (const std::exception& exception) {
            output = failure(request_id, -1, "ProtocolError", "invalid_request", exception.what());
        } catch (...) {
            output = failure(request_id, -1, "ProtocolError", "invalid_request", "Unknown request-processing failure.");
        }
        std::cout << json::dump(output) << '\n' << std::flush;
    }
    return 0;
}
