#include "mapped_atomic.hpp"
#include "recovery.hpp"
#include "test_support_v2.hpp"

#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <iostream>
#include <memory>
#include <span>
#include <string>
#include <string_view>
#include <vector>

using namespace sms::detail;

namespace {

std::atomic<int> failures{};

void expect(bool condition, const char* message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

struct ObservationContext {
    ProcessObservationKind kind{ProcessObservationKind::available};
    std::int64_t start_value{1000};
    std::int32_t observed_process_id{1001};
    LeaseRecordV2* lease_to_replace{};
    std::uint64_t replacement_control{};
    bool replace_on_observation{};
    std::int32_t calls{};
};

ProcessIdentityObservation observe_process(
    void* raw,
    std::int32_t process_id,
    std::int32_t) noexcept {
    auto& context = *static_cast<ObservationContext*>(raw);
    ++context.calls;
    if (context.replace_on_observation && context.lease_to_replace != nullptr) {
        MappedAtomic64::store_release(
            context.lease_to_replace->Control,
            context.replacement_control);
        context.replace_on_observation = false;
    }
    if (process_id != context.observed_process_id) {
        return {ProcessObservationKind::missing, 0};
    }
    return {context.kind, context.start_value};
}

std::span<const std::byte> bytes(std::string_view value) noexcept {
    return {
        reinterpret_cast<const std::byte*>(value.data()),
        value.size()};
}

struct Fixture {
    explicit Fixture(
        std::int32_t current_process_id = 1001,
        std::uint64_t current_namespace = 77,
        RecoveryPlatform platform = RecoveryPlatform::linux,
        std::int32_t slot_count = 3,
        std::int32_t lease_count = 3,
        std::int32_t participant_count = 3) {
        expect(LayoutV2::calculate(
            1'000'000,
            slot_count,
            32,
            16,
            32,
            lease_count,
            participant_count,
            layout), "recovery fixture layout calculation");
        words.resize(
            (static_cast<std::size_t>(layout.required_bytes) +
             sizeof(std::uint64_t) - 1U) /
            sizeof(std::uint64_t));
        auto* header = reinterpret_cast<StoreHeaderV2*>(base());
        header->PidNamespaceId = platform == RecoveryPlatform::linux ? 77 : 0;
        MappedAtomic64::store_release(
            header->PidNamespaceMode,
            sms2_pid_namespace_recovery_enabled);

        participants = std::make_unique<ParticipantRegistry>(
            base(), byte_count(), layout);
        expect(participants->initialize(OperationBudget::unbounded_scan()),
               "participant registry initialization");
        expect(SlotTable::initialize_mapping(
            base(), byte_count(), layout, OperationBudget::unbounded_scan()) ==
            SMS_STATUS_SUCCESS, "slot table initialization");
        expect(LeaseRegistry::initialize_mapping(
            base(), byte_count(), layout, OperationBudget::unbounded_scan()) ==
            SMS_STATUS_SUCCESS, "lease registry initialization");

        std::uint64_t token_raw{};
        expect(ParticipantToken::try_encode(
            0, 1, participant_count, token_raw),
            "participant token encoding");
        participant_token = static_cast<std::uint32_t>(token_raw);
        active_control = set_participant(
            participant_active,
            1001,
            platform == RecoveryPlatform::windows
                ? identity_windows_creation_file_time
                : identity_linux_proc_start_ticks,
            1000,
            1,
            platform == RecoveryPlatform::linux ? 77 : 0);

        slots = std::make_unique<SlotTable>(
            base(),
            byte_count(),
            layout,
            store_id,
            SlotParticipant{participant_token, active_control});
        leases = std::make_unique<LeaseRegistry>(
            base(),
            byte_count(),
            layout,
            store_id,
            LeaseParticipant{participant_token, active_control});
        directory = std::make_unique<KeyDirectory>(
            base(), byte_count(), layout);
        reclaimer = std::make_unique<Reclaimer>(
            base(),
            byte_count(),
            layout,
            *slots,
            *directory,
            *leases);
        observations.context = &observation;
        observations.observe = &observe_process;
        observations.platform = platform;
        observations.current_process_id = current_process_id;
        observations.current_pid_namespace_id = current_namespace;
        recovery = std::make_unique<RecoveryCoordinator>(
            base(),
            byte_count(),
            layout,
            *participants,
            *slots,
            *directory,
            *leases,
            *reclaimer,
            observations);
        expect(recovery->valid(), "recovery coordinator construction");
    }

    [[nodiscard]] std::uint8_t* base() noexcept {
        return reinterpret_cast<std::uint8_t*>(words.data());
    }

    [[nodiscard]] std::size_t byte_count() const noexcept {
        return words.size() * sizeof(std::uint64_t);
    }

    [[nodiscard]] StoreHeaderV2& header() noexcept {
        return *reinterpret_cast<StoreHeaderV2*>(base());
    }

    [[nodiscard]] ParticipantRecordV2& participant_record() noexcept {
        return *participants->record(0);
    }

    [[nodiscard]] std::uint64_t set_participant(
        std::int32_t state,
        std::int32_t process_id = 1001,
        std::int32_t identity_kind = identity_linux_proc_start_ticks,
        std::int64_t process_start = 1000,
        std::int64_t open_sequence = 1,
        std::uint64_t pid_namespace = 77,
        std::int32_t generation = 1) noexcept {
        auto& record = *participants->record(0);
        record.IdentityKind = identity_kind;
        record.Reserved = 0;
        record.ProcessStartValue = process_start;
        record.OpenSequence = open_sequence;
        record.PidNamespaceId = pid_namespace;
        std::uint64_t control{};
        expect(ParticipantControl::try_encode(
            state,
            generation,
            state >= participant_registering && state <= participant_recovering
                ? process_id
                : 0,
            control), "participant control encoding");
        MappedAtomic64::store_release(record.Control, control);
        return control;
    }

    [[nodiscard]] std::uint64_t slot_binding(
        std::int32_t index = 0,
        std::int64_t generation = 1) const noexcept {
        std::uint64_t result{};
        expect(IndexBinding::try_encode(index, generation, result),
               "slot binding encoding");
        return result;
    }

    void seed_lease(
        LeaseState state,
        std::int64_t generation = 1,
        std::uint64_t binding = 0) noexcept {
        auto* record = leases->record(0);
        record->SlotBinding = binding;
        record->AcquireSequence = 10;
        std::uint64_t control{};
        expect(LeaseControl::try_encode(
            static_cast<std::int32_t>(state),
            generation,
            state == LeaseState::claiming || state == LeaseState::active
                ? participant_token
                : 0,
            control), "lease control encoding");
        MappedAtomic64::store_release(record->Control, control);
    }

    [[nodiscard]] ReservationToken claim_initializing(
        std::uint64_t hash = 0xcbf2'9ce4'8422'2325ULL,
        std::string_view key = "key") noexcept {
        ReservationToken reservation{};
        expect(slots->try_claim_reservation(
            hash,
            static_cast<std::int32_t>(key.size()),
            0,
            4,
            SlotPublicationIntent::explicit_reservation,
            OperationBudget::structural_attempt(),
            reservation) == SMS_STATUS_SUCCESS,
            "claim Initializing reservation");
        IndexBinding decoded{};
        expect(IndexBinding::try_decode(reservation.slot_binding, decoded),
               "reservation binding decode");
        auto* slot = slots->slot(decoded.slot_index);
        expect(slot != nullptr, "reservation slot projection");
        if (slot != nullptr) {
            std::memcpy(base() + slot->KeyOffset, key.data(), key.size());
        }
        return reservation;
    }

    [[nodiscard]] ReservationToken insert_reserved(
        std::uint64_t hash = 9001,
        std::string_view key = "reserved") noexcept {
        auto reservation = claim_initializing(hash, key);
        DirectoryLocation location{};
        expect(directory->try_insert(
            bytes(key),
            hash,
            reservation.slot_binding,
            OperationBudget::unbounded_scan(),
            location) == SMS_STATUS_SUCCESS,
            "directory insert publishes Reserved");
        expect(location.value != 0, "reserved directory location");
        return reservation;
    }

    LayoutV2 layout{};
    std::vector<std::uint64_t> words;
    std::uint64_t store_id{0x1020'3040'5060'7080ULL};
    std::uint32_t participant_token{};
    std::uint64_t active_control{};
    ObservationContext observation{};
    RecoveryObservationSource observations{};
    std::unique_ptr<ParticipantRegistry> participants;
    std::unique_ptr<SlotTable> slots;
    std::unique_ptr<LeaseRegistry> leases;
    std::unique_ptr<KeyDirectory> directory;
    std::unique_ptr<Reclaimer> reclaimer;
    std::unique_ptr<RecoveryCoordinator> recovery;
};

SlotControl decode_slot(std::uint64_t raw, const char* message) {
    SlotControl result{};
    expect(SlotControl::try_decode(raw, result), message);
    return result;
}

LeaseControl decode_lease(std::uint64_t raw, const char* message) {
    LeaseControl result{};
    expect(LeaseControl::try_decode(raw, result), message);
    return result;
}

void source_contract_has_no_recovery_lock() {
    const auto source = sms::test::v2::load_exact_text(
        sms::test::v2::repository_root() / "src" / "cpp" / "src" /
        "recovery.cpp");
    expect(source.find("std::mutex") == std::string::npos &&
            source.find("lock_guard") == std::string::npos &&
            source.find("flock(") == std::string::npos &&
            source.find("CreateMutex") == std::string::npos,
           "explicit recovery contains no process-global or OS lock");
}

void pid_start_and_namespace_classification() {
    {
        Fixture fixture;
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::current_process,
               "exact PID/start/namespace classifies current process");
        expect(classification.incarnation.control == fixture.active_control &&
                classification.incarnation.token == fixture.participant_token,
               "classification carries exact participant snapshot");
    }
    {
        Fixture fixture(2002);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind == ParticipantClassificationKind::live,
               "exact other PID/start/namespace classifies live");
    }
    {
        Fixture fixture;
        fixture.observation.start_value = 2000;
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind == ParticipantClassificationKind::stale,
               "PID reuse start mismatch classifies stale");
    }
    {
        Fixture fixture;
        fixture.observation.kind = ProcessObservationKind::missing;
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind == ParticipantClassificationKind::stale,
               "definitely missing PID classifies stale");
    }
    {
        Fixture fixture(1001, 88);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::unsupported,
               "unproven Linux PID namespace preserves owner");
        expect(fixture.observation.calls == 0,
               "namespace mismatch never observes ambiguous numeric PID");
    }
    {
        Fixture fixture;
        (void)fixture.set_participant(
            participant_active,
            1001,
            identity_unknown,
            0,
            1,
            77);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::unsupported,
               "unknown active identity is conservatively unsupported");
    }
}

