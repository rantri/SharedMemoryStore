#include "internal.hpp"
#include "linux_owner_lifecycle.hpp"
#include "shared_memory_store/store.hpp"
#include "test_support.hpp"

#if !defined(_WIN32)

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fcntl.h>
#include <fstream>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>
#include <string_view>
#include <sys/file.h>
#include <sys/stat.h>
#include <thread>
#include <unistd.h>
#include <vector>

#if !defined(F_OFD_SETLK)
#define F_OFD_SETLK 37
#endif

using namespace shared_memory_store;
using namespace sms::detail;

namespace {

int failures{};

void expect(bool condition, std::string_view message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

class TemporaryDirectory final {
public:
    explicit TemporaryDirectory(std::string_view suffix) {
        path_ = std::filesystem::temp_directory_path() /
            ("sms-linux-v2-" + std::to_string(::getpid()) + "-" +
             std::to_string(std::chrono::steady_clock::now()
                                .time_since_epoch()
                                .count()) +
             "-" + std::string(suffix));
        std::filesystem::create_directories(path_);
        ::chmod(path_.c_str(), 0700);
    }

    ~TemporaryDirectory() {
        std::error_code error;
        std::filesystem::remove_all(path_, error);
    }

    [[nodiscard]] std::string child(std::string_view name) const {
        return (path_ / std::string(name)).string();
    }

private:
    std::filesystem::path path_;
};

class OfdLock final {
public:
    explicit OfdLock(const std::string& path) {
        descriptor_ = ::open(
            path.c_str(),
            O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK,
            0600);
        if (descriptor_ >= 0) ::fchmod(descriptor_, 0600);
    }

    ~OfdLock() {
        unlock();
        if (descriptor_ >= 0) ::close(descriptor_);
    }

    OfdLock(const OfdLock&) = delete;
    OfdLock& operator=(const OfdLock&) = delete;

    [[nodiscard]] bool usable() const noexcept { return descriptor_ >= 0; }

    [[nodiscard]] bool try_lock() noexcept {
        if (descriptor_ < 0) return false;
        struct flock request{};
        request.l_type = F_WRLCK;
        request.l_whence = SEEK_SET;
        request.l_start = 0;
        request.l_len = 1;
        if (::fcntl(descriptor_, F_OFD_SETLK, &request) != 0) return false;
        locked_ = true;
        return true;
    }

    void unlock() noexcept {
        if (!locked_ || descriptor_ < 0) return;
        struct flock request{};
        request.l_type = F_UNLCK;
        request.l_whence = SEEK_SET;
        request.l_start = 0;
        request.l_len = 1;
        (void)::fcntl(descriptor_, F_OFD_SETLK, &request);
        locked_ = false;
    }

private:
    int descriptor_{-1};
    bool locked_{};
};

bool regular_mode(const std::string& path, mode_t expected) {
    struct stat information{};
    return ::lstat(path.c_str(), &information) == 0 &&
        !S_ISLNK(information.st_mode) && S_ISREG(information.st_mode) &&
        (information.st_mode & 0777) == expected;
}

bool directory_mode(const std::string& path, mode_t expected) {
    struct stat information{};
    return ::lstat(path.c_str(), &information) == 0 &&
        !S_ISLNK(information.st_mode) && S_ISDIR(information.st_mode) &&
        (information.st_mode & 0777) == expected;
}

ino_t inode_of(const std::string& path) {
    struct stat information{};
    return ::lstat(path.c_str(), &information) == 0
        ? information.st_ino
        : static_cast<ino_t>(0);
}

std::vector<std::string> read_lines(const std::string& path) {
    std::vector<std::string> result;
    std::ifstream input(path);
    std::string line;
    while (std::getline(input, line)) result.push_back(line);
    return result;
}

void write_text(const std::string& path, std::string_view text) {
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(text.data(), static_cast<std::streamsize>(text.size()));
    output.flush();
    output.close();
    ::chmod(path.c_str(), 0600);
}

ResourceName integration_resource(std::string_view suffix) {
    ResourceName result{};
    const auto name = sms_test_name(std::string("linux-v2-") + std::string(suffix));
    if (!make_resource_name(name, result)) {
        throw std::runtime_error("Could not derive the Linux integration resource.");
    }
    const auto directory = std::filesystem::path(result.linux_region_path).parent_path();
    std::filesystem::create_directories(directory);
    ::chmod(directory.c_str(), 0700);
    return result;
}

store_options options_for(const ResourceName& resource) {
    return store_options::create(
        resource.public_name,
        4,
        128,
        32,
        64,
        8,
        8,
        open_mode::create_or_open);
}

void cleanup_resource(const ResourceName& resource) noexcept {
    LinuxOwnerLifecycle::delete_stale_owner_artifacts(resource.linux_owners_path);
    (void)::unlink(resource.linux_region_path.c_str());
    (void)::unlink(resource.linux_owners_path.c_str());
    (void)::unlink(resource.linux_lock_path.c_str());
    (void)::unlink(resource.linux_lifecycle_path.c_str());
    std::error_code error;
    std::filesystem::remove_all(resource.linux_owners_path + ".artifacts", error);
}

void owner_artifacts_are_isolated_per_store() {
    TemporaryDirectory temporary("artifact-isolation");
    const auto owners_path = temporary.child("store.owners");
    const std::string token = "00112233445566778899aabbccddeeff";
    const auto artifact_directory = owners_path + ".artifacts";
    const auto anchor = LinuxOwnerAnchor::artifact_path(owners_path, token);
    const auto marker = LinuxOwnerLifecycle::release_marker_path(
        owners_path, token);

    expect(std::filesystem::path(anchor).parent_path() == artifact_directory,
           "owner anchor is isolated below the exact store artifact directory");
    expect(std::filesystem::path(anchor).filename() == "anchor." + token,
           "owner anchor uses the canonical isolated basename");
    expect(std::filesystem::path(marker).parent_path() == artifact_directory,
           "release marker is isolated below the exact store artifact directory");
    expect(std::filesystem::path(marker).filename() ==
               "released." + token + ".ready",
           "release marker uses the canonical isolated basename");

    const auto unrelated_flat_marker = owners_path + ".released." + token + ".ready";
    write_text(unrelated_flat_marker, "malformed legacy-shaped noise");
    LinuxOwnerSnapshot snapshot{};
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) == SMS_STATUS_SUCCESS,
           "cold preparation never enumerates unrelated flat-root artifacts");
    expect(std::filesystem::exists(unrelated_flat_marker),
           "flat-root noise remains outside exact per-store cleanup");
}

