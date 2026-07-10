#include "internal.hpp"

#if !defined(_WIN32)

#include <cerrno>
#include <csignal>
#include <fcntl.h>
#include <filesystem>
#include <fstream>
#include <random>
#include <sstream>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <thread>
#include <unordered_map>
#include <unistd.h>

namespace sms::detail {
namespace {

using clock_type = std::chrono::steady_clock;

class FileState {
public:
    explicit FileState(std::string path) : path_(std::move(path)) {
        fd_ = ::open(path_.c_str(), O_RDWR | O_CREAT | O_CLOEXEC, 0600);
        if (fd_ >= 0) ::fchmod(fd_, 0600);
    }
    ~FileState() { if (fd_ >= 0) ::close(fd_); }
    int fd() const noexcept { return fd_; }
    std::timed_mutex mutex;
private:
    std::string path_;
    int fd_{-1};
};

std::mutex file_states_gate;
std::unordered_map<std::string, std::weak_ptr<FileState>> file_states;

std::shared_ptr<FileState> get_file_state(const std::string& raw_path) {
    std::error_code error;
    auto path = std::filesystem::absolute(raw_path, error).lexically_normal().string();
    if (error) path = raw_path;
    std::lock_guard guard(file_states_gate);
    if (auto found = file_states[path].lock()) return found;
    auto created = std::make_shared<FileState>(path);
    file_states[path] = created;
    return created;
}

class LinuxFileLock final : public SharedLock {
public:
    explicit LinuxFileLock(std::shared_ptr<FileState> state) : state_(std::move(state)) {}
    ~LinuxFileLock() override { release(); }

    bool usable() const noexcept { return state_ && state_->fd() >= 0; }

