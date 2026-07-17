#include "control_words.hpp"
#include "lease_registry.hpp"
#include "mapped_atomic.hpp"
#include "test_support_v2.hpp"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <memory>
#include <string>
#include <vector>

using namespace sms::detail;

static_assert(static_cast<std::int32_t>(LeaseState::free) == 0);
static_assert(static_cast<std::int32_t>(LeaseState::claiming) == 1);
static_assert(static_cast<std::int32_t>(LeaseState::active) == 2);
static_assert(static_cast<std::int32_t>(LeaseState::releasing) == 3);
static_assert(static_cast<std::int32_t>(LeaseState::recovering) == 4);
static_assert(static_cast<std::int32_t>(LeaseState::retired) == 5);

namespace {

std::atomic<int> failures{};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

struct Fixture {
    explicit Fixture(
        std::int32_t lease_count = 2,
        std::int32_t slot_count = 2,
        std::int32_t participant_count = 2) {
        expect(LayoutV2::calculate(
            1'000'000,
            slot_count,
            32,
            16,
            24,
            lease_count,
            participant_count,
            layout), "lease fixture layout calculation");
        words.resize(
            (static_cast<std::size_t>(layout.required_bytes) + sizeof(std::uint64_t) - 1U) /
            sizeof(std::uint64_t));
        expect(LeaseRegistry::initialize_mapping(
            bytes(), byte_count(), layout, OperationBudget::unbounded_scan()) ==
            SMS_STATUS_SUCCESS, "lease mapping initialization");

        std::uint64_t token{};
        std::uint64_t active{};
        expect(ParticipantToken::try_encode(0, 1, participant_count, token),
               "lease participant token encoding");
        expect(ParticipantControl::try_encode(2, 1, 2002, active),
               "lease participant active control encoding");
        participant = LeaseParticipant{
            static_cast<std::uint32_t>(token),
            active};
        auto* owner = participant_record(0);
        expect(owner != nullptr, "lease participant record projection");
        if (owner != nullptr) MappedAtomic64::store_release(owner->Control, active);
        registry = std::make_unique<LeaseRegistry>(
            bytes(), byte_count(), layout, store_id, participant);
        expect(registry->valid(), "lease registry construction");
    }

    [[nodiscard]] std::uint8_t* bytes() noexcept {
        return reinterpret_cast<std::uint8_t*>(words.data());
    }

    [[nodiscard]] std::size_t byte_count() const noexcept {
        return words.size() * sizeof(std::uint64_t);
    }

    [[nodiscard]] ParticipantRecordV2* participant_record(std::int32_t index) noexcept {
        if (index < 0 || index >= layout.participant_record_count) return nullptr;
        return reinterpret_cast<ParticipantRecordV2*>(
            bytes() + layout.participant_offset +
            static_cast<std::int64_t>(index) * layout.participant_stride);
    }

    [[nodiscard]] std::uint64_t slot_binding(
        std::int32_t slot_index = 0,
        std::int64_t generation = 1) const noexcept {
        std::uint64_t binding{};
        expect(IndexBinding::try_encode(slot_index, generation, binding),
               "slot binding encoding");
        return binding;
    }

