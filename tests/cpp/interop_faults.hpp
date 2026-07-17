#pragma once

// Test-only SMS2 raw-fault and inherited cold-lock primitives. They are linked
// only into the repository interoperability agent and never enter the native
// package or the mapped protocol.

#include "internal.hpp"

#include <shared_memory_store/store.hpp>

#include <atomic>
#include <bit>
#include <cstdint>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#elif defined(__linux__)
#  include <cerrno>
#  include <fcntl.h>
#  include <sys/mman.h>
#  include <sys/stat.h>
#  include <unistd.h>
#  if !defined(F_OFD_SETLK)
#    define F_OFD_SETLK 37
#  endif
#endif

namespace sms::interop_test {

class unsupported_primitive : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

enum class raw_fault_kind {
    layout_major_version,
    required_features,
    directory_mutation,
    participant_process_id,
    participant_namespace,
    header_namespace,
};

struct raw_fault_request {
    raw_fault_kind kind{};
    std::int32_t target_process_id{};
    std::int32_t replacement_process_id{};
    std::uint64_t replacement_pid_namespace_id{};
    std::uint16_t replacement_layout_major_version{};
    std::uint64_t replacement_required_features{};
};

struct raw_fault_result {
    std::int32_t participant_index{-1};
    std::int32_t original_process_id{};
    std::int32_t replacement_process_id{};
    std::uint64_t original_pid_namespace_id{};
    std::uint64_t replacement_pid_namespace_id{};
    std::int64_t original_raw{};
    std::int64_t replacement_raw{};
};

inline detail::ResourceName make_test_resource_name(std::string_view public_name) {
    detail::ResourceName result;
    if (!detail::make_resource_name(public_name, result)) {
        throw std::invalid_argument(
            "The store name is not a valid canonical SMS2 resource name.");
    }
    return result;
}

class raw_mapping {
public:
    explicit raw_mapping(const shared_memory_store::store_options& options)
        : size_(options.total_bytes) {
        if (size_ < static_cast<std::int64_t>(sizeof(detail::StoreHeaderV2)) ||
            static_cast<std::uint64_t>(size_) >
                static_cast<std::uint64_t>(std::numeric_limits<std::size_t>::max())) {
            throw std::runtime_error("The raw SMS2 mapping size is invalid.");
        }
        const auto resource = make_test_resource_name(options.name);
#if defined(_WIN32)
        mapping_ = OpenFileMappingW(
            FILE_MAP_ALL_ACCESS, FALSE, resource.windows_region_name.c_str());
        if (!mapping_) {
            throw std::runtime_error(
                "OpenFileMappingW failed with error " +
                std::to_string(GetLastError()) + '.');
        }
        data_ = static_cast<std::uint8_t*>(MapViewOfFile(
            mapping_, FILE_MAP_ALL_ACCESS, 0, 0, static_cast<SIZE_T>(size_)));
        if (!data_) {
            const auto error = GetLastError();
            CloseHandle(mapping_);
            mapping_ = nullptr;
            throw std::runtime_error(
                "MapViewOfFile failed with error " + std::to_string(error) + '.');
        }
#elif defined(__linux__)
        descriptor_ = ::open(
            resource.linux_region_path.c_str(), O_RDWR | O_CLOEXEC | O_NOFOLLOW);
        if (descriptor_ < 0) {
            throw std::runtime_error(
                "Opening the raw SMS2 region failed with errno " +
                std::to_string(errno) + '.');
        }
        struct stat information{};
        if (::fstat(descriptor_, &information) != 0 ||
            !S_ISREG(information.st_mode) || information.st_size != size_) {
            const auto error = errno == 0 ? EINVAL : errno;
            ::close(descriptor_);
            descriptor_ = -1;
            throw std::runtime_error(
                "The raw SMS2 region is not the exact expected regular file (errno " +
                std::to_string(error) + ").");
        }
        auto* mapped = ::mmap(
            nullptr,
            static_cast<std::size_t>(size_),
            PROT_READ | PROT_WRITE,
            MAP_SHARED,
            descriptor_,
            0);
        if (mapped == MAP_FAILED) {
            const auto error = errno;
            ::close(descriptor_);
            descriptor_ = -1;
            throw std::runtime_error(
                "mmap failed with errno " + std::to_string(error) + '.');
        }
        data_ = static_cast<std::uint8_t*>(mapped);
#else
        (void)resource;
        throw unsupported_primitive(
            "Raw SMS2 fault injection supports Windows and Linux only.");
#endif
    }