void registering_uses_presence_only() {
    {
        Fixture fixture;
        (void)fixture.set_participant(
            participant_registering,
            1001,
            identity_linux_proc_start_ticks,
            9999,
            0,
            9999);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::current_process,
               "Registering ignores mixed ordinary identity fields");
    }
    {
        Fixture fixture(2002);
        (void)fixture.set_participant(
            participant_registering,
            1001,
            identity_unknown,
            0,
            0,
            0);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::unsupported,
               "present noncurrent Registering PID cannot disambiguate reuse");
    }
    {
        Fixture fixture;
        (void)fixture.set_participant(
            participant_registering,
            1001,
            identity_unknown,
            0,
            0,
            0);
        fixture.observation.kind = ProcessObservationKind::missing;
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind == ParticipantClassificationKind::stale,
               "definitely absent Registering PID is stale");
    }
    {
        Fixture fixture;
        (void)fixture.set_participant(
            participant_registering,
            1001,
            identity_unknown,
            0,
            0,
            0);
        MappedAtomic64::store_release(fixture.header().PidNamespaceMode, 0);
        auto classification = fixture.recovery->classify_participant(
            fixture.participant_token);
        expect(classification.kind ==
                ParticipantClassificationKind::unsupported,
               "disabled namespace mode preserves Registering owner");
    }
}