void lifecycle_order_anchor_and_stable_inode() {
    auto resource = integration_resource("ordering");
    cleanup_resource(resource);

    OfdLock ordinary(resource.linux_lock_path);
    expect(ordinary.usable() && ordinary.try_lock(), "test holds ordinary lock");

    memory_store store;
    std::atomic<std::int32_t> opened{static_cast<std::int32_t>(open_status::mapping_failed)};
    std::thread opener([&] {
        opened.store(
            static_cast<std::int32_t>(memory_store::try_create_or_open(
                options_for(resource),
                store,
                wait_options{1000})),
            std::memory_order_release);
    });

    // Give the opener an uncontended chance to acquire .lifecycle before the
    // probe opens a second OFD. Repeated eager probes can otherwise starve the
    // very acquisition this schedule is intended to observe.
    std::this_thread::sleep_for(std::chrono::milliseconds(100));
    bool lifecycle_observed_held{};
    for (std::int32_t attempt = 0; attempt < 10; ++attempt) {
        OfdLock probe(resource.linux_lifecycle_path);
        if (probe.usable() && !probe.try_lock() &&
            (errno == EAGAIN || errno == EACCES)) {
            lifecycle_observed_held = true;
            break;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    expect(lifecycle_observed_held, "open acquires lifecycle before ordinary lock");
    expect(!std::filesystem::exists(resource.linux_region_path),
           "mapping is not created before ordinary lock acquisition");
    ordinary.unlock();
    opener.join();
    expect(opened.load(std::memory_order_acquire) ==
               static_cast<std::int32_t>(open_status::success),
           "ordered open succeeds after ordinary lock release");

    const auto owners = read_lines(resource.linux_owners_path);
    expect(owners.size() == 1, "one exact owner line is committed");
    LinuxOwnerRecord record{};
    expect(owners.size() == 1 &&
               LinuxOwnerLifecycle::parse_exact_owner_line(owners.front(), record),
           "owner line uses canonical pid/start/token form");
    const auto anchor_path = owners.empty()
        ? std::string{}
        : LinuxOwnerAnchor::artifact_path(
              resource.linux_owners_path, record.owner_token);
    expect(!anchor_path.empty() && regular_mode(anchor_path, 0600),
           "private owner anchor is a mode-0600 regular file");
    expect(!anchor_path.empty() &&
               LinuxOwnerAnchor::probe(
                   resource.linux_owners_path, record.owner_token) ==
                   LinuxOwnerAnchorState::locked,
           "independent anchor probe proves the owner live");
    expect(regular_mode(resource.linux_region_path, 0600),
           "region has private file mode");
    expect(regular_mode(resource.linux_owners_path, 0600),
           "owner sidecar has private file mode");
    expect(regular_mode(resource.linux_lock_path, 0600),
           "ordinary rendezvous has private file mode");
    expect(regular_mode(resource.linux_lifecycle_path, 0600),
           "lifecycle rendezvous has private file mode");
    expect(directory_mode(
               std::filesystem::path(resource.linux_region_path)
                   .parent_path()
                   .string(),
               0700),
           "resource directory has private directory mode");

    const auto lock_inode = inode_of(resource.linux_lock_path);
    const auto lifecycle_inode = inode_of(resource.linux_lifecycle_path);
    store.close();
    expect(lock_inode != 0 && inode_of(resource.linux_lock_path) == lock_inode,
           "final close preserves the ordinary lock inode");
    expect(lifecycle_inode != 0 &&
               inode_of(resource.linux_lifecycle_path) == lifecycle_inode,
           "final close preserves the lifecycle inode");

    memory_store reopened;
    expect(memory_store::try_create_or_open(
               options_for(resource), reopened, wait_options{1000}) ==
               open_status::success,
           "reopen succeeds through the retained rendezvous inodes");
    expect(inode_of(resource.linux_lock_path) == lock_inode,
           "ordinary lock inode remains stable across reincarnation");
    expect(inode_of(resource.linux_lifecycle_path) == lifecycle_inode,
           "lifecycle inode remains stable across reincarnation");
    reopened.close();
    cleanup_resource(resource);
}

void bounded_close_marker_and_exact_reconciliation() {
    auto resource = integration_resource("bounded-close");
    cleanup_resource(resource);
    memory_store store;
    expect(memory_store::try_create_or_open(
               options_for(resource), store, wait_options{1000}) ==
               open_status::success,
           "bounded-close fixture opens");
    const auto owners = read_lines(resource.linux_owners_path);
    LinuxOwnerRecord record{};
    expect(owners.size() == 1 &&
               LinuxOwnerLifecycle::parse_exact_owner_line(owners.front(), record),
           "bounded-close owner is canonical");

    OfdLock lifecycle(resource.linux_lifecycle_path);
    expect(lifecycle.usable() && lifecycle.try_lock(),
           "test blocks lifecycle cleanup");
    const auto started = std::chrono::steady_clock::now();
    store.close();
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started);
    expect(elapsed < std::chrono::milliseconds(1000),
           "close remains bounded while lifecycle is contended");

    const auto marker = LinuxOwnerLifecycle::release_marker_path(
        resource.linux_owners_path, record.owner_token);
    expect(regular_mode(marker, 0600),
           "bounded close durably finalizes a mode-0600 release marker");
    expect(read_lines(marker) == std::vector<std::string>{record.line},
           "release marker stores the ordinal-exact owner line");
    expect(read_lines(resource.linux_owners_path) ==
               std::vector<std::string>{record.line},
           "contended close leaves the sidecar unchanged until replay");
    expect(!std::filesystem::exists(LinuxOwnerAnchor::artifact_path(
               resource.linux_owners_path, record.owner_token)),
           "finalized marker permits private anchor release");

    lifecycle.unlock();
    memory_store reopened;
    expect(memory_store::try_create_or_open(
               options_for(resource), reopened, wait_options{1000}) ==
               open_status::success,
           "next cold open replays the finalized marker");
    expect(!std::filesystem::exists(marker),
           "marker is deleted only after sidecar replacement commits");
    const auto replacement = read_lines(resource.linux_owners_path);
    expect(std::find(replacement.begin(), replacement.end(), record.line) ==
               replacement.end(),
           "replay removes only the exact released owner");
    reopened.close();
    cleanup_resource(resource);
}

void namespace_anchor_and_orphan_sweep() {
    TemporaryDirectory temporary("namespace");
    const auto owners_path = temporary.child("case.owners");
    LinuxOwnerRecord local{};
    std::unique_ptr<LinuxOwnerAnchor> anchor;
    expect(LinuxOwnerLifecycle::create_current_owner(
               owners_path, local, anchor) == SMS_STATUS_SUCCESS && anchor,
           "namespace fixture creates a locked anchor");

    const auto foreign_line =
        std::string("2147483647:proc-1:") + local.owner_token;
    expect(LinuxOwnerLifecycle::commit_registration(
               owners_path, {}, foreign_line) == SMS_STATUS_SUCCESS,
           "foreign namespace-shaped owner line commits");
    LinuxOwnerSnapshot snapshot{};
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_SUCCESS &&
               snapshot.has_live_owner &&
               snapshot.committed_owners ==
                   std::vector<std::string>{foreign_line},
           "locked anchor dominates namespace-local pid visibility");

    const auto locked_unreferenced_token =
        std::string("11112222333344445555666677778888");
    std::unique_ptr<LinuxOwnerAnchor> locked_unreferenced;
    expect(LinuxOwnerAnchor::create(
               owners_path,
               locked_unreferenced_token,
               locked_unreferenced) == SMS_STATUS_SUCCESS,
           "unreferenced locked anchor fixture creates");
    LinuxOwnerLifecycle::sweep_unreferenced_anchors(
        owners_path, snapshot.committed_owners);
    expect(std::filesystem::exists(LinuxOwnerAnchor::artifact_path(
               owners_path, locked_unreferenced_token)),
           "orphan sweep retains a locked unreferenced anchor");

    const auto unlocked_token = std::string("9999aaaabbbbccccddddeeeeffff0000");
    const auto unlocked_path = LinuxOwnerAnchor::artifact_path(
        owners_path, unlocked_token);
    write_text(unlocked_path, "");
    LinuxOwnerLifecycle::sweep_unreferenced_anchors(
        owners_path, snapshot.committed_owners);
    expect(!std::filesystem::exists(unlocked_path),
           "orphan sweep removes only an unlocked canonical anchor");

    anchor->release_and_remove();
    anchor.reset();
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_SUCCESS &&
               !snapshot.has_live_owner &&
               snapshot.committed_owners.empty(),
           "unlocked foreign-namespace anchor becomes safely stale");
    locked_unreferenced.reset();
}

