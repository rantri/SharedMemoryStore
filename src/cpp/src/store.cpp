#include "internal.hpp"

#include "cold_open.hpp"
#include "checkpoint.hpp"
#include "diagnostics_v2.hpp"
#include "key_directory.hpp"
#include "lease_registry.hpp"
#include "mapped_atomic.hpp"
#include "participant_registry.hpp"
#include "platform_identity.hpp"
#include "reclaimer.hpp"
#include "recovery.hpp"
#include "reservation_memory.hpp"
#include "slot_table.hpp"
#include "store_control.hpp"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <limits>
#include <new>
#include <random>
#include <stdexcept>

namespace sms::detail {
namespace {

constexpr std::size_t byte_chunk = 64;

[[nodiscard]] OperationBudget make_budget(
    const Wait& wait,
    OperationBudget::clock::time_point started =
        OperationBudget::clock::now()) noexcept {
    return OperationBudget::start_at(
        std::chrono::milliseconds(wait.milliseconds),
        started,
        wait.cancellation);
}

[[nodiscard]] std::span<const std::byte> as_bytes(
    std::span<const std::uint8_t> value) noexcept {
    return {
        reinterpret_cast<const std::byte*>(value.data()),
        value.size()};
}

[[nodiscard]] sms_status bounded_hash(
    std::span<const std::uint8_t> key,
    const OperationBudget& budget,
    std::uint64_t& hash) noexcept {
    constexpr std::uint64_t offset = 14695981039346656037ULL;
    constexpr std::uint64_t prime = 1099511628211ULL;
    hash = offset;
    std::int32_t chunk{};
    for (std::size_t start = 0; start < key.size(); start += byte_chunk, ++chunk) {
        const auto bound = budget.check_periodic(chunk);
        if (bound != SMS_STATUS_SUCCESS) {
            hash = 0;
            return bound;
        }
        const auto length = std::min(byte_chunk, key.size() - start);
        for (std::size_t index = 0; index < length; ++index) {
            hash ^= key[start + index];
            hash *= prime;
        }
    }
    return budget.check_periodic(chunk);
}

[[nodiscard]] sms_status bounded_copy(
    std::span<const std::uint8_t> source,
    std::span<std::uint8_t> destination,
    const OperationBudget& budget,
    std::int64_t* copied = nullptr) noexcept {
    if (destination.size() < source.size()) return SMS_STATUS_CORRUPT_STORE;
    std::int32_t chunk{};
    for (std::size_t start = 0; start < source.size(); start += byte_chunk, ++chunk) {
        const auto bound = budget.check_periodic(chunk);
        if (bound != SMS_STATUS_SUCCESS) return bound;
        const auto length = std::min(byte_chunk, source.size() - start);
        std::memcpy(destination.data() + start, source.data() + start, length);
        if (copied != nullptr) *copied += static_cast<std::int64_t>(length);
    }
    return budget.check_periodic(chunk);
}

[[nodiscard]] bool range_valid(
    std::int64_t offset,
    std::int64_t length,
    std::size_t capacity) noexcept {
    if (offset < 0 || length < 0) return false;
    const auto start = static_cast<std::uint64_t>(offset);
    const auto size = static_cast<std::uint64_t>(length);
    return start <= capacity && size <= capacity - start;
}

[[nodiscard]] sms_open_status map_cold_status(
    ColdOpenStatus status) noexcept {
    switch (status) {
    case ColdOpenStatus::success: return SMS_OPEN_SUCCESS;
    case ColdOpenStatus::invalid_options: return SMS_OPEN_INVALID_OPTIONS;
    case ColdOpenStatus::already_exists: return SMS_OPEN_ALREADY_EXISTS;
    case ColdOpenStatus::not_found: return SMS_OPEN_NOT_FOUND;
    case ColdOpenStatus::insufficient_capacity:
        return SMS_OPEN_INSUFFICIENT_CAPACITY;
    case ColdOpenStatus::participant_table_full:
        return SMS_OPEN_PARTICIPANT_TABLE_FULL;
    case ColdOpenStatus::store_busy: return SMS_OPEN_STORE_BUSY;
    case ColdOpenStatus::operation_canceled:
        return SMS_OPEN_OPERATION_CANCELED;
    case ColdOpenStatus::unsupported_platform:
        return SMS_OPEN_UNSUPPORTED_PLATFORM;
    case ColdOpenStatus::corrupt_store:
    case ColdOpenStatus::incompatible_layout:
    default:
        return SMS_OPEN_INCOMPATIBLE_LAYOUT;
    }
}

[[nodiscard]] ColdOpenMode map_open_mode(sms_open_mode mode) noexcept {
    switch (mode) {
    case SMS_OPEN_MODE_CREATE_NEW: return ColdOpenMode::create_new;
    case SMS_OPEN_MODE_OPEN_EXISTING: return ColdOpenMode::open_existing;
    default: return ColdOpenMode::create_or_open;
    }
}

void release_cold_gates(PlatformOpenResult& platform) noexcept {
    if (platform.cold_lock) {
        platform.cold_lock->release();
        platform.cold_lock.reset();
    }
    if (platform.lifecycle_lock) {
        platform.lifecycle_lock->release();
        platform.lifecycle_lock.reset();
    }
}

void close_failed_open(PlatformOpenResult& platform) noexcept {
    // Mapping/owner cleanup is part of the retained cold transaction. Linux
    // owner markers and anchors must be reconciled while both gates are still
    // held; Windows likewise unmaps before releasing its named mutex.
    if (platform.region) {
        platform.region->close_while_cold_locked();
        platform.region.reset();
    }
    release_cold_gates(platform);
}

[[nodiscard]] std::uint64_t new_store_id() noexcept {
    auto value = static_cast<std::uint64_t>(
        std::chrono::system_clock::now().time_since_epoch().count());
    value ^= static_cast<std::uint64_t>(
        std::chrono::steady_clock::now().time_since_epoch().count()) << 1U;
    value ^= static_cast<std::uint64_t>(
        static_cast<std::uint32_t>(current_process_id())) << 32U;
    try {
        std::random_device random;
        value ^= static_cast<std::uint64_t>(random()) << 32U;
        value ^= static_cast<std::uint64_t>(random());
    } catch (...) {
    }
    return value == 0 ? 1 : value;
}

[[nodiscard]] std::int64_t monotonic_sequence() noexcept {
    const auto value = std::chrono::steady_clock::now()
        .time_since_epoch().count();
    return value <= 0 ? 1 : static_cast<std::int64_t>(value);
}

[[nodiscard]] sms_status classify_exact_slot(
    SlotTable& slots,
    const LayoutV2& layout,
    std::uint64_t exact_binding,
    SlotControl& control,
    ValueSlotMetadataV2*& slot) noexcept {
    slot = nullptr;
    IndexBinding binding{};
    if (!IndexBinding::try_decode(exact_binding, binding) ||
        binding.slot_index < 0 || binding.slot_index >= layout.slot_count) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    slot = slots.slot(binding.slot_index);
    if (slot == nullptr) return SMS_STATUS_CORRUPT_STORE;
    const auto raw = MappedAtomic64::load_acquire(slot->Control);
    bool occupied{};
    if (!SlotTable::try_classify_structural_control(
            raw, layout.participant_record_count, occupied) ||
        !SlotControl::try_decode(raw, control)) {
        return SMS_STATUS_CORRUPT_STORE;
    }
    if (control.generation > binding.generation ||
        (control.generation == binding.generation &&
         control.state == static_cast<std::int32_t>(SlotState::retired))) {
        return SMS_STATUS_NOT_FOUND;
    }
    return control.generation < binding.generation
        ? SMS_STATUS_CORRUPT_STORE
        : SMS_STATUS_SUCCESS;
}

} // namespace

struct Store::State {
    State(
        MappedRegion& mapped_region,
        const LayoutV2& mapped_layout,
        ParticipantRegistration mapped_registration,
        bool lease_recovery)
        : layout(mapped_layout),
          control(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout),
          participants(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout),
          registration(mapped_registration),
          slots(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout,
              reinterpret_cast<StoreHeaderV2*>(mapped_region.data())->StoreId,
              SlotParticipant{
                  registration.token,
                  registration.active_control}),
          directory(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout),
          leases(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout,
              reinterpret_cast<StoreHeaderV2*>(mapped_region.data())->StoreId,
              LeaseParticipant{
                  registration.token,
                  registration.active_control}),
          diagnostics(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout),
          reclaimer(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout,
              slots,
              directory,
              leases),
          recovery(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout,
              participants,
              slots,
              directory,
              leases,
              reclaimer),
          reservation_memory(
              mapped_region.data(),
              static_cast<std::size_t>(mapped_region.size()),
              layout,
              slots),
          recovery_enabled(lease_recovery) {
        if (mapped_region.size() <= 0 || !registration.valid(
                layout.participant_record_count) ||
            !control.valid_mapping() || !participants.valid() ||
            !slots.valid() || !directory.valid() || !leases.valid() ||
            !diagnostics.valid() || !reclaimer.valid() || !recovery.valid() ||
            !reservation_memory.valid()) {
            throw std::invalid_argument("Invalid SMS2 store attachment.");
        }
    }

