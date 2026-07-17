#include "participant_registry.hpp"

#include <cstdint>
#include <iostream>
#include <stdexcept>
#include <vector>

using namespace sms::detail;

namespace {

int failures{};

class aligned_buffer {
public:
    explicit aligned_buffer(std::uint64_t byte_count)
        : words_(static_cast<std::size_t>((byte_count + 7U) / 8U)) {
        if (words_.empty()) {
            throw std::runtime_error("participant fixture buffer cannot be empty");
        }
    }

    [[nodiscard]] std::uint8_t* data() noexcept {
        return reinterpret_cast<std::uint8_t*>(words_.data());
    }

    [[nodiscard]] std::size_t size() const noexcept {
        return words_.size() * sizeof(std::uint64_t);
    }

private:
    std::vector<std::uint64_t> words_;
};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        ++failures;
    }
}

LayoutV2 layout_for(std::int32_t participant_count) {
    LayoutV2 layout{};
    const auto calculated = LayoutV2::calculate(
        1'000'000,
        2,
        64,
        8,
        16,
        4,
        participant_count,
        layout);
    if (!calculated) {
        throw std::runtime_error("participant fixture layout calculation failed");
    }
    return layout;
}

ParticipantIdentity identity(std::int32_t pid) {
    return ParticipantIdentity{
        pid,
        identity_linux_proc_start_ticks,
        123'456 + pid,
        77};
}

void registration_capacity_and_reuse() {
    auto layout = layout_for(2);
    aligned_buffer bytes(
        static_cast<std::size_t>(layout.required_bytes));
    auto& header = *reinterpret_cast<StoreHeaderV2*>(bytes.data());
    MappedAtomic64::store_release(header.Control, sms2_store_ready);
    ParticipantRegistry registry(bytes.data(), bytes.size(), layout);
    expect(registry.initialize(OperationBudget::unbounded_scan()), "initialize participants");

    ParticipantRegistration first{};
    ParticipantRegistration second{};
    ParticipantRegistration third{};
    expect(registry.try_register(
        header, identity(10), OperationBudget::structural_attempt(), first) ==
        ParticipantRegistrationStatus::success, "first registration");
    expect(registry.try_register(
        header, identity(11), OperationBudget::structural_attempt(), second) ==
        ParticipantRegistrationStatus::success, "second registration");
    expect(registry.try_register(
        header, identity(12), OperationBudget::structural_attempt(), third) ==
        ParticipantRegistrationStatus::table_full, "stable participant table full");
    expect(first.token != second.token, "unique tokens");
    expect(registry.is_active(first.token), "first token active");

    expect(registry.close_and_retire(first), "retire first registration");
    expect(!registry.is_active(first.token), "stale token rejected");
    ParticipantRegistration replacement{};
    expect(registry.try_register(
        header, identity(13), OperationBudget::structural_attempt(), replacement) ==
        ParticipantRegistrationStatus::success, "reused registration");
    expect(replacement.record_index == first.record_index, "same record reused");
    expect(replacement.generation == first.generation + 1, "generation advanced");
    expect(replacement.token != first.token, "token fenced across reuse");
}

void active_publication_contains_identity() {
    auto layout = layout_for(1);
    aligned_buffer bytes(
        static_cast<std::size_t>(layout.required_bytes));
    auto& header = *reinterpret_cast<StoreHeaderV2*>(bytes.data());
    MappedAtomic64::store_release(header.Control, sms2_store_ready);
    ParticipantRegistry registry(bytes.data(), bytes.size(), layout);
    expect(registry.initialize(OperationBudget::unbounded_scan()), "identity initialize");

    ParticipantRegistration registration{};
    const auto expected_identity = identity(42);
    expect(registry.try_register(
        header,
        expected_identity,
        OperationBudget::structural_attempt(),
        registration) == ParticipantRegistrationStatus::success,
        "identity registration");
    const auto* current = registry.record(0);
    expect(current != nullptr, "identity record address");
    if (current != nullptr) {
        expect(current->IdentityKind == expected_identity.identity_kind, "identity kind published");
        expect(current->ProcessStartValue == expected_identity.process_start_value, "start value published");
        expect(current->PidNamespaceId == expected_identity.pid_namespace_id, "namespace published");
        expect(current->OpenSequence == 1, "open sequence published");
    }
}

void malformed_control_fails_closed() {
    auto layout = layout_for(1);
    aligned_buffer bytes(
        static_cast<std::size_t>(layout.required_bytes));
    auto& header = *reinterpret_cast<StoreHeaderV2*>(bytes.data());
    MappedAtomic64::store_release(header.Control, sms2_store_ready);
    ParticipantRegistry registry(bytes.data(), bytes.size(), layout);
    expect(registry.initialize(OperationBudget::unbounded_scan()), "malformed initialize");
    auto* current = registry.record(0);
    expect(current != nullptr, "malformed record address");
    if (current != nullptr) current->Control = 1ULL << 63U;
    ParticipantRegistration registration{};
    expect(registry.try_register(
        header, identity(99), OperationBudget::structural_attempt(), registration) ==
        ParticipantRegistrationStatus::incompatible_layout,
        "malformed control rejected");
}

void first_claim_revalidates_store_control() {
    struct scenario {
        std::uint64_t control;
        ParticipantRegistrationStatus expected;
        const char* message;
    };
    constexpr scenario scenarios[] = {
        {sms2_store_initializing,
         ParticipantRegistrationStatus::store_busy,
         "Initializing store rejects first participant claim"},
        {sms2_store_corrupt,
         ParticipantRegistrationStatus::corrupt_store,
         "Corrupt store rejects first participant claim"},
        {sms2_store_unsupported,
         ParticipantRegistrationStatus::unsupported_platform,
         "Unsupported store rejects first participant claim"},
        {99,
         ParticipantRegistrationStatus::incompatible_layout,
         "unknown store control rejects first participant claim"},
    };
    for (const auto& current_scenario : scenarios) {
        auto layout = layout_for(1);
        aligned_buffer bytes(
            static_cast<std::size_t>(layout.required_bytes));
        auto& header = *reinterpret_cast<StoreHeaderV2*>(bytes.data());
        ParticipantRegistry registry(bytes.data(), bytes.size(), layout);
        expect(registry.initialize(OperationBudget::unbounded_scan()),
               "store-control fixture initialization");
        MappedAtomic64::store_release(
            header.Control, current_scenario.control);
        ParticipantRegistration registration{};
        expect(registry.try_register(
                   header,
                   identity(123),
                   OperationBudget::structural_attempt(),
                   registration) == current_scenario.expected,
               current_scenario.message);
        ParticipantControl remaining{};
        expect(!registration.valid(layout.participant_record_count) &&
                ParticipantControl::try_decode(
                    MappedAtomic64::load_acquire(
                        registry.record(0)->Control),
                    remaining) &&
                remaining.state == participant_free &&
                remaining.incarnation == 1,
               "failed first claim leaves participant record reusable");
    }
}

} // namespace

int main() {
    registration_capacity_and_reuse();
    active_publication_contains_identity();
    malformed_control_fails_closed();
    first_claim_revalidates_store_control();
    if (failures == 0) {
        std::cout << "participant_registry_tests: PASS\n";
        return 0;
    }
    return 1;
}
