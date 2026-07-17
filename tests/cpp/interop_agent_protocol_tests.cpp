#include "test_support.hpp"

#include "checkpoint.hpp"
#include "interop_checkpoint_catalog.hpp"

#include <array>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <string_view>
#include <vector>

#if !defined(_WIN32)
#  include <sys/wait.h>
#endif

namespace {

constexpr bool checkpoint_catalog_matches_runtime() {
    if (sms::test_detail::checkpoint_count !=
        static_cast<std::int32_t>(sms::interop_test::checkpoints.size())) {
        return false;
    }
    for (const auto& checkpoint : sms::interop_test::checkpoints) {
        if (checkpoint.id < 1 ||
            checkpoint.id > sms::test_detail::checkpoint_count ||
            sms::test_detail::checkpoint_name(
                static_cast<sms::test_detail::CheckpointId>(checkpoint.id)) !=
                checkpoint.name) {
            return false;
        }
    }
    return true;
}

static_assert(checkpoint_catalog_matches_runtime(),
              "The native runtime and interop checkpoint catalogs diverged.");
static_assert(sms::interop_test::abrupt_exit_code ==
              sms::test_detail::abrupt_exit_code);

bool contains(const std::string& value, const char* fragment) {
    return value.find(fragment) != std::string::npos;
}

std::size_t count_fragment(
    std::string_view value,
    std::string_view fragment) {
    std::size_t count{};
    for (std::size_t offset{};
         (offset = value.find(fragment, offset)) != std::string_view::npos;
         offset += fragment.size()) {
        ++count;
    }
    return count;
}

std::string quote(const std::filesystem::path& value) {
    return "\"" + value.string() + "\"";
}

std::string agent_command(
    const std::filesystem::path& agent,
    const std::filesystem::path& input,
    const std::filesystem::path& output) {
    auto command = quote(agent) + " < " + quote(input) +
        " > " + quote(output);
#if defined(_WIN32)
    // cmd.exe strips the first and last quote when a /C command starts with a
    // quoted executable. Preserve the executable and redirects as one line.
    command = "\"" + command + "\"";
#endif
    return command;
}

std::vector<std::string> read_lines(const std::filesystem::path& path) {
    std::ifstream input(path, std::ios::binary);
    std::vector<std::string> result;
    for (std::string line; std::getline(input, line);) {
        if (!line.empty()) result.push_back(std::move(line));
    }
    return result;
}

bool exited_with(int result, int exit_code) {
#if defined(_WIN32)
    return result == exit_code;
#else
    return result != -1 && WIFEXITED(result) &&
        WEXITSTATUS(result) == exit_code;
#endif
}

std::string checkpoint_arguments(
    std::string_view store_name,
    std::int32_t checkpoint_id,
    std::string_view operation,
    std::string_view key,
    std::int32_t open_mode = 1) {
    return "{\"checkpointId\":" + std::to_string(checkpoint_id) +
        ",\"operation\":\"" + std::string(operation) +
        "\",\"name\":\"" + std::string(store_name) +
        "\",\"openMode\":" + std::to_string(open_mode) +
        ",\"slotCount\":4,\"maxValueBytes\":32,"
        "\"maxDescriptorBytes\":8,\"maxKeyBytes\":8,"
        "\"leaseRecordCount\":2,\"participantRecordCount\":3,"
        "\"enableLeaseRecovery\":true,\"key\":\"" +
        std::string(key) +
        "\",\"value\":\"dg==\",\"descriptor\":\"ZA==\"}";
}

} // namespace

