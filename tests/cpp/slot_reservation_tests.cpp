#include "control_words.hpp"
#include "mapped_atomic.hpp"
#include "reservation_memory.hpp"
#include "slot_table.hpp"
#include "test_support_v2.hpp"

#include <atomic>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <memory>
#include <span>
#include <thread>
#include <vector>

using namespace sms::detail;

static_assert(static_cast<std::int32_t>(SlotState::free) == 0);
static_assert(static_cast<std::int32_t>(SlotState::retired) == 7);
static_assert(static_cast<std::int32_t>(SlotPublicationIntent::explicit_reservation) == 1);
static_assert(static_cast<std::int32_t>(SlotPublicationIntent::atomic_publication) == 2);

namespace {

std::atomic<int> failures{};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

struct Fixture {
    explicit Fixture(std::int32_t slot_count = 2, std::int32_t participant_count = 2) {
        expect(LayoutV2::calculate(
            1'000'000,
            slot_count,
            32,
            16,
            24,
            4,
            participant_count,
            layout), "fixture layout calculation");
        words.resize(
            (static_cast<std::size_t>(layout.required_bytes) + sizeof(std::uint64_t) - 1U) /
            sizeof(std::uint64_t));
        expect(SlotTable::initialize_mapping(
            bytes(), byte_count(), layout, OperationBudget::unbounded_scan()) ==
            SMS_STATUS_SUCCESS, "slot mapping initialization");

        std::uint64_t token{};
        std::uint64_t active{};
        expect(ParticipantToken::try_encode(0, 1, participant_count, token),
               "participant token encoding");
        expect(ParticipantControl::try_encode(2, 1, 1001, active),
               "participant active control encoding");
        participant = SlotParticipant{
            static_cast<std::uint32_t>(token),
            active};
        auto* record = participant_record(0);
        expect(record != nullptr, "participant record projection");
        if (record != nullptr) {
            MappedAtomic64::store_release(record->Control, active);
        }
        table = std::make_unique<SlotTable>(
            bytes(), byte_count(), layout, store_id, participant);
        expect(table->valid(), "slot table construction");
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

    LayoutV2 layout{};
    std::vector<std::uint64_t> words;
    SlotParticipant participant{};
    std::unique_ptr<SlotTable> table;
    std::uint64_t store_id{0x0123'4567'89ab'cdefULL};
};

SlotControl decode_slot(std::uint64_t raw, const char* message) {
    SlotControl result{};
    expect(SlotControl::try_decode(raw, result), message);
    return result;
}

ReservationToken claim_and_mark(
    Fixture& fixture,
    SlotPublicationIntent intent = SlotPublicationIntent::explicit_reservation,
    std::int32_t payload_length = 8) {
    ReservationToken reservation{};
    expect(fixture.table->try_claim_reservation(
        0xcbf2'9ce4'8422'2325ULL,
        3,
        2,
        payload_length,
        intent,
        OperationBudget::structural_attempt(),
        reservation) == SMS_STATUS_SUCCESS, "claim reservation");
    expect(reservation.valid(), "claimed reservation token");
    expect(fixture.table->mark_reserved(reservation) == SMS_STATUS_SUCCESS,
           "explicit reservation ordering publication");
    return reservation;
}

void manifest_slot_contract_is_exact() {
    const auto manifest = sms::test::v2::load_manifest();
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"explicit_reservation_ordering\": \"Initializing -> Reserved\"") > 0,
        "manifest explicit reservation ordering");
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"atomic_publication_ordering\": \"Reserved -> Published\"") > 0,
        "manifest atomic publication ordering");
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"store_full_basis\": \"all_value_slots_non_free\"") > 0,
        "manifest physical StoreFull basis");
    expect(sms::test::v2::require_unique_json_fragment(
        manifest.json,
        "\"metadata_ready_marker\": \"current-generation directory_operation Insert/Prepared release publication\"") > 0,
        "manifest metadata-ready handoff boundary");
}

