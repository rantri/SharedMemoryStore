#include "cold_open.hpp"
#include "store_control.hpp"

#include <cstdint>
#include <cstring>
#include <iostream>
#include <vector>

using namespace sms::detail;

namespace {

int failures{};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

LayoutV2 layout_for(
    std::int32_t slots = 2,
    std::int32_t participants = 2,
    std::int32_t value_bytes = 64) {
    LayoutV2 layout{};
    expect(LayoutV2::calculate(
        1'000'000, slots, value_bytes, 8, 16, 4, participants, layout),
        "layout calculation");
    return layout;
}

ParticipantIdentity owner(std::int32_t pid) {
#if defined(_WIN32)
    constexpr auto identity_kind = identity_windows_creation_file_time;
    constexpr std::uint64_t namespace_id = 0;
#else
    constexpr auto identity_kind = identity_linux_proc_start_ticks;
    constexpr std::uint64_t namespace_id = 55;
#endif
    return ParticipantIdentity{
        pid,
        identity_kind,
        1000 + pid,
        namespace_id};
}

constexpr std::uint64_t owner_namespace() noexcept {
#if defined(_WIN32)
    return 0;
#else
    return 55;
#endif
}

void creator_initializes_and_attaches() {
    auto layout = layout_for();
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    ColdOpenV2 open(bytes.data(), bytes.size());
    const auto result = open.attach(
        true,
        ColdOpenMode::create_new,
        layout,
        owner(10),
        123,
        owner_namespace(),
        OperationBudget::unbounded_scan());
    expect(result.status == ColdOpenStatus::success, "creator open succeeds");
    expect(result.initialized, "creator records initialization authority");
    const auto& header = *reinterpret_cast<const StoreHeaderV2*>(bytes.data());
    expect(header.Magic == sms2_magic, "creator writes SMS2");
    expect(header.RequiredFeatures == 7 && header.OptionalFeatures == 0, "creator writes feature masks");
    expect(header.Control == sms2_store_ready, "creator release-publishes ready");
    expect(result.registration.valid(layout.participant_record_count), "creator participant active");
}

void existing_zero_and_noncurrent_headers_fail_closed() {
    auto layout = layout_for();
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    ColdOpenV2 open(bytes.data(), bytes.size());
    auto result = open.attach(
        false,
        ColdOpenMode::create_or_open,
        layout,
        owner(11),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt());
    expect(result.status == ColdOpenStatus::store_busy, "existing zero create-or-open stays unmodified");
    expect(reinterpret_cast<StoreHeaderV2*>(bytes.data())->Magic == 0, "zero header not initialized by opener");

    reinterpret_cast<StoreHeaderV2*>(bytes.data())->Magic = 0x3153'4d53U;
    result = open.attach(
        false,
        ColdOpenMode::open_existing,
        layout,
        owner(12),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt());
    expect(result.status == ColdOpenStatus::incompatible_layout, "noncurrent header rejected");
}

void feature_dimension_and_capacity_mismatch_reject() {
    auto layout = layout_for();
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    ColdOpenV2 creator(bytes.data(), bytes.size());
    expect(creator.attach(
        true,
        ColdOpenMode::create_new,
        layout,
        owner(20),
        999,
        owner_namespace(),
        OperationBudget::unbounded_scan()).status == ColdOpenStatus::success,
        "mismatch fixture creation");

    auto* header = reinterpret_cast<StoreHeaderV2*>(bytes.data());
    header->RequiredFeatures = 3;
    expect(creator.attach(
        false,
        ColdOpenMode::open_existing,
        layout,
        owner(21),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status == ColdOpenStatus::incompatible_layout,
        "required mask mismatch rejected");
    header->RequiredFeatures = 7;

    auto other = layout_for(3);
    expect(creator.attach(
        false,
        ColdOpenMode::open_existing,
        other,
        owner(22),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status == ColdOpenStatus::incompatible_layout,
        "dimension mismatch rejected");

    const auto saved_total = header->TotalBytes;
    header->TotalBytes = static_cast<std::int64_t>(bytes.size()) + 8;
    expect(creator.attach(
        false,
        ColdOpenMode::open_existing,
        layout,
        owner(23),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status == ColdOpenStatus::incompatible_layout,
        "header beyond actual capacity rejected");
    header->TotalBytes = saved_total;
}

void existing_protocol_precedes_requested_capacity() {
    auto layout = layout_for();
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    ColdOpenV2 open(bytes.data(), bytes.size());
    expect(open.attach(
        true,
        ColdOpenMode::create_new,
        layout,
        owner(24),
        1001,
        owner_namespace(),
        OperationBudget::unbounded_scan()).status == ColdOpenStatus::success,
        "capacity precedence fixture creation");

    LayoutV2 oversized_request{};
    expect(LayoutV2::calculate(
        layout.total_bytes + 4096,
        2,
        64,
        8,
        16,
        4,
        3,
        oversized_request), "oversized existing request layout calculation");
    expect(open.attach(
        false,
        ColdOpenMode::open_existing,
        oversized_request,
        owner(25),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status ==
        ColdOpenStatus::incompatible_layout,
        "existing SMS2 mismatch is layout incompatibility before requested capacity");

    LayoutV2 undersized_request{};
    expect(LayoutV2::calculate(
        4096,
        3,
        17,
        5,
        9,
        4,
        64,
        undersized_request), "undersized request layout calculation");
    std::vector<std::uint8_t> retired_bytes(4096);
    retired_bytes[0] = 0x53;
    retired_bytes[1] = 0x4d;
    retired_bytes[2] = 0x53;
    retired_bytes[3] = 0x31;
    ColdOpenV2 retired(retired_bytes.data(), retired_bytes.size());
    expect(retired.attach(
        false,
        ColdOpenMode::open_existing,
        undersized_request,
        owner(26),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status ==
        ColdOpenStatus::incompatible_layout,
        "retired mapping identity precedes requested SMS2 capacity");
    expect(retired.attach(
        true,
        ColdOpenMode::create_or_open,
        undersized_request,
        owner(27),
        1002,
        owner_namespace(),
        OperationBudget::structural_attempt()).status ==
        ColdOpenStatus::insufficient_capacity,
        "physical creator reports insufficient requested capacity");
}

void disposition_participant_capacity_and_architecture() {
    auto layout = layout_for(1, 1);
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    ColdOpenV2 open(bytes.data(), bytes.size());
    expect(open.attach(
        true,
        ColdOpenMode::create_new,
        layout,
        owner(30),
        777,
        owner_namespace(),
        OperationBudget::unbounded_scan()).status == ColdOpenStatus::success,
        "single participant creator");
    expect(open.attach(
        false,
        ColdOpenMode::create_new,
        layout,
        owner(31),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status == ColdOpenStatus::already_exists,
        "existing create-new reports already exists before payload");
    expect(open.attach(
        false,
        ColdOpenMode::open_existing,
        layout,
        owner(32),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt()).status == ColdOpenStatus::participant_table_full,
        "participant capacity is open status");
    expect(open.attach(
        false,
        ColdOpenMode::open_existing,
        layout,
        owner(33),
        1,
        owner_namespace(),
        OperationBudget::structural_attempt(),
        false).status == ColdOpenStatus::unsupported_platform,
        "unsupported architecture never falls back");
}

void store_control_validation_and_exact_corruption_latch() {
    auto layout = layout_for();
    std::vector<std::uint8_t> bytes(static_cast<std::size_t>(layout.total_bytes));
    StoreControlV2 control(bytes.data(), bytes.size(), layout);
    expect(control.initialize_creator(
               0x1234'5678ULL,
               owner_namespace(),
               sms2_pid_namespace_recovery_enabled,
               OperationBudget::unbounded_scan()),
           "physical creator initializes canonical SMS2 control");
    expect(control.validate_existing() == StoreControlStatus::success,
           "canonical initialized store validates Ready");

    auto* header = reinterpret_cast<StoreHeaderV2*>(bytes.data());
    const auto canonical = *header;
    const auto reject = [&](auto mutate, const char* message) {
        *header = canonical;
        mutate(*header);
        expect(control.validate_existing() ==
                   StoreControlStatus::incompatible_layout,
               message);
    };
    reject([](StoreHeaderV2& value) { value.Magic = 0x3153'4d53U; },
           "retired SMS1 identity is incompatible");
    reject([](StoreHeaderV2& value) { ++value.LayoutMajorVersion; },
           "unknown layout major is incompatible");
    reject([](StoreHeaderV2& value) { ++value.LayoutMinorVersion; },
           "unknown layout minor is incompatible");
    reject([](StoreHeaderV2& value) { value.HeaderLength -= 8; },
           "malformed header length is incompatible");
    reject([](StoreHeaderV2& value) { ++value.ResourceProtocolVersion; },
           "unknown resource protocol is incompatible");
    reject([](StoreHeaderV2& value) { value.RequiredFeatures ^= 1; },
           "missing required feature is incompatible");
    reject([](StoreHeaderV2& value) { value.RequiredFeatures |= 1ULL << 63U; },
           "unknown required feature is incompatible");
    reject([](StoreHeaderV2& value) { value.StoreId = 0; },
           "zero store identity is incompatible");
    reject([](StoreHeaderV2& value) { value.ParticipantRecordCount = 0; },
           "zero participant dimension is incompatible");
    reject([](StoreHeaderV2& value) { ++value.ParticipantIndexBits; },
           "malformed participant token sizing is incompatible");
    reject([](StoreHeaderV2& value) { value.ParticipantOffset += 8; },
           "misaligned participant topology is incompatible");
    reject([](StoreHeaderV2& value) { value.PrimaryDirectoryOffset = value.ParticipantOffset; },
           "overlapping section topology is incompatible");
    reject([](StoreHeaderV2& value) { value.PidNamespaceMode = 0; },
           "unknown PID namespace recovery mode is incompatible");
    reject([](StoreHeaderV2& value) { value.Control = 5; },
           "impossible store-control state is incompatible");

    *header = canonical;
    StoreControlV2 truncated(
        bytes.data(), sizeof(StoreHeaderV2) - 1U, layout);
    expect(truncated.validate_existing() ==
               StoreControlStatus::incompatible_layout,
           "truncated actual mapping capacity rejects before projection");

    expect(control.latch_corrupt() && header->Control == sms2_store_corrupt &&
               control.validate_existing() == StoreControlStatus::corrupt_store,
           "exact Ready-to-Corrupt CAS publishes terminal corruption");
    expect(control.latch_corrupt() && header->Control == sms2_store_corrupt,
           "corruption latch replay is idempotent");

    header->Control = sms2_store_unsupported;
    expect(!control.latch_corrupt() &&
               header->Control == sms2_store_unsupported,
           "corruption latch cannot rewrite Unsupported");
    header->Control = sms2_store_initializing;
    expect(!control.latch_corrupt() &&
               header->Control == sms2_store_initializing,
           "corruption latch cannot rewrite Initializing");
}

} // namespace

int main() {
    creator_initializes_and_attaches();
    existing_zero_and_noncurrent_headers_fail_closed();
    feature_dimension_and_capacity_mismatch_reject();
    existing_protocol_precedes_requested_capacity();
    disposition_participant_capacity_and_architecture();
    store_control_validation_and_exact_corruption_latch();
    if (failures == 0) {
        std::cout << "cold_open_tests: PASS\n";
        return 0;
    }
    return 1;
}
