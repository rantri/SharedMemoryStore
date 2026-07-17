#include "platform_identity.hpp"

#include "internal.hpp"

#include <charconv>
#include <fstream>
#include <string>
#include <string_view>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <unistd.h>
#endif

namespace sms::detail {
namespace {

#if !defined(_WIN32)
bool read_linux_start_ticks(
    std::int32_t process_id,
    std::int64_t& value) noexcept {
    value = 0;
    try {
        std::ifstream input("/proc/" + std::to_string(process_id) + "/stat");
        std::string stat;
        std::getline(input, stat);
        const auto command_end = stat.rfind(')');
        if (command_end == std::string::npos || command_end + 2 >= stat.size()) {
            return false;
        }
        std::string_view fields(stat.data() + command_end + 2,
                                stat.size() - command_end - 2);
        for (std::int32_t index = 0; index <= 19; ++index) {
            const auto separator = fields.find(' ');
            const auto field = fields.substr(0, separator);
            if (index == 19) {
                const auto* first = field.data();
                const auto* last = first + field.size();
                const auto parsed = std::from_chars(first, last, value);
                return parsed.ec == std::errc{} && parsed.ptr == last && value > 0;
            }
            if (separator == std::string_view::npos) return false;
            fields.remove_prefix(separator + 1);
            while (!fields.empty() && fields.front() == ' ') fields.remove_prefix(1);
        }
    } catch (...) {
    }
    value = 0;
    return false;
}

bool read_linux_pid_namespace(std::uint64_t& value) noexcept {
    value = 0;
    char target[128]{};
    const auto length = ::readlink(
        "/proc/self/ns/pid", target, sizeof(target) - 1U);
    if (length <= 0 || static_cast<std::size_t>(length) >= sizeof(target)) {
        return false;
    }
    const std::string_view text(target, static_cast<std::size_t>(length));
    constexpr std::string_view prefix = "pid:[";
    if (!text.starts_with(prefix) || text.size() <= prefix.size() + 1U ||
        text.back() != ']') {
        return false;
    }
    const auto digits = text.substr(prefix.size(), text.size() - prefix.size() - 1U);
    const auto* first = digits.data();
    const auto* last = first + digits.size();
    const auto parsed = std::from_chars(first, last, value);
    if (parsed.ec != std::errc{} || parsed.ptr != last || value == 0) {
        value = 0;
        return false;
    }
    return true;
}
#endif

} // namespace

std::uint64_t capture_pid_namespace_id() noexcept {
#if defined(_WIN32)
    return 0;
#else
    std::uint64_t value{};
    return read_linux_pid_namespace(value) ? value : 0;
#endif
}

ParticipantIdentity capture_participant_identity() noexcept {
    ParticipantIdentity identity{};
    identity.process_id = current_process_id();
    if (identity.process_id <= 0) return identity;

#if defined(_WIN32)
    FILETIME created{};
    FILETIME exited{};
    FILETIME kernel{};
    FILETIME user{};
    if (GetProcessTimes(
            GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE) {
        const auto unsigned_value =
            (static_cast<std::uint64_t>(created.dwHighDateTime) << 32U) |
            created.dwLowDateTime;
        if (unsigned_value > 0 &&
            unsigned_value <= static_cast<std::uint64_t>(INT64_MAX)) {
            identity.identity_kind = identity_windows_creation_file_time;
            identity.process_start_value = static_cast<std::int64_t>(unsigned_value);
        }
    }
#else
    identity.pid_namespace_id = capture_pid_namespace_id();
    std::int64_t start_ticks{};
    if (identity.pid_namespace_id != 0 &&
        read_linux_start_ticks(identity.process_id, start_ticks)) {
        identity.identity_kind = identity_linux_proc_start_ticks;
        identity.process_start_value = start_ticks;
    } else {
        identity.identity_kind = identity_unknown;
        identity.process_start_value = 0;
        identity.pid_namespace_id = 0;
    }
#endif
    return identity;
}

} // namespace sms::detail
