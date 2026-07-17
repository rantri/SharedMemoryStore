#include "mapped_atomic.hpp"
#include "test_support.hpp"

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <future>
#include <string>
#include <system_error>
#include <thread>
#include <utility>

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

class TemporaryFile {
public:
    explicit TemporaryFile(std::filesystem::path path) : path_(std::move(path)) {
        std::array<std::byte, mapping_size> zeroes{};
        std::ofstream output(path_, std::ios::binary | std::ios::trunc);
        if (!output) return;
        output.write(
            reinterpret_cast<const char*>(zeroes.data()),
            static_cast<std::streamsize>(zeroes.size()));
        output.flush();
        valid_ = static_cast<bool>(output);
    }

    ~TemporaryFile() {
        std::error_code ignored;
        std::filesystem::remove(path_, ignored);
    }

    TemporaryFile(const TemporaryFile&) = delete;
    TemporaryFile& operator=(const TemporaryFile&) = delete;

    [[nodiscard]] bool valid() const noexcept { return valid_; }
    [[nodiscard]] const std::filesystem::path& path() const noexcept { return path_; }

private:
    std::filesystem::path path_;
    bool valid_{};
};

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

std::filesystem::path current_executable() {
#if defined(_WIN32)
    std::wstring buffer(32'768, L'\0');
    const DWORD length = GetModuleFileNameW(
        nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
    if (length == 0 || length >= buffer.size()) return {};
    buffer.resize(length);
    return std::filesystem::path(std::move(buffer));
#else
    std::error_code error;
    auto result = std::filesystem::read_symlink("/proc/self/exe", error);
    return error ? std::filesystem::path{} : result;
#endif
}

std::string quoted(const std::filesystem::path& path) {
    std::string result = "\"";
    for (const char current : path.string()) {
        if (current == '\"') result += '\\';
        result += current;
    }
    result += '\"';
    return result;
}

std::future<int> launch_agent(
    const std::filesystem::path& agent,
    std::string mode,
    const std::filesystem::path& mapping) {
    std::string command = quoted(agent) + " " + std::move(mode) + " " + quoted(mapping);
#if defined(_WIN32)
    // cmd.exe consumes the first pair of quotes when the command begins with a
    // quoted executable path.  Quote the complete command as well so paths
    // containing spaces reach the child process unchanged.
    command = '"' + command + '"';
#endif
    return std::async(std::launch::async, [command = std::move(command)] {
        return std::system(command.c_str());
    });
}

} // namespace

int main() {
    using namespace std::chrono_literals;
    using sms::detail::MappedAtomic64;

    SMS_CHECK(MappedAtomic64::supported());
    const auto executable = current_executable();
    SMS_CHECK(!executable.empty());
    const auto agent = executable.parent_path() /
        (std::string("sms_mapped_atomic_agent") + executable.extension().string());
    SMS_CHECK(std::filesystem::is_regular_file(agent));

    TemporaryFile file(
        std::filesystem::temp_directory_path() /
        (sms_test_name("mapped-atomic-litmus") + ".bin"));
    SMS_CHECK(file.valid());

    SharedMapping mapping(file.path());
    SMS_CHECK(mapping.valid());
    SMS_CHECK(MappedAtomic64::is_aligned(mapping.words()));
    auto* const words = mapping.words();

    MappedAtomic64::store_release(words[0], 0);
    words[1] = 0;
    auto observer = launch_agent(agent, "observe-release", file.path());
    words[1] = release_payload;
    MappedAtomic64::store_release(words[0], release_marker);
    SMS_CHECK(observer.get() == 0);

    MappedAtomic64::store_release(words[0], 0);
    words[1] = 0;
    auto publisher = launch_agent(agent, "publish-cas", file.path());
    const auto deadline = std::chrono::steady_clock::now() + 10s;
    while (MappedAtomic64::load_acquire(words[0]) != cas_marker &&
           std::chrono::steady_clock::now() < deadline) {
        std::this_thread::yield();
    }
    SMS_CHECK(MappedAtomic64::load_acquire(words[0]) == cas_marker);
    SMS_CHECK(words[1] == cas_payload);
    SMS_CHECK(publisher.get() == 0);
    return 0;
}