    ~raw_mapping() { close(); }
    raw_mapping(const raw_mapping&) = delete;
    raw_mapping& operator=(const raw_mapping&) = delete;

    std::uint8_t* data() const noexcept { return data_; }
    std::int64_t size() const noexcept { return size_; }

    void flush() {
#if defined(_WIN32)
        if (!FlushViewOfFile(data_, static_cast<SIZE_T>(size_))) {
            throw std::runtime_error(
                "FlushViewOfFile failed with error " +
                std::to_string(GetLastError()) + '.');
        }
#elif defined(__linux__)
        if (::msync(data_, static_cast<std::size_t>(size_), MS_SYNC) != 0) {
            throw std::runtime_error(
                "msync failed with errno " + std::to_string(errno) + '.');
        }
#endif
    }

private:
    void close() noexcept {
#if defined(_WIN32)
        if (data_) UnmapViewOfFile(data_);
        if (mapping_) CloseHandle(mapping_);
        mapping_ = nullptr;
#elif defined(__linux__)
        if (data_) ::munmap(data_, static_cast<std::size_t>(size_));
        if (descriptor_ >= 0) ::close(descriptor_);
        descriptor_ = -1;
#endif
        data_ = nullptr;
    }

    std::uint8_t* data_{};
    std::int64_t size_{};
#if defined(_WIN32)
    HANDLE mapping_{};
#elif defined(__linux__)
    int descriptor_{-1};
#endif
};

inline detail::StoreHeaderV2& validate_raw_mapping(
    raw_mapping& mapping,
    const shared_memory_store::store_options& options) {
    auto& header = *reinterpret_cast<detail::StoreHeaderV2*>(mapping.data());
    const auto ready =
        (std::atomic_ref(header.Control).load(std::memory_order_acquire) & 0x7ULL) ==
        detail::sms2_store_ready;
    const auto participants_end = header.ParticipantOffset + header.ParticipantLength;
    const auto primary_end =
        header.PrimaryDirectoryOffset + header.PrimaryDirectoryLength;
    if (header.Magic != detail::sms2_magic ||
        header.LayoutMajorVersion != detail::sms2_layout_major ||
        header.LayoutMinorVersion != detail::sms2_layout_minor ||
        header.HeaderLength != detail::sms2_header_length ||
        header.ResourceProtocolVersion != detail::sms2_resource_protocol ||
        header.RequiredFeatures != detail::sms2_required_features ||
        header.OptionalFeatures != detail::sms2_optional_features ||
        header.TotalBytes != options.total_bytes || header.TotalBytes != mapping.size() ||
        !ready || header.StoreId == 0 || header.SlotCount != options.slot_count ||
        header.LeaseRecordCount != options.lease_record_count ||
        header.ParticipantRecordCount != options.participant_record_count ||
        header.MaxValueBytes != options.max_value_bytes ||
        header.MaxDescriptorBytes != options.max_descriptor_bytes ||
        header.MaxKeyBytes != options.max_key_bytes ||
        header.ParticipantStride != detail::sms2_participant_stride ||
        header.ParticipantOffset < detail::sms2_header_length ||
        header.ParticipantLength < 0 || participants_end < header.ParticipantOffset ||
        participants_end > mapping.size() ||
        header.PrimaryDirectoryOffset < participants_end ||
        header.PrimaryDirectoryLength <
            static_cast<std::int64_t>(sizeof(detail::PrimaryDirectoryBucketV2)) ||
        primary_end < header.PrimaryDirectoryOffset || primary_end > mapping.size()) {
        throw std::runtime_error(
            "Raw mapping does not match the exact opened SMS2 layout.");
    }
    return header;
}

inline std::int64_t signed_raw(std::uint64_t value) noexcept {
    return std::bit_cast<std::int64_t>(value);
}

inline raw_fault_result inject_raw_fault(
    const shared_memory_store::store_options& options,
    const raw_fault_request& request) {
    raw_mapping mapping(options);
    auto& header = validate_raw_mapping(mapping, options);
    raw_fault_result result{};

    if (request.kind == raw_fault_kind::layout_major_version) {
        result.original_raw = header.LayoutMajorVersion;
        std::atomic_ref(header.LayoutMajorVersion).store(
            request.replacement_layout_major_version,
            std::memory_order_release);
        mapping.flush();
        result.replacement_raw = request.replacement_layout_major_version;
        return result;
    }
    if (request.kind == raw_fault_kind::required_features) {
        const auto original = std::atomic_ref(header.RequiredFeatures).load(
            std::memory_order_acquire);
        std::atomic_ref(header.RequiredFeatures).store(
            request.replacement_required_features,
            std::memory_order_release);
        mapping.flush();
        result.original_raw = signed_raw(original);
        result.replacement_raw = signed_raw(request.replacement_required_features);
        return result;
    }
    if (request.kind == raw_fault_kind::directory_mutation) {
        auto& mutation = *reinterpret_cast<std::uint64_t*>(
            mapping.data() + header.PrimaryDirectoryOffset + sizeof(std::uint64_t));
        const auto original = std::atomic_ref(mutation).load(std::memory_order_acquire);
        const auto malformed =
            (std::uint64_t{1} << 31U) |
            static_cast<std::uint64_t>(header.SlotCount + 1);
        std::atomic_ref(mutation).store(malformed, std::memory_order_release);
        mapping.flush();
        result.original_raw = signed_raw(original);
        result.replacement_raw = signed_raw(malformed);
        return result;
    }
    if (request.kind == raw_fault_kind::participant_process_id ||
        request.kind == raw_fault_kind::participant_namespace) {
        if (request.target_process_id <= 0) {
            throw std::invalid_argument("The target process id must be positive.");
        }
        for (std::int32_t index = 0; index < header.ParticipantRecordCount; ++index) {
            auto& participant = *reinterpret_cast<detail::ParticipantRecordV2*>(
                mapping.data() + header.ParticipantOffset +
                (static_cast<std::int64_t>(index) * header.ParticipantStride));
            const auto original = std::atomic_ref(participant.Control).load(
                std::memory_order_acquire);
            const auto state = original & 0x7ULL;
            const auto process_id = original >> 31U;
            if (process_id != static_cast<std::uint64_t>(request.target_process_id) ||
                (state != 1 && state != 2 && state != 3)) {
                continue;
            }
            result.participant_index = index;
            result.original_process_id = request.target_process_id;
            result.original_pid_namespace_id = participant.PidNamespaceId;
            result.original_raw = signed_raw(original);
            if (request.kind == raw_fault_kind::participant_process_id) {
                if (request.replacement_process_id <= 0) {
                    throw std::invalid_argument(
                        "The replacement process id must be positive.");
                }
                const auto generation = (original >> 3U) & 0x0fff'ffffULL;
                const auto replacement =
                    state | (generation << 3U) |
                    (static_cast<std::uint64_t>(request.replacement_process_id) << 31U);
                std::atomic_ref(participant.Control).store(
                    replacement, std::memory_order_release);
                result.replacement_process_id = request.replacement_process_id;
                result.replacement_pid_namespace_id = participant.PidNamespaceId;
                result.replacement_raw = signed_raw(replacement);
            } else {
                const auto original_namespace = participant.PidNamespaceId;
                participant.PidNamespaceId = request.replacement_pid_namespace_id;
                result.replacement_process_id = request.target_process_id;
                result.original_pid_namespace_id = original_namespace;
                result.replacement_pid_namespace_id =
                    request.replacement_pid_namespace_id;
                result.replacement_raw = signed_raw(original);
            }
            mapping.flush();
            return result;
        }
        throw std::runtime_error(
            "No live participant record owned by PID " +
            std::to_string(request.target_process_id) + " was found.");
    }
    if (request.kind == raw_fault_kind::header_namespace) {
        result.original_pid_namespace_id = header.PidNamespaceId;
        header.PidNamespaceId = request.replacement_pid_namespace_id;
        mapping.flush();
        result.replacement_pid_namespace_id = request.replacement_pid_namespace_id;
        return result;
    }
    throw std::invalid_argument("Unknown raw fault kind.");
}

class cold_lock {
public:
    static std::unique_ptr<cold_lock> acquire(std::string_view public_name) {
        auto result = std::unique_ptr<cold_lock>(new cold_lock());
        const auto resource = make_test_resource_name(public_name);
#if defined(_WIN32)
        result->mutex_ = CreateMutexW(
            nullptr, FALSE, resource.windows_lock_name.c_str());
        if (!result->mutex_) {
            const auto error = GetLastError();
            if (error == ERROR_NOT_SUPPORTED || error == ERROR_CALL_NOT_IMPLEMENTED) {
                throw unsupported_primitive(
                    "The Windows cold mutex primitive is unavailable.");
            }
            throw std::runtime_error(
                "CreateMutexW failed with error " + std::to_string(error) + '.');
        }
        const auto wait = WaitForSingleObject(result->mutex_, 5000);
        if (wait != WAIT_OBJECT_0 && wait != WAIT_ABANDONED) {
            const auto error = wait == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError();
            CloseHandle(result->mutex_);
            result->mutex_ = nullptr;
            throw std::runtime_error(
                "Waiting for the Windows cold mutex failed with error " +
                std::to_string(error) + '.');
        }
        result->held_ = true;
#elif defined(__linux__)
        result->descriptor_ = ::open(
            resource.linux_lock_path.c_str(),
            O_RDWR | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK);
        if (result->descriptor_ < 0) {
            throw std::runtime_error(
                "Opening the Linux cold lock failed with errno " +
                std::to_string(errno) + '.');
        }
        struct stat information{};
        if (::fstat(result->descriptor_, &information) != 0 ||
            !S_ISREG(information.st_mode)) {
            const auto error = errno == 0 ? EINVAL : errno;
            ::close(result->descriptor_);
            result->descriptor_ = -1;
            throw std::runtime_error(
                "The Linux cold lock is not a regular file (errno " +
                std::to_string(error) + ").");
        }
        struct flock request{};
        request.l_type = F_WRLCK;
        request.l_whence = SEEK_SET;
        request.l_start = 0;
        request.l_len = 1;
        if (::fcntl(result->descriptor_, F_OFD_SETLK, &request) != 0) {
            const auto error = errno;
            ::close(result->descriptor_);
            result->descriptor_ = -1;
            if (error == EINVAL || error == ENOSYS || error == ENOTSUP ||
                error == EOPNOTSUPP) {
                throw unsupported_primitive(
                    "Linux open-file-description locks are unavailable.");
            }
            throw std::runtime_error(
                "Acquiring the Linux cold lock failed with errno " +
                std::to_string(error) + '.');
        }
        result->held_ = true;
#else
        (void)resource;
        throw unsupported_primitive(
            "Cold-lock injection supports Windows and Linux only.");
#endif
        return result;
    }

    ~cold_lock() { release(); }
    cold_lock(const cold_lock&) = delete;
    cold_lock& operator=(const cold_lock&) = delete;

    void release() noexcept {
#if defined(_WIN32)
        if (held_ && mutex_) ReleaseMutex(mutex_);
        if (mutex_) CloseHandle(mutex_);
        mutex_ = nullptr;
#elif defined(__linux__)
        if (held_ && descriptor_ >= 0) {
            struct flock request{};
            request.l_type = F_UNLCK;
            request.l_whence = SEEK_SET;
            request.l_start = 0;
            request.l_len = 1;
            (void)::fcntl(descriptor_, F_OFD_SETLK, &request);
        }
        if (descriptor_ >= 0) ::close(descriptor_);
        descriptor_ = -1;
#endif
        held_ = false;
    }

private:
    cold_lock() = default;
    bool held_{};
#if defined(_WIN32)
    HANDLE mutex_{};
#elif defined(__linux__)
    int descriptor_{-1};
#endif
};

} // namespace sms::interop_test