    void adopt_region(std::unique_ptr<MappedRegion> mapped_region) noexcept {
        region = std::move(mapped_region);
    }

    std::unique_ptr<MappedRegion> region;
    LayoutV2 layout;
    StoreControlV2 control;
    ParticipantRegistry participants;
    ParticipantRegistration registration;
    SlotTable slots;
    KeyDirectory directory;
    LeaseRegistry leases;
    DiagnosticsV2 diagnostics;
    Reclaimer reclaimer;
    RecoveryCoordinator recovery;
    ReservationMemory reservation_memory;
    bool recovery_enabled{};

    std::array<std::atomic<std::int64_t>, SMS_STATUS_COUNT> failures{};
    std::atomic<sms_status> last_failure{SMS_STATUS_SUCCESS};
    std::atomic<std::int64_t> aborted_reservations{};
    std::atomic<std::int64_t> recovered_leases{};
    std::atomic<std::int64_t> active_lease_recoveries{};
    std::atomic<std::int64_t> unsupported_lease_recoveries{};
    std::atomic<std::int64_t> failed_lease_recoveries{};
    std::atomic<std::int64_t> recovered_reservations{};
    std::atomic<std::int64_t> active_reservation_recoveries{};
    std::atomic<std::int64_t> unsupported_reservation_recoveries{};
    std::atomic<std::int64_t> failed_reservation_recoveries{};
    std::atomic<std::int64_t> capacity_pressure{};
    std::atomic<std::int64_t> overflow_scans{};
    std::atomic<std::int64_t> cas_retries{};
    std::atomic<std::int64_t> helped_transitions{};
    std::atomic<std::int64_t> contention_exhaustions{};
    std::atomic<std::int64_t> invalid_tokens{};
    std::atomic<std::int64_t> stale_tokens{};
    std::atomic<std::int64_t> recovery_attempts{};
    std::atomic<std::int64_t> recovered_transitions{};
    std::atomic<std::int64_t> current_owner_classifications{};
    std::atomic<std::int64_t> live_owner_classifications{};
    std::atomic<std::int64_t> stale_owner_classifications{};
    std::atomic<std::int64_t> unsupported_owner_classifications{};
    std::atomic<std::int64_t> inconsistent_owner_classifications{};
    std::atomic<std::int64_t> changing_owner_classifications{};
};

Store::Store(std::unique_ptr<State> state) noexcept
    : state_(std::move(state)) {}

Store::~Store() { close(); }

sms_open_status Store::open(
    const Options& options,
    const Wait& wait,
    std::shared_ptr<Store>& result) noexcept {
    result.reset();
    PlatformOpenResult platform{};
    ParticipantRegistration registration{};
    LayoutV2 layout{};
    bool attached{};
    try {
        if (!wait.valid() || utf8_whitespace_only(options.name) ||
            options.name.find('\0') != std::string::npos ||
            !valid_utf8(options.name) || utf16_length(options.name) > 240 ||
            options.open_mode < SMS_OPEN_MODE_CREATE_NEW ||
            options.open_mode > SMS_OPEN_MODE_CREATE_OR_OPEN ||
            options.total_bytes <= 0 ||
            !MappedAtomic64::supported()) {
            return !MappedAtomic64::supported()
                ? SMS_OPEN_UNSUPPORTED_PLATFORM
                : SMS_OPEN_INVALID_OPTIONS;
        }
        if (!LayoutV2::calculate(
                options.total_bytes,
                options.slot_count,
                options.max_value_bytes,
                options.max_descriptor_bytes,
                options.max_key_bytes,
                options.lease_record_count,
                options.participant_record_count,
                layout)) {
            return SMS_OPEN_INVALID_OPTIONS;
        }
        // Requested dimensions must fit the caller's declared capacity for
        // every open mode. Check before touching any platform resource so an
        // undersized create/open request has one deterministic cross-runtime
        // result and cannot leave a partially-created mapping behind.
        if (!layout.fits_within_total_bytes()) {
            return SMS_OPEN_INSUFFICIENT_CAPACITY;
        }
        ResourceName resource{};
        if (!make_resource_name(options.name, resource)) {
            return SMS_OPEN_INVALID_OPTIONS;
        }

        const auto started = OperationBudget::clock::now();
        platform = platform_open(resource, options, wait);
        if (platform.status != SMS_OPEN_SUCCESS || !platform.region ||
            !platform.cold_lock || platform.region->size() <= 0 ||
            static_cast<std::uint64_t>(platform.region->size()) >
                std::numeric_limits<std::size_t>::max()) {
            close_failed_open(platform);
            return platform.status;
        }
        auto identity = capture_participant_identity();
        if (!identity.valid()) {
            close_failed_open(platform);
            return SMS_OPEN_UNSUPPORTED_PLATFORM;
        }
        ColdOpenV2 cold(
            platform.region->data(),
            static_cast<std::size_t>(platform.region->size()));
        const auto cold_result = cold.attach(
            platform.physical_creator,
            map_open_mode(options.open_mode),
            layout,
            identity,
            new_store_id(),
            capture_pid_namespace_id(),
            make_budget(wait, started));
        if (cold_result.status != ColdOpenStatus::success) {
            close_failed_open(platform);
            return map_cold_status(cold_result.status);
        }
        registration = cold_result.registration;
        attached = true;

        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::ParticipantAfterRegistrationBeforeEngineConstruction);

        auto state = std::make_unique<State>(
            *platform.region,
            layout,
            registration,
            options.enable_lease_recovery);
        auto* raw_candidate = new (std::nothrow) Store(std::move(state));
        if (raw_candidate == nullptr) {
            ParticipantRegistry participants(
                platform.region->data(),
                static_cast<std::size_t>(platform.region->size()),
                layout);
            (void)participants.close_and_retire(registration);
            close_failed_open(platform);
            return SMS_OPEN_MAPPING_FAILED;
        }
        raw_candidate->state_->adopt_region(std::move(platform.region));
        auto candidate = std::shared_ptr<Store>(raw_candidate);
        release_cold_gates(platform);
        result = std::move(candidate);
        return SMS_OPEN_SUCCESS;
    } catch (const std::bad_alloc&) {
        // Fall through to registration rollback and mapping cleanup.
    } catch (...) {
        // Native ABI boundaries convert every construction exception.
    }

