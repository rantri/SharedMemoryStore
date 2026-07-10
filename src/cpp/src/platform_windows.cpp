#include "internal.hpp"

#if defined(_WIN32)

#ifndef NOMINMAX
#  define NOMINMAX
#endif
#include <windows.h>

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
        DWORD timeout = INFINITE;
        if (!wait.infinite()) {
            timeout = wait.milliseconds >= static_cast<std::int64_t>(INFINITE - 1)
                ? INFINITE - 1
                : static_cast<DWORD>(wait.milliseconds);
        }
        const auto result = WaitForSingleObject(mutex_, timeout);
        if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) {
            held_ = true;
            return SMS_STATUS_SUCCESS;
        }
        if (result == WAIT_TIMEOUT) return SMS_STATUS_STORE_BUSY;
        const auto error = GetLastError();
        return error == ERROR_ACCESS_DENIED ? SMS_STATUS_ACCESS_DENIED : SMS_STATUS_UNKNOWN_FAILURE;
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

PlatformOpenResult platform_open(const ResourceName& resource, const Options& options, const Wait&) noexcept {
    PlatformOpenResult result{};
    HANDLE mapping{};
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
        if (options.open_mode == SMS_OPEN_MODE_CREATE_NEW && GetLastError() == ERROR_ALREADY_EXISTS) {
            CloseHandle(mapping);
            result.status = SMS_OPEN_ALREADY_EXISTS;
            return result;
        }
    }

    auto* data = static_cast<std::uint8_t*>(MapViewOfFile(
        mapping, FILE_MAP_ALL_ACCESS, 0, 0, static_cast<SIZE_T>(options.total_bytes)));
    if (!data) {
        const auto error = GetLastError();
        CloseHandle(mapping);
        result.status = error == ERROR_MAPPED_ALIGNMENT ? SMS_OPEN_INVALID_OPTIONS : map_windows_open_error(error);
        return result;
    }

    const auto mutex = CreateMutexW(nullptr, FALSE, resource.windows_lock_name.c_str());
    if (!mutex) {
        const auto error = GetLastError();
        UnmapViewOfFile(data);
        CloseHandle(mapping);
        result.status = map_windows_open_error(error);
        return result;
    }
    result.region = std::make_unique<WindowsRegion>(mapping, data, options.total_bytes);
    result.lock = std::make_unique<WindowsLock>(mutex);
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