int main(int argc, char** argv) {
    if (argc <= 0) return 1;
    auto executable = std::filesystem::absolute(argv[0]);
#if defined(_WIN32)
    const auto agent = executable.parent_path() / "sms_cpp_interop_agent.exe";
#else
    const auto agent = executable.parent_path() / "sms_cpp_interop_agent";
#endif
    SMS_CHECK(std::filesystem::is_regular_file(agent));

    // The catalog is useful only if every declared pause point is connected to
    // a real engine boundary. Keep this source-level check beside the protocol
    // contract so adding an enum without instrumenting the engine fails CTest.
    constexpr std::array engine_sources{
        "store.cpp",
        "participant_registry.cpp",
        "key_directory.cpp",
        "slot_table.cpp",
        "lease_registry.cpp",
        "reclaimer.cpp",
        "recovery.cpp",
    };
    std::string native_sources;
    for (const auto* source : engine_sources) {
        const auto path = std::filesystem::path(SMS_REPOSITORY_ROOT) /
            "src" / "cpp" / "src" / source;
        std::ifstream input(path, std::ios::binary);
        SMS_CHECK(static_cast<bool>(input));
        native_sources.append(
            std::istreambuf_iterator<char>(input),
            std::istreambuf_iterator<char>());
    }
    for (const auto& checkpoint : sms::interop_test::checkpoints) {
        const auto site = "CheckpointId::" + std::string(checkpoint.name);
        SMS_CHECK(native_sources.find(site) != std::string::npos);
    }

    const auto unique = std::to_string(
        std::chrono::steady_clock::now().time_since_epoch().count());
    const auto root = std::filesystem::temp_directory_path() /
        ("sms-cpp-agent-contract-" + unique);
    std::filesystem::create_directories(root);
    const auto input_path = root / "requests.jsonl";
    const auto output_path = root / "responses.jsonl";
    const auto store_name = sms_test_name("agent-protocol");
    const auto pause_arguments = checkpoint_arguments(
        store_name, 1, "publish", "azE=");
    const auto cancel_arguments = checkpoint_arguments(
        store_name, 1, "publish", "azI=");
    const auto before_location_arguments = checkpoint_arguments(
        store_name, 66, "publish", "azM=");
    const auto after_location_arguments = checkpoint_arguments(
        store_name, 67, "publish", "azQ=");
    {
        std::ofstream input(input_path, std::ios::binary | std::ios::trunc);
        input
            << "{\"id\":\"ping\",\"command\":\"ping\",\"arguments\":{}}\n"
            << "{\"id\":\"open\",\"command\":\"create\",\"arguments\":{"
            << "\"storeId\":\"store\",\"name\":\"" << store_name
            << "\",\"slotCount\":4,\"maxValueBytes\":32,"
               "\"maxDescriptorBytes\":8,\"maxKeyBytes\":8,"
               "\"leaseRecordCount\":2,\"participantRecordCount\":3,"
               "\"enableLeaseRecovery\":true}}\n"
            << "{\"id\":\"diagnostics\",\"command\":\"diagnostics\","
               "\"arguments\":{\"storeId\":\"store\"}}\n"
            << "{\"id\":\"catalog\",\"command\":\"checkpointCatalog\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"pause\",\"command\":\"pauseAtCheckpoint\","
               "\"arguments\":" << pause_arguments << "}\n"
            << "{\"id\":\"pause-again\",\"command\":\"pauseAtCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"resume\",\"command\":\"resumeCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"pause-cancel\",\"command\":\"pauseAtCheckpoint\","
               "\"arguments\":" << cancel_arguments << "}\n"
            << "{\"id\":\"cancel\",\"command\":\"cancelCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"pause-before-location\",\"command\":\"pauseAtCheckpoint\","
               "\"arguments\":" << before_location_arguments << "}\n"
            << "{\"id\":\"resume-before-location\",\"command\":\"resumeCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"pause-after-location\",\"command\":\"pauseAtCheckpoint\","
               "\"arguments\":" << after_location_arguments << "}\n"
            << "{\"id\":\"resume-after-location\",\"command\":\"resumeCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"resume-none\",\"command\":\"resumeCheckpoint\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"hold\",\"command\":\"holdColdLock\","
               "\"arguments\":{\"name\":\"" << store_name << "\"}}\n"
            << "{\"id\":\"hold-again\",\"command\":\"holdColdLock\","
               "\"arguments\":{\"name\":\"" << store_name << "\"}}\n"
            << "{\"id\":\"release-lock\",\"command\":\"releaseColdLock\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"release-again\",\"command\":\"releaseColdLock\","
               "\"arguments\":{}}\n"
            << "{\"id\":\"close\",\"command\":\"close\","
               "\"arguments\":{\"storeId\":\"store\"}}\n"
            << "{\"id\":\"future\",\"command\":\"future-command\","
               "\"arguments\":{}}\n";
        SMS_CHECK(static_cast<bool>(input));
    }

    const auto command = agent_command(agent, input_path, output_path);
    SMS_CHECK(std::system(command.c_str()) == 0);

    const auto lines = read_lines(output_path);
    SMS_CHECK(lines.size() == 20);
    SMS_CHECK(contains(lines[0], "\"id\":\"ping\"") &&
              contains(lines[0], "\"ok\":true") &&
              contains(lines[0], "\"runtime\":\"cpp\"") &&
              contains(lines[0], "\"protocolVersion\":2") &&
              contains(lines[0], "\"checkpointCatalogVersion\":1") &&
              contains(lines[0], "\"layoutMajorVersion\":2") &&
              contains(lines[0], "\"layoutMinorVersion\":0") &&
              contains(lines[0], "\"resourceProtocolVersion\":2") &&
              contains(lines[0], "\"requiredFeatures\":7") &&
              contains(lines[0], "\"optionalFeatures\":0"));
    SMS_CHECK(contains(lines[1], "\"id\":\"open\"") &&
              contains(lines[1], "\"ok\":true") &&
              contains(lines[1], "\"participantRecordCount\":3") &&
              contains(lines[1], "\"protocolInfo\":{") &&
              contains(lines[1], "\"layoutMajorVersion\":2"));
    SMS_CHECK(contains(lines[2], "\"id\":\"diagnostics\"") &&
              contains(lines[2], "\"participantRecordCount\":3") &&
              contains(lines[2], "\"activeParticipantCount\":1") &&
              contains(lines[2], "\"casRetryCount\":") &&
              contains(lines[2], "\"helpedTransitionCount\":") &&
              contains(lines[2], "\"recoveryAttemptCount\":") &&
              !contains(lines[2], "tombstone") &&
              !contains(lines[2], "compaction"));
    SMS_CHECK(contains(lines[3], "\"id\":\"catalog\"") &&
              contains(lines[3], "\"checkpointCatalogVersion\":1") &&
              contains(lines[3], "\"name\":\"PublishBeforeSlotClaim\"") &&
              contains(lines[3], "\"name\":\"DirectoryAfterLocationPublicationBeforeSourceRevalidation\"") &&
              count_fragment(lines[3], "\"family\":") == 67 &&
              count_fragment(lines[3], "\"isPublicOrderingPoint\":") == 67);

    SMS_CHECK(contains(lines[4], "\"id\":\"pause\"") &&
              contains(lines[4], "\"ok\":true") &&
              contains(lines[4], "\"code\":0") &&
              contains(lines[4], "\"checkpointId\":1") &&
              contains(lines[4], "\"checkpointName\":\"PublishBeforeSlotClaim\"") &&
              contains(lines[4], "\"operation\":\"publish\""));
    SMS_CHECK(contains(lines[5], "\"id\":\"pause-again\"") &&
              contains(lines[5], "\"ok\":false") &&
              contains(lines[5], "\"code\":-3") &&
              contains(lines[5], "\"name\":\"CheckpointAlreadyArmed\"") &&
              contains(lines[5], "\"code\":\"checkpoint_already_armed\""));
    SMS_CHECK(contains(lines[6], "\"id\":\"resume\"") &&
              contains(lines[6], "\"ok\":true") &&
              contains(lines[6], "\"code\":0") &&
              contains(lines[6], "\"canceled\":false") &&
              contains(lines[6], "\"checkpointId\":1") &&
              contains(lines[6], "\"openStatus\":{") &&
              contains(lines[6], "\"name\":\"Success\""));
    SMS_CHECK(contains(lines[7], "\"id\":\"pause-cancel\"") &&
              contains(lines[7], "\"ok\":true") &&
              contains(lines[7], "\"checkpointId\":1"));
    SMS_CHECK(contains(lines[8], "\"id\":\"cancel\"") &&
              contains(lines[8], "\"ok\":true") &&
              contains(lines[8], "\"code\":22") &&
              contains(lines[8], "\"name\":\"OperationCanceled\"") &&
              contains(lines[8], "\"canceled\":true") &&
              contains(lines[8], "\"checkpointId\":1"));
    SMS_CHECK(contains(lines[9], "\"id\":\"pause-before-location\"") &&
              contains(lines[9], "\"ok\":true") &&
              contains(lines[9], "\"checkpointId\":66") &&
              contains(lines[9], "\"checkpointName\":\"DirectoryAfterEmptyLocationSourceRevalidationBeforePublicationCas\""));
    SMS_CHECK(contains(lines[10], "\"id\":\"resume-before-location\"") &&
              contains(lines[10], "\"ok\":true") &&
              contains(lines[10], "\"code\":0") &&
              contains(lines[10], "\"checkpointId\":66"));
    SMS_CHECK(contains(lines[11], "\"id\":\"pause-after-location\"") &&
              contains(lines[11], "\"ok\":true") &&
              contains(lines[11], "\"checkpointId\":67") &&
              contains(lines[11], "\"checkpointName\":\"DirectoryAfterLocationPublicationBeforeSourceRevalidation\""));
    SMS_CHECK(contains(lines[12], "\"id\":\"resume-after-location\"") &&
              contains(lines[12], "\"ok\":true") &&
              contains(lines[12], "\"code\":0") &&
              contains(lines[12], "\"checkpointId\":67"));
    SMS_CHECK(contains(lines[13], "\"id\":\"resume-none\"") &&
              contains(lines[13], "\"ok\":false") &&
              contains(lines[13], "\"code\":-5") &&
              contains(lines[13], "\"name\":\"CheckpointNotArmed\"") &&
              contains(lines[13], "\"code\":\"checkpoint_not_armed\""));

    SMS_CHECK(contains(lines[14], "\"id\":\"hold\"") &&
              contains(lines[14], "\"ok\":true") &&
              contains(lines[14], "\"name\":\"Success\""));
    SMS_CHECK(contains(lines[15], "\"id\":\"hold-again\"") &&
              contains(lines[15], "\"ok\":false") &&
              contains(lines[15], "\"code\":\"cold_lock_already_held\""));
    SMS_CHECK(contains(lines[16], "\"id\":\"release-lock\"") &&
              contains(lines[16], "\"released\":true"));
    SMS_CHECK(contains(lines[17], "\"id\":\"release-again\"") &&
              contains(lines[17], "\"ok\":false") &&
              contains(lines[17], "\"code\":\"cold_lock_not_held\""));
    SMS_CHECK(contains(lines[18], "\"id\":\"close\"") &&
              contains(lines[18], "\"ok\":true") &&
              contains(lines[18], "\"closed\":true"));
    SMS_CHECK(contains(lines[19], "\"id\":\"future\"") &&
              contains(lines[19], "\"ok\":false") &&
              contains(lines[19], "\"name\":\"UnsupportedCommand\"") &&
              contains(lines[19], "\"code\":\"unsupported_command\""));

    // Crash is tested in a separate process because the contract deliberately
    // forbids a response or orderly cleanup after the checkpoint is reached.
    const auto crash_input_path = root / "crash-requests.jsonl";
    const auto crash_output_path = root / "crash-responses.jsonl";
    const auto crash_store_name = sms_test_name("agent-checkpoint-crash");
    const auto crash_arguments = checkpoint_arguments(
        crash_store_name, 4, "reserve", "Y3I=");
    {
        std::ofstream input(
            crash_input_path, std::ios::binary | std::ios::trunc);
        input
            << "{\"id\":\"crash-open\",\"command\":\"create\",\"arguments\":{"
            << "\"storeId\":\"store\",\"name\":\"" << crash_store_name
            << "\",\"slotCount\":4,\"maxValueBytes\":32,"
               "\"maxDescriptorBytes\":8,\"maxKeyBytes\":8,"
               "\"leaseRecordCount\":2,\"participantRecordCount\":3,"
               "\"enableLeaseRecovery\":true}}\n"
            << "{\"id\":\"crash\",\"command\":\"crashAtCheckpoint\","
               "\"arguments\":" << crash_arguments << "}\n";
        SMS_CHECK(static_cast<bool>(input));
    }
    const auto crash_command = agent_command(
        agent, crash_input_path, crash_output_path);
    const auto crash_result = std::system(crash_command.c_str());
    SMS_CHECK(exited_with(
        crash_result, sms::interop_test::abrupt_exit_code));
    const auto crash_lines = read_lines(crash_output_path);
    SMS_CHECK(crash_lines.size() == 1);
    SMS_CHECK(contains(crash_lines[0], "\"id\":\"crash-open\"") &&
              contains(crash_lines[0], "\"ok\":true"));

    std::error_code cleanup_error;
    std::filesystem::remove_all(root, cleanup_error);
    std::cout
        << "interop_agent_protocol_tests: PASS "
           "(67/67 catalog entries and production sites; "
           "pause/cancel/crash transport verified)\n";
    return 0;
}