void marker_reconciliation_is_ordinal_exact_and_idempotent() {
    TemporaryDirectory temporary("marker-exact");
    const auto owners_path = temporary.child("case.owners");
    LinuxOwnerRecord released{};
    LinuxOwnerRecord survivor{};
    std::unique_ptr<LinuxOwnerAnchor> released_anchor;
    std::unique_ptr<LinuxOwnerAnchor> survivor_anchor;
    expect(LinuxOwnerLifecycle::create_current_owner(
               owners_path, released, released_anchor) == SMS_STATUS_SUCCESS,
           "released-owner fixture creates");
    expect(LinuxOwnerLifecycle::create_current_owner(
               owners_path, survivor, survivor_anchor) == SMS_STATUS_SUCCESS,
           "surviving-owner fixture creates");
    expect(LinuxOwnerLifecycle::commit_registration(
               owners_path, {}, released.line) == SMS_STATUS_SUCCESS,
           "first exact owner commits");
    expect(LinuxOwnerLifecycle::commit_registration(
               owners_path, {released.line}, survivor.line) == SMS_STATUS_SUCCESS,
           "second exact owner commits");
    expect(LinuxOwnerLifecycle::publish_release_marker(
               owners_path, released.line),
           "exact replay marker publishes");
    expect(LinuxOwnerLifecycle::reconcile_release_markers(owners_path) ==
               SMS_STATUS_SUCCESS,
           "exact replay marker reconciles");
    expect(read_lines(owners_path) == std::vector<std::string>{survivor.line},
           "reconciliation removes only the ordinal-exact released line");

    // Replay after a crash between sidecar replacement and marker deletion is
    // idempotent even when the exact owner line is already absent.
    expect(LinuxOwnerLifecycle::publish_release_marker(
               owners_path, released.line),
           "idempotent replay marker republishes");
    expect(LinuxOwnerLifecycle::reconcile_release_markers(owners_path) ==
               SMS_STATUS_SUCCESS &&
               read_lines(owners_path) ==
                   std::vector<std::string>{survivor.line},
           "idempotent replay preserves every unrelated owner");
    released_anchor.reset();
    survivor_anchor.reset();
}