    LayoutV2 layout{};
    std::vector<std::uint64_t> words;
    LeaseParticipant participant{};
    std::unique_ptr<LeaseRegistry> registry;
    std::uint64_t store_id{0x1234'5678'9abc'def0ULL};
};

LeaseControl decode_lease(std::uint64_t raw, const char* message) {
    LeaseControl result{};
    expect(LeaseControl::try_decode(raw, result), message);
    return result;
}

LeaseToken claim(Fixture& fixture, std::uint64_t slot_binding, std::int64_t sequence = 91) {
    LeaseToken lease{};
    expect(fixture.registry->try_claim(
        slot_binding,
        sequence,
        OperationBudget::structural_attempt(),
        lease) == SMS_STATUS_SUCCESS, "claim lease record");
    expect(lease.valid(), "claimed lease token");
    return lease;
}

LeaseToken claim_and_activate(
    Fixture& fixture,
    std::uint64_t slot_binding,
    std::int64_t sequence = 91) {
    auto lease = claim(fixture, slot_binding, sequence);
    expect(fixture.registry->try_activate(lease) == SMS_STATUS_SUCCESS,
           "activate claimed lease");
    return lease;
}

void manifest_and_source_contract() {
    const auto manifest = sms::test::v2::load_manifest();
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"encoded_hex\": \"0000000ffffffffd\"") > 0,
        "manifest terminal lease control");
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"lease_table_full\": 7") > 0,
        "manifest LeaseTableFull status");

    const auto source = sms::test::v2::load_exact_text(
        sms::test::v2::repository_root() / "src" / "cpp" / "src" /
        "lease_registry.cpp");
    expect(source.find("std::mutex") == std::string::npos
            && source.find("lock_guard") == std::string::npos
            && source.find("SharedLock") == std::string::npos
            && source.find("flock(") == std::string::npos
            && source.find("CreateMutex") == std::string::npos,
           "lease hot path has no process-global or OS lock primitive");
}

void structural_control_and_participant_tagged_claim() {
    Fixture fixture(2);
    for (std::int32_t index = 0; index < fixture.layout.lease_record_count; ++index) {
        auto* record = fixture.registry->record(index);
        expect(record != nullptr, "initialized lease record address");
        if (record == nullptr) continue;
        const auto control = decode_lease(
            MappedAtomic64::load_acquire(record->Control),
            "initialized lease control decode");
        bool occupied = true;
        expect(LeaseRegistry::try_classify_structural_control(
            control.value, fixture.layout.participant_record_count, occupied),
            "free lease control structural");
        expect(!occupied && control.state == static_cast<std::int32_t>(LeaseState::free)
                && control.generation == 1 && control.participant_token == 0,
               "canonical generation-one free lease");
    }

    std::uint64_t malformed{};
    expect(LeaseControl::try_encode(
        static_cast<std::int32_t>(LeaseState::active), 1, 0, malformed),
        "ownerless active lease encoding representable");
    bool occupied{};
    expect(!LeaseRegistry::try_classify_structural_control(
        malformed, fixture.layout.participant_record_count, occupied),
        "ownerless active lease rejected");

    const auto binding = fixture.slot_binding();
    const auto lease = claim(fixture, binding, 123);
    IndexBinding lease_binding{};
    expect(IndexBinding::try_decode(lease.lease_binding, lease_binding),
           "lease record binding decode");
    auto* record = fixture.registry->record(lease_binding.slot_index);
    const auto claiming = decode_lease(
        MappedAtomic64::load_acquire(record->Control), "claiming lease decode");
    expect(claiming.state == static_cast<std::int32_t>(LeaseState::claiming)
            && claiming.participant_token == fixture.participant.token,
           "first lease CAS carries complete participant token");
    expect(MappedAtomic64::load_acquire(record->SlotBinding) == binding
            && record->AcquireSequence == 123,
           "claim owner publishes exact slot binding and sequence");
    std::uint64_t terminal_retired{};
    expect(LeaseControl::try_encode(
        static_cast<std::int32_t>(LeaseState::retired),
        LeaseRegistry::terminal_incarnation,
        0,
        terminal_retired), "terminal lease retired encoding");
    expect(LeaseRegistry::try_classify_structural_control(
        terminal_retired, fixture.layout.participant_record_count, occupied) && occupied,
        "terminal retired lease is structural and occupied");
}