    if (attached && platform.region) {
        ParticipantRegistry participants(
            platform.region->data(),
            static_cast<std::size_t>(platform.region->size()),
            layout);
        (void)participants.close_and_retire(registration);
    }
    close_failed_open(platform);
    return SMS_OPEN_MAPPING_FAILED;
}

sms_status Store::enter(
    const Wait& wait,
    LifecycleGate::Operation& operation) noexcept {
    const auto entered = lifecycle_.try_enter(operation);
    if (entered != SMS_STATUS_SUCCESS) return entered;
    if (state_ == nullptr) {
        operation.reset();
        return SMS_STATUS_STORE_DISPOSED;
    }
    const auto ready = ensure_ready();
    if (ready != SMS_STATUS_SUCCESS) return ready;
    if (!wait.valid()) return SMS_STATUS_UNKNOWN_FAILURE;
    if (wait.cancellation != nullptr && wait.cancellation->is_canceled()) {
        return SMS_STATUS_OPERATION_CANCELED;
    }
    return SMS_STATUS_SUCCESS;
}

sms_status Store::ensure_ready() const noexcept {
    return state_ == nullptr
        ? SMS_STATUS_STORE_DISPOSED
        : state_->control.ensure_ready();
}

sms_status Store::validate_key(
    std::span<const std::uint8_t> key) const noexcept {
    if (state_ == nullptr) return SMS_STATUS_STORE_DISPOSED;
    if (key.empty()) return SMS_STATUS_INVALID_KEY;
    return key.size() > static_cast<std::size_t>(state_->layout.max_key_bytes)
        ? SMS_STATUS_KEY_TOO_LARGE
        : SMS_STATUS_SUCCESS;
}

sms_status Store::validate_value(
    std::span<const std::uint8_t> key,
    std::size_t value_length,
    std::size_t descriptor_length) const noexcept {
    const auto key_status = validate_key(key);
    if (key_status != SMS_STATUS_SUCCESS) return key_status;
    if (value_length > static_cast<std::size_t>(state_->layout.max_value_bytes)) {
        return SMS_STATUS_VALUE_TOO_LARGE;
    }
    return descriptor_length >
            static_cast<std::size_t>(state_->layout.max_descriptor_bytes)
        ? SMS_STATUS_DESCRIPTOR_TOO_LARGE
        : SMS_STATUS_SUCCESS;
}

sms_status Store::record(sms_status status) noexcept {
    auto* state = state_.get();
    if (state == nullptr || status == SMS_STATUS_SUCCESS) return status;
    const auto index = static_cast<std::int32_t>(status);
    if (index >= 0 && index < SMS_STATUS_COUNT) {
        state->failures[static_cast<std::size_t>(index)].fetch_add(
            1, std::memory_order_relaxed);
    }
    state->last_failure.store(status, std::memory_order_release);
    if (status == SMS_STATUS_STORE_FULL ||
        status == SMS_STATUS_LEASE_TABLE_FULL) {
        state->capacity_pressure.fetch_add(1, std::memory_order_relaxed);
    }
    if (status == SMS_STATUS_STORE_BUSY) {
        state->contention_exhaustions.fetch_add(
            1, std::memory_order_relaxed);
    }
    if (status == SMS_STATUS_INVALID_LEASE ||
        status == SMS_STATUS_INVALID_RESERVATION) {
        state->invalid_tokens.fetch_add(1, std::memory_order_relaxed);
    }
    if (status == SMS_STATUS_LEASE_ALREADY_RELEASED ||
        status == SMS_STATUS_RESERVATION_ALREADY_COMPLETED) {
        state->stale_tokens.fetch_add(1, std::memory_order_relaxed);
    }
    if (status == SMS_STATUS_CORRUPT_STORE) {
        (void)state->control.latch_corrupt();
    }
    return status;
}

ReservationToken Store::to_reservation(
    const LifecycleId& lifecycle) noexcept {
    return ReservationToken{
        lifecycle.store_id,
        lifecycle.participant_token,
        lifecycle.slot_binding,
        lifecycle.payload_length};
}

LeaseToken Store::to_lease(const LifecycleId& lifecycle) noexcept {
    return LeaseToken{
        lifecycle.store_id,
        lifecycle.participant_token,
        lifecycle.slot_binding,
        lifecycle.resource_binding};
}

LifecycleId Store::from_reservation(
    const ReservationToken& reservation) noexcept {
    return LifecycleId{
        reservation.store_id,
        reservation.slot_binding,
        0,
        reservation.participant_token,
        reservation.payload_length};
}

LifecycleId Store::from_lease(const LeaseToken& lease) noexcept {
    return LifecycleId{
        lease.store_id,
        lease.slot_binding,
        lease.lease_binding,
        lease.participant_token,
        0};
}

sms_status Store::abort_core(
    const ReservationToken& reservation,
    const OperationBudget& budget) noexcept {
    if (state_ == nullptr) return SMS_STATUS_STORE_DISPOSED;
    const auto begin = state_->slots.try_begin_abort(reservation);
    if (begin != SMS_STATUS_SUCCESS) return begin;

    // The Aborting CAS is the public ownership-release point. Cleanup is now
    // universally helpable and must not turn a successful abort into a timeout.
    const auto structural = OperationBudget::structural_attempt();
    const auto unlink = state_->directory.try_unlink(
        reservation.slot_binding, structural);
    if (unlink == SMS_STATUS_CORRUPT_STORE) return unlink;
    if (unlink == SMS_STATUS_SUCCESS || unlink == SMS_STATUS_NOT_FOUND) {
        sms::test_detail::reach_checkpoint(
            sms::test_detail::CheckpointId::AbortAfterUnlinkCompletion);
    }
    const auto reclaimed = state_->reclaimer.try_reclaim(
        reservation.slot_binding, structural);
    if (reclaimed == SMS_STATUS_CORRUPT_STORE) return reclaimed;
    state_->helped_transitions.fetch_add(1, std::memory_order_relaxed);
    (void)budget;
    state_->aborted_reservations.fetch_add(1, std::memory_order_relaxed);
    return SMS_STATUS_SUCCESS;
}

