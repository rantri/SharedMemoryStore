#pragma once

#if !defined(_WIN32)

#include "shared_memory_store/c_api.h"

#include <cstdint>
#include <memory>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace sms::detail {

enum class LinuxOwnerAnchorState : std::int32_t {
    missing,
    locked,
    unlocked,
    ambiguous
};

struct LinuxOwnerRecord {
    std::int32_t process_id{};
    std::string process_start_token;
    std::string owner_token;
    std::string line;
};

struct LinuxOwnerSnapshot {
    // When any live or ambiguous witness exists, this is the complete
    // committed sidecar.  When none exists, prepare() first commits an empty
    // sidecar and returns an empty vector.
    std::vector<std::string> committed_owners;
    bool has_live_owner{};
};

// One private owner-liveness artifact.  Its exclusive flock is tied to this
// open file description and remains held until the mapped view is gone and the
// exact owner release has either committed or acquired a durable marker.
class LinuxOwnerAnchor final {
public:
    LinuxOwnerAnchor(const LinuxOwnerAnchor&) = delete;
    LinuxOwnerAnchor& operator=(const LinuxOwnerAnchor&) = delete;
    ~LinuxOwnerAnchor();

    [[nodiscard]] const std::string& path() const noexcept { return path_; }
    [[nodiscard]] const std::string& owner_token() const noexcept {
        return owner_token_;
    }

    void release_and_remove() noexcept;

    [[nodiscard]] static sms_status create(
        std::string_view owners_path,
        std::string_view owner_token,
        std::unique_ptr<LinuxOwnerAnchor>& result) noexcept;

    [[nodiscard]] static LinuxOwnerAnchorState probe(
        std::string_view owners_path,
        std::string_view owner_token) noexcept;

    [[nodiscard]] static std::string artifact_path(
        std::string_view owners_path,
        std::string_view owner_token);

private:
    LinuxOwnerAnchor(int descriptor, std::string path, std::string owner_token)
        : descriptor_(descriptor),
          path_(std::move(path)),
          owner_token_(std::move(owner_token)) {}

    int descriptor_{-1};
    std::string path_;
    std::string owner_token_;
};

class LinuxOwnerLifecycle final {
public:
    static constexpr std::int64_t bounded_close_milliseconds = 250;

    // Creates the canonical current-process owner line and acquires its private
    // anchor before the line can be published.
    [[nodiscard]] static sms_status create_current_owner(
        std::string_view owners_path,
        LinuxOwnerRecord& record,
        std::unique_ptr<LinuxOwnerAnchor>& anchor) noexcept;

    // Must run while .lifecycle is held.  Finalized markers are reconciled
    // before liveness classification and any no-live result is committed as an
    // empty sidecar before orphan-anchor sweeping.
    [[nodiscard]] static sms_status prepare(
        std::string_view owners_path,
        LinuxOwnerSnapshot& snapshot) noexcept;

    // Publishes one exact owner line by atomic sidecar replacement.  The caller
    // must already own the matching locked anchor and .lifecycle.
    [[nodiscard]] static sms_status commit_registration(
        std::string_view owners_path,
        const std::vector<std::string>& committed_owners,
        std::string_view exact_owner_line) noexcept;

    // Reconciles markers, filters provably stale owners, removes only the
    // ordinal-exact line, commits the replacement sidecar, and sweeps safe
    // orphan anchors.  The caller holds .lifecycle.
    [[nodiscard]] static sms_status remove_exact_under_lifecycle(
        std::string_view owners_path,
        std::string_view exact_owner_line,
        bool& no_owners_remain) noexcept;

    [[nodiscard]] static sms_status reconcile_release_markers(
        std::string_view owners_path) noexcept;

    // Used only after the bounded .lifecycle acquisition fails.  The marker is
    // written and flushed through a unique temporary file, atomically renamed,
    // and its directory entry is synchronized before success is reported.
    [[nodiscard]] static bool publish_release_marker(
        std::string_view owners_path,
        std::string_view exact_owner_line) noexcept;

    [[nodiscard]] static std::string release_marker_path(
        std::string_view owners_path,
        std::string_view owner_token);

    static void sweep_unreferenced_anchors(
        std::string_view owners_path,
        const std::vector<std::string>& committed_owners) noexcept;

    // Deletes only owner artifacts whose canonical identity is proven.  The
    // persistent .lock and .lifecycle rendezvous paths are intentionally not
    // accepted by this API and therefore cannot be unlinked here.
    static void delete_stale_owner_artifacts(
        std::string_view owners_path) noexcept;

    // If close cannot commit a sidecar update or marker, retain the lock for
    // the rest of this process rather than turning uncertain evidence stale.
    static void retain_ambiguous_anchor(
        std::unique_ptr<LinuxOwnerAnchor> anchor) noexcept;

    [[nodiscard]] static bool parse_exact_owner_line(
        std::string_view line,
        LinuxOwnerRecord& record) noexcept;
};

} // namespace sms::detail

#endif