void exact_lease_recovery_and_reuse_fencing() {
    {
        Fixture fixture;
        fixture.seed_lease(
            LeaseState::active, 1, fixture.slot_binding());
        fixture.observation.start_value = 2000;
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_leases(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "stale active lease recovery succeeds");
        const auto control = decode_lease(
            MappedAtomic64::load_acquire(fixture.leases->record(0)->Control),
            "recovered lease decode");
        expect(report.recovered == 1 &&
                control.state == static_cast<std::int32_t>(LeaseState::free) &&
                control.generation == 2 && control.participant_token == 0,
               "exact lease CAS advances only recovered incarnation");
    }
    {
        Fixture fixture;
        fixture.seed_lease(LeaseState::claiming);
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_leases(
            true,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "current Claiming lease scan succeeds");
        const auto control = decode_lease(
            MappedAtomic64::load_acquire(fixture.leases->record(0)->Control),
            "preserved claiming lease decode");
        expect(report.active == 1 && report.recovered == 0 &&
                control.state == static_cast<std::int32_t>(LeaseState::claiming),
               "current-process override never reclaims Claiming ordinary writes");
    }
    {
        Fixture fixture;
        fixture.seed_lease(LeaseState::claiming);
        (void)fixture.set_participant(participant_closing);
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_leases(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "Closing participant hands off Claiming lease");
        const auto control = decode_lease(
            MappedAtomic64::load_acquire(fixture.leases->record(0)->Control),
            "handoff lease decode");
        expect(report.recovered == 1 && control.generation == 2 &&
                control.state == static_cast<std::int32_t>(LeaseState::free),
               "exact Closing handoff overrides live owner classification");
    }
    {
        Fixture fixture;
        fixture.seed_lease(
            LeaseState::active, 1, fixture.slot_binding());
        auto* record = fixture.leases->record(0);
        std::uint64_t replacement{};
        expect(LeaseControl::try_encode(
            static_cast<std::int32_t>(LeaseState::active),
            2,
            fixture.participant_token,
            replacement), "replacement lease control encoding");
        fixture.observation.start_value = 2000;
        fixture.observation.lease_to_replace = record;
        fixture.observation.replacement_control = replacement;
        fixture.observation.replace_on_observation = true;
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_leases(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "lease reuse race remains a successful bounded scan");
        expect(report.recovered == 0 &&
                MappedAtomic64::load_acquire(record->Control) == replacement,
               "recovery never follows a lease record into replacement incarnation");
    }
    {
        Fixture fixture;
        fixture.seed_lease(
            LeaseState::active,
            LeaseRegistry::terminal_incarnation,
            fixture.slot_binding());
        fixture.observation.start_value = 2000;
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_leases(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "terminal stale lease recovery succeeds");
        const auto control = decode_lease(
            MappedAtomic64::load_acquire(fixture.leases->record(0)->Control),
            "terminal recovered lease decode");
        expect(control.state == static_cast<std::int32_t>(LeaseState::retired) &&
                control.generation == LeaseRegistry::terminal_incarnation,
               "terminal lease incarnation retires instead of wrapping");
    }
}

