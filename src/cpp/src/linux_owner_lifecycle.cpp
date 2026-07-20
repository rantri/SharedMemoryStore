#include "linux_owner_lifecycle.hpp"

#if !defined(_WIN32)

#include <algorithm>
#include <array>
#include <cerrno>
#include <charconv>
#include <csignal>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <dirent.h>
#include <fcntl.h>
#include <fstream>
#include <limits>
#include <mutex>
#include <random>
#include <string>
#include <string_view>
#include <sys/file.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>
#include <utility>
#include <vector>

namespace sms::detail {
namespace {

constexpr std::string_view artifact_directory_suffix = ".artifacts";
constexpr std::string_view anchor_prefix = "anchor.";
constexpr std::string_view release_prefix = "released.";
constexpr std::string_view release_ready_suffix = ".ready";
constexpr std::size_t owner_line_limit = 1024;
constexpr std::size_t owner_file_limit = 4U * 1024U * 1024U;
constexpr std::size_t marker_file_limit = 1024;

struct PathParts {
    std::string directory;
    std::string name;
};

struct FileRead {
    sms_status status{SMS_STATUS_UNKNOWN_FAILURE};
    bool exists{};
    std::string bytes;
};

enum class ProcessEvidence : std::int32_t {
    stale,
    live,
    ambiguous
};

sms_status status_from_errno(int error) noexcept {
    if (error == EACCES || error == EPERM) return SMS_STATUS_ACCESS_DENIED;
    return SMS_STATUS_UNKNOWN_FAILURE;
}

PathParts split_path(std::string_view raw) {
    const auto slash = raw.find_last_of('/');
    if (slash == std::string_view::npos) {
        return PathParts{".", std::string(raw)};
    }
    if (slash == 0) {
        return PathParts{"/", std::string(raw.substr(1))};
    }
    return PathParts{
        std::string(raw.substr(0, slash)),
        std::string(raw.substr(slash + 1))};
}

bool is_lower_hex_token(std::string_view token) noexcept {
    if (token.size() != 32) return false;
    return std::all_of(token.begin(), token.end(), [](char value) {
        return (value >= '0' && value <= '9') ||
            (value >= 'a' && value <= 'f');
    });
}

bool is_safe_line(std::string_view line) noexcept {
    return !line.empty() && line.size() <= owner_line_limit &&
        line.find('\0') == std::string_view::npos &&
        line.find('\r') == std::string_view::npos &&
        line.find('\n') == std::string_view::npos;
}

sms_status ensure_private_directory(std::string_view child_path) noexcept {
    try {
        const auto parts = split_path(child_path);
        struct stat information{};
        if (::lstat(parts.directory.c_str(), &information) != 0) {
            const auto error = errno;
            if (error != ENOENT || ::mkdir(parts.directory.c_str(), 0700) != 0) {
                return status_from_errno(error == ENOENT ? errno : error);
            }
            if (::lstat(parts.directory.c_str(), &information) != 0) {
                return status_from_errno(errno);
            }
        }
        if (S_ISLNK(information.st_mode) || !S_ISDIR(information.st_mode)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        if (::chmod(parts.directory.c_str(), 0700) != 0) {
            return status_from_errno(errno);
        }
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

std::string artifact_directory_path(std::string_view owners_path) {
    return std::string(owners_path) + std::string(artifact_directory_suffix);
}

sms_status ensure_artifact_directory(std::string_view owners_path) noexcept {
    try {
        return ensure_private_directory(
            artifact_directory_path(owners_path) + "/artifact");
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

bool fill_random(std::array<std::uint8_t, 16>& bytes) noexcept {
    try {
        std::random_device source;
        for (auto& value : bytes) {
            value = static_cast<std::uint8_t>(source());
        }
        return true;
    } catch (...) {
        return false;
    }
}

std::string random_token() noexcept {
    try {
        std::array<std::uint8_t, 16> bytes{};
        if (!fill_random(bytes)) return {};
        // Match a conventional Guid.NewGuid/UUID-v4 token while retaining the
        // protocol's lowercase 32-hex textual form.
        bytes[6] = static_cast<std::uint8_t>((bytes[6] & 0x0fU) | 0x40U);
        bytes[8] = static_cast<std::uint8_t>((bytes[8] & 0x3fU) | 0x80U);
        constexpr char hexadecimal[] = "0123456789abcdef";
        std::string result(32, '0');
        for (std::size_t index = 0; index < bytes.size(); ++index) {
            result[index * 2] = hexadecimal[(bytes[index] >> 4U) & 0x0fU];
            result[index * 2 + 1] = hexadecimal[bytes[index] & 0x0fU];
        }
        return result;
    } catch (...) {
        return {};
    }
}

std::string process_start_token(std::int32_t process_id) noexcept {
    try {
        std::ifstream input("/proc/" + std::to_string(process_id) + "/stat");
        std::string stat;
        std::getline(input, stat);
        const auto command_end = stat.rfind(')');
        if (command_end == std::string::npos || command_end + 2 >= stat.size()) {
            return {};
        }
        const auto fields_text = stat.substr(command_end + 2);
        std::size_t start{};
        for (std::int32_t index = 0; index <= 19; ++index) {
            while (start < fields_text.size() && fields_text[start] == ' ') ++start;
            const auto end = fields_text.find(' ', start);
            if (index == 19) {
                const auto token = fields_text.substr(
                    start,
                    end == std::string::npos ? std::string::npos : end - start);
                return token.empty() ? std::string{} : "proc-" + token;
            }
            if (end == std::string::npos) return {};
            start = end + 1;
        }
    } catch (...) {
    }
    return {};
}

ProcessEvidence classify_process(
    std::int32_t process_id,
    std::string_view expected_start_token) noexcept {
    if (process_id <= 0) return ProcessEvidence::stale;
    if (::kill(process_id, 0) != 0) {
        const auto error = errno;
        if (error == ESRCH) return ProcessEvidence::stale;
        if (error != EPERM) return ProcessEvidence::ambiguous;
    }
    if (expected_start_token.empty()) return ProcessEvidence::ambiguous;
    const auto observed = process_start_token(process_id);
    if (observed.empty()) return ProcessEvidence::ambiguous;
    return observed == expected_start_token
        ? ProcessEvidence::live
        : ProcessEvidence::stale;
}

FileRead read_regular_file(
    std::string_view raw_path,
    std::size_t maximum_bytes) noexcept {
    FileRead result{};
    std::string path;
    try {
        path = std::string(raw_path);
    } catch (...) {
        result.status = SMS_STATUS_UNKNOWN_FAILURE;
        return result;
    }
    const auto descriptor = ::open(
        path.c_str(), O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    if (descriptor < 0) {
        const auto error = errno;
        if (error == ENOENT) {
            result.status = SMS_STATUS_SUCCESS;
            return result;
        }
        result.status = error == ELOOP
            ? SMS_STATUS_CORRUPT_STORE
            : status_from_errno(error);
        return result;
    }

    result.exists = true;
    struct stat information{};
    if (::fstat(descriptor, &information) != 0) {
        result.status = status_from_errno(errno);
        ::close(descriptor);
        return result;
    }
    if (!S_ISREG(information.st_mode) || information.st_size < 0 ||
        static_cast<std::uint64_t>(information.st_size) > maximum_bytes) {
        result.status = SMS_STATUS_CORRUPT_STORE;
        ::close(descriptor);
        return result;
    }

    try {
        result.bytes.reserve(static_cast<std::size_t>(information.st_size));
        std::array<char, 4096> buffer{};
        for (;;) {
            const auto count = ::read(descriptor, buffer.data(), buffer.size());
            if (count == 0) break;
            if (count < 0) {
                if (errno == EINTR) continue;
                result.status = status_from_errno(errno);
                ::close(descriptor);
                return result;
            }
            if (result.bytes.size() + static_cast<std::size_t>(count) >
                maximum_bytes) {
                result.status = SMS_STATUS_CORRUPT_STORE;
                ::close(descriptor);
                return result;
            }
            result.bytes.append(buffer.data(), static_cast<std::size_t>(count));
        }
    } catch (...) {
        result.status = SMS_STATUS_UNKNOWN_FAILURE;
        ::close(descriptor);
        return result;
    }

    ::close(descriptor);
    result.status = SMS_STATUS_SUCCESS;
    return result;
}

sms_status read_owner_lines(
    std::string_view owners_path,
    std::vector<std::string>& owners) noexcept {
    owners.clear();
    auto file = read_regular_file(owners_path, owner_file_limit);
    if (file.status != SMS_STATUS_SUCCESS || !file.exists || file.bytes.empty()) {
        return file.status;
    }
    if (file.bytes.find('\0') != std::string::npos ||
        file.bytes.find('\r') != std::string::npos) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    try {
        std::size_t start{};
        while (start < file.bytes.size()) {
            const auto end = file.bytes.find('\n', start);
            const auto length = (end == std::string::npos
                ? file.bytes.size()
                : end) - start;
            if (length == 0 || length > owner_line_limit) {
                // A single final newline is represented by start advancing to
                // size and never enters this branch; internal blank lines fail
                // closed rather than being normalized away.
                return SMS_STATUS_CORRUPT_STORE;
            }
            owners.emplace_back(file.bytes.substr(start, length));
            if (end == std::string::npos) break;
            start = end + 1;
        }
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        owners.clear();
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

bool write_all(int descriptor, std::string_view bytes) noexcept {
    std::size_t offset{};
    while (offset < bytes.size()) {
        const auto count = ::write(
            descriptor, bytes.data() + offset, bytes.size() - offset);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return false;
        offset += static_cast<std::size_t>(count);
    }
    return true;
}

bool sync_directory(std::string_view child_path) noexcept {
    try {
        const auto parts = split_path(child_path);
        const auto descriptor = ::open(
            parts.directory.c_str(),
            O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor < 0) return false;
        int result{};
        do {
            result = ::fsync(descriptor);
        } while (result != 0 && errno == EINTR);
        ::close(descriptor);
        return result == 0;
    } catch (...) {
        return false;
    }
}

sms_status validate_replacement_target(std::string_view raw_path) noexcept {
    try {
        const std::string path(raw_path);
        struct stat information{};
        if (::lstat(path.c_str(), &information) != 0) {
            return errno == ENOENT ? SMS_STATUS_SUCCESS : status_from_errno(errno);
        }
        return !S_ISLNK(information.st_mode) && S_ISREG(information.st_mode)
            ? SMS_STATUS_SUCCESS
            : SMS_STATUS_CORRUPT_STORE;
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status atomic_write_owners(
    std::string_view owners_path,
    const std::vector<std::string>& owners) noexcept {
    std::string bytes;
    std::string target;
    std::string temporary;
    int descriptor{-1};
    bool owns_temporary{};
    try {
        auto status = ensure_private_directory(owners_path);
        if (status != SMS_STATUS_SUCCESS) return status;
        status = validate_replacement_target(owners_path);
        if (status != SMS_STATUS_SUCCESS) return status;

        target = std::string(owners_path);
        std::size_t total{};
        for (const auto& owner : owners) {
            if (!is_safe_line(owner) ||
                total > owner_file_limit - owner.size() - 1U) {
                return SMS_STATUS_CORRUPT_STORE;
            }
            total += owner.size() + 1U;
        }
        bytes.reserve(total);
        for (const auto& owner : owners) {
            bytes.append(owner);
            bytes.push_back('\n');
        }

        temporary = target + ".tmp";
        status = validate_replacement_target(temporary);
        if (status != SMS_STATUS_SUCCESS) return status;
        descriptor = ::open(
            temporary.c_str(),
            O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC | O_NOFOLLOW,
            0600);
        if (descriptor >= 0) owns_temporary = true;
        if (descriptor < 0) return status_from_errno(errno);

        bool success = ::fchmod(descriptor, 0600) == 0 &&
            write_all(descriptor, bytes);
        if (success) {
            int result{};
            do {
                result = ::fsync(descriptor);
            } while (result != 0 && errno == EINTR);
            success = result == 0;
        }
        const auto write_error = errno;
        if (::close(descriptor) != 0) success = false;
        descriptor = -1;
        if (success) {
            if (::rename(temporary.c_str(), target.c_str()) != 0) {
                success = false;
            } else {
                owns_temporary = false;
            }
        }
        if (success && !sync_directory(owners_path)) success = false;
        if (!success) {
            if (owns_temporary) ::unlink(temporary.c_str());
            return status_from_errno(write_error == 0 ? errno : write_error);
        }
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        if (descriptor >= 0) ::close(descriptor);
        if (owns_temporary) ::unlink(temporary.c_str());
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status enumerate_matching(
    std::string_view owners_path,
    std::string_view prefix,
    std::vector<std::string>& paths) noexcept {
    paths.clear();
    DIR* directory{};
    try {
        const auto status = ensure_artifact_directory(owners_path);
        if (status != SMS_STATUS_SUCCESS) return status;
        const auto artifact_directory = artifact_directory_path(owners_path);
        directory = ::opendir(artifact_directory.c_str());
        if (directory == nullptr) {
            return errno == ENOENT ? SMS_STATUS_SUCCESS : status_from_errno(errno);
        }
        errno = 0;
        while (auto* entry = ::readdir(directory)) {
            const std::string_view name(entry->d_name);
            if (name.starts_with(prefix)) {
                paths.push_back(artifact_directory + "/" + std::string(name));
            }
            errno = 0;
        }
        const auto read_error = errno;
        ::closedir(directory);
        directory = nullptr;
        if (read_error != 0) return status_from_errno(read_error);
        std::sort(paths.begin(), paths.end());
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        if (directory != nullptr) ::closedir(directory);
        paths.clear();
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status finalized_markers(
    std::string_view owners_path,
    std::vector<std::string>& markers) noexcept {
    std::vector<std::string> candidates;
    const auto status = enumerate_matching(owners_path, release_prefix, candidates);
    if (status != SMS_STATUS_SUCCESS) return status;
    try {
        for (auto& path : candidates) {
            const auto name = split_path(path).name;
            if (name.ends_with(release_ready_suffix)) {
                // Every artifact shaped like a finalized marker is a protocol
                // record.  Token/metadata validation happens during replay and
                // malformed state fails the cold operation closed.
                markers.push_back(std::move(path));
            }
        }
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        markers.clear();
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status read_release_marker(
    std::string_view marker_path,
    std::string& exact_owner) noexcept {
    exact_owner.clear();
    auto file = read_regular_file(marker_path, marker_file_limit);
    if (file.status != SMS_STATUS_SUCCESS || !file.exists || file.bytes.empty()) {
        return file.status == SMS_STATUS_SUCCESS
            ? SMS_STATUS_CORRUPT_STORE
            : file.status;
    }
    if (!file.bytes.empty() && file.bytes.back() == '\n') file.bytes.pop_back();
    if (!is_safe_line(file.bytes)) return SMS_STATUS_CORRUPT_STORE;

    LinuxOwnerRecord record{};
    if (!LinuxOwnerLifecycle::parse_exact_owner_line(file.bytes, record)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    try {
        const auto marker_name = split_path(marker_path).name;
        const auto prefix = std::string(release_prefix);
        if (!marker_name.starts_with(prefix) ||
            !marker_name.ends_with(release_ready_suffix)) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        const auto token = std::string_view(marker_name).substr(
            prefix.size(),
            marker_name.size() - prefix.size() - release_ready_suffix.size());
        if (!is_lower_hex_token(token) || token != record.owner_token) {
            return SMS_STATUS_CORRUPT_STORE;
        }
        exact_owner = std::move(file.bytes);
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        exact_owner.clear();
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

bool owner_is_live_or_ambiguous(
    std::string_view owners_path,
    std::string_view line) noexcept {
    LinuxOwnerRecord record{};
    if (!LinuxOwnerLifecycle::parse_exact_owner_line(line, record)) {
        // Unrecognized evidence can never authorize resource deletion.
        return true;
    }
    const auto anchor = LinuxOwnerAnchor::probe(owners_path, record.owner_token);
    if (anchor == LinuxOwnerAnchorState::locked ||
        anchor == LinuxOwnerAnchorState::ambiguous) {
        return true;
    }
    if (anchor == LinuxOwnerAnchorState::unlocked) return false;
    return classify_process(record.process_id, record.process_start_token) !=
        ProcessEvidence::stale;
}

void remove_unlocked_anchor(std::string_view raw_path) noexcept {
    std::string path;
    try {
        path = std::string(raw_path);
    } catch (...) {
        // Cleanup is conservative: allocation uncertainty retains evidence.
        return;
    }
    const auto descriptor = ::open(
        path.c_str(), O_RDWR | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
    if (descriptor < 0) return;
    struct stat opened{};
    if (::fstat(descriptor, &opened) != 0 || !S_ISREG(opened.st_mode)) {
        ::close(descriptor);
        return;
    }
    if (::flock(descriptor, LOCK_EX | LOCK_NB) != 0) {
        ::close(descriptor);
        return;
    }
    struct stat named{};
    if (::lstat(path.c_str(), &named) == 0 && !S_ISLNK(named.st_mode) &&
        S_ISREG(named.st_mode) && named.st_dev == opened.st_dev &&
        named.st_ino == opened.st_ino) {
        ::unlink(path.c_str());
    }
    ::flock(descriptor, LOCK_UN);
    ::close(descriptor);
}

bool exact_anchor_name(
    std::string_view path,
    std::string& token) {
    const auto name = split_path(path).name;
    const auto prefix = std::string(anchor_prefix);
    if (!name.starts_with(prefix)) return false;
    const auto candidate = std::string_view(name).substr(prefix.size());
    if (!is_lower_hex_token(candidate)) return false;
    token.assign(candidate);
    return true;
}

bool unlink_regular_exact(std::string_view raw_path) noexcept {
    try {
        const std::string path(raw_path);
        struct stat information{};
        if (::lstat(path.c_str(), &information) != 0) return errno == ENOENT;
        if (S_ISLNK(information.st_mode) || !S_ISREG(information.st_mode)) {
            return false;
        }
        return ::unlink(path.c_str()) == 0 || errno == ENOENT;
    } catch (...) {
        // Cleanup is conservative: allocation uncertainty retains evidence.
        return false;
    }
}

} // namespace

LinuxOwnerAnchor::~LinuxOwnerAnchor() {
    release_and_remove();
}

void LinuxOwnerAnchor::release_and_remove() noexcept {
    const auto descriptor = std::exchange(descriptor_, -1);
    if (descriptor < 0) return;
    struct stat opened{};
    struct stat named{};
    if (::fstat(descriptor, &opened) == 0 &&
        ::lstat(path_.c_str(), &named) == 0 &&
        !S_ISLNK(named.st_mode) && S_ISREG(named.st_mode) &&
        named.st_dev == opened.st_dev && named.st_ino == opened.st_ino) {
        ::unlink(path_.c_str());
    }
    ::flock(descriptor, LOCK_UN);
    ::close(descriptor);
}

sms_status LinuxOwnerAnchor::create(
    std::string_view owners_path,
    std::string_view owner_token,
    std::unique_ptr<LinuxOwnerAnchor>& result) noexcept {
    result.reset();
    if (!is_lower_hex_token(owner_token)) return SMS_STATUS_CORRUPT_STORE;
    const auto directory_status = ensure_artifact_directory(owners_path);
    if (directory_status != SMS_STATUS_SUCCESS) return directory_status;
    std::string path;
    std::string token;
    int descriptor{-1};
    bool owns_path{};
    try {
        path = artifact_path(owners_path, owner_token);
        token = std::string(owner_token);
        descriptor = ::open(
            path.c_str(),
            O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
            0600);
        if (descriptor < 0) {
            return errno == EEXIST
                ? SMS_STATUS_STORE_BUSY
                : status_from_errno(errno);
        }
        owns_path = true;
        bool success = ::fchmod(descriptor, 0600) == 0;
        struct stat information{};
        success = success && ::fstat(descriptor, &information) == 0 &&
            S_ISREG(information.st_mode) &&
            ::flock(descriptor, LOCK_EX | LOCK_NB) == 0;
        if (!success) {
            const auto error = errno;
            ::close(descriptor);
            descriptor = -1;
            ::unlink(path.c_str());
            owns_path = false;
            return status_from_errno(error);
        }
        auto created = std::unique_ptr<LinuxOwnerAnchor>(
            new LinuxOwnerAnchor(
                descriptor, std::move(path), std::move(token)));
        descriptor = -1;
        owns_path = false;
        result = std::move(created);
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        if (descriptor >= 0) ::close(descriptor);
        if (owns_path) ::unlink(path.c_str());
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

LinuxOwnerAnchorState LinuxOwnerAnchor::probe(
    std::string_view owners_path,
    std::string_view owner_token) noexcept {
    if (!is_lower_hex_token(owner_token)) {
        return LinuxOwnerAnchorState::ambiguous;
    }
    if (ensure_artifact_directory(owners_path) != SMS_STATUS_SUCCESS) {
        return LinuxOwnerAnchorState::ambiguous;
    }
    try {
        const auto path = artifact_path(owners_path, owner_token);
        const auto descriptor = ::open(
            path.c_str(), O_RDWR | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
        if (descriptor < 0) {
            return errno == ENOENT
                ? LinuxOwnerAnchorState::missing
                : LinuxOwnerAnchorState::ambiguous;
        }
        struct stat information{};
        if (::fstat(descriptor, &information) != 0 ||
            !S_ISREG(information.st_mode)) {
            ::close(descriptor);
            return LinuxOwnerAnchorState::ambiguous;
        }
        if (::flock(descriptor, LOCK_EX | LOCK_NB) == 0) {
            ::flock(descriptor, LOCK_UN);
            ::close(descriptor);
            return LinuxOwnerAnchorState::unlocked;
        }
        const auto error = errno;
        ::close(descriptor);
        return error == EWOULDBLOCK || error == EAGAIN
            ? LinuxOwnerAnchorState::locked
            : LinuxOwnerAnchorState::ambiguous;
    } catch (...) {
        return LinuxOwnerAnchorState::ambiguous;
    }
}

std::string LinuxOwnerAnchor::artifact_path(
    std::string_view owners_path,
    std::string_view owner_token) {
    return artifact_directory_path(owners_path) + "/" +
        std::string(anchor_prefix) + std::string(owner_token);
}

sms_status LinuxOwnerLifecycle::create_current_owner(
    std::string_view owners_path,
    LinuxOwnerRecord& record,
    std::unique_ptr<LinuxOwnerAnchor>& anchor) noexcept {
    record = {};
    anchor.reset();
    const auto process_id = static_cast<std::int64_t>(::getpid());
    if (process_id <= 0 || process_id > std::numeric_limits<std::int32_t>::max()) {
        return SMS_STATUS_UNSUPPORTED_PLATFORM;
    }
    const auto start_token = process_start_token(static_cast<std::int32_t>(process_id));
    if (start_token.empty()) return SMS_STATUS_UNSUPPORTED_PLATFORM;
    for (std::int32_t attempt = 0; attempt < 16; ++attempt) {
        const auto token = random_token();
        if (token.empty()) return SMS_STATUS_UNKNOWN_FAILURE;
        auto status = LinuxOwnerAnchor::create(owners_path, token, anchor);
        if (status == SMS_STATUS_STORE_BUSY) continue;
        if (status != SMS_STATUS_SUCCESS) return status;
        try {
            record.process_id = static_cast<std::int32_t>(process_id);
            record.process_start_token = start_token;
            record.owner_token = token;
            record.line = std::to_string(process_id) + ":" + start_token + ":" + token;
            return SMS_STATUS_SUCCESS;
        } catch (...) {
            anchor.reset();
            record = {};
            return SMS_STATUS_UNKNOWN_FAILURE;
        }
    }
    return SMS_STATUS_STORE_BUSY;
}

sms_status LinuxOwnerLifecycle::prepare(
    std::string_view owners_path,
    LinuxOwnerSnapshot& snapshot) noexcept {
    snapshot = {};
    auto status = reconcile_release_markers(owners_path);
    if (status != SMS_STATUS_SUCCESS) return status;
    std::vector<std::string> committed;
    status = read_owner_lines(owners_path, committed);
    if (status != SMS_STATUS_SUCCESS) return status;
    bool has_live{};
    for (const auto& owner : committed) {
        if (owner_is_live_or_ambiguous(owners_path, owner)) {
            has_live = true;
            break;
        }
    }
    if (!has_live) {
        committed.clear();
        status = atomic_write_owners(owners_path, committed);
        if (status != SMS_STATUS_SUCCESS) return status;
    }
    sweep_unreferenced_anchors(owners_path, committed);
    snapshot.committed_owners = std::move(committed);
    snapshot.has_live_owner = has_live;
    return SMS_STATUS_SUCCESS;
}

sms_status LinuxOwnerLifecycle::commit_registration(
    std::string_view owners_path,
    const std::vector<std::string>& committed_owners,
    std::string_view exact_owner_line) noexcept {
    LinuxOwnerRecord record{};
    if (!parse_exact_owner_line(exact_owner_line, record)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    try {
        auto next = committed_owners;
        if (std::find(next.begin(), next.end(), exact_owner_line) == next.end()) {
            next.emplace_back(exact_owner_line);
        }
        return atomic_write_owners(owners_path, next);
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status LinuxOwnerLifecycle::remove_exact_under_lifecycle(
    std::string_view owners_path,
    std::string_view exact_owner_line,
    bool& no_owners_remain) noexcept {
    no_owners_remain = false;
    LinuxOwnerRecord record{};
    if (!parse_exact_owner_line(exact_owner_line, record)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    auto status = reconcile_release_markers(owners_path);
    if (status != SMS_STATUS_SUCCESS) return status;
    std::vector<std::string> committed;
    status = read_owner_lines(owners_path, committed);
    if (status != SMS_STATUS_SUCCESS) return status;
    try {
        std::vector<std::string> live;
        live.reserve(committed.size());
        for (const auto& owner : committed) {
            if (owner != exact_owner_line &&
                owner_is_live_or_ambiguous(owners_path, owner)) {
                live.push_back(owner);
            }
        }
        status = atomic_write_owners(owners_path, live);
        if (status != SMS_STATUS_SUCCESS) return status;
        sweep_unreferenced_anchors(owners_path, live);
        no_owners_remain = live.empty();
        return SMS_STATUS_SUCCESS;
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

sms_status LinuxOwnerLifecycle::reconcile_release_markers(
    std::string_view owners_path) noexcept {
    std::vector<std::string> markers;
    auto status = finalized_markers(owners_path, markers);
    if (status != SMS_STATUS_SUCCESS || markers.empty()) return status;
    std::vector<std::string> owners;
    status = read_owner_lines(owners_path, owners);
    if (status != SMS_STATUS_SUCCESS) return status;
    try {
        for (const auto& marker : markers) {
            std::string exact_owner;
            status = read_release_marker(marker, exact_owner);
            if (status != SMS_STATUS_SUCCESS) return status;
            owners.erase(
                std::remove(owners.begin(), owners.end(), exact_owner),
                owners.end());
        }
        status = atomic_write_owners(owners_path, owners);
        if (status != SMS_STATUS_SUCCESS) return status;
        sweep_unreferenced_anchors(owners_path, owners);
        for (const auto& marker : markers) {
            if (::unlink(marker.c_str()) != 0 && errno != ENOENT) {
                return status_from_errno(errno);
            }
        }
        return sync_directory(markers.front())
            ? SMS_STATUS_SUCCESS
            : status_from_errno(errno);
    } catch (...) {
        return SMS_STATUS_UNKNOWN_FAILURE;
    }
}

bool LinuxOwnerLifecycle::publish_release_marker(
    std::string_view owners_path,
    std::string_view exact_owner_line) noexcept {
    LinuxOwnerRecord record{};
    if (!parse_exact_owner_line(exact_owner_line, record) ||
        ensure_artifact_directory(owners_path) != SMS_STATUS_SUCCESS) {
        return false;
    }
    std::string final_path;
    std::string temporary;
    int descriptor{-1};
    bool owns_temporary{};
    try {
        final_path = release_marker_path(owners_path, record.owner_token);
        struct stat existing{};
        if (::lstat(final_path.c_str(), &existing) == 0) {
            if (S_ISLNK(existing.st_mode) || !S_ISREG(existing.st_mode)) return false;
            std::string observed;
            if (read_release_marker(final_path, observed) !=
                    SMS_STATUS_SUCCESS || observed != exact_owner_line) {
                return false;
            }
            return sync_directory(final_path);
        }
        if (errno != ENOENT) return false;

        const auto contents = std::string(exact_owner_line) + "\n";
        for (std::int32_t attempt = 0; attempt < 16; ++attempt) {
            const auto token = random_token();
            if (token.empty()) return false;
            temporary = final_path + ".tmp." + token;
            descriptor = ::open(
                temporary.c_str(),
                O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW,
                0600);
            if (descriptor >= 0) {
                owns_temporary = true;
                break;
            }
            if (errno != EEXIST) break;
        }
        if (descriptor < 0) return false;
        bool success = ::fchmod(descriptor, 0600) == 0 &&
            write_all(descriptor, contents);
        if (success) {
            int result{};
            do {
                result = ::fsync(descriptor);
            } while (result != 0 && errno == EINTR);
            success = result == 0;
        }
        if (::close(descriptor) != 0) success = false;
        descriptor = -1;
        if (success) {
            if (::rename(temporary.c_str(), final_path.c_str()) != 0) {
                success = false;
            } else {
                owns_temporary = false;
            }
        }
        if (success && ::chmod(final_path.c_str(), 0600) != 0) success = false;
        if (success && !sync_directory(final_path)) success = false;
        if (!success && owns_temporary) ::unlink(temporary.c_str());
        return success;
    } catch (...) {
        if (descriptor >= 0) ::close(descriptor);
        if (owns_temporary) ::unlink(temporary.c_str());
        return false;
    }
}

std::string LinuxOwnerLifecycle::release_marker_path(
    std::string_view owners_path,
    std::string_view owner_token) {
    return artifact_directory_path(owners_path) + "/" +
        std::string(release_prefix) + std::string(owner_token) +
        std::string(release_ready_suffix);
}

void LinuxOwnerLifecycle::sweep_unreferenced_anchors(
    std::string_view owners_path,
    const std::vector<std::string>& committed_owners) noexcept {
    try {
        std::vector<std::string> artifacts;
        if (enumerate_matching(owners_path, anchor_prefix, artifacts) !=
            SMS_STATUS_SUCCESS) {
            return;
        }
        std::vector<std::string> referenced;
        referenced.reserve(committed_owners.size());
        for (const auto& owner : committed_owners) {
            LinuxOwnerRecord record{};
            if (parse_exact_owner_line(owner, record)) {
                referenced.push_back(std::move(record.owner_token));
            }
        }
        for (const auto& artifact : artifacts) {
            std::string token;
            if (!exact_anchor_name(artifact, token) ||
                std::find(referenced.begin(), referenced.end(), token) !=
                    referenced.end()) {
                continue;
            }
            remove_unlocked_anchor(artifact);
        }
    } catch (...) {
        // Cleanup is conservative: uncertainty retains evidence.
    }
}

void LinuxOwnerLifecycle::delete_stale_owner_artifacts(
    std::string_view owners_path) noexcept {
    try {
        const std::vector<std::string> none;
        sweep_unreferenced_anchors(owners_path, none);
        (void)unlink_regular_exact(owners_path);
        (void)unlink_regular_exact(std::string(owners_path) + ".tmp");

        std::vector<std::string> candidates;
        if (enumerate_matching(owners_path, release_prefix, candidates) ==
            SMS_STATUS_SUCCESS) {
            const auto prefix = std::string(release_prefix);
            for (const auto& candidate : candidates) {
                const auto name = split_path(candidate).name;
                const auto remainder = std::string_view(name).substr(prefix.size());
                bool canonical = false;
                if (remainder.size() == 32 + release_ready_suffix.size() &&
                    remainder.ends_with(release_ready_suffix)) {
                    canonical = is_lower_hex_token(remainder.substr(0, 32));
                } else {
                    constexpr std::string_view temporary_segment = ".ready.tmp.";
                    if (remainder.size() == 32 + temporary_segment.size() + 32 &&
                        remainder.substr(32, temporary_segment.size()) ==
                            temporary_segment) {
                        canonical = is_lower_hex_token(remainder.substr(0, 32)) &&
                            is_lower_hex_token(remainder.substr(
                                32 + temporary_segment.size(), 32));
                    }
                }
                if (canonical) (void)unlink_regular_exact(candidate);
            }
        }
    } catch (...) {
    }
}

void LinuxOwnerLifecycle::retain_ambiguous_anchor(
    std::unique_ptr<LinuxOwnerAnchor> anchor) noexcept {
    if (!anchor) return;
    try {
        static std::mutex gate;
        static std::vector<std::unique_ptr<LinuxOwnerAnchor>> retained;
        std::lock_guard guard(gate);
        retained.push_back(std::move(anchor));
    } catch (...) {
        // If even conservative retention allocation fails, normal destruction
        // is the only remaining bounded-close behavior.  The exact owner line
        // itself remains on disk and therefore still fails conservatively.
    }
}

bool LinuxOwnerLifecycle::parse_exact_owner_line(
    std::string_view line,
    LinuxOwnerRecord& record) noexcept {
    record = {};
    if (!is_safe_line(line)) return false;
    const auto first = line.find(':');
    if (first == std::string_view::npos || first == 0) return false;
    const auto second = line.find(':', first + 1);
    if (second == std::string_view::npos || second == first + 1 ||
        line.find(':', second + 1) != std::string_view::npos) {
        return false;
    }
    const auto pid_text = line.substr(0, first);
    std::int32_t process_id{};
    const auto parsed = std::from_chars(
        pid_text.data(), pid_text.data() + pid_text.size(), process_id);
    if (parsed.ec != std::errc{} || parsed.ptr != pid_text.data() + pid_text.size() ||
        process_id <= 0 || pid_text.front() == '0') {
        return false;
    }
    const auto start_token = line.substr(first + 1, second - first - 1);
    const auto owner_token = line.substr(second + 1);
    if (start_token.find_first_of("\0\r\n:") != std::string_view::npos ||
        !is_lower_hex_token(owner_token)) {
        return false;
    }
    try {
        record.process_id = process_id;
        record.process_start_token.assign(start_token);
        record.owner_token.assign(owner_token);
        record.line.assign(line);
        return true;
    } catch (...) {
        record = {};
        return false;
    }
}

} // namespace sms::detail

#endif