void malformed_artifacts_fail_closed() {
    TemporaryDirectory temporary("malformed");
    const auto owners_path = temporary.child("case.owners");

    write_text(owners_path, "malformed-owner-evidence\n");
    LinuxOwnerSnapshot snapshot{};
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_SUCCESS && snapshot.has_live_owner &&
               snapshot.committed_owners ==
                   std::vector<std::string>{"malformed-owner-evidence"},
           "malformed owner evidence is retained conservatively");

    const auto token = std::string("00112233445566778899aabbccddeeff");
    const auto ambiguous_line = std::string("2147483647:proc-1:") + token;
    expect(LinuxOwnerLifecycle::commit_registration(
               owners_path, {}, ambiguous_line) == SMS_STATUS_SUCCESS,
           "ambiguous anchor fixture owner commits");
    const auto anchor_path = LinuxOwnerAnchor::artifact_path(owners_path, token);
    const auto symlink_target = temporary.child("target");
    write_text(symlink_target, "");
    std::filesystem::create_symlink(symlink_target, anchor_path);
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_SUCCESS && snapshot.has_live_owner,
           "symlink anchor is ambiguous and cannot authorize deletion");
    expect(std::filesystem::is_symlink(anchor_path),
           "conservative sweep retains a symlink anchor");
    (void)::unlink(anchor_path.c_str());

    const auto marker = LinuxOwnerLifecycle::release_marker_path(
        owners_path, token);
    write_text(marker, "2147483647:proc-1:ffeeddccbbaa99887766554433221100\n");
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_CORRUPT_STORE,
           "marker filename/content token mismatch fails cold preparation");
    expect(std::filesystem::exists(marker),
           "malformed finalized marker is retained");
    (void)::unlink(marker.c_str());

    const auto owners_target = temporary.child("owners-target");
    write_text(owners_target, "");
    (void)::unlink(owners_path.c_str());
    std::filesystem::create_symlink(owners_target, owners_path);
    expect(LinuxOwnerLifecycle::prepare(owners_path, snapshot) ==
               SMS_STATUS_CORRUPT_STORE,
           "symbolic-link owner sidecar fails closed");
}