void exact_reservation_and_directory_recovery() {
    {
        Fixture fixture;
        auto reservation = fixture.claim_initializing();
        fixture.observation.start_value = 2000;
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "stale pre-metadata Initializing recovery succeeds");
        IndexBinding binding{};
        expect(IndexBinding::try_decode(reservation.slot_binding, binding),
               "pre-metadata recovered binding decode");
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(
                fixture.slots->slot(binding.slot_index)->Control),
            "pre-metadata recovered slot decode");
        expect(report.recovered == 1 &&
                control.state == static_cast<std::int32_t>(SlotState::free) &&
                control.generation == 2,
               "stale Initializing is exact-CASed and advanced");
    }
    {
        Fixture fixture;
        auto reservation = fixture.claim_initializing();
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            true,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "current Initializing reservation scan succeeds");
        IndexBinding binding{};
        expect(IndexBinding::try_decode(reservation.slot_binding, binding),
               "current Initializing binding decode");
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(
                fixture.slots->slot(binding.slot_index)->Control),
            "current Initializing slot decode");
        expect(report.active == 1 && report.recovered == 0 &&
                control.state == static_cast<std::int32_t>(SlotState::initializing),
               "current-process override preserves Initializing ordinary writes");
    }
    {
        Fixture fixture;
        auto reservation = fixture.claim_initializing();
        (void)fixture.set_participant(participant_closing);
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "Closing participant hands off Initializing reservation");
        IndexBinding binding{};
        expect(IndexBinding::try_decode(reservation.slot_binding, binding),
               "handoff reservation binding decode");
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(
                fixture.slots->slot(binding.slot_index)->Control),
            "handoff reservation slot decode");
        expect(report.recovered == 1 && control.generation == 2 &&
                control.state == static_cast<std::int32_t>(SlotState::free),
               "exact Closing handoff recovers Initializing slot");
    }
    {
        Fixture fixture;
        constexpr std::string_view key = "directory-key";
        constexpr std::uint64_t hash = 707;
        auto reservation = fixture.insert_reserved(hash, key);
        fixture.observation.start_value = 2000;
        bool before{};
        expect(fixture.directory->contains_exact_reference(
            reservation.slot_binding,
            OperationBudget::structural_attempt(),
            before) == SMS_STATUS_SUCCESS && before,
            "reserved binding is discoverable before recovery");
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "stale Reserved directory recovery succeeds");
        bool after = true;
        expect(fixture.directory->contains_exact_reference(
            reservation.slot_binding,
            OperationBudget::structural_attempt(),
            after) == SMS_STATUS_SUCCESS && !after,
            "recovery unlinks the exact directory binding");
        IndexBinding binding{};
        expect(IndexBinding::try_decode(reservation.slot_binding, binding),
               "directory recovered binding decode");
        auto* slot = fixture.slots->slot(binding.slot_index);
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(slot->Control),
            "directory recovered slot decode");
        expect(report.recovered == 1 && control.generation == 2 &&
                control.state == static_cast<std::int32_t>(SlotState::free) &&
                MappedAtomic64::load_acquire(slot->DirectoryLocation) == 0 &&
                MappedAtomic64::load_acquire(slot->DirectoryOperation) == 0,
               "directory cleanup precedes recovered slot generation reuse");
    }
    {
        Fixture fixture;
        auto reservation = fixture.claim_initializing();
        expect(fixture.slots->mark_reserved(reservation) == SMS_STATUS_SUCCESS,
               "construct malformed Reserved without operation marker");
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            true,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_CORRUPT_STORE,
            "Reserved without directory operation fails closed");
        expect(report.failed == 1,
               "malformed reservation is counted failed");
    }
    {
        Fixture fixture;
        auto* slot = fixture.slots->slot(0);
        const auto binding = fixture.slot_binding(
            0, SlotTable::terminal_generation);
        slot->DirectoryBinding = binding;
        std::uint64_t initializing{};
        expect(SlotControl::try_encode(
            static_cast<std::int32_t>(SlotState::initializing),
            SlotTable::terminal_generation,
            fixture.participant_token,
            initializing), "terminal reservation control encoding");
        MappedAtomic64::store_release(slot->Control, initializing);
        fixture.observation.start_value = 2000;
        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS,
            "terminal stale reservation recovery succeeds");
        const auto control = decode_slot(
            MappedAtomic64::load_acquire(slot->Control),
            "terminal recovered slot decode");
        expect(control.state == static_cast<std::int32_t>(SlotState::retired) &&
                control.generation == SlotTable::terminal_generation,
               "terminal slot generation retires instead of wrapping");
    }
}