    sms_status acquire(const Wait& wait) noexcept override {
        if (!usable()) return errno == EACCES ? SMS_STATUS_ACCESS_DENIED : SMS_STATUS_UNKNOWN_FAILURE;
        if (held_) return SMS_STATUS_SUCCESS;
        const auto started = clock_type::now();
        if (wait.infinite()) {
            state_->mutex.lock();
            local_held_ = true;
        } else if (wait.milliseconds == 0) {
            local_held_ = state_->mutex.try_lock();
        } else {
            local_held_ = state_->mutex.try_lock_for(std::chrono::milliseconds(wait.milliseconds));
        }
        if (!local_held_) return SMS_STATUS_STORE_BUSY;

        for (;;) {
            struct flock request{};
            request.l_type = F_WRLCK;
            request.l_whence = SEEK_SET;
            request.l_start = 0;
            request.l_len = 1;
            if (::fcntl(state_->fd(), F_SETLK, &request) == 0) {
                held_ = true;
                return SMS_STATUS_SUCCESS;
            }
            const auto error = errno;
            if (error != EACCES && error != EAGAIN) {
                release();
                if (error == EACCES || error == EPERM) return SMS_STATUS_ACCESS_DENIED;
                if (error == ENOSYS || error == ENOTSUP) return SMS_STATUS_UNSUPPORTED_PLATFORM;
                return SMS_STATUS_UNKNOWN_FAILURE;
            }
            if (!wait.infinite()) {
                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(clock_type::now() - started);
                if (elapsed.count() >= wait.milliseconds) {
                    release();
                    return SMS_STATUS_STORE_BUSY;
                }
                const auto remaining = std::chrono::milliseconds(wait.milliseconds) - elapsed;
                std::this_thread::sleep_for(std::min(std::chrono::milliseconds(10), remaining));
            } else {
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        }
    }

    void release() noexcept override {
        if (held_ && usable()) {
            struct flock request{};
            request.l_type = F_UNLCK;
            request.l_whence = SEEK_SET;
            request.l_start = 0;
            request.l_len = 1;
            ::fcntl(state_->fd(), F_SETLK, &request);
            held_ = false;
        }
        if (local_held_) {
            state_->mutex.unlock();
            local_held_ = false;
        }
    }

private:
    std::shared_ptr<FileState> state_;
    bool local_held_{};
    bool held_{};
};

std::unique_ptr<LinuxFileLock> open_lock(const std::string& path) {
    auto result = std::make_unique<LinuxFileLock>(get_file_state(path));
    return result->usable() ? std::move(result) : nullptr;
}

bool ensure_directory(const std::string& child_path) noexcept {
    try {
        const auto directory = std::filesystem::path(child_path).parent_path();
        struct stat information{};
        if (::lstat(directory.c_str(), &information) == 0) {
            if (S_ISLNK(information.st_mode) || !S_ISDIR(information.st_mode)) {
                errno = EACCES;
                return false;
            }
        } else {
            if (errno != ENOENT || ::mkdir(directory.c_str(), 0700) != 0) return false;
        }
        return ::chmod(directory.c_str(), 0700) == 0;
    } catch (...) {
        return false;
    }
}

bool exists(const std::string& path) noexcept {
    struct stat value{};
    return ::stat(path.c_str(), &value) == 0;
}

std::string process_start_token(std::int32_t pid) noexcept {
    try {
        std::ifstream input("/proc/" + std::to_string(pid) + "/stat");
        std::string stat;
        std::getline(input, stat);
        const auto command_end = stat.rfind(')');
        if (command_end == std::string::npos || command_end + 2 >= stat.size()) return {};
        std::istringstream fields(stat.substr(command_end + 2));
        std::string value;
        for (int index = 0; fields >> value; ++index) {
            if (index == 19) return "proc-" + value;
        }
    } catch (...) {
    }
    return {};
}

bool process_live(std::int32_t pid, std::string_view start_token) noexcept {
    if (pid <= 0) return false;
    if (::kill(pid, 0) != 0 && errno == ESRCH) return false;
    if (start_token.empty()) return true;
    const auto observed = process_start_token(pid);
    return observed.empty() || observed == start_token;
}

bool parse_owner(std::string_view line, std::int32_t& pid, std::string& token) noexcept {
    try {
        const auto first = line.find(':');
        const auto pid_text = line.substr(0, first);
        std::size_t used{};
        const auto parsed = std::stoll(std::string(pid_text), &used, 10);
        if (used != pid_text.size() || parsed < std::numeric_limits<std::int32_t>::min() ||
            parsed > std::numeric_limits<std::int32_t>::max()) return false;
        pid = static_cast<std::int32_t>(parsed);
        token.clear();
        if (first != std::string_view::npos) {
            const auto second = line.find(':', first + 1);
            if (second != std::string_view::npos) token = std::string(line.substr(first + 1, second - first - 1));
        }
        return true;
    } catch (...) {
        return false;
    }
}

std::vector<std::string> read_live_owners(const std::string& path) {
    std::vector<std::string> owners;
    std::ifstream input(path);
    std::string line;
    while (std::getline(input, line)) {
        while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' ' || line.back() == '\t')) line.pop_back();
        const auto first = line.find_first_not_of(" \t");
        if (first == std::string::npos) continue;
        line.erase(0, first);
        std::int32_t pid{};
        std::string token;
        if (parse_owner(line, pid, token) && process_live(pid, token)) owners.push_back(line);
    }
    return owners;
}

bool write_owners(const std::string& path, const std::vector<std::string>& owners) noexcept {
    const auto temporary = path + ".tmp";
    const auto fd = ::open(temporary.c_str(), O_WRONLY | O_CREAT | O_TRUNC | O_CLOEXEC, 0600);
    if (fd < 0) return false;
    bool success = ::fchmod(fd, 0600) == 0;
    for (const auto& owner : owners) {
        const auto line = owner + "\n";
        std::size_t offset{};
        while (success && offset < line.size()) {
            const auto written = ::write(fd, line.data() + offset, line.size() - offset);
            if (written <= 0) success = false;
            else offset += static_cast<std::size_t>(written);
        }
    }
    if (success) success = ::fsync(fd) == 0;
    ::close(fd);
    if (success) success = ::rename(temporary.c_str(), path.c_str()) == 0;
    if (!success) ::unlink(temporary.c_str());
    return success;
}

void delete_stale(const ResourceName& resource) noexcept {
    ::unlink(resource.linux_region_path.c_str());
    ::unlink(resource.linux_lock_path.c_str());
    ::unlink(resource.linux_owners_path.c_str());
    ::unlink((resource.linux_owners_path + ".tmp").c_str());
}

std::string random_hex() {
    std::random_device random;
    constexpr char hex[] = "0123456789abcdef";
    std::string result(32, '0');
    for (auto& value : result) value = hex[random() & 15U];
    return result;
}

std::string create_owner_record() {
    const auto pid = current_process_id();
    return std::to_string(pid) + ":" + process_start_token(pid) + ":" + random_hex();
}

void release_owner(const ResourceName& resource, const std::string& owner) noexcept {
    try {
        auto lifecycle = open_lock(resource.linux_lifecycle_path);
        if (!lifecycle || lifecycle->acquire(Wait{-1}) != SMS_STATUS_SUCCESS) return;
        auto owners = read_live_owners(resource.linux_owners_path);
        owners.erase(std::remove(owners.begin(), owners.end(), owner), owners.end());
        if (owners.empty()) delete_stale(resource);
        else write_owners(resource.linux_owners_path, owners);
        lifecycle->release();
    } catch (...) {
    }
}

class LinuxRegion final : public MappedRegion {
public:
    LinuxRegion(int fd, std::uint8_t* data, std::int64_t size, ResourceName resource, std::string owner)
        : fd_(fd), data_(data), size_(size), resource_(std::move(resource)), owner_(std::move(owner)) {}
    ~LinuxRegion() override { close(); }
    std::uint8_t* data() noexcept override { return data_; }
    std::int64_t size() const noexcept override { return size_; }
    void close() noexcept override {
        if (closed_) return;
        closed_ = true;
        if (data_ && data_ != MAP_FAILED) ::munmap(data_, static_cast<std::size_t>(size_));
        data_ = nullptr;
        if (fd_ >= 0) ::close(fd_);
        fd_ = -1;
        release_owner(resource_, owner_);
    }
private:
    int fd_{-1};
    std::uint8_t* data_{};
    std::int64_t size_{};
    ResourceName resource_;
    std::string owner_;
    bool closed_{};
};

sms_open_status map_lock_status(sms_status status) noexcept {
    switch (status) {
        case SMS_STATUS_SUCCESS: return SMS_OPEN_SUCCESS;
        case SMS_STATUS_STORE_BUSY: return SMS_OPEN_STORE_BUSY;
        case SMS_STATUS_OPERATION_CANCELED: return SMS_OPEN_OPERATION_CANCELED;
        case SMS_STATUS_ACCESS_DENIED: return SMS_OPEN_ACCESS_DENIED;
        case SMS_STATUS_UNSUPPORTED_PLATFORM: return SMS_OPEN_UNSUPPORTED_PLATFORM;
        default: return SMS_OPEN_MAPPING_FAILED;
    }
}

} // namespace