sms_status Store::reserve_core(
    std::span<const std::uint8_t> key,
    std::int32_t payload_length,
    std::span<const std::uint8_t> descriptor,
    SlotPublicationIntent intent,
    const OperationBudget& budget,
    ReservationToken& reservation) noexcept {
    reservation = {};
    const auto input = payload_length < 0
        ? SMS_STATUS_VALUE_TOO_LARGE
        : validate_value(
            key,
            static_cast<std::size_t>(payload_length),
            descriptor.size());
    if (input != SMS_STATUS_SUCCESS) return input;

    std::uint64_t hash{};
    auto status = bounded_hash(key, budget, hash);
    if (status != SMS_STATUS_SUCCESS) return status;

    for (std::int32_t attempt = 0;; ++attempt) {
        if (attempt > 0) {
            state_->cas_retries.fetch_add(1, std::memory_order_relaxed);
        }
        DirectoryEntry existing{};
        status = state_->directory.try_lookup(
            as_bytes(key), hash, budget, existing);
        if (status == SMS_STATUS_SUCCESS) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::ReserveAfterExistingLookup);
            SlotControl current{};
            ValueSlotMetadataV2* existing_slot{};
            const auto classified = classify_exact_slot(
                state_->slots,
                state_->layout,
                existing.binding,
                current,
                existing_slot);
            if (classified == SMS_STATUS_NOT_FOUND) continue;
            if (classified != SMS_STATUS_SUCCESS || existing_slot == nullptr) {
                return classified;
            }
            const auto current_state = static_cast<SlotState>(current.state);
            const auto publication_intent =
                std::atomic_ref<std::int32_t>(
                    existing_slot->PublicationIntent)
                    .load(std::memory_order_acquire);
            if (current_state == SlotState::published) {
                return SMS_STATUS_DUPLICATE_KEY;
            }
            if (current_state == SlotState::remove_requested) {
                const auto helped = state_->reclaimer.try_reclaim(
                    existing.binding, budget);
                if (helped == SMS_STATUS_SUCCESS) continue;
                if (helped == SMS_STATUS_CORRUPT_STORE) return helped;
                return SMS_STATUS_DUPLICATE_KEY;
            }
            if (current_state == SlotState::reserved &&
                publication_intent == static_cast<std::int32_t>(
                    SlotPublicationIntent::explicit_reservation)) {
                return SMS_STATUS_DUPLICATE_KEY;
            }
            if (current_state == SlotState::aborting ||
                current_state == SlotState::reclaiming) {
                const auto helped = state_->reclaimer.try_reclaim(
                    existing.binding, budget);
                if (helped == SMS_STATUS_SUCCESS ||
                    helped == SMS_STATUS_STORE_BUSY) {
                    sms_status terminal{};
                    if (!budget.try_continue_after_contention(
                            attempt, terminal)) {
                        return terminal;
                    }
                    continue;
                }
                return helped;
            }

            std::int32_t canonical{};
            std::int32_t alternate{};
            state_->directory.buckets_for_hash(
                hash, canonical, alternate);
            (void)alternate;
            const auto helped = state_->directory.help_mutation(
                canonical, budget, 8);
            if (helped != SMS_STATUS_SUCCESS &&
                helped != SMS_STATUS_STORE_BUSY) {
                return helped;
            }
            sms_status terminal{};
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
            continue;
        }
        if (status != SMS_STATUS_NOT_FOUND) return status;

        sms::test_detail::reach_checkpoint(
            intent == SlotPublicationIntent::atomic_publication
                ? sms::test_detail::CheckpointId::PublishBeforeSlotClaim
                : sms::test_detail::CheckpointId::ReserveBeforeSlotClaim);
        status = state_->slots.try_claim_reservation(
            hash,
            static_cast<std::int32_t>(key.size()),
            static_cast<std::int32_t>(descriptor.size()),
            payload_length,
            intent,
            budget,
            reservation);
        if (status == SMS_STATUS_STORE_FULL) {
            std::int32_t reclaimed{};
            const auto helped = state_->reclaimer.help_reclaimable_slots(
                budget, reclaimed);
            if (helped != SMS_STATUS_SUCCESS) return helped;
            if (reclaimed != 0) continue;
            bool proven{};
            const auto proof = state_->slots.try_prove_store_full(
                budget, proven);
            if (proof != SMS_STATUS_SUCCESS) return proof;
            if (proven) return SMS_STATUS_STORE_FULL;
            sms_status terminal{};
            if (!budget.try_continue_after_contention(attempt, terminal)) {
                return terminal;
            }
            continue;
        }
        if (status != SMS_STATUS_SUCCESS) return status;

        IndexBinding claimed{};
        auto* claimed_slot = IndexBinding::try_decode(
                reservation.slot_binding, claimed)
            ? state_->slots.slot(claimed.slot_index)
            : nullptr;
        if (claimed_slot == nullptr ||
            claimed_slot->KeyOffset != state_->layout.key_storage_offset +
                static_cast<std::int64_t>(claimed.slot_index) *
                    state_->layout.key_stride ||
            claimed_slot->DescriptorOffset !=
                state_->layout.descriptor_storage_offset +
                    static_cast<std::int64_t>(claimed.slot_index) *
                        state_->layout.descriptor_stride ||
            !range_valid(
                claimed_slot->KeyOffset,
                static_cast<std::int64_t>(key.size()),
                static_cast<std::size_t>(state_->region->size())) ||
            !range_valid(
                claimed_slot->DescriptorOffset,
                static_cast<std::int64_t>(descriptor.size()),
                static_cast<std::size_t>(state_->region->size()))) {
            (void)abort_core(reservation, budget);
            reservation = {};
            return SMS_STATUS_CORRUPT_STORE;
        }

        status = bounded_copy(
            key,
            {state_->region->data() + claimed_slot->KeyOffset, key.size()},
            budget);
        if (status == SMS_STATUS_SUCCESS) {
            status = bounded_copy(
                descriptor,
                {state_->region->data() + claimed_slot->DescriptorOffset,
                 descriptor.size()},
                budget);
        }
        if (status != SMS_STATUS_SUCCESS) {
            (void)abort_core(reservation, budget);
            reservation = {};
            return status;
        }

        DirectoryLocation inserted_location{};
        status = state_->directory.try_insert(
            as_bytes(key),
            hash,
            reservation.slot_binding,
            budget,
            inserted_location);
        if (status == SMS_STATUS_SUCCESS) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::ReserveAfterDirectoryInsertBeforePendingClassification);
        }
        if (status == SMS_STATUS_SUCCESS &&
            state_->slots.reservation_pending(reservation)) {
            return SMS_STATUS_SUCCESS;
        }

        if (status != SMS_STATUS_SUCCESS &&
            intent == SlotPublicationIntent::explicit_reservation &&
            state_->slots.reservation_pending(reservation)) {
            bool ordered{};
            const auto contains = state_->directory.contains_exact_reference(
                reservation.slot_binding,
                OperationBudget::structural_attempt(),
                ordered);
            if (contains == SMS_STATUS_SUCCESS && ordered) {
                return SMS_STATUS_SUCCESS;
            }
            if (contains == SMS_STATUS_CORRUPT_STORE) status = contains;
        }

        const auto failure = status == SMS_STATUS_SUCCESS
            ? SMS_STATUS_INVALID_RESERVATION
            : status;
        const auto cleanup = abort_core(
            reservation, OperationBudget::structural_attempt());
        reservation = {};
        if (cleanup == SMS_STATUS_CORRUPT_STORE) return cleanup;
        return failure;
    }
}

sms_status Store::publish(
    std::span<const std::uint8_t> key,
    std::span<const std::uint8_t> value,
    std::span<const std::uint8_t> descriptor,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (value.size() > static_cast<std::size_t>(
            std::numeric_limits<std::int32_t>::max())) {
        return record(SMS_STATUS_VALUE_TOO_LARGE);
    }
    const auto budget = make_budget(wait);
    ReservationToken reservation{};
    status = reserve_core(
        key,
        static_cast<std::int32_t>(value.size()),
        descriptor,
        SlotPublicationIntent::atomic_publication,
        budget,
        reservation);
    if (status != SMS_STATUS_SUCCESS) return record(status);

    auto destination = state_->reservation_memory.get_span(
        reservation, static_cast<std::int32_t>(value.size()));
    if (destination.size() < value.size()) {
        (void)abort_core(reservation, budget);
        return record(SMS_STATUS_CORRUPT_STORE);
    }
    status = bounded_copy(
        value,
        {reinterpret_cast<std::uint8_t*>(destination.data()),
         destination.size()},
        budget);
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->slots.advance_reservation(
            reservation,
            static_cast<std::int32_t>(value.size()),
            budget);
    }
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->slots.commit_reservation(
            reservation, monotonic_sequence());
        if (status == SMS_STATUS_SUCCESS) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::PublishAfterCommitPublication);
        }
    }
    if (status != SMS_STATUS_SUCCESS) {
        (void)abort_core(reservation, OperationBudget::structural_attempt());
    }
    return record(status);
}

