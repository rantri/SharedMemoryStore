#include "internal.hpp"
#include "operation_budget.hpp"

#if defined(_WIN32)

#ifndef NOMINMAX
#  define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>

namespace sms::detail {
namespace {

sms_open_status map_windows_open_error(DWORD error) noexcept {
    switch (error) {
        case ERROR_FILE_NOT_FOUND:
        case ERROR_INVALID_NAME:
            return error == ERROR_FILE_NOT_FOUND ? SMS_OPEN_NOT_FOUND : SMS_OPEN_INVALID_OPTIONS;
        case ERROR_ACCESS_DENIED:
        case ERROR_PRIVILEGE_NOT_HELD:
            return SMS_OPEN_ACCESS_DENIED;
        case ERROR_NOT_SUPPORTED:
        case ERROR_CALL_NOT_IMPLEMENTED:
            return SMS_OPEN_UNSUPPORTED_PLATFORM;
        case ERROR_INVALID_PARAMETER:
            return SMS_OPEN_INVALID_OPTIONS;
        default:
            return SMS_OPEN_MAPPING_FAILED;
    }
}

class WindowsRegion final : public MappedRegion {
public:
    WindowsRegion(HANDLE mapping, std::uint8_t* data, std::int64_t size)
        : mapping_(mapping), data_(data), size_(size) {}
    ~WindowsRegion() override { close(); }
    std::uint8_t* data() noexcept override { return data_; }
    std::int64_t size() const noexcept override { return size_; }
    void close() noexcept override {
        if (data_) UnmapViewOfFile(data_);
        data_ = nullptr;
        if (mapping_) CloseHandle(mapping_);
        mapping_ = nullptr;
    }
private:
    HANDLE mapping_{};
    std::uint8_t* data_{};
    std::int64_t size_{};
};

class WindowsLock final : public SharedLock {
public:
    explicit WindowsLock(HANDLE mutex) : mutex_(mutex) {}
    ~WindowsLock() override {
        release();
        if (mutex_) CloseHandle(mutex_);
    }
    sms_status acquire(const Wait& wait) noexcept override {
        if (!mutex_) return SMS_STATUS_STORE_DISPOSED;
        if (!wait.valid()) return SMS_STATUS_UNKNOWN_FAILURE;
        const auto started = std::chrono::steady_clock::now();
        for (;;) {
            if (wait.cancellation != nullptr &&
                wait.cancellation->is_canceled()) {
                return SMS_STATUS_OPERATION_CANCELED;
            }
            DWORD timeout = 0;
            if (wait.infinite()) {
                timeout = 10;
            } else {
                const auto elapsed =
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - started).count();
                if (elapsed >= wait.milliseconds && wait.milliseconds != 0) {
                    return SMS_STATUS_STORE_BUSY;
                }
                const auto remaining = std::max<std::int64_t>(
                    0, wait.milliseconds - elapsed);
                timeout = static_cast<DWORD>(std::min<std::int64_t>(10, remaining));
            }
            const auto result = WaitForSingleObject(mutex_, timeout);
            if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) {
                held_ = true;
                return SMS_STATUS_SUCCESS;
            }
            if (result != WAIT_TIMEOUT) {
                const auto error = GetLastError();
                return error == ERROR_ACCESS_DENIED
                    ? SMS_STATUS_ACCESS_DENIED
                    : SMS_STATUS_UNKNOWN_FAILURE;
            }
            if (!wait.infinite() && wait.milliseconds == 0) {
                return SMS_STATUS_STORE_BUSY;
            }
        }
    }
    void release() noexcept override {
        if (held_ && mutex_) {
            ReleaseMutex(mutex_);
            held_ = false;
        }
    }
private:
    HANDLE mutex_{};
    bool held_{};
};

} // namespace