PlatformOpenResult platform_open(const ResourceName& resource, const Options& options, const Wait& wait) noexcept {
    PlatformOpenResult result{};
    try {
        if (!ensure_directory(resource.linux_region_path)) {
            result.status = (errno == EACCES || errno == EPERM) ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        auto lifecycle = open_lock(resource.linux_lifecycle_path);
        if (!lifecycle) {
            result.status = (errno == EACCES || errno == EPERM) ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        const auto lock_status = lifecycle->acquire(wait);
        if (lock_status != SMS_STATUS_SUCCESS) {
            result.status = map_lock_status(lock_status);
            return result;
        }

        auto owners = read_live_owners(resource.linux_owners_path);
        auto live_resource = exists(resource.linux_region_path) && !owners.empty();
        if (!live_resource) delete_stale(resource);
        if (options.open_mode == SMS_OPEN_MODE_CREATE_NEW && live_resource) {
            lifecycle->release();
            result.status = SMS_OPEN_ALREADY_EXISTS;
            return result;
        }
        if (options.open_mode == SMS_OPEN_MODE_OPEN_EXISTING && !live_resource) {
            lifecycle->release();
            result.status = SMS_OPEN_NOT_FOUND;
            return result;
        }

        const bool create = !live_resource;
        const auto flags = O_RDWR | O_CLOEXEC | (create ? (O_CREAT | O_EXCL) : 0);
        const auto fd = ::open(resource.linux_region_path.c_str(), flags, 0600);
        if (fd < 0) {
            lifecycle->release();
            result.status = errno == EEXIST ? SMS_OPEN_ALREADY_EXISTS :
                            (errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED);
            return result;
        }
        ::fchmod(fd, 0600);
        if (create) {
            if (::ftruncate(fd, options.total_bytes) != 0) {
                ::close(fd); delete_stale(resource); lifecycle->release();
                result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
                return result;
            }
        } else {
            struct stat information{};
            if (::fstat(fd, &information) != 0 || information.st_size < options.total_bytes) {
                ::close(fd); lifecycle->release();
                result.status = SMS_OPEN_INCOMPATIBLE_LAYOUT;
                return result;
            }
        }
        if (options.total_bytes <= 0 || static_cast<std::uint64_t>(options.total_bytes) > std::numeric_limits<std::size_t>::max()) {
            ::close(fd); lifecycle->release(); result.status = SMS_OPEN_INVALID_OPTIONS; return result;
        }
        auto* mapped = static_cast<std::uint8_t*>(::mmap(nullptr, static_cast<std::size_t>(options.total_bytes),
                                                         PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0));
        if (mapped == MAP_FAILED) {
            ::close(fd); if (create) delete_stale(resource); lifecycle->release();
            result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }

        const auto owner = create_owner_record();
        owners = read_live_owners(resource.linux_owners_path);
        owners.push_back(owner);
        if (!write_owners(resource.linux_owners_path, owners)) {
            ::munmap(mapped, static_cast<std::size_t>(options.total_bytes));
            ::close(fd); if (create) delete_stale(resource); lifecycle->release();
            result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        lifecycle->release();

        auto shared_lock = open_lock(resource.linux_lock_path);
        if (!shared_lock) {
            release_owner(resource, owner);
            ::munmap(mapped, static_cast<std::size_t>(options.total_bytes));
            ::close(fd);
            result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        result.region = std::make_unique<LinuxRegion>(fd, mapped, options.total_bytes, resource, owner);
        result.lock = std::move(shared_lock);
        result.status = SMS_OPEN_SUCCESS;
        return result;
    } catch (...) {
        result.status = SMS_OPEN_MAPPING_FAILED;
        return result;
    }
}

OwnerKind classify_process(std::int32_t pid) noexcept {
    if (pid <= 0) return OwnerKind::stale;
    if (pid == current_process_id()) return OwnerKind::current;
    if (::kill(pid, 0) == 0 || errno == EPERM) return OwnerKind::live;
    return errno == ESRCH ? OwnerKind::stale : OwnerKind::unsupported;
}

} // namespace sms::detail

#endif