void platform_rejects_linked_rendezvous_and_nonregular_region() {
    {
        auto resource = integration_resource("linked-lifecycle");
        cleanup_resource(resource);
        const auto target = resource.linux_lifecycle_path + ".target";
        write_text(target, "");
        std::filesystem::create_symlink(target, resource.linux_lifecycle_path);
        memory_store store;
        expect(memory_store::try_create_or_open(
                   options_for(resource), store, wait_options::no_wait()) ==
                   open_status::mapping_failed,
               "linked lifecycle rendezvous is never followed");
        expect(!std::filesystem::exists(resource.linux_region_path),
               "linked lifecycle failure creates no mapping");
        (void)::unlink(resource.linux_lifecycle_path.c_str());
        (void)::unlink(target.c_str());
        cleanup_resource(resource);
    }

    {
        auto resource = integration_resource("nonregular-region");
        cleanup_resource(resource);
        std::filesystem::create_directory(resource.linux_region_path);
        memory_store store;
        expect(memory_store::try_create_or_open(
                   options_for(resource), store, wait_options::no_wait()) ==
                   open_status::mapping_failed,
               "nonregular data-region artifact fails closed");
        expect(std::filesystem::is_directory(resource.linux_region_path),
               "nonregular data artifact is retained conservatively");
        std::filesystem::remove(resource.linux_region_path);
        cleanup_resource(resource);
    }
}

void failed_post_mapping_open_is_recoverable() {
    auto resource = integration_resource("failed-open");
    cleanup_resource(resource);
    auto insufficient = options_for(resource);
    --insufficient.total_bytes;

    for (std::int32_t attempt = 0; attempt < 2; ++attempt) {
        memory_store rejected;
        const auto started = std::chrono::steady_clock::now();
        const auto status = memory_store::try_create_or_open(
            insufficient, rejected, wait_options{1000});
        const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started);
        expect(status == open_status::insufficient_capacity,
               "post-mapping Linux creator validation reports InsufficientCapacity");
        expect(elapsed < std::chrono::milliseconds(
                   LinuxOwnerLifecycle::bounded_close_milliseconds),
               "cold-locked failed-open cleanup never self-contends for lifecycle");
        expect(!std::filesystem::exists(resource.linux_region_path) &&
                   !std::filesystem::exists(resource.linux_owners_path),
               "cold-locked cleanup removes exact mapping and owner evidence");
    }

    memory_store replacement;
    expect(memory_store::try_create_or_open(
               options_for(resource), replacement, wait_options{1000}) ==
               open_status::success,
           "failed Linux creator leaves recoverable mapping/owner state");
    replacement.close();
    cleanup_resource(resource);
}

} // namespace

int main() {
    owner_artifacts_are_isolated_per_store();
    lifecycle_order_anchor_and_stable_inode();
    bounded_close_marker_and_exact_reconciliation();
    namespace_anchor_and_orphan_sweep();
    marker_reconciliation_is_ordinal_exact_and_idempotent();
    malformed_artifacts_fail_closed();
    platform_rejects_linked_rendezvous_and_nonregular_region();
    failed_post_mapping_open_is_recoverable();
    if (failures == 0) {
        std::cout << "platform_linux_v2_tests: PASS\n";
        return 0;
    }
    return 1;
}

#endif
