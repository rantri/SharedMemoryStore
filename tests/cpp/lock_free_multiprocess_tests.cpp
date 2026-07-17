#include "test_support.hpp"

#include <algorithm>
#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <span>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

#if defined(_WIN32)
#  ifndef NOMINMAX
#    define NOMINMAX
#  endif
#  include <windows.h>
#else
#  include <csignal>
#  include <sys/types.h>
#  include <sys/wait.h>
#  include <unistd.h>
#endif

namespace {

std::atomic<int> failures{};
std::filesystem::path fault_agent;

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

std::string hex(std::span<const std::byte> bytes) {
    constexpr char digits[] = "0123456789abcdef";
    std::string result;
    result.reserve(bytes.size() * 2U);
    for (const auto value : bytes) {
        const auto raw = std::to_integer<unsigned int>(value);
        result.push_back(digits[(raw >> 4U) & 0xfU]);
        result.push_back(digits[raw & 0xfU]);
    }
    return result;
}

class ChildProcess {
public:
    ChildProcess() = default;
    ~ChildProcess() { terminate_and_wait(); }
    ChildProcess(const ChildProcess&) = delete;
    ChildProcess& operator=(const ChildProcess&) = delete;

    bool start(const std::vector<std::string>& arguments) {
        if (arguments.empty()) return false;
#if defined(_WIN32)
        std::wstring command_line;
        for (const auto& argument : arguments) {
            if (!command_line.empty()) command_line.push_back(L' ');
            command_line.push_back(L'"');
            for (const auto character : argument) {
                if (character == '"') command_line.push_back(L'\\');
                command_line.push_back(static_cast<wchar_t>(
                    static_cast<unsigned char>(character)));
            }
            command_line.push_back(L'"');
        }
        STARTUPINFOW startup{};
        startup.cb = sizeof(startup);
        std::vector<wchar_t> mutable_line(
            command_line.begin(), command_line.end());
        mutable_line.push_back(L'\0');
        return CreateProcessW(
            nullptr,
            mutable_line.data(),
            nullptr,
            nullptr,
            FALSE,
            CREATE_NO_WINDOW,
            nullptr,
            nullptr,
            &startup,
            &process_) != FALSE;
#else
        const auto child = fork();
        if (child < 0) return false;
        if (child == 0) {
            std::vector<char*> native;
            native.reserve(arguments.size() + 1U);
            for (const auto& argument : arguments) {
                native.push_back(const_cast<char*>(argument.c_str()));
            }
            native.push_back(nullptr);
            execv(native.front(), native.data());
            _exit(127);
        }
        process_id_ = child;
        return true;
#endif
    }

    bool running() {
#if defined(_WIN32)
        if (process_.hProcess == nullptr) return false;
        DWORD code{};
        return GetExitCodeProcess(process_.hProcess, &code) != FALSE &&
            code == STILL_ACTIVE;
#else
        if (process_id_ <= 0) return false;
        int status{};
        const auto observed = waitpid(process_id_, &status, WNOHANG);
        if (observed == 0) return true;
        if (observed == process_id_) {
            exit_code_ = WIFEXITED(status)
                ? WEXITSTATUS(status)
                : (WIFSIGNALED(status) ? 128 + WTERMSIG(status) : -1);
            process_id_ = -1;
        }
        return false;
#endif
    }