void structural_classification_and_initialization() {
    Fixture fixture(2);
    for (std::int32_t index = 0; index < fixture.layout.slot_count; ++index) {
        auto* slot = fixture.table->slot(index);
        expect(slot != nullptr, "initialized slot address");
        if (slot == nullptr) continue;
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(slot->Control),
            "initialized slot control decode");
        bool occupied = true;
        expect(SlotTable::try_classify_structural_control(
            control.value, fixture.layout.participant_record_count, occupied),
            "free slot structural classification");
        expect(!occupied && control.state == static_cast<std::int32_t>(SlotState::free)
                   && control.generation == 1 && control.participant_token == 0,
               "canonical generation-one free slot");
        expect(slot->KeyOffset == fixture.layout.key_storage_offset
                + static_cast<std::int64_t>(index) * fixture.layout.key_stride,
               "canonical key offset");
        expect(slot->DescriptorOffset == fixture.layout.descriptor_storage_offset
                + static_cast<std::int64_t>(index) * fixture.layout.descriptor_stride,
               "canonical descriptor offset");
        expect(slot->PayloadOffset == fixture.layout.payload_storage_offset
                + static_cast<std::int64_t>(index) * fixture.layout.payload_stride,
               "canonical payload offset");
    }

    std::uint64_t malformed{};
    expect(SlotControl::try_encode(
        static_cast<std::int32_t>(SlotState::free), 1, fixture.participant.token, malformed),
        "malformed free encoding is representable");
    bool occupied{};
    expect(!SlotTable::try_classify_structural_control(
        malformed, fixture.layout.participant_record_count, occupied),
        "owned free state rejected structurally");

    std::uint64_t retired{};
    expect(SlotControl::try_encode(
        static_cast<std::int32_t>(SlotState::retired),
        SlotTable::terminal_generation,
        0,
        retired), "terminal retired encoding");
    expect(SlotTable::try_classify_structural_control(
        retired, fixture.layout.participant_record_count, occupied) && occupied,
        "terminal retired state is occupied and structural");
    expect(SlotControl::try_encode(
        static_cast<std::int32_t>(SlotState::retired), 7, 0, retired),
        "nonterminal retired encoding is representable");
    expect(!SlotTable::try_classify_structural_control(
        retired, fixture.layout.participant_record_count, occupied),
        "nonterminal retired state rejected structurally");
}

void participant_owned_claim_and_metadata_publication() {
    Fixture fixture(2);
    ReservationToken explicit_reservation{};
    expect(fixture.table->try_claim_reservation(
        77,
        3,
        2,
        8,
        SlotPublicationIntent::explicit_reservation,
        OperationBudget::structural_attempt(),
        explicit_reservation) == SMS_STATUS_SUCCESS, "explicit claim");
    auto binding = IndexBinding{};
    expect(IndexBinding::try_decode(explicit_reservation.slot_binding, binding),
           "claimed slot binding decode");
    auto* slot = fixture.table->slot(binding.slot_index);
    expect(slot != nullptr, "claimed slot address");
    if (slot != nullptr) {
        auto control = decode_slot(
            MappedAtomic64::load_acquire(slot->Control), "initializing control decode");
        expect(control.state == static_cast<std::int32_t>(SlotState::initializing)
                && control.participant_token == fixture.participant.token,
               "claim is participant-owned Initializing");
        expect(slot->DirectoryBinding == explicit_reservation.slot_binding
                && slot->KeyHash == 77 && slot->KeyLength == 3
                && slot->DescriptorLength == 2 && slot->ValueLength == 8
                && slot->PublicationIntent ==
                    static_cast<std::int32_t>(SlotPublicationIntent::explicit_reservation),
               "complete explicit metadata precedes Reserved publication");
        expect(MappedAtomic64::load_acquire(slot->DirectoryOperation) == 0,
               "pre-marker Initializing claim has no discoverable directory operation");
    }

    std::atomic<bool> reader_saw_metadata{false};
    std::thread reader([&] {
        const auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(5);
        while (slot != nullptr) {
            const auto control = decode_slot(
                MappedAtomic64::load_acquire(slot->Control), "reader control decode");
            if (control.state == static_cast<std::int32_t>(SlotState::reserved)) {
                reader_saw_metadata.store(
                    slot->DirectoryBinding == explicit_reservation.slot_binding
                        && slot->KeyHash == 77 && slot->ValueLength == 8
                        && slot->PublicationIntent == static_cast<std::int32_t>(
                            SlotPublicationIntent::explicit_reservation),
                    std::memory_order_relaxed);
                return;
            }
            if (std::chrono::steady_clock::now() >= deadline) return;
            std::this_thread::yield();
        }
    });
    expect(fixture.table->mark_reserved(explicit_reservation) == SMS_STATUS_SUCCESS,
           "Initializing to Reserved explicit-ordering CAS");
    reader.join();
    expect(reader_saw_metadata.load(std::memory_order_relaxed),
           "Reserved acquire observes complete metadata and intent");

    auto atomic_reservation = claim_and_mark(
        fixture, SlotPublicationIntent::atomic_publication, 4);
    expect(IndexBinding::try_decode(atomic_reservation.slot_binding, binding),
           "atomic binding decode");
    slot = fixture.table->slot(binding.slot_index);
    expect(slot != nullptr && slot->PublicationIntent ==
            static_cast<std::int32_t>(SlotPublicationIntent::atomic_publication),
           "atomic publication intent is explicit in mapped metadata");
}