PlatformOpenResult platform_open(
    const ResourceName& resource,
    const Options& options,
    const Wait& wait) noexcept {
    PlatformOpenResult result{};
    const auto mutex = CreateMutexW(
        nullptr, FALSE, resource.windows_lock_name.c_str());
    if (!mutex) {
        result.status = map_windows_open_error(GetLastError());
        return result;
    }
    std::unique_ptr<WindowsLock> cold_lock;
    try {
        cold_lock = std::make_unique<WindowsLock>(mutex);
    } catch (...) {
        CloseHandle(mutex);
        result.status = SMS_OPEN_MAPPING_FAILED;
        return result;
    }
    const auto lock_status = cold_lock->acquire(wait);
    if (lock_status != SMS_STATUS_SUCCESS) {
        switch (lock_status) {
        case SMS_STATUS_STORE_BUSY:
            result.status = SMS_OPEN_STORE_BUSY;
            break;
        case SMS_STATUS_OPERATION_CANCELED:
            result.status = SMS_OPEN_OPERATION_CANCELED;
            break;
        case SMS_STATUS_ACCESS_DENIED:
            result.status = SMS_OPEN_ACCESS_DENIED;
            break;
        case SMS_STATUS_UNSUPPORTED_PLATFORM:
            result.status = SMS_OPEN_UNSUPPORTED_PLATFORM;
            break;
        default:
            result.status = SMS_OPEN_MAPPING_FAILED;
            break;
        }
        return result;
    }

    HANDLE mapping{};
    bool physical_creator{};
    if (options.open_mode == SMS_OPEN_MODE_OPEN_EXISTING) {
        mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, resource.windows_region_name.c_str());
        if (!mapping) {
            result.status = map_windows_open_error(GetLastError());
            return result;
        }
    } else {
        const auto unsigned_size = static_cast<std::uint64_t>(options.total_bytes);
        mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE,
                                     static_cast<DWORD>(unsigned_size >> 32),
                                     static_cast<DWORD>(unsigned_size),
                                     resource.windows_region_name.c_str());
        if (!mapping) {
            result.status = map_windows_open_error(GetLastError());
            return result;
        }
        const auto already_exists = GetLastError() == ERROR_ALREADY_EXISTS;
        if (options.open_mode == SMS_OPEN_MODE_CREATE_NEW && already_exists) {
            CloseHandle(mapping);
            result.status = SMS_OPEN_ALREADY_EXISTS;
            return result;
        }
        physical_creator = !already_exists;
    }

    // A zero byte count projects the existing section's actual extent. Header
    // validation, not the caller's requested dimensions, decides compatibility.
    auto* data = static_cast<std::uint8_t*>(MapViewOfFile(
        mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0));
    if (!data) {
        const auto error = GetLastError();
        CloseHandle(mapping);
        result.status = error == ERROR_MAPPED_ALIGNMENT ? SMS_OPEN_INVALID_OPTIONS : map_windows_open_error(error);
        return result;
    }

    MEMORY_BASIC_INFORMATION view{};
    if (VirtualQuery(data, &view, sizeof(view)) == 0 || view.RegionSize == 0 ||
        view.RegionSize > static_cast<SIZE_T>(std::numeric_limits<std::int64_t>::max())) {
        const auto error = GetLastError();
        UnmapViewOfFile(data);
        CloseHandle(mapping);
        result.status = error == ERROR_SUCCESS
            ? SMS_OPEN_MAPPING_FAILED
            : map_windows_open_error(error);
        return result;
    }
    const auto actual_size = physical_creator
        ? options.total_bytes
        : static_cast<std::int64_t>(view.RegionSize);
    try {
        result.region = std::make_unique<WindowsRegion>(
            mapping, data, actual_size);
    } catch (...) {
        UnmapViewOfFile(data);
        CloseHandle(mapping);
        result.status = SMS_OPEN_MAPPING_FAILED;
        return result;
    }
    result.cold_lock = std::move(cold_lock);
    result.physical_creator = physical_creator;
    result.status = SMS_OPEN_SUCCESS;
    return result;
}

OwnerKind classify_process(std::int32_t pid) noexcept {
    if (pid <= 0) return OwnerKind::stale;
    if (pid == current_process_id()) return OwnerKind::current;
    const auto process = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE,
                                     static_cast<DWORD>(pid));
    if (!process) {
        const auto error = GetLastError();
        return error == ERROR_INVALID_PARAMETER ? OwnerKind::stale : OwnerKind::unsupported;
    }
    const auto wait = WaitForSingleObject(process, 0);
    CloseHandle(process);
    if (wait == WAIT_OBJECT_0) return OwnerKind::stale;
    if (wait == WAIT_TIMEOUT) return OwnerKind::live;
    return OwnerKind::unsupported;
}

} // namespace sms::detail

#endif
