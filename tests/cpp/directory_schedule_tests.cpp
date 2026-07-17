#include "directory_test_support.hpp"
#include "test_support.hpp"

#include <cstdint>

namespace {

using sms::detail::DirectoryCheckpoint;
using sms::detail::DirectoryEntry;
using sms::detail::DirectoryLocation;
using sms::detail::DirectoryOperation;
using sms::detail::MappedAtomic64;
using sms::detail::OperationBudget;
using sms::detail::SlotControl;
using sms::test::directory::Fixture;
using sms::test::directory::bytes;

std::uint64_t& target_word(Fixture& fixture, const DirectoryOperation& operation) {
    return operation.target_kind == sms::detail::directory_target_primary
        ? fixture.primary(operation.target_index)
        : fixture.overflow(operation.target_index);
}

struct ScheduleContext {
    Fixture* fixture{};
    sms::detail::CancellationFlag* cancellation{};
    std::uint64_t blocker{};
    bool target_lost{};
    bool location_won{};
    bool before_reserved{};
    bool after_reserved{};
    std::int32_t before_reserved_state{-1};
    std::int32_t after_reserved_state{-1};
    bool cancel_after_prepare{};
    bool arbitrate_alternate{};
    bool cancel_before_reserved{};
    bool advance_source_generation{};
    std::uint64_t proposed_location{};
    std::uint64_t alternate_location{};
    std::uint64_t future_operation{};
    std::uint64_t* rollback_target{};
};

void scheduled_reach(
    void* raw_context,
    DirectoryCheckpoint checkpoint,
    std::uint64_t binding,
    std::uint64_t detail) noexcept {
    auto& context = *static_cast<ScheduleContext*>(raw_context);
    if (context.fixture == nullptr) return;

    if (checkpoint == DirectoryCheckpoint::after_insert_prepared &&
        context.cancel_after_prepare && context.cancellation != nullptr &&
        !context.cancellation->is_canceled()) {
        context.cancellation->cancel();
    }

    if (checkpoint == DirectoryCheckpoint::before_target_binding_cas &&
        context.blocker != 0 && !context.target_lost) {
        DirectoryOperation operation{};
        if (DirectoryOperation::try_decode(detail, operation)) {
            MappedAtomic64::store_release(
                target_word(*context.fixture, operation), context.blocker);
            context.target_lost = true;
        }
    }

    if (checkpoint == DirectoryCheckpoint::before_source_revalidation &&
        context.advance_source_generation) {
        sms::detail::IndexBinding old_binding{};
        DirectoryOperation operation{};
        if (!sms::detail::IndexBinding::try_decode(binding, old_binding) ||
            !DirectoryOperation::try_decode(detail, operation)) {
            return;
        }
        auto& value_slot = context.fixture->slot(old_binding.slot_index);
        std::uint64_t future_binding{};
        std::uint64_t participant{};
        std::uint64_t control{};
        if (!sms::detail::IndexBinding::try_encode(
                old_binding.slot_index,
                old_binding.generation + 1,
                future_binding) ||
            !sms::detail::ParticipantToken::try_encode(
                0,
                1,
                context.fixture->layout().participant_record_count,
                participant) ||
            !sms::detail::SlotControl::try_encode(
                1,
                old_binding.generation + 1,
                static_cast<std::uint32_t>(participant),
                control) ||
            !DirectoryOperation::try_encode(
                sms::detail::directory_intent_insert,
                sms::detail::directory_phase_complete,
                operation.target_kind,
                operation.target_index,
                old_binding.generation + 1,
                context.future_operation)) {
            return;
        }
        context.rollback_target = &target_word(*context.fixture, operation);
        value_slot.DirectoryBinding = future_binding;
        MappedAtomic64::store_release(
            value_slot.DirectoryOperation, context.future_operation);
        MappedAtomic64::store_release(value_slot.Control, control);
        context.advance_source_generation = false;
    }

    if (checkpoint == DirectoryCheckpoint::before_location_arbitration &&
        context.arbitrate_alternate && !context.location_won) {
        DirectoryLocation proposed{};
        if (!DirectoryLocation::try_decode(detail, proposed)) return;
        const auto alternate_index = proposed.index + 1;
        if (proposed.kind == sms::detail::directory_target_primary &&
            alternate_index < context.fixture->layout().primary_lane_count) {
            const auto alternate = context.fixture->location(
                proposed.kind, alternate_index, proposed.generation);
            MappedAtomic64::store_release(
                context.fixture->primary(alternate.index), binding);
            sms::detail::IndexBinding decoded{};
            if (!sms::detail::IndexBinding::try_decode(binding, decoded)) return;
            MappedAtomic64::store_release(
                context.fixture->slot(decoded.slot_index).DirectoryLocation,
                alternate.value);
            context.proposed_location = proposed.value;
            context.alternate_location = alternate.value;
            context.location_won = true;
        }
    }

    if (checkpoint == DirectoryCheckpoint::before_reserved_publication) {
        context.before_reserved = true;
        sms::detail::IndexBinding decoded{};
        SlotControl control{};
        if (sms::detail::IndexBinding::try_decode(binding, decoded) &&
            SlotControl::try_decode(
                MappedAtomic64::load_acquire(
                    context.fixture->slot(decoded.slot_index).Control),
                control)) {
            context.before_reserved_state = control.state;
        }
        if (context.cancel_before_reserved && context.cancellation != nullptr) {
            context.cancellation->cancel();
        }
    }
    if (checkpoint == DirectoryCheckpoint::after_reserved_publication) {
        context.after_reserved = true;
        sms::detail::IndexBinding decoded{};
        SlotControl control{};
        if (sms::detail::IndexBinding::try_decode(binding, decoded) &&
            SlotControl::try_decode(
                MappedAtomic64::load_acquire(
                    context.fixture->slot(decoded.slot_index).Control),
                control)) {
            context.after_reserved_state = control.state;
        }
    }
}

std::int32_t slot_state(Fixture& fixture, std::int32_t index) {
    SlotControl control{};
    if (!SlotControl::try_decode(
            MappedAtomic64::load_acquire(fixture.slot(index).Control), control)) {
        return -1;
    }
    return control.state;
}

} // namespace