void stable_full_proof_and_reuse() {
    Fixture fixture(1);
    auto reservation = claim_and_mark(fixture);
    ReservationToken unavailable{};
    expect(fixture.table->try_claim_reservation(
        11, 1, 0, 1, SlotPublicationIntent::explicit_reservation,
        OperationBudget::structural_attempt(), unavailable) == SMS_STATUS_STORE_FULL,
        "exhausted claim scan produces StoreFull candidate");

    bool proven_full = false;
    expect(fixture.table->try_prove_store_full(
        OperationBudget::structural_attempt(), proven_full) == SMS_STATUS_SUCCESS
        && proven_full, "equal all-occupied double collect proves StoreFull");

    expect(fixture.table->abort_reservation(
        reservation, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "abort makes capacity reusable");
    proven_full = true;
    expect(fixture.table->try_prove_store_full(
        OperationBudget::structural_attempt(), proven_full) == SMS_STATUS_SUCCESS
        && !proven_full, "a free slot defeats exact StoreFull proof");

    auto* reusable_slot = fixture.table->slot(0);
    std::uint64_t old_location{};
    std::uint64_t old_operation{};
    expect(DirectoryLocation::try_encode(1, 0, 1, old_location),
           "older directory location encoding");
    expect(DirectoryOperation::try_encode(1, 5, 1, 0, 1, old_operation),
           "older directory operation encoding");
    MappedAtomic64::store_release(reusable_slot->DirectoryLocation, old_location);
    MappedAtomic64::store_release(reusable_slot->DirectoryOperation, old_operation);

    auto replacement = claim_and_mark(fixture);
    expect(replacement.slot_binding != reservation.slot_binding,
           "reuse advances the slot binding generation");
    expect(MappedAtomic64::load_acquire(reusable_slot->DirectoryLocation) == 0
            && MappedAtomic64::load_acquire(reusable_slot->DirectoryOperation) == 0,
           "exclusive next-generation claim removes only older tagged residue");
    expect(fixture.table->advance_reservation(
        reservation, 1, OperationBudget::structural_attempt()) ==
        SMS_STATUS_INVALID_RESERVATION, "stale generation cannot advance replacement");
}

void recovery_reclaim_defers_exact_generation_directory_residue() {
    Fixture fixture(1);
    auto reservation = claim_and_mark(fixture);
    IndexBinding binding{};
    expect(IndexBinding::try_decode(reservation.slot_binding, binding),
           "recovery reclaim binding decode");
    auto* slot = fixture.table->slot(binding.slot_index);
    expect(slot != nullptr, "recovery reclaim slot projection");
    expect(fixture.table->try_begin_abort(reservation) == SMS_STATUS_SUCCESS,
           "recovery reclaim begins ownerless abort");

    std::uint64_t exact_operation{};
    expect(DirectoryOperation::try_encode(
               1, 5, 1, 0, binding.generation, exact_operation),
           "exact-generation directory operation encoding");
    MappedAtomic64::store_release(slot->DirectoryOperation, exact_operation);

    expect(fixture.table->complete_reclaim(
               reservation.slot_binding, OperationBudget::structural_attempt()) ==
               SMS_STATUS_STORE_BUSY,
           "exact-generation directory operation defers recovery reclaim");
    expect(MappedAtomic64::load_acquire(slot->DirectoryOperation) == exact_operation,
           "recovery reclaim preserves a concurrent exact-generation operation");
    auto control = decode_slot(
        MappedAtomic64::load_acquire(slot->Control),
        "deferred recovery reclaim control decode");
    expect(control.state == static_cast<std::int32_t>(SlotState::reclaiming) &&
               control.generation == binding.generation,
           "deferred recovery reclaim remains helpable");

    MappedAtomic64::store_release(slot->DirectoryOperation, 0);
    expect(fixture.table->complete_reclaim(
               reservation.slot_binding, OperationBudget::structural_attempt()) ==
               SMS_STATUS_SUCCESS,
           "recovery reclaim completes after directory helper cleanup");
    control = decode_slot(
        MappedAtomic64::load_acquire(slot->Control),
        "completed recovery reclaim control decode");
    expect(control.state == static_cast<std::int32_t>(SlotState::free) &&
               control.generation == binding.generation + 1,
           "recovery reclaim retry advances the slot generation");

    Fixture future_fixture(1);
    auto future_reservation = claim_and_mark(future_fixture);
    IndexBinding future_binding{};
    expect(IndexBinding::try_decode(
               future_reservation.slot_binding, future_binding),
           "future-residue recovery reclaim binding decode");
    auto* future_slot = future_fixture.table->slot(future_binding.slot_index);
    expect(future_fixture.table->try_begin_abort(future_reservation) ==
               SMS_STATUS_SUCCESS,
           "future-residue recovery reclaim begins ownerless abort");
    std::uint64_t future_operation{};
    expect(DirectoryOperation::try_encode(
               1, 5, 1, 0, future_binding.generation + 1, future_operation),
           "future-generation directory operation encoding");
    MappedAtomic64::store_release(future_slot->DirectoryOperation, future_operation);
    expect(future_fixture.table->complete_reclaim(
               future_reservation.slot_binding,
               OperationBudget::structural_attempt()) == SMS_STATUS_CORRUPT_STORE,
           "future-generation directory operation fails recovery reclaim closed");
    expect(MappedAtomic64::load_acquire(future_slot->DirectoryOperation) ==
               future_operation,
           "recovery reclaim preserves future-generation residue for diagnosis");
}

void advancement_commit_and_stale_token_fencing() {
    Fixture fixture(1);
    auto reservation = claim_and_mark(fixture, SlotPublicationIntent::explicit_reservation, 8);
    WritableReservationRange range{};
    expect(fixture.table->try_get_writable_range(reservation, 3, range)
            && range.offset == 0 && range.length == 8,
           "initial writable range covers remaining payload");
    expect(fixture.table->advance_reservation(
        reservation, 3, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "exact partial reservation advance");
    expect(fixture.table->try_get_writable_range(reservation, 5, range)
            && range.offset == 3 && range.length == 5,
           "projection begins at exact advanced byte count");
    expect(fixture.table->advance_reservation(
        reservation, 6, OperationBudget::structural_attempt()) ==
        SMS_STATUS_RESERVATION_WRITE_OUT_OF_RANGE,
        "advance beyond announced payload rejected");
    expect(fixture.table->commit_reservation(reservation, 41) ==
        SMS_STATUS_RESERVATION_INCOMPLETE, "incomplete reservation cannot commit");
    expect(fixture.table->advance_reservation(
        reservation, 5, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "final exact reservation advance");
    expect(fixture.table->commit_reservation(reservation, 42) == SMS_STATUS_SUCCESS,
           "complete reservation commits");
    expect(!fixture.table->try_get_writable_range(reservation, 0, range),
           "commit ends writable projection lifetime");
    expect(fixture.table->commit_reservation(reservation, 43) ==
        SMS_STATUS_RESERVATION_ALREADY_COMPLETED,
        "copied token observes already-completed commit");

    IndexBinding binding{};
    expect(IndexBinding::try_decode(reservation.slot_binding, binding),
           "committed binding decode");
    auto* slot = fixture.table->slot(binding.slot_index);
    auto control = decode_slot(
        MappedAtomic64::load_acquire(slot->Control), "published control decode");
    expect(control.state == static_cast<std::int32_t>(SlotState::published)
            && control.participant_token == 0 && slot->CommitSequence == 42,
           "commit clears ownership only after diagnostic sequence write");
}

void abort_handoff_participant_retirement_and_terminal_generation() {
    Fixture fixture(1);
    auto reservation = claim_and_mark(fixture);

    CancellationFlag canceled;
    canceled.cancel();
    auto canceled_budget = OperationBudget::unbounded_scan(&canceled);
    expect(fixture.table->abort_reservation(reservation, canceled_budget) ==
        SMS_STATUS_OPERATION_CANCELED,
        "cancellation before abort ordering retains exact ownership");
    expect(fixture.table->reservation_pending(reservation),
           "pre-order cancellation does not leak reservation ownership");

    std::uint64_t second_token{};
    expect(ParticipantToken::try_encode(
        1, 1, fixture.layout.participant_record_count, second_token),
        "second participant token encoding");
    auto forged = reservation;
    forged.participant_token = static_cast<std::uint32_t>(second_token);
    expect(fixture.table->advance_reservation(
        forged, 1, OperationBudget::structural_attempt()) ==
        SMS_STATUS_INVALID_RESERVATION, "wrong participant token is fenced");

    std::uint64_t closing{};
    expect(ParticipantControl::try_encode(3, 1, 1001, closing),
           "participant closing control encoding");
    MappedAtomic64::store_release(fixture.participant_record(0)->Control, closing);
    expect(fixture.table->advance_reservation(
        reservation, 1, OperationBudget::structural_attempt()) ==
        SMS_STATUS_STORE_DISPOSED, "retired owner cannot continue reservation");
    expect(fixture.table->try_begin_abort(reservation) == SMS_STATUS_SUCCESS,
           "exact dead-owner lifecycle hands off to unowned Aborting");
    expect(fixture.table->complete_reclaim(
        reservation.slot_binding, OperationBudget::structural_attempt()) ==
        SMS_STATUS_SUCCESS, "unowned abort remains universally helpable");

    Fixture terminal(1);
    auto* slot = terminal.table->slot(0);
    std::uint64_t terminal_free{};
    expect(SlotControl::try_encode(
        static_cast<std::int32_t>(SlotState::free),
        SlotTable::terminal_generation,
        0,
        terminal_free), "terminal free control encoding");
    MappedAtomic64::store_release(slot->Control, terminal_free);
    auto final_reservation = claim_and_mark(terminal);
    expect(terminal.table->abort_reservation(
        final_reservation, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "terminal lifecycle abort completion");
    auto final_control = decode_slot(
        MappedAtomic64::load_acquire(slot->Control), "retired control decode");
    expect(final_control.state == static_cast<std::int32_t>(SlotState::retired)
            && final_control.generation == SlotTable::terminal_generation,
           "terminal generation retires instead of wrapping");
    ReservationToken none{};
    expect(terminal.table->try_claim_reservation(
        1, 1, 0, 1, SlotPublicationIntent::explicit_reservation,
        OperationBudget::structural_attempt(), none) == SMS_STATUS_STORE_FULL,
        "retired capacity is never reused");
}

void lifetime_validated_writable_projection() {
    Fixture fixture(1);
    ReservationMemory memory(
        fixture.bytes(), fixture.byte_count(), fixture.layout, *fixture.table);
    auto reservation = claim_and_mark(fixture, SlotPublicationIntent::explicit_reservation, 8);
    auto first = memory.get_span(reservation, 3);
    expect(first.size() == 8, "projection returns the exact remaining payload span");
    if (first.size() >= 3) {
        first[0] = std::byte{0x11};
        first[1] = std::byte{0x22};
        first[2] = std::byte{0x33};
    }
    expect(fixture.table->advance_reservation(
        reservation, 3, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "projection advance");
    auto second = memory.get_span(reservation, 5);
    expect(second.size() == 5 && first.data() + 3 == second.data(),
           "next projection revalidates and starts after advancement");
    expect(fixture.table->abort_reservation(
        reservation, OperationBudget::structural_attempt()) == SMS_STATUS_SUCCESS,
        "projection abort");
    expect(memory.get_span(reservation, 0).empty(),
           "abort invalidates copied writable projection token");

    auto replacement = claim_and_mark(fixture);
    expect(!memory.get_span(replacement, 1).empty(), "replacement projection is writable");
    fixture.table->invalidate_local();
    expect(memory.get_span(replacement, 1).empty(),
           "local lifetime invalidation prevents mapped projection");
}

} // namespace

int main() {
    manifest_slot_contract_is_exact();
    structural_classification_and_initialization();
    participant_owned_claim_and_metadata_publication();
    stable_full_proof_and_reuse();
    recovery_reclaim_defers_exact_generation_directory_residue();
    advancement_commit_and_stale_token_fencing();
    abort_handoff_participant_retirement_and_terminal_generation();
    lifetime_validated_writable_projection();
    if (failures.load(std::memory_order_relaxed) == 0) {
        std::cout << "slot_reservation_tests: PASS\n";
        return 0;
    }
    return 1;
}