void activation_revalidation_and_owner_retirement() {
    Fixture fixture(1, 2);
    const auto binding = fixture.slot_binding(0);
    auto lease = claim(fixture, binding);
    IndexBinding lease_binding{};
    expect(IndexBinding::try_decode(lease.lease_binding, lease_binding),
           "activation lease binding decode");
    auto* record = fixture.registry->record(lease_binding.slot_index);
    MappedAtomic64::store_release(record->SlotBinding, fixture.slot_binding(1));
    expect(fixture.registry->try_activate(lease) == SMS_STATUS_INVALID_LEASE,
           "activation rejects changed exact slot binding and cancels claim");
    auto recycled = decode_lease(
        MappedAtomic64::load_acquire(record->Control), "canceled claim recycle decode");
    expect(recycled.state == static_cast<std::int32_t>(LeaseState::free)
            && recycled.generation == 2,
           "failed activation advances claim incarnation");

    auto replacement = claim(fixture, binding);
    std::uint64_t closing{};
    expect(ParticipantControl::try_encode(3, 1, 2002, closing),
           "closing lease participant control encoding");
    MappedAtomic64::store_release(fixture.participant_record(0)->Control, closing);
    expect(fixture.registry->try_activate(replacement) == SMS_STATUS_STORE_DISPOSED,
           "participant retirement after claim prevents Active publication");
    recycled = decode_lease(
        MappedAtomic64::load_acquire(record->Control), "retired owner recycle decode");
    expect(recycled.state == static_cast<std::int32_t>(LeaseState::free)
            && recycled.generation == 3,
           "participant recheck hands claim to helpable recovery and reuse");
}

void exact_full_proof_release_and_reuse() {
    Fixture fixture(1);
    const auto binding = fixture.slot_binding();
    auto lease = claim_and_activate(fixture, binding);
    LeaseToken none{};
    expect(fixture.registry->try_claim(
        binding,
        92,
        OperationBudget::structural_attempt(),
        none) == SMS_STATUS_LEASE_TABLE_FULL,
        "stable all-occupied proof exposes LeaseTableFull");
    bool proven_full = false;
    expect(fixture.registry->try_prove_lease_table_full(
        OperationBudget::structural_attempt(), proven_full) == SMS_STATUS_SUCCESS
        && proven_full, "exact lease table double collect confirms full");

    expect(fixture.registry->try_release(lease) == SMS_STATUS_SUCCESS,
           "exact Active to Releasing release");
    expect(fixture.registry->try_release(lease) == SMS_STATUS_LEASE_ALREADY_RELEASED,
           "copied token observes completed release before reuse");
    proven_full = true;
    expect(fixture.registry->try_prove_lease_table_full(
        OperationBudget::structural_attempt(), proven_full) == SMS_STATUS_SUCCESS
        && !proven_full, "free lease defeats full proof");

    auto replacement = claim_and_activate(fixture, binding, 93);
    expect(replacement.lease_binding != lease.lease_binding,
           "lease reuse advances exact incarnation token");
    expect(fixture.registry->try_release(lease) == SMS_STATUS_INVALID_LEASE,
           "stale lease cannot release reused incarnation");
    std::uint64_t active_binding{};
    expect(fixture.registry->try_get_active_slot_binding(
        replacement, active_binding) && active_binding == binding,
        "replacement exact Active binding validates");
}

void final_revalidation_projection_lifetime_and_active_scan() {
    Fixture fixture(2);
    const auto binding = fixture.slot_binding();
    auto lease = claim(fixture, binding);
    std::uint64_t projected_binding{};
    expect(!fixture.registry->try_get_active_slot_binding(lease, projected_binding),
           "Claiming is not an immutable projection lifetime token");
    expect(fixture.registry->try_activate(lease) == SMS_STATUS_SUCCESS,
           "lease activation before final store revalidation");
    expect(fixture.registry->try_get_active_slot_binding(lease, projected_binding)
            && projected_binding == binding,
           "Active token validates exact immutable projection binding");
    bool has_active = false;
    expect(fixture.registry->scan_has_active_lease(
        binding, OperationBudget::structural_attempt(), has_active) ==
        SMS_STATUS_SUCCESS && has_active,
        "stable Active scan protects exact slot generation");

    // The store/directory owns this last check. A failed Published/source-word
    // revalidation must release the already Active internal record before a
    // public lease or immutable bytes can escape.
    const bool final_slot_revalidation_succeeded = false;
    if (!final_slot_revalidation_succeeded) {
        expect(fixture.registry->try_release(lease) == SMS_STATUS_SUCCESS,
               "failed final slot revalidation releases internal Active lease");
    }
    expect(!fixture.registry->try_get_active_slot_binding(lease, projected_binding),
           "release ends immutable projection lifetime immediately");
    has_active = true;
    expect(fixture.registry->scan_has_active_lease(
        binding, OperationBudget::structural_attempt(), has_active) ==
        SMS_STATUS_SUCCESS && !has_active,
        "released incarnation no longer protects slot");

    auto local = claim_and_activate(fixture, binding, 94);
    fixture.registry->invalidate_local();
    expect(!fixture.registry->try_get_active_slot_binding(local, projected_binding),
           "local close invalidates lease projection before unmap");
}