sms_status Store::publish_segments(
    std::span<const std::uint8_t> key,
    std::span<const sms_segment> segments,
    std::span<const std::uint8_t> descriptor,
    const Wait& wait,
    std::int64_t& copied) noexcept {
    copied = 0;
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    std::uint64_t total{};
    for (const auto& segment : segments) {
        if (segment.length >
                static_cast<std::uint64_t>(
                    std::numeric_limits<std::int32_t>::max()) ||
            total > static_cast<std::uint64_t>(
                        std::numeric_limits<std::int32_t>::max()) -
                    segment.length) {
            return record(SMS_STATUS_VALUE_TOO_LARGE);
        }
        total += segment.length;
    }

    const auto budget = make_budget(wait);
    ReservationToken reservation{};
    status = reserve_core(
        key,
        static_cast<std::int32_t>(total),
        descriptor,
        SlotPublicationIntent::atomic_publication,
        budget,
        reservation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    auto destination = state_->reservation_memory.get_span(
        reservation, static_cast<std::int32_t>(total));
    if (destination.size() < total) {
        (void)abort_core(reservation, budget);
        return record(SMS_STATUS_CORRUPT_STORE);
    }

    std::size_t offset{};
    for (const auto& segment : segments) {
        const auto source = std::span<const std::uint8_t>(
            segment.data, static_cast<std::size_t>(segment.length));
        auto target = std::span<std::uint8_t>(
            reinterpret_cast<std::uint8_t*>(destination.data()) + offset,
            destination.size() - offset);
        status = bounded_copy(source, target, budget, &copied);
        if (status != SMS_STATUS_SUCCESS) break;
        offset += source.size();
    }
    if (status == SMS_STATUS_SUCCESS && offset != total) {
        status = SMS_STATUS_UNKNOWN_FAILURE;
    }
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->slots.advance_reservation(
            reservation, static_cast<std::int32_t>(total), budget);
    }
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->slots.commit_reservation(
            reservation, monotonic_sequence());
        if (status == SMS_STATUS_SUCCESS) {
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::PublishAfterCommitPublication);
        }
    }
    if (status != SMS_STATUS_SUCCESS) {
        (void)abort_core(reservation, OperationBudget::structural_attempt());
    }
    return record(status);
}

sms_status Store::acquire(
    std::span<const std::uint8_t> key,
    const Wait& wait,
    std::int32_t& slot_index,
    LifecycleId& lifecycle,
    std::int32_t& lease_id) noexcept {
    slot_index = -1;
    lifecycle = {};
    lease_id = -1;
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_key(key);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto budget = make_budget(wait);
    std::uint64_t hash{};
    status = bounded_hash(key, budget, hash);
    if (status != SMS_STATUS_SUCCESS) return record(status);

    DirectoryEntry found{};
    status = state_->directory.try_lookup(
        as_bytes(key), hash, budget, found);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    SlotControl slot_control{};
    ValueSlotMetadataV2* value_slot{};
    status = classify_exact_slot(
        state_->slots,
        state_->layout,
        found.binding,
        slot_control,
        value_slot);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (slot_control.state != static_cast<std::int32_t>(SlotState::published)) {
        return record(SMS_STATUS_NOT_FOUND);
    }

    LeaseToken lease{};
    status = state_->leases.try_claim(
        found.binding, monotonic_sequence(), budget, lease);
    if (status != SMS_STATUS_SUCCESS) {
        if (status == SMS_STATUS_LEASE_TABLE_FULL) {
            DirectoryEntry confirmed{};
            const auto lookup = state_->directory.try_lookup(
                as_bytes(key), hash, budget, confirmed);
            if (lookup != SMS_STATUS_SUCCESS ||
                confirmed.binding != found.binding) {
                return record(lookup == SMS_STATUS_SUCCESS
                    ? SMS_STATUS_NOT_FOUND
                    : lookup);
            }
        }
        return record(status);
    }
    status = state_->leases.try_activate(lease);
    if (status != SMS_STATUS_SUCCESS) {
        (void)state_->leases.try_cancel_claim(lease);
        return record(status);
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::AcquireAfterLeaseActivationBeforeFinalLookup);

    const auto bound = budget.check();
    DirectoryEntry confirmed{};
    const auto lookup = bound == SMS_STATUS_SUCCESS
        ? state_->directory.try_lookup(
            as_bytes(key), hash, budget, confirmed)
        : bound;
    SlotControl confirmed_control{};
    ValueSlotMetadataV2* confirmed_slot{};
    const auto publication =
        lookup == SMS_STATUS_SUCCESS && confirmed.binding == found.binding
        ? classify_exact_slot(
            state_->slots,
            state_->layout,
            found.binding,
            confirmed_control,
            confirmed_slot)
        : SMS_STATUS_NOT_FOUND;
    if (lookup != SMS_STATUS_SUCCESS ||
        confirmed.binding != found.binding ||
        publication != SMS_STATUS_SUCCESS ||
        confirmed_control.state !=
            static_cast<std::int32_t>(SlotState::published)) {
        (void)state_->leases.try_release(lease);
        if (bound != SMS_STATUS_SUCCESS) return record(bound);
        if (lookup != SMS_STATUS_SUCCESS) return record(lookup);
        if (publication == SMS_STATUS_CORRUPT_STORE) return record(publication);
        return record(SMS_STATUS_NOT_FOUND);
    }

    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::AcquireAfterPublishedRevalidation);

    IndexBinding slot_binding{};
    IndexBinding lease_binding{};
    if (!IndexBinding::try_decode(found.binding, slot_binding) ||
        !IndexBinding::try_decode(lease.lease_binding, lease_binding)) {
        (void)state_->leases.try_release(lease);
        return record(SMS_STATUS_CORRUPT_STORE);
    }
    slot_index = slot_binding.slot_index;
    lease_id = lease_binding.slot_index;
    lifecycle = from_lease(lease);
    return SMS_STATUS_SUCCESS;
}