int main() {
    {
        ScheduleContext context{};
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        const auto binding = fixture.seed_slot(0, "helped", 101, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("helped"),
                      101,
                      binding,
                      OperationBudget::unbounded_scan(),
                      location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(location.value != 0);
        SMS_CHECK(slot_state(fixture, 0) == 2);
        SMS_CHECK(fixture.directory().read_mutation([&] {
            std::int32_t canonical{};
            std::int32_t alternate{};
            fixture.directory().buckets_for_hash(101, canonical, alternate);
            return canonical;
        }()) == 0);
        DirectoryEntry found{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("helped"),
                      101,
                      OperationBudget::unbounded_scan(),
                      found) == SMS_STATUS_SUCCESS);
        SMS_CHECK(found.binding == binding);
    }

    {
        // Cancellation after operation preparation leaves a fully described
        // descriptor without claiming the canonical mutation. A later
        // participant can claim and complete it without process-local state.
        sms::detail::CancellationFlag cancellation;
        ScheduleContext context{};
        context.cancellation = &cancellation;
        context.cancel_after_prepare = true;
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        constexpr std::uint64_t hash = 202;
        const auto binding = fixture.seed_slot(0, "cancel-help", hash, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("cancel-help"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(&cancellation),
                      location) == SMS_STATUS_OPERATION_CANCELED);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        SMS_CHECK(fixture.directory().read_mutation(canonical) == 0);
        context.cancel_after_prepare = false;
        DirectoryLocation completed{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("cancel-help"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(),
                      completed) ==
                  SMS_STATUS_SUCCESS);
        DirectoryEntry found{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("cancel-help"),
                      hash,
                      OperationBudget::unbounded_scan(),
                      found) == SMS_STATUS_SUCCESS);
        SMS_CHECK(found.binding == binding);
        SMS_CHECK(slot_state(fixture, 0) == 2);
    }

    {
        ScheduleContext context{};
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        constexpr std::uint64_t hash = 303;
        const auto binding = fixture.seed_slot(0, "target-loss", hash, 1, 1);
        context.blocker = fixture.seed_slot(1, "blocker", 909, 1, 3);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("target-loss"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(),
                      location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(context.target_lost);
        SMS_CHECK(location.value != 0);
        // The occupied first target was never overwritten; insertion selected
        // a fresh location after revalidating its source descriptor.
        bool blocker_remains{};
        SMS_CHECK(fixture.directory().contains_exact_reference(
                      context.blocker,
                      OperationBudget::unbounded_scan(),
                      blocker_remains) == SMS_STATUS_SUCCESS);
        SMS_CHECK(blocker_remains);
    }

    {
        ScheduleContext context{};
        context.advance_source_generation = true;
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        constexpr std::uint64_t hash = 353;
        const auto binding = fixture.seed_slot(
            0, "source-revalidation", hash, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("source-revalidation"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(),
                      location) == SMS_STATUS_INVALID_RESERVATION);
        SMS_CHECK(context.rollback_target != nullptr);
        SMS_CHECK(MappedAtomic64::load_acquire(*context.rollback_target) == 0);
        SMS_CHECK(context.future_operation != 0);
        SMS_CHECK(MappedAtomic64::load_acquire(
                      fixture.slot(0).DirectoryOperation) ==
                  context.future_operation);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        SMS_CHECK(fixture.directory().read_mutation(canonical) == 0);
    }

    {
        ScheduleContext context{};
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        context.arbitrate_alternate = true;
        constexpr std::uint64_t hash = 404;
        const auto binding = fixture.seed_slot(0, "arbitrate", hash, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("arbitrate"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(),
                      location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(context.location_won);
        SMS_CHECK(location.value == context.alternate_location);
        DirectoryLocation proposed{};
        SMS_CHECK(DirectoryLocation::try_decode(
            context.proposed_location, proposed));
        SMS_CHECK(MappedAtomic64::load_acquire(
                      fixture.primary(proposed.index)) == 0);
        SMS_CHECK(MappedAtomic64::load_acquire(
                      fixture.primary(location.index)) == binding);
    }

    {
        // Once the binding is visible, a helper owns the exact
        // Initializing->Reserved publication. Cancellation at the pause point
        // cannot expose a completed directory operation with Initializing
        // control.
        sms::detail::CancellationFlag cancellation;
        ScheduleContext context{};
        context.cancellation = &cancellation;
        context.cancel_before_reserved = true;
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &scheduled_reach});
        context.fixture = &fixture;
        constexpr std::uint64_t hash = 505;
        const auto binding = fixture.seed_slot(0, "reserve-boundary", hash, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("reserve-boundary"),
                      hash,
                      binding,
                      OperationBudget::unbounded_scan(&cancellation),
                      location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(context.before_reserved);
        SMS_CHECK(context.after_reserved);
        SMS_CHECK(context.before_reserved_state == 1);
        SMS_CHECK(context.after_reserved_state == 2);
        SMS_CHECK(cancellation.is_canceled());
        SMS_CHECK(slot_state(fixture, 0) == 2);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 606;
        const auto old_binding = fixture.seed_slot(0, "future", hash, 1, 1);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        MappedAtomic64::store_release(fixture.mutation(canonical), old_binding);

        const auto future_binding = fixture.seed_slot(0, "future", hash, 2, 1);
        std::uint64_t future_operation{};
        SMS_CHECK(DirectoryOperation::try_encode(
            sms::detail::directory_intent_insert,
            sms::detail::directory_phase_complete,
            sms::detail::directory_target_primary,
            canonical * sms::detail::sms2_primary_lanes_per_bucket,
            2,
            future_operation));
        MappedAtomic64::store_release(
            fixture.slot(0).DirectoryOperation, future_operation);
        SMS_CHECK(fixture.slot(0).DirectoryBinding == future_binding);
        SMS_CHECK(fixture.directory().help_mutation(
                      canonical, OperationBudget::unbounded_scan()) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(fixture.directory().read_mutation(canonical) == 0);
        SMS_CHECK(MappedAtomic64::load_acquire(
                      fixture.slot(0).DirectoryOperation) == future_operation);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 707;
        const auto binding = fixture.seed_slot(0, "corrupt", hash, 1, 1);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        MappedAtomic64::store_release(fixture.mutation(canonical), binding);
        MappedAtomic64::store_release(fixture.slot(0).DirectoryOperation, 1);
        SMS_CHECK(fixture.directory().help_mutation(
                      canonical, OperationBudget::unbounded_scan()) ==
                  SMS_STATUS_CORRUPT_STORE);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 808;
        const auto binding = fixture.seed_slot(0, "unlink", hash, 1, 3);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        const auto primary = fixture.location(
            sms::detail::directory_target_primary,
            canonical * sms::detail::sms2_primary_lanes_per_bucket,
            1);
        fixture.publish_reference(binding, primary);
        // A delayed helper duplicated the exact reference elsewhere. Unlink
        // removes the first location and every exact alternate.
        MappedAtomic64::store_release(
            fixture.overflow(fixture.directory().overflow_start_for_hash(hash)),
            binding);
        fixture.set_slot_state(0, 1, 5);
        SMS_CHECK(fixture.directory().try_unlink(
                      binding, OperationBudget::unbounded_scan()) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(MappedAtomic64::load_acquire(
                      fixture.primary(primary.index)) == 0);
        bool remains{};
        SMS_CHECK(fixture.directory().contains_exact_reference(
                      binding,
                      OperationBudget::unbounded_scan(),
                      remains) == SMS_STATUS_SUCCESS);
        SMS_CHECK(!remains);
        sms::detail::SpillSummary summary{};
        SMS_CHECK(sms::detail::SpillSummary::try_decode(
            fixture.directory().read_spill_summary(canonical), summary));
        SMS_CHECK(!summary.is_initial());
        SMS_CHECK(!summary.is_present);
        SMS_CHECK(summary.binding() == binding);
    }

    return 0;
}
