#include "internal.hpp"
#include "linux_owner_lifecycle.hpp"
#include "operation_budget.hpp"

#if !defined(_WIN32)

#include <cerrno>
#include <algorithm>
#include <csignal>
#include <fcntl.h>
#include <filesystem>
#include <sys/mman.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <thread>
#include <unordered_map>
#include <unistd.h>

#if !defined(F_OFD_SETLK)
#define F_OFD_SETLK 37
#endif

namespace sms::detail {
namespace {

using clock_type = std::chrono::steady_clock;

class FileState {
public:
    explicit FileState(std::string path) : path_(std::move(path)) {
        auto descriptor = ::open(
            path_.c_str(),
            O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK,
            0600);
        if (descriptor >= 0) {
            struct stat information{};
            if (::fstat(descriptor, &information) != 0 ||
                !S_ISREG(information.st_mode) ||
                ::fchmod(descriptor, 0600) != 0) {
                const auto error = errno == 0 ? EINVAL : errno;
                ::close(descriptor);
                descriptor = -1;
                errno = error;
            }
        }
        fd_.store(descriptor, std::memory_order_release);
    }
    ~FileState() { retire(); }
    int fd() const noexcept { return fd_.load(std::memory_order_acquire); }
    bool usable() const noexcept { return fd() >= 0; }
    void retire() noexcept {
        const auto descriptor = fd_.exchange(-1, std::memory_order_acq_rel);
        if (descriptor >= 0) ::close(descriptor);
    }
    std::timed_mutex mutex;
private:
    std::string path_;
    std::atomic<int> fd_{-1};
};

std::mutex file_states_gate;
std::unordered_map<std::string, std::weak_ptr<FileState>> file_states;

std::shared_ptr<FileState> get_file_state(const std::string& raw_path) {
    std::error_code error;
    auto path = std::filesystem::absolute(raw_path, error).lexically_normal().string();
    if (error) path = raw_path;
    std::lock_guard guard(file_states_gate);

    // The registry is process-local cold-path coordination, not permanent
    // mapped state. Remove every expired weak entry before lookup so churn
    // across unique public names cannot retain path strings/map nodes forever.
    for (auto iterator = file_states.begin(); iterator != file_states.end();) {
        if (iterator->second.expired()) {
            iterator = file_states.erase(iterator);
        } else {
            ++iterator;
        }
    }

    const auto existing = file_states.find(path);
    if (existing != file_states.end()) {
        if (auto found = existing->second.lock(); found && found->usable()) {
            return found;
        }
    }
    auto created = std::make_shared<FileState>(path);
    file_states.insert_or_assign(path, created);
    return created;
}

class LinuxFileLock final : public SharedLock {
public:
    explicit LinuxFileLock(std::shared_ptr<FileState> state) : state_(std::move(state)) {}
    ~LinuxFileLock() override { release(); }

    bool usable() const noexcept { return state_ && state_->usable(); }