void release_help_and_terminal_retirement() {
    Fixture fixture(1, 2);
    const auto first_binding = fixture.slot_binding(0);
    auto first = claim_and_activate(fixture, first_binding);
    IndexBinding decoded{};
    expect(IndexBinding::try_decode(first.lease_binding, decoded),
           "help lease binding decode");
    auto* record = fixture.registry->record(decoded.slot_index);
    std::uint64_t releasing{};
    expect(LeaseControl::try_encode(
        static_cast<std::int32_t>(LeaseState::releasing),
        decoded.generation,
        0,
        releasing), "paused releasing control encoding");
    MappedAtomic64::store_release(record->Control, releasing);
    const auto second_binding = fixture.slot_binding(1);
    auto helped = claim(fixture, second_binding, 95);
    expect(helped.lease_binding != first.lease_binding,
           "claimant helps paused unowned release before reuse");
    expect(MappedAtomic64::load_acquire(record->SlotBinding) == second_binding,
           "new exclusive Claiming owner overwrites stale binding");
    expect(fixture.registry->try_cancel_claim(helped) == SMS_STATUS_SUCCESS,
           "unexposed helped claim cancellation");

    Fixture terminal(1);
    record = terminal.registry->record(0);
    std::uint64_t terminal_free{};
    expect(LeaseControl::try_encode(
        static_cast<std::int32_t>(LeaseState::free),
        LeaseRegistry::terminal_incarnation,
        0,
        terminal_free), "terminal free lease encoding");
    MappedAtomic64::store_release(record->Control, terminal_free);
    auto final_lease = claim_and_activate(terminal, terminal.slot_binding());
    expect(terminal.registry->try_release(final_lease) == SMS_STATUS_SUCCESS,
           "terminal lease release");
    const auto retired = decode_lease(
        MappedAtomic64::load_acquire(record->Control), "terminal lease decode");
    expect(retired.state == static_cast<std::int32_t>(LeaseState::retired)
            && retired.generation == LeaseRegistry::terminal_incarnation,
           "terminal lease incarnation retires without wrap");
    LeaseToken none{};
    expect(terminal.registry->try_claim(
        terminal.slot_binding(),
        96,
        OperationBudget::structural_attempt(),
        none) == SMS_STATUS_LEASE_TABLE_FULL,
        "retired lease capacity remains permanently occupied");
}

void stale_participant_token_is_fenced() {
    Fixture fixture(1);
    auto lease = claim_and_activate(fixture, fixture.slot_binding());
    std::uint64_t second_token{};
    expect(ParticipantToken::try_encode(
        1, 1, fixture.layout.participant_record_count, second_token),
        "second lease participant token encoding");
    auto forged = lease;
    forged.participant_token = static_cast<std::uint32_t>(second_token);
    expect(fixture.registry->try_release(forged) == SMS_STATUS_INVALID_LEASE,
           "wrong participant cannot release active lease");
    std::uint64_t binding{};
    expect(!fixture.registry->try_get_active_slot_binding(forged, binding),
           "wrong participant cannot project active binding");
}

} // namespace

int main() {
    manifest_and_source_contract();
    structural_control_and_participant_tagged_claim();
    activation_revalidation_and_owner_retirement();
    exact_full_proof_release_and_reuse();
    final_revalidation_projection_lifetime_and_active_scan();
    release_help_and_terminal_retirement();
    stale_participant_token_is_fenced();
    if (failures.load(std::memory_order_relaxed) == 0) {
        std::cout << "lease_v2_tests: PASS\n";
        return 0;
    }
    return 1;
}