void participant_handoff_reference_fencing_and_retirement() {
    {
        Fixture fixture;
        fixture.observation.start_value = 2000;
        std::int32_t retired{};
        expect(fixture.recovery->help_recovering_participants(
            OperationBudget::structural_attempt(),
            retired) == SMS_STATUS_SUCCESS,
            "stale unreferenced participant recovery succeeds");
        ParticipantControl control{};
        const auto raw = MappedAtomic64::load_acquire(
            fixture.participant_record().Control);
        expect(ParticipantControl::try_decode(raw, control) && retired == 1 &&
                control.state == participant_free && control.incarnation == 2,
               "unreferenced stale participant advances to next Free incarnation");
    }
    {
        Fixture fixture;
        auto reservation = fixture.claim_initializing();
        (void)fixture.set_participant(participant_closing);
        std::int32_t retired{};
        expect(fixture.recovery->help_recovering_participants(
            OperationBudget::structural_attempt(),
            retired) == SMS_STATUS_SUCCESS,
            "Closing participant publishes recovery handoff");
        ParticipantControl handoff{};
        expect(ParticipantControl::try_decode(
                   MappedAtomic64::load_acquire(
                       fixture.participant_record().Control),
                   handoff) &&
                retired == 0 && handoff.state == participant_recovering &&
                handoff.incarnation == 1,
               "exact reservation reference prevents participant reuse");

        RecoveryScanReport report{};
        expect(fixture.recovery->try_recover_reservations(
            false,
            OperationBudget::structural_attempt(),
            report) == SMS_STATUS_SUCCESS && report.recovered == 1,
            "Recovering handoff releases the exact reservation");
        expect(fixture.recovery->help_recovering_participants(
            OperationBudget::structural_attempt(),
            retired) == SMS_STATUS_SUCCESS,
            "reference-free participant completes recovery");
        ParticipantControl completed{};
        expect(ParticipantControl::try_decode(
                   MappedAtomic64::load_acquire(
                       fixture.participant_record().Control),
                   completed) &&
                retired == 1 && completed.state == participant_free &&
                completed.incarnation == 2 &&
                !fixture.slots->reservation_pending(reservation),
               "participant advances only after its exact resource is gone");
    }
    {
        Fixture fixture;
        std::uint64_t recovering{};
        std::uint64_t reclaiming{};
        const auto closing = fixture.set_participant(participant_closing);
        expect(fixture.participants->try_begin_recovery(
                   fixture.participant_token,
                   closing,
                   recovering) == SMS_STATUS_SUCCESS &&
                fixture.participants->try_begin_reclaim(
                   fixture.participant_token,
                   recovering,
                   reclaiming) == SMS_STATUS_SUCCESS,
               "construct interrupted Reclaiming participant");
        std::int32_t retired{};
        expect(fixture.recovery->help_recovering_participants(
            OperationBudget::structural_attempt(),
            retired) == SMS_STATUS_SUCCESS,
            "ownerless Reclaiming participant is helpable");
        ParticipantControl completed{};
        expect(ParticipantControl::try_decode(
                   MappedAtomic64::load_acquire(
                       fixture.participant_record().Control),
                   completed) &&
                retired == 1 && completed.state == participant_free &&
                completed.incarnation == 2,
               "Reclaiming crash window completes idempotently");
    }
    {
        Fixture fixture;
        std::uint64_t terminal_token{};
        expect(ParticipantToken::try_encode(
            0,
            fixture.layout.participant_generation_mask,
            fixture.layout.participant_record_count,
            terminal_token),
            "terminal participant token encoding");
        fixture.participant_token = static_cast<std::uint32_t>(terminal_token);
        fixture.active_control = fixture.set_participant(
            participant_active,
            1001,
            identity_linux_proc_start_ticks,
            1000,
            1,
            77,
            fixture.layout.participant_generation_mask);
        fixture.observation.start_value = 2000;
        std::int32_t retired{};
        expect(fixture.recovery->help_recovering_participants(
            OperationBudget::structural_attempt(),
            retired) == SMS_STATUS_SUCCESS,
            "terminal stale participant recovery succeeds");
        ParticipantControl completed{};
        expect(ParticipantControl::try_decode(
                   MappedAtomic64::load_acquire(
                       fixture.participant_record().Control),
                   completed) &&
                retired == 1 && completed.state == participant_retired &&
                completed.incarnation ==
                    fixture.layout.participant_generation_mask,
               "terminal participant incarnation retires instead of wrapping");
    }
}

} // namespace

int main() {
    source_contract_has_no_recovery_lock();
    pid_start_and_namespace_classification();
    registering_uses_presence_only();
    exact_lease_recovery_and_reuse_fencing();
    exact_reservation_and_directory_recovery();
    participant_handoff_reference_fencing_and_retirement();
    if (failures.load(std::memory_order_relaxed) == 0) {
        std::cout << "recovery_v2_tests: PASS\n";
        return 0;
    }
    return 1;
}