    int wait_for_exit(std::chrono::milliseconds timeout) {
        const auto deadline = std::chrono::steady_clock::now() + timeout;
        while (running() && std::chrono::steady_clock::now() < deadline) {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        if (running()) return -1;
#if defined(_WIN32)
        DWORD code{};
        if (process_.hProcess == nullptr ||
            GetExitCodeProcess(process_.hProcess, &code) == FALSE) {
            return -1;
        }
        return static_cast<int>(code);
#else
        return exit_code_;
#endif
    }

    void terminate_and_wait() noexcept {
#if defined(_WIN32)
        if (process_.hProcess != nullptr) {
            DWORD code{};
            if (GetExitCodeProcess(process_.hProcess, &code) != FALSE &&
                code == STILL_ACTIVE) {
                (void)TerminateProcess(process_.hProcess, 97);
            }
            (void)WaitForSingleObject(process_.hProcess, 10'000);
            CloseHandle(process_.hThread);
            CloseHandle(process_.hProcess);
            process_ = {};
        }
#else
        if (process_id_ > 0) {
            (void)kill(process_id_, SIGKILL);
            int status{};
            (void)waitpid(process_id_, &status, 0);
            process_id_ = -1;
        }
#endif
    }

private:
#if defined(_WIN32)
    PROCESS_INFORMATION process_{};
#else
    pid_t process_id_{-1};
    int exit_code_{-1};
#endif
};

struct Markers {
    explicit Markers(std::string_view suffix) {
        const auto unique = std::to_string(
            std::chrono::steady_clock::now().time_since_epoch().count());
        root = std::filesystem::temp_directory_path() /
            ("sms-native-fault-" + unique + "-" + std::string(suffix));
        std::filesystem::create_directories(root);
        ready = root / "ready";
        release = root / "release";
    }

    ~Markers() {
        std::error_code error;
        std::filesystem::remove_all(root, error);
    }

    bool wait_ready(ChildProcess& child) const {
        const auto deadline = std::chrono::steady_clock::now() +
            std::chrono::seconds(10);
        while (std::chrono::steady_clock::now() < deadline) {
            std::error_code error;
            if (std::filesystem::exists(ready, error) && !error) return true;
            if (!child.running()) return false;
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        return false;
    }

    bool resume() const {
        std::ofstream output(release, std::ios::binary | std::ios::trunc);
        output << "CONTINUE\n";
        return static_cast<bool>(output);
    }

    std::filesystem::path root;
    std::filesystem::path ready;
    std::filesystem::path release;
};

std::vector<std::string> arguments(
    std::string_view command,
    const shared_memory_store::store_options& options,
    std::span<const std::byte> key,
    std::span<const std::byte> value,
    const Markers& markers) {
    return {
        fault_agent.string(),
        std::string(command),
        options.name,
        std::to_string(options.slot_count),
        std::to_string(options.max_value_bytes),
        std::to_string(options.max_descriptor_bytes),
        std::to_string(options.max_key_bytes),
        std::to_string(options.lease_record_count),
        std::to_string(options.participant_record_count),
        hex(key),
        hex(value),
        markers.ready.string(),
        markers.release.string(),
    };
}

bool exact_bytes(
    shared_memory_store::memory_store& store,
    std::span<const std::byte> key,
    std::span<const std::byte> expected) {
    shared_memory_store::value_lease lease;
    if (store.try_acquire(key, lease) !=
        shared_memory_store::status::success) {
        return false;
    }
    const auto value = lease.value();
    const auto equal = value.size() == expected.size() &&
        std::equal(value.begin(), value.end(), expected.begin());
    return lease.release() == shared_memory_store::status::success && equal;
}

void paused_participant_allows_progress_and_raw_visibility() {
    using namespace shared_memory_store;
    auto options = sms_test_options("multiprocess-pause", 5, 5);
    options.participant_record_count = 3;
    options.total_bytes = store_options::calculate_required_bytes(
        options.slot_count,
        options.max_value_bytes,
        options.max_descriptor_bytes,
        options.max_key_bytes,
        options.lease_record_count,
        options.participant_record_count);
    memory_store creator;
    expect(memory_store::try_create_or_open(options, creator) ==
               open_status::success,
           "create pause fixture");

    const std::array<std::byte, 2> child_key{std::byte{1}, std::byte{2}};
    const std::array<std::byte, 4> child_value{
        std::byte{3}, std::byte{0}, std::byte{4}, std::byte{5}};
    const std::array<std::byte, 1> healthy_key{std::byte{9}};
    const std::array<std::byte, 2> healthy_value{
        std::byte{8}, std::byte{7}};
    Markers markers("pause");
    ChildProcess child;
    expect(child.start(arguments(
               "pause-before-publish",
               options,
               child_key,
               child_value,
               markers)),
           "start paused publisher");
    expect(markers.wait_ready(child), "publisher reaches test-only checkpoint");
    expect(creator.try_publish(healthy_key, healthy_value) == status::success,
           "unrelated participant progresses while publisher is paused");
    expect(markers.resume(), "resume paused publisher");
    expect(child.wait_for_exit(std::chrono::seconds(10)) == 0,
           "resumed publisher exits successfully");
    expect(exact_bytes(creator, child_key, child_value) &&
               exact_bytes(creator, healthy_key, healthy_value),
           "both participants observe exact binary publication bytes");
}

void crashed_reservation_is_recovered_and_participant_reused() {
    using namespace shared_memory_store;
    auto options = sms_test_options("multiprocess-reservation-crash", 2, 2);
    options.participant_record_count = 2;
    options.total_bytes = store_options::calculate_required_bytes(
        options.slot_count,
        options.max_value_bytes,
        options.max_descriptor_bytes,
        options.max_key_bytes,
        options.lease_record_count,
        options.participant_record_count);
    memory_store creator;
    expect(memory_store::try_create_or_open(options, creator) ==
               open_status::success,
           "create reservation crash fixture");
    const std::array<std::byte, 1> key{std::byte{0x31}};
    const std::array<std::byte, 3> value{
        std::byte{0x41}, std::byte{0}, std::byte{0x42}};
    Markers markers("reservation");
    ChildProcess child;
    expect(child.start(arguments(
               "hold-reservation", options, key, value, markers)),
           "start reservation owner");
    expect(markers.wait_ready(child), "reservation reaches Reserved checkpoint");

    auto third_options = options;
    third_options.mode = open_mode::open_existing;
    memory_store blocked;
    expect(memory_store::try_create_or_open(third_options, blocked) ==
               open_status::participant_table_full,
           "participant capacity is exhausted while owner is alive");

    child.terminate_and_wait();
    recovery_report report{};
    expect(creator.try_recover_reservations(false, report) == status::success &&
               report.recovered_count == 1,
           "abrupt Reserved owner is recovered exactly");
    memory_store replacement;
    expect(memory_store::try_create_or_open(third_options, replacement) ==
               open_status::success,
           "recovered participant record is reusable");
    value_lease absent;
    expect(creator.try_acquire(key, absent) == status::not_found,
           "abandoned reservation never becomes visible");
    diagnostics_snapshot diagnostics;
    expect(creator.try_get_diagnostics(diagnostics) == status::success &&
               diagnostics.active_reservation_count() == 0 &&
               diagnostics.free_slot_count() == 2 &&
               diagnostics.active_participant_count() == 2,
           "reservation and participant capacities are restored");
}

void crashed_lease_is_recovered_and_pending_remove_reclaimed() {
    using namespace shared_memory_store;
    auto options = sms_test_options("multiprocess-lease-crash", 2, 2);
    options.participant_record_count = 3;
    options.total_bytes = store_options::calculate_required_bytes(
        options.slot_count,
        options.max_value_bytes,
        options.max_descriptor_bytes,
        options.max_key_bytes,
        options.lease_record_count,
        options.participant_record_count);
    memory_store creator;
    expect(memory_store::try_create_or_open(options, creator) ==
               open_status::success,
           "create lease crash fixture");
    const std::array<std::byte, 1> key{std::byte{0x51}};
    const std::array<std::byte, 4> value{
        std::byte{0x61}, std::byte{0}, std::byte{0x62}, std::byte{0x63}};
    expect(creator.try_publish(key, value) == status::success,
           "publish lease crash value");
    Markers markers("lease");
    ChildProcess child;
    expect(child.start(arguments("hold-lease", options, key, value, markers)),
           "start lease owner");
    expect(markers.wait_ready(child),
           "lease owner observes exact value before checkpoint");
    expect(creator.try_remove(key) == status::remove_pending,
           "active foreign lease defers removal");

    child.terminate_and_wait();
    recovery_report report{};
    expect(creator.try_recover_leases(false, report) == status::success &&
               report.recovered_count == 1,
           "abrupt lease owner is recovered exactly");
    value_lease absent;
    expect(creator.try_acquire(key, absent) == status::not_found,
           "recovered final lease completes pending removal");
    diagnostics_snapshot diagnostics;
    expect(creator.try_get_diagnostics(diagnostics) == status::success &&
               diagnostics.active_lease_count() == 0 &&
               diagnostics.pending_removal_count() == 0 &&
               diagnostics.free_slot_count() == 2 &&
               diagnostics.free_participant_count() == 2,
           "lease, slot, and participant capacity is restored");
}

void hot_native_sources_have_no_os_lock_path() {
    const auto root = std::filesystem::path(SMS_REPOSITORY_ROOT);
    constexpr std::array sources{
        "store.cpp",
        "key_directory.cpp",
        "slot_table.cpp",
        "lease_registry.cpp",
        "reclaimer.cpp",
        "recovery.cpp",
    };
    for (const auto* source : sources) {
        std::ifstream input(root / "src" / "cpp" / "src" / source);
        const std::string text(
            (std::istreambuf_iterator<char>(input)),
            std::istreambuf_iterator<char>());
        expect(!text.empty(), "hot-path source is readable");
        expect(text.find("flock(") == std::string::npos &&
                   text.find("CreateMutex") == std::string::npos &&
                   text.find("WaitForSingleObject") == std::string::npos &&
                   text.find("std::mutex") == std::string::npos &&
                   text.find("lock_guard") == std::string::npos,
               "hot-path source contains no OS or process-global lock");
    }
}

} // namespace

int main(int argc, char** argv) {
    if (argc <= 0) return 1;
    auto executable = std::filesystem::absolute(argv[0]);
#if defined(_WIN32)
    fault_agent = executable.parent_path() / "sms_native_fault_agent.exe";
#else
    fault_agent = executable.parent_path() / "sms_native_fault_agent";
#endif
    expect(std::filesystem::is_regular_file(fault_agent),
           "native fault agent is built beside the test executable");
    if (std::filesystem::is_regular_file(fault_agent)) {
        paused_participant_allows_progress_and_raw_visibility();
        crashed_reservation_is_recovered_and_participant_reused();
        crashed_lease_is_recovered_and_pending_remove_reclaimed();
    }
    hot_native_sources_have_no_os_lock_path();
    if (failures.load(std::memory_order_relaxed) == 0) {
        std::cout << "lock_free_multiprocess_tests: PASS\n";
        return 0;
    }
    return 1;
}