bool Store::project_lease(
    const LeaseToken& lease,
    ValueSlotMetadataV2*& value_slot,
    std::int32_t& value_length,
    std::int32_t& descriptor_length) noexcept {
    value_slot = nullptr;
    value_length = 0;
    descriptor_length = 0;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ProjectBeforeHandleValidation);
    if (state_ == nullptr || !lease.valid()) return false;
    std::uint64_t registry_binding{};
    if (!state_->leases.try_get_active_slot_binding(
            lease, registry_binding) ||
        registry_binding != lease.slot_binding) {
        return false;
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lease.slot_binding, binding) ||
        binding.slot_index < 0 ||
        binding.slot_index >= state_->layout.slot_count) {
        return false;
    }
    auto* current = state_->slots.slot(binding.slot_index);
    if (current == nullptr) return false;
    const auto control1 = MappedAtomic64::load_acquire(current->Control);
    bool occupied{};
    SlotControl decoded{};
    if (!SlotTable::try_classify_structural_control(
            control1, state_->layout.participant_record_count, occupied) ||
        !SlotControl::try_decode(control1, decoded) ||
        decoded.generation != binding.generation ||
        (decoded.state != static_cast<std::int32_t>(SlotState::published) &&
         decoded.state !=
            static_cast<std::int32_t>(SlotState::remove_requested))) {
        return false;
    }

    const auto directory_binding =
        MappedAtomic64::load_acquire(current->DirectoryBinding);
    const auto key_length = std::atomic_ref<std::int32_t>(current->KeyLength)
        .load(std::memory_order_acquire);
    const auto observed_descriptor =
        std::atomic_ref<std::int32_t>(current->DescriptorLength)
            .load(std::memory_order_acquire);
    const auto observed_value =
        std::atomic_ref<std::int32_t>(current->ValueLength)
            .load(std::memory_order_acquire);
    const auto descriptor_offset = current->DescriptorOffset;
    const auto payload_offset = current->PayloadOffset;
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ProjectAfterMetadataReadBeforeControlRevalidation);
    const auto control2 = MappedAtomic64::load_acquire(current->Control);
    if (control1 != control2 || directory_binding != lease.slot_binding ||
        key_length < 1 || key_length > state_->layout.max_key_bytes ||
        observed_descriptor < 0 ||
        observed_descriptor > state_->layout.max_descriptor_bytes ||
        observed_value < 0 || observed_value > state_->layout.max_value_bytes ||
        descriptor_offset != state_->layout.descriptor_storage_offset +
            static_cast<std::int64_t>(binding.slot_index) *
                state_->layout.descriptor_stride ||
        payload_offset != state_->layout.payload_storage_offset +
            static_cast<std::int64_t>(binding.slot_index) *
                state_->layout.payload_stride ||
        !range_valid(
            descriptor_offset,
            observed_descriptor,
            static_cast<std::size_t>(state_->region->size())) ||
        !range_valid(
            payload_offset,
            observed_value,
            static_cast<std::size_t>(state_->region->size())) ||
        !state_->leases.is_active(lease)) {
        return false;
    }
    value_slot = current;
    value_length = observed_value;
    descriptor_length = observed_descriptor;
    return true;
}

bool Store::lease_valid(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t lease_id) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.lease_valid()) {
        return false;
    }
    IndexBinding slot_binding{};
    IndexBinding lease_binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, slot_binding) ||
        !IndexBinding::try_decode(
            lifecycle.resource_binding, lease_binding) ||
        slot_binding.slot_index != slot_index ||
        lease_binding.slot_index != lease_id) {
        return false;
    }
    ValueSlotMetadataV2* value_slot{};
    std::int32_t value_length{};
    std::int32_t descriptor_length{};
    return project_lease(
        to_lease(lifecycle),
        value_slot,
        value_length,
        descriptor_length);
}

std::span<const std::uint8_t> Store::lease_value(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t lease_id) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.lease_valid()) {
        return {};
    }
    IndexBinding slot_binding{};
    IndexBinding lease_binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, slot_binding) ||
        !IndexBinding::try_decode(
            lifecycle.resource_binding, lease_binding) ||
        slot_binding.slot_index != slot_index ||
        lease_binding.slot_index != lease_id) {
        return {};
    }
    ValueSlotMetadataV2* value_slot{};
    std::int32_t value_length{};
    std::int32_t descriptor_length{};
    if (!project_lease(
            to_lease(lifecycle),
            value_slot,
            value_length,
            descriptor_length)) {
        return {};
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ProjectAfterSpanProjection);
    return {
        state_->region->data() + value_slot->PayloadOffset,
        static_cast<std::size_t>(value_length)};
}

std::span<const std::uint8_t> Store::lease_descriptor(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t lease_id) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.lease_valid()) {
        return {};
    }
    IndexBinding slot_binding{};
    IndexBinding lease_binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, slot_binding) ||
        !IndexBinding::try_decode(
            lifecycle.resource_binding, lease_binding) ||
        slot_binding.slot_index != slot_index ||
        lease_binding.slot_index != lease_id) {
        return {};
    }
    ValueSlotMetadataV2* value_slot{};
    std::int32_t value_length{};
    std::int32_t descriptor_length{};
    if (!project_lease(
            to_lease(lifecycle),
            value_slot,
            value_length,
            descriptor_length)) {
        return {};
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ProjectAfterSpanProjection);
    return {
        state_->region->data() + value_slot->DescriptorOffset,
        static_cast<std::size_t>(descriptor_length)};
}

sms_status Store::release_lease(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t lease_id,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!lifecycle.lease_valid()) return record(SMS_STATUS_INVALID_LEASE);
    IndexBinding slot_binding{};
    IndexBinding lease_binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, slot_binding) ||
        !IndexBinding::try_decode(
            lifecycle.resource_binding, lease_binding) ||
        slot_binding.slot_index != slot_index ||
        lease_binding.slot_index != lease_id) {
        return record(SMS_STATUS_INVALID_LEASE);
    }
    const auto budget = make_budget(wait);
    status = budget.check();
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = state_->leases.try_release(to_lease(lifecycle));
    if (status == SMS_STATUS_SUCCESS) {
        const auto reclaim = state_->reclaimer.try_reclaim(
            lifecycle.slot_binding,
            OperationBudget::structural_attempt());
        if (reclaim == SMS_STATUS_CORRUPT_STORE) {
            (void)state_->control.latch_corrupt();
        } else if (reclaim == SMS_STATUS_SUCCESS) {
            state_->helped_transitions.fetch_add(
                1, std::memory_order_relaxed);
        }
    }
    return record(status);
}

sms_status Store::remove(
    std::span<const std::uint8_t> key,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = validate_key(key);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto budget = make_budget(wait);
    std::uint64_t hash{};
    status = bounded_hash(key, budget, hash);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    DirectoryEntry found{};
    status = state_->directory.try_lookup(
        as_bytes(key), hash, budget, found);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    status = state_->reclaimer.try_logical_remove(
        found.binding, budget);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (wait.milliseconds == 0) return record(SMS_STATUS_REMOVE_PENDING);
    const auto reclaimed = state_->reclaimer.try_reclaim(
        found.binding, budget);
    if (reclaimed == SMS_STATUS_SUCCESS) return SMS_STATUS_SUCCESS;
    if (reclaimed == SMS_STATUS_CORRUPT_STORE) return record(reclaimed);
    return record(SMS_STATUS_REMOVE_PENDING);
}

sms_status Store::reserve(
    std::span<const std::uint8_t> key,
    std::int32_t payload_length,
    std::span<const std::uint8_t> descriptor,
    const Wait& wait,
    std::int32_t& slot_index,
    LifecycleId& lifecycle) noexcept {
    slot_index = -1;
    lifecycle = {};
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    const auto budget = make_budget(wait);
    ReservationToken reservation{};
    status = reserve_core(
        key,
        payload_length,
        descriptor,
        SlotPublicationIntent::explicit_reservation,
        budget,
        reservation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::ReserveAfterReservationPublication);
    IndexBinding binding{};
    if (!IndexBinding::try_decode(reservation.slot_binding, binding)) {
        (void)abort_core(reservation, OperationBudget::structural_attempt());
        return record(SMS_STATUS_CORRUPT_STORE);
    }
    slot_index = binding.slot_index;
    lifecycle = from_reservation(reservation);
    return SMS_STATUS_SUCCESS;
}

bool Store::reservation_valid(
    std::int32_t slot_index,
    LifecycleId lifecycle) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.reservation_valid()) {
        return false;
    }
    IndexBinding binding{};
    return IndexBinding::try_decode(lifecycle.slot_binding, binding) &&
        binding.slot_index == slot_index &&
        state_->slots.reservation_pending(to_reservation(lifecycle));
}