    sms_status acquire(const Wait& wait) noexcept override {
        if (!usable()) return errno == EACCES ? SMS_STATUS_ACCESS_DENIED : SMS_STATUS_UNKNOWN_FAILURE;
        if (held_) return SMS_STATUS_SUCCESS;
        if (!wait.valid()) return SMS_STATUS_UNKNOWN_FAILURE;
        const auto started = clock_type::now();
        for (;;) {
            if (wait.cancellation != nullptr &&
                wait.cancellation->is_canceled()) {
                return SMS_STATUS_OPERATION_CANCELED;
            }
            if (state_->mutex.try_lock()) {
                local_held_ = true;
                break;
            }
            if (!wait.infinite()) {
                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
                    clock_type::now() - started);
                if (wait.milliseconds == 0 || elapsed.count() >= wait.milliseconds) {
                    return SMS_STATUS_STORE_BUSY;
                }
                const auto remaining =
                    std::chrono::milliseconds(wait.milliseconds) - elapsed;
                std::this_thread::sleep_for(
                    std::min(std::chrono::milliseconds(10), remaining));
            } else {
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
        }
        if (!usable()) {
            release();
            return SMS_STATUS_UNKNOWN_FAILURE;
        }

        for (;;) {
            if (wait.cancellation != nullptr &&
                wait.cancellation->is_canceled()) {
                release();
                return SMS_STATUS_OPERATION_CANCELED;
            }
            struct flock request{};
            request.l_type = F_WRLCK;
            request.l_whence = SEEK_SET;
            request.l_start = 0;
            request.l_len = 1;
            if (::fcntl(state_->fd(), F_OFD_SETLK, &request) == 0) {
                held_ = true;
                return SMS_STATUS_SUCCESS;
            }
            const auto error = errno;
            if (error != EACCES && error != EAGAIN) {
                release();
                if (error == EACCES || error == EPERM) return SMS_STATUS_ACCESS_DENIED;
                if (error == EINVAL || error == ENOSYS || error == ENOTSUP || error == EOPNOTSUPP) {
                    return SMS_STATUS_UNSUPPORTED_PLATFORM;
                }
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
            int result{};
            do {
                result = ::fcntl(state_->fd(), F_OFD_SETLK, &request);
            } while (result != 0 && errno == EINTR);
            if (result != 0) {
                // Closing a descriptor releases every OFD lock attached to it.
                // Retire before the local gate is reopened so local work never
                // proceeds while foreign participants remain excluded.
                state_->retire();
            }
            held_ = false;
        }
        if (local_held_) {
            local_held_ = false;
            state_->mutex.unlock();
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

sms_status inspect_regular_file(
    const std::string& path,
    bool& present) noexcept {
    present = false;
    struct stat information{};
    if (::lstat(path.c_str(), &information) != 0) {
        if (errno == ENOENT) return SMS_STATUS_SUCCESS;
        return errno == EACCES || errno == EPERM
            ? SMS_STATUS_ACCESS_DENIED
            : SMS_STATUS_UNKNOWN_FAILURE;
    }
    if (S_ISLNK(information.st_mode) || !S_ISREG(information.st_mode)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    present = true;
    return SMS_STATUS_SUCCESS;
}

void delete_stale(const ResourceName& resource) noexcept {
    ::unlink(resource.linux_region_path.c_str());
    // Keep the operation-lock inode as a permanent rendezvous, matching the
    // lifecycle inode. It has no generation state and prevents pathname split.
    LinuxOwnerLifecycle::delete_stale_owner_artifacts(
        resource.linux_owners_path);
}

void finalize_owner_while_lifecycle_held(
    const ResourceName& resource,
    const LinuxOwnerRecord& owner,
    std::unique_ptr<LinuxOwnerAnchor> anchor) noexcept {
    bool safely_recorded{};
    bool no_owners{};
    try {
        const auto status =
            LinuxOwnerLifecycle::remove_exact_under_lifecycle(
                resource.linux_owners_path,
                owner.line,
                no_owners);
        if (status == SMS_STATUS_SUCCESS) {
            if (no_owners) delete_stale(resource);
            safely_recorded = true;
        }
    } catch (...) {
    }

    // A durable exact marker remains the fail-closed fallback even when the
    // already-held lifecycle transaction cannot replace its sidecar.
    if (!safely_recorded) {
        safely_recorded = LinuxOwnerLifecycle::publish_release_marker(
            resource.linux_owners_path,
            owner.line);
    }
    if (safely_recorded) {
        if (anchor) anchor->release_and_remove();
    } else {
        LinuxOwnerLifecycle::retain_ambiguous_anchor(std::move(anchor));
    }
}

void release_owner(
    const ResourceName& resource,
    const LinuxOwnerRecord& owner,
    std::unique_ptr<LinuxOwnerAnchor> anchor) noexcept {
    try {
        auto lifecycle = open_lock(resource.linux_lifecycle_path);
        if (lifecycle && lifecycle->acquire(
                Wait{LinuxOwnerLifecycle::bounded_close_milliseconds}) ==
                SMS_STATUS_SUCCESS) {
            finalize_owner_while_lifecycle_held(
                resource, owner, std::move(anchor));
            lifecycle->release();
            return;
        }
    } catch (...) {
    }

    const auto safely_recorded = LinuxOwnerLifecycle::publish_release_marker(
        resource.linux_owners_path,
        owner.line);
    if (safely_recorded) {
        if (anchor) anchor->release_and_remove();
    } else {
        LinuxOwnerLifecycle::retain_ambiguous_anchor(std::move(anchor));
    }
}

class LinuxRegion final : public MappedRegion {
public:
    LinuxRegion(
        int fd,
        std::uint8_t* data,
        std::int64_t size,
        ResourceName resource,
        LinuxOwnerRecord owner,
        std::unique_ptr<LinuxOwnerAnchor> anchor)
        : fd_(fd),
          data_(data),
          size_(size),
          resource_(std::move(resource)),
          owner_(std::move(owner)),
          anchor_(std::move(anchor)) {}
    ~LinuxRegion() override { close(); }
    std::uint8_t* data() noexcept override { return data_; }
    std::int64_t size() const noexcept override { return size_; }
    void mark_owner_registered() noexcept { owner_registered_ = true; }
    void close() noexcept override {
        if (!close_mapping()) return;
        if (owner_registered_) {
            release_owner(resource_, owner_, std::move(anchor_));
        } else if (anchor_) {
            anchor_->release_and_remove();
            anchor_.reset();
        }
    }
    void close_while_cold_locked() noexcept override {
        if (!close_mapping()) return;
        if (owner_registered_) {
            finalize_owner_while_lifecycle_held(
                resource_, owner_, std::move(anchor_));
        } else if (anchor_) {
            anchor_->release_and_remove();
            anchor_.reset();
        }
    }
private:
    [[nodiscard]] bool close_mapping() noexcept {
        if (closed_) return false;
        closed_ = true;
        if (data_ && data_ != MAP_FAILED) {
            ::munmap(data_, static_cast<std::size_t>(size_));
        }
        data_ = nullptr;
        if (fd_ >= 0) ::close(fd_);
        fd_ = -1;
        return true;
    }

    int fd_{-1};
    std::uint8_t* data_{};
    std::int64_t size_{};
    ResourceName resource_;
    LinuxOwnerRecord owner_;
    std::unique_ptr<LinuxOwnerAnchor> anchor_;
    bool owner_registered_{};
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

Wait remaining_wait(
    const Wait& wait,
    clock_type::time_point started) noexcept {
    if (wait.infinite()) return wait;
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        clock_type::now() - started).count();
    return Wait{
        std::max<std::int64_t>(0, wait.milliseconds - elapsed),
        wait.cancellation};
}

} // namespace

PlatformOpenResult platform_open(const ResourceName& resource, const Options& options, const Wait& wait) noexcept {
    PlatformOpenResult result{};
    const auto started = clock_type::now();
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
        const auto lock_status = lifecycle->acquire(remaining_wait(wait, started));
        if (lock_status != SMS_STATUS_SUCCESS) {
            result.status = map_lock_status(lock_status);
            return result;
        }

        LinuxOwnerSnapshot owner_snapshot{};
        const auto owner_status = LinuxOwnerLifecycle::prepare(
            resource.linux_owners_path,
            owner_snapshot);
        if (owner_status != SMS_STATUS_SUCCESS) {
            result.status = map_lock_status(owner_status);
            return result;
        }
        bool region_present{};
        const auto region_status = inspect_regular_file(
            resource.linux_region_path,
            region_present);
        if (region_status != SMS_STATUS_SUCCESS) {
            result.status = map_lock_status(region_status);
            return result;
        }
        if (owner_snapshot.has_live_owner && !region_present) {
            // Live or ambiguous evidence without its data object is not proof
            // that a new store may be created under the same public name.
            result.status = SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        const auto live_resource =
            region_present && owner_snapshot.has_live_owner;
        if (!live_resource) {
            delete_stale(resource);
            owner_snapshot.committed_owners.clear();
        }
        if (options.open_mode == SMS_OPEN_MODE_CREATE_NEW && live_resource) {
            result.status = SMS_OPEN_ALREADY_EXISTS;
            return result;
        }
        if (options.open_mode == SMS_OPEN_MODE_OPEN_EXISTING && !live_resource) {
            result.status = SMS_OPEN_NOT_FOUND;
            return result;
        }

        const bool create = !live_resource;
        // SMS2 cold ordering is lifecycle -> stable ordinary rendezvous ->
        // mapping/owner publication. Both gates remain held through participant
        // registration by Store::open; hot operations never use either gate.
        auto cold_lock = open_lock(resource.linux_lock_path);
        if (!cold_lock) {
            result.status = (errno == EACCES || errno == EPERM)
                ? SMS_OPEN_ACCESS_DENIED
                : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        const auto cold_status = cold_lock->acquire(remaining_wait(wait, started));
        if (cold_status != SMS_STATUS_SUCCESS) {
            result.status = map_lock_status(cold_status);
            return result;
        }

        const auto flags = O_RDWR | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK |
            (create ? (O_CREAT | O_EXCL) : 0);
        const auto fd = ::open(resource.linux_region_path.c_str(), flags, 0600);
        if (fd < 0) {
            result.status = errno == EEXIST ? SMS_OPEN_ALREADY_EXISTS :
                            (errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED);
            return result;
        }
        struct stat mapped_file{};
        if (::fstat(fd, &mapped_file) != 0 || !S_ISREG(mapped_file.st_mode) ||
            ::fchmod(fd, 0600) != 0) {
            const auto error = errno;
            ::close(fd);
            if (create) delete_stale(resource);
            result.status = error == EACCES || error == EPERM
                ? SMS_OPEN_ACCESS_DENIED
                : SMS_OPEN_MAPPING_FAILED;
            return result;
        }
        std::int64_t mapping_size{};
        if (create) {
            if (::ftruncate(fd, options.total_bytes) != 0) {
                ::close(fd);
                delete_stale(resource);
                result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
                return result;
            }
            mapping_size = options.total_bytes;
        } else {
            if (mapped_file.st_size <= 0 ||
                static_cast<std::uint64_t>(mapped_file.st_size) >
                    static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) {
                ::close(fd);
                result.status = SMS_OPEN_MAPPING_FAILED;
                return result;
            }
            mapping_size = static_cast<std::int64_t>(mapped_file.st_size);
        }
        if (mapping_size <= 0 ||
            static_cast<std::uint64_t>(mapping_size) >
                std::numeric_limits<std::size_t>::max()) {
            ::close(fd);
            if (create) delete_stale(resource);
            result.status = SMS_OPEN_INVALID_OPTIONS;
            return result;
        }
        auto* mapped = static_cast<std::uint8_t*>(::mmap(nullptr, static_cast<std::size_t>(mapping_size),
                                                         PROT_READ | PROT_WRITE, MAP_SHARED, fd, 0));
        if (mapped == MAP_FAILED) {
            ::close(fd);
            if (create) delete_stale(resource);
            result.status = errno == EACCES || errno == EPERM ? SMS_OPEN_ACCESS_DENIED : SMS_OPEN_MAPPING_FAILED;
            return result;
        }

        LinuxOwnerRecord owner{};
        std::unique_ptr<LinuxOwnerAnchor> anchor;
        const auto create_owner_status = LinuxOwnerLifecycle::create_current_owner(
            resource.linux_owners_path,
            owner,
            anchor);
        if (create_owner_status != SMS_STATUS_SUCCESS || !anchor) {
            ::munmap(mapped, static_cast<std::size_t>(mapping_size));
            ::close(fd);
            if (create) delete_stale(resource);
            result.status = map_lock_status(create_owner_status);
            return result;
        }
        auto candidate = std::make_unique<LinuxRegion>(
            fd,
            mapped,
            mapping_size,
            resource,
            owner,
            std::move(anchor));
        const auto registration_status = LinuxOwnerLifecycle::commit_registration(
            resource.linux_owners_path,
            owner_snapshot.committed_owners,
            owner.line);
        if (registration_status != SMS_STATUS_SUCCESS) {
            candidate.reset();
            if (create) delete_stale(resource);
            result.status = map_lock_status(registration_status);
            return result;
        }
        candidate->mark_owner_registered();
        result.region = std::move(candidate);
        result.lifecycle_lock = std::move(lifecycle);
        result.cold_lock = std::move(cold_lock);
        result.physical_creator = create;
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
