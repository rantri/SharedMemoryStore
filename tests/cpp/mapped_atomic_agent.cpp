#include "mapped_atomic.hpp"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <iostream>
#include <string_view>
#include <thread>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <fcntl.h>
#  include <sys/mman.h>
#  include <sys/stat.h>
#  include <unistd.h>
#endif

namespace {

constexpr std::size_t mapping_size = 4'096;
constexpr std::uint64_t release_marker = 1;
constexpr std::uint64_t cas_marker = 2;
constexpr std::uint64_t release_payload = 0x7265'6c65'6173'6521ULL;
constexpr std::uint64_t cas_payload = 0x7365'712d'6361'7321ULL;

class SharedMapping {
public:
    explicit SharedMapping(const std::filesystem::path& path) noexcept {
#if defined(_WIN32)
        file_ = CreateFileW(
            path.c_str(),
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            nullptr,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (file_ == INVALID_HANDLE_VALUE) return;

        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file_, &size) || size.QuadPart < static_cast<LONGLONG>(mapping_size)) {
            return;
        }
        mapping_ = CreateFileMappingW(file_, nullptr, PAGE_READWRITE, 0, 0, nullptr);
        if (mapping_ == nullptr) return;
        view_ = MapViewOfFile(mapping_, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, mapping_size);
#else
        descriptor_ = ::open(path.c_str(), O_RDWR);
        if (descriptor_ < 0) return;
        struct stat status {};
        if (::fstat(descriptor_, &status) != 0 || status.st_size < static_cast<off_t>(mapping_size)) {
            return;
        }
        void* mapped = ::mmap(
            nullptr, mapping_size, PROT_READ | PROT_WRITE, MAP_SHARED, descriptor_, 0);
        if (mapped != MAP_FAILED) view_ = mapped;
#endif
    }

    ~SharedMapping() {
#if defined(_WIN32)
        if (view_ != nullptr) UnmapViewOfFile(view_);
        if (mapping_ != nullptr) CloseHandle(mapping_);
        if (file_ != INVALID_HANDLE_VALUE) CloseHandle(file_);
#else
        if (view_ != nullptr) ::munmap(view_, mapping_size);
        if (descriptor_ >= 0) ::close(descriptor_);
#endif
    }

    SharedMapping(const SharedMapping&) = delete;
    SharedMapping& operator=(const SharedMapping&) = delete;

    [[nodiscard]] bool valid() const noexcept { return view_ != nullptr; }
    [[nodiscard]] std::uint64_t* words() const noexcept {
        return static_cast<std::uint64_t*>(view_);
    }

private:
#if defined(_WIN32)
    HANDLE file_{INVALID_HANDLE_VALUE};
    HANDLE mapping_{};
#else
    int descriptor_{-1};
#endif
    void* view_{};
};

int observe_release_acquire(std::uint64_t* words) {
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(10);
    while (sms::detail::MappedAtomic64::load_acquire(words[0]) != release_marker) {
        if (std::chrono::steady_clock::now() >= deadline) {
            std::cerr << "timed out waiting for the release marker\n";
            return 4;
        }
        std::this_thread::yield();
    }
    if (words[1] != release_payload) {
        std::cerr << "release/acquire did not publish the preceding payload\n";
        return 5;
    }
    return 0;
}

int publish_with_sequential_cas(std::uint64_t* words) {
    words[1] = cas_payload;
    std::uint64_t expected = 0;
    if (!sms::detail::MappedAtomic64::compare_exchange(words[0], expected, cas_marker)) {
        std::cerr << "sequentially-consistent CAS saw unexpected marker " << expected << '\n';
        return 6;
    }
    return 0;
}

} // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::cerr << "usage: sms_mapped_atomic_agent <observe-release|publish-cas> <mapping-path>\n";
        return 2;
    }

    SharedMapping mapping{std::filesystem::path(argv[2])};
    if (!mapping.valid() || !sms::detail::MappedAtomic64::is_aligned(mapping.words())) {
        std::cerr << "could not map an aligned atomic test file\n";
        return 3;
    }

    const std::string_view mode(argv[1]);
    if (mode == "observe-release") return observe_release_acquire(mapping.words());
    if (mode == "publish-cas") return publish_with_sequential_cas(mapping.words());
    std::cerr << "unknown mapped-atomic agent mode\n";
    return 2;
}