std::int32_t Store::reservation_payload_length(
    std::int32_t slot_index,
    LifecycleId lifecycle) noexcept {
    return reservation_valid(slot_index, lifecycle)
        ? lifecycle.payload_length
        : 0;
}

std::int32_t Store::reservation_bytes_written(
    std::int32_t slot_index,
    LifecycleId lifecycle) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.reservation_valid()) {
        return 0;
    }
    IndexBinding binding{};
    return IndexBinding::try_decode(lifecycle.slot_binding, binding) &&
        binding.slot_index == slot_index
        ? state_->slots.bytes_advanced(to_reservation(lifecycle))
        : 0;
}

std::span<std::uint8_t> Store::reservation_buffer(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t size_hint) noexcept {
    LifecycleGate::Operation operation;
    if (lifecycle_.try_enter(operation) != SMS_STATUS_SUCCESS ||
        state_ == nullptr || !lifecycle.reservation_valid()) {
        return {};
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, binding) ||
        binding.slot_index != slot_index) {
        return {};
    }
    auto bytes = state_->reservation_memory.get_span(
        to_reservation(lifecycle), size_hint);
    return {
        reinterpret_cast<std::uint8_t*>(bytes.data()), bytes.size()};
}

sms_status Store::advance_reservation(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    std::int32_t count,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!lifecycle.reservation_valid()) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, binding) ||
        binding.slot_index != slot_index) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    status = state_->slots.advance_reservation(
        to_reservation(lifecycle), count, make_budget(wait));
    return record(status);
}

sms_status Store::commit_reservation(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!lifecycle.reservation_valid()) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, binding) ||
        binding.slot_index != slot_index) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    const auto budget = make_budget(wait);
    status = budget.check();
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->slots.commit_reservation(
            to_reservation(lifecycle), monotonic_sequence());
    }
    return record(status);
}

sms_status Store::abort_reservation(
    std::int32_t slot_index,
    LifecycleId lifecycle,
    bool count_abort,
    const Wait& wait) noexcept {
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!lifecycle.reservation_valid()) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    IndexBinding binding{};
    if (!IndexBinding::try_decode(lifecycle.slot_binding, binding) ||
        binding.slot_index != slot_index) {
        return record(SMS_STATUS_INVALID_RESERVATION);
    }
    const auto budget = make_budget(wait);
    status = budget.check();
    if (status == SMS_STATUS_SUCCESS) {
        status = abort_core(to_reservation(lifecycle), budget);
    }
    if (!count_abort && status == SMS_STATUS_SUCCESS) {
        state_->aborted_reservations.fetch_sub(1, std::memory_order_relaxed);
    }
    return record(status);
}

sms_status Store::recover_leases(
    bool recover_current,
    const Wait& wait,
    RecoveryReport& report) noexcept {
    report = {};
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!state_->recovery_enabled) {
        return record(SMS_STATUS_UNSUPPORTED_PLATFORM);
    }

    RecoveryScanReport recovered{};
    const auto budget = make_budget(wait);
    status = state_->recovery.try_recover_leases(
        recover_current, budget, recovered);
    std::int32_t retired_participants{};
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->recovery.help_recovering_participants(
            budget, retired_participants);
    }
    report = RecoveryReport{
        recovered.scanned,
        recovered.recovered,
        recovered.active,
        recovered.unsupported,
        recovered.failed};
    state_->recovery_attempts.fetch_add(
        recovered.scanned, std::memory_order_relaxed);
    state_->recovered_leases.fetch_add(
        recovered.recovered, std::memory_order_relaxed);
    state_->recovered_transitions.fetch_add(
        recovered.recovered + retired_participants,
        std::memory_order_relaxed);
    state_->helped_transitions.fetch_add(
        retired_participants, std::memory_order_relaxed);
    state_->active_lease_recoveries.fetch_add(
        recovered.active, std::memory_order_relaxed);
    state_->unsupported_lease_recoveries.fetch_add(
        recovered.unsupported, std::memory_order_relaxed);
    state_->failed_lease_recoveries.fetch_add(
        recovered.failed, std::memory_order_relaxed);
    state_->stale_owner_classifications.fetch_add(
        recovered.recovered, std::memory_order_relaxed);
    state_->live_owner_classifications.fetch_add(
        recovered.active, std::memory_order_relaxed);
    state_->unsupported_owner_classifications.fetch_add(
        recovered.unsupported, std::memory_order_relaxed);
    state_->inconsistent_owner_classifications.fetch_add(
        recovered.failed, std::memory_order_relaxed);
    return record(status);
}

sms_status Store::recover_reservations(
    bool recover_current,
    const Wait& wait,
    RecoveryReport& report) noexcept {
    report = {};
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);
    if (!state_->recovery_enabled) {
        return record(SMS_STATUS_UNSUPPORTED_PLATFORM);
    }

    RecoveryScanReport recovered{};
    const auto budget = make_budget(wait);
    status = state_->recovery.try_recover_reservations(
        recover_current, budget, recovered);
    std::int32_t retired_participants{};
    if (status == SMS_STATUS_SUCCESS) {
        status = state_->recovery.help_recovering_participants(
            budget, retired_participants);
    }
    report = RecoveryReport{
        recovered.scanned,
        recovered.recovered,
        recovered.active,
        recovered.unsupported,
        recovered.failed};
    state_->recovery_attempts.fetch_add(
        recovered.scanned, std::memory_order_relaxed);
    state_->recovered_reservations.fetch_add(
        recovered.recovered, std::memory_order_relaxed);
    state_->recovered_transitions.fetch_add(
        recovered.recovered + retired_participants,
        std::memory_order_relaxed);
    state_->helped_transitions.fetch_add(
        retired_participants, std::memory_order_relaxed);
    state_->active_reservation_recoveries.fetch_add(
        recovered.active, std::memory_order_relaxed);
    state_->unsupported_reservation_recoveries.fetch_add(
        recovered.unsupported, std::memory_order_relaxed);
    state_->failed_reservation_recoveries.fetch_add(
        recovered.failed, std::memory_order_relaxed);
    state_->stale_owner_classifications.fetch_add(
        recovered.recovered, std::memory_order_relaxed);
    state_->live_owner_classifications.fetch_add(
        recovered.active, std::memory_order_relaxed);
    state_->unsupported_owner_classifications.fetch_add(
        recovered.unsupported, std::memory_order_relaxed);
    state_->inconsistent_owner_classifications.fetch_add(
        recovered.failed, std::memory_order_relaxed);
    return record(status);
}

sms_status Store::diagnostics(
    const Wait& wait,
    Diagnostics& result) noexcept {
    result = {};
    LifecycleGate::Operation operation;
    auto status = enter(wait, operation);
    if (status != SMS_STATUS_SUCCESS) return record(status);

    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::DiagnosticsBeforeBoundedScan);
    StructuralDiagnosticsV2 shared{};
    status = state_->diagnostics.snapshot(make_budget(wait), shared);
    if (status != SMS_STATUS_SUCCESS) return record(status);

    result.total_bytes = shared.total_bytes;
    result.store_control = shared.store_control;
    result.slot_count = shared.slot_count;
    result.free_slots = shared.free_slot_count;
    result.initializing_slots = shared.initializing_slot_count;
    result.reserved_slots = shared.reserved_slot_count;
    result.published_slots = shared.published_slot_count;
    result.pending_removal = shared.pending_removal_count;
    result.reclaiming_slots = shared.reclaiming_slot_count;
    result.retired_slots = shared.retired_slot_count;
    result.active_reservations = shared.active_reservation_count();

    result.lease_record_count = shared.lease_record_count;
    result.free_leases = shared.free_lease_count;
    result.claiming_leases = shared.claiming_lease_count;
    result.active_leases = shared.active_lease_count;
    result.recovering_leases = shared.recovering_lease_count;
    result.retired_leases = shared.retired_lease_count;

    result.participant_record_count = shared.participant_record_count;
    result.free_participants = shared.free_participant_count;
    result.registering_participants = shared.registering_participant_count;
    result.active_participants = shared.active_participant_count;
    result.closing_participants = shared.closing_participant_count;
    result.recovering_participants = shared.recovering_participant_count;
    result.reclaiming_participants = shared.reclaiming_participant_count;
    result.retired_participants = shared.retired_participant_count;

    result.index_entries = shared.index_entry_count;
    result.occupied_index_entries = shared.occupied_index_entry_count;
    result.empty_index_entries = shared.empty_index_entry_count;
    result.usable_index_capacity = shared.usable_index_capacity();
    result.primary_directory_occupancy =
        shared.primary_directory_occupancy;
    result.spilled_bucket_count = shared.spilled_bucket_count;
    result.overflow_directory_occupancy =
        shared.overflow_directory_occupancy;

    result.last_failure =
        state_->last_failure.load(std::memory_order_acquire);
    result.aborted_reservations =
        state_->aborted_reservations.load(std::memory_order_acquire);
    result.recovered_leases =
        state_->recovered_leases.load(std::memory_order_acquire);
    result.active_lease_recoveries =
        state_->active_lease_recoveries.load(std::memory_order_acquire);
    result.unsupported_lease_recoveries =
        state_->unsupported_lease_recoveries.load(std::memory_order_acquire);
    result.failed_lease_recoveries =
        state_->failed_lease_recoveries.load(std::memory_order_acquire);
    result.recovered_reservations =
        state_->recovered_reservations.load(std::memory_order_acquire);
    result.active_reservation_recoveries =
        state_->active_reservation_recoveries.load(std::memory_order_acquire);
    result.unsupported_reservation_recoveries =
        state_->unsupported_reservation_recoveries.load(
            std::memory_order_acquire);
    result.failed_reservation_recoveries =
        state_->failed_reservation_recoveries.load(std::memory_order_acquire);
    result.capacity_pressure =
        state_->capacity_pressure.load(std::memory_order_acquire);
    result.overflow_scans =
        state_->overflow_scans.load(std::memory_order_acquire);
    result.cas_retries =
        state_->cas_retries.load(std::memory_order_acquire);
    result.helped_transitions =
        state_->helped_transitions.load(std::memory_order_acquire);
    result.contention_exhaustions =
        state_->contention_exhaustions.load(std::memory_order_acquire);
    result.invalid_tokens =
        state_->invalid_tokens.load(std::memory_order_acquire);
    result.stale_tokens =
        state_->stale_tokens.load(std::memory_order_acquire);
    result.recovery_attempts =
        state_->recovery_attempts.load(std::memory_order_acquire);
    result.recovered_transitions =
        state_->recovered_transitions.load(std::memory_order_acquire);
    result.current_owner_classifications =
        state_->current_owner_classifications.load(std::memory_order_acquire);
    result.live_owner_classifications =
        state_->live_owner_classifications.load(std::memory_order_acquire);
    result.stale_owner_classifications =
        state_->stale_owner_classifications.load(std::memory_order_acquire);
    result.unsupported_owner_classifications =
        state_->unsupported_owner_classifications.load(
            std::memory_order_acquire);
    result.inconsistent_owner_classifications =
        state_->inconsistent_owner_classifications.load(
            std::memory_order_acquire);
    result.changing_owner_classifications =
        state_->changing_owner_classifications.load(
            std::memory_order_acquire);
    for (std::size_t index = 0; index < result.failures.size(); ++index) {
        result.failures[index] =
            state_->failures[index].load(std::memory_order_acquire);
    }
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::DiagnosticsAfterSnapshotAssembly);
    return SMS_STATUS_SUCCESS;
}

void Store::cleanup_owned_resources() noexcept {
    if (state_ == nullptr) return;
    const auto structural = OperationBudget::unbounded_scan();
    const auto store_id = reinterpret_cast<StoreHeaderV2*>(
        state_->region->data())->StoreId;

    for (std::int32_t index = 0;
         index < state_->layout.lease_record_count;
         ++index) {
        auto* current = state_->leases.record(index);
        if (current == nullptr) break;
        const auto raw = MappedAtomic64::load_acquire(current->Control);
        LeaseControl control{};
        if (!LeaseControl::try_decode(raw, control) ||
            control.participant_token != state_->registration.token ||
            (control.state != static_cast<std::int32_t>(LeaseState::claiming) &&
             control.state != static_cast<std::int32_t>(LeaseState::active))) {
            continue;
        }
        std::uint64_t lease_binding{};
        if (!IndexBinding::try_encode(index, control.generation, lease_binding) ||
            current->SlotBinding == 0) {
            continue;
        }
        LeaseToken lease{
            store_id,
            control.participant_token,
            current->SlotBinding,
            lease_binding};
        if (control.state == static_cast<std::int32_t>(LeaseState::claiming)) {
            (void)state_->leases.try_cancel_claim(lease);
        } else {
            (void)state_->leases.try_release(lease);
        }
        (void)state_->reclaimer.try_reclaim(
            lease.slot_binding, structural);
    }

    for (std::int32_t index = 0; index < state_->layout.slot_count; ++index) {
        auto* current = state_->slots.slot(index);
        if (current == nullptr) break;
        const auto raw = MappedAtomic64::load_acquire(current->Control);
        SlotControl control{};
        if (!SlotControl::try_decode(raw, control) ||
            control.participant_token != state_->registration.token ||
            (control.state != static_cast<std::int32_t>(SlotState::initializing) &&
             control.state != static_cast<std::int32_t>(SlotState::reserved))) {
            continue;
        }
        std::uint64_t binding{};
        if (!IndexBinding::try_encode(index, control.generation, binding) ||
            current->ValueLength < 0 ||
            current->ValueLength > state_->layout.max_value_bytes) {
            continue;
        }
        (void)abort_core(
            ReservationToken{
                store_id,
                control.participant_token,
                binding,
                current->ValueLength},
            structural);
    }
}

void Store::close() noexcept {
    sms::test_detail::reach_checkpoint(
        sms::test_detail::CheckpointId::DisposalBeforeLocalGateClose);
    if (!lifecycle_.begin_close_and_drain()) return;
    try {
        if (state_ != nullptr) {
            std::uint64_t closing{};
            if (state_->participants.try_begin_close(
                    state_->registration,
                    closing) == SMS_STATUS_SUCCESS) {
                sms::test_detail::reach_checkpoint(
                    sms::test_detail::CheckpointId::DisposalAfterParticipantClosingPublication);
                const auto structural = OperationBudget::unbounded_scan();
                RecoveryScanReport lease_report{};
                RecoveryScanReport reservation_report{};
                (void)state_->recovery.try_recover_leases(
                    false, structural, lease_report);
                (void)state_->recovery.try_recover_reservations(
                    false, structural, reservation_report);
                std::int32_t retired_participants{};
                (void)state_->recovery.help_recovering_participants(
                    structural, retired_participants);
            }
            sms::test_detail::reach_checkpoint(
                sms::test_detail::CheckpointId::DisposalAfterParticipantRelease);
            state_->slots.invalidate_local();
            state_->leases.invalidate_local();
            state_.reset();
        }
    } catch (...) {
        state_.reset();
    }
    lifecycle_.complete_close();
}

} // namespace sms::detail
