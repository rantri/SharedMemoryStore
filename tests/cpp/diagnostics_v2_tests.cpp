#include "test_support.hpp"

#include <array>
#include <cstddef>
#include <span>

template <class T>
concept has_tombstone_entries = requires(T value) {
    value.tombstone_index_entry_count;
};

template <class T>
concept has_index_compactions = requires(T value) {
    value.index_compaction_count;
};

static_assert(sizeof(sms_diagnostics) == 560);
static_assert(offsetof(sms_diagnostics, failure_counts) == 376);
static_assert(!has_tombstone_entries<sms_diagnostics>);
static_assert(!has_index_compactions<sms_diagnostics>);

namespace {

template <std::size_t N>
std::span<const std::byte> bytes(const std::array<std::uint8_t, N>& value) {
    return {
        reinterpret_cast<const std::byte*>(value.data()),
        value.size()};
}

bool same_shared_facts(
    const sms_diagnostics& left,
    const sms_diagnostics& right) {
    return left.layout_major == right.layout_major &&
        left.layout_minor == right.layout_minor &&
        left.resource_protocol == right.resource_protocol &&
        left.required_features == right.required_features &&
        left.optional_features == right.optional_features &&
        left.total_bytes == right.total_bytes &&
        left.slot_count == right.slot_count &&
        left.free_slot_count == right.free_slot_count &&
        left.initializing_slot_count == right.initializing_slot_count &&
        left.reserved_slot_count == right.reserved_slot_count &&
        left.published_slot_count == right.published_slot_count &&
        left.pending_removal_count == right.pending_removal_count &&
        left.reclaiming_slot_count == right.reclaiming_slot_count &&
        left.retired_slot_count == right.retired_slot_count &&
        left.active_reservation_count == right.active_reservation_count &&
        left.active_lease_count == right.active_lease_count &&
        left.claiming_lease_count == right.claiming_lease_count &&
        left.recovering_lease_count == right.recovering_lease_count &&
        left.free_lease_count == right.free_lease_count &&
        left.retired_lease_count == right.retired_lease_count &&
        left.participant_record_count == right.participant_record_count &&
        left.free_participant_count == right.free_participant_count &&
        left.active_participant_count == right.active_participant_count &&
        left.primary_directory_occupancy ==
            right.primary_directory_occupancy &&
        left.spilled_bucket_count == right.spilled_bucket_count &&
        left.overflow_directory_occupancy ==
            right.overflow_directory_occupancy;
}

} // namespace

int main() {
    using namespace shared_memory_store;

    auto creator_options = sms_test_options("diagnostics-v2", 3, 4);
    creator_options.participant_record_count = 4;
    creator_options.total_bytes = store_options::calculate_required_bytes(
        creator_options.slot_count,
        creator_options.max_value_bytes,
        creator_options.max_descriptor_bytes,
        creator_options.max_key_bytes,
        creator_options.lease_record_count,
        creator_options.participant_record_count);
    memory_store creator;
    SMS_CHECK(memory_store::try_create_or_open(creator_options, creator) ==
              open_status::success);
    auto peer_options = creator_options;
    peer_options.mode = open_mode::open_existing;
    memory_store peer;
    SMS_CHECK(memory_store::try_create_or_open(peer_options, peer) ==
              open_status::success);

    const std::array<std::uint8_t, 1> leased_key{1};
    const std::array<std::uint8_t, 2> value{7, 0};
    SMS_CHECK(creator.try_publish(bytes(leased_key), bytes(value)) ==
              status::success);
    value_lease lease;
    SMS_CHECK(creator.try_acquire(bytes(leased_key), lease) == status::success);
    SMS_CHECK(peer.try_remove(bytes(leased_key)) == status::remove_pending);

    recovery_report recovery{};
    SMS_CHECK(creator.try_recover_leases(false, recovery) == status::success);
    SMS_CHECK(recovery.scanned_count == 4 && recovery.recovered_count == 0 &&
              recovery.active_count == 1);

    const std::array<std::uint8_t, 1> reserved_key{2};
    value_reservation reservation;
    SMS_CHECK(peer.try_reserve(bytes(reserved_key), 3, {}, reservation) ==
              status::success);

    diagnostics_snapshot creator_snapshot;
    diagnostics_snapshot peer_snapshot;
    SMS_CHECK(creator.try_get_diagnostics(creator_snapshot) == status::success);
    SMS_CHECK(peer.try_get_diagnostics(peer_snapshot) == status::success);
    const auto& first = creator_snapshot.native();
    const auto& second = peer_snapshot.native();
    SMS_CHECK(same_shared_facts(first, second));
    SMS_CHECK((creator_snapshot.protocol() == protocol_info{2, 0, 2, 7, 0}));
    SMS_CHECK(first.slot_count == 3);
    SMS_CHECK(first.free_slot_count == 1);
    SMS_CHECK(first.initializing_slot_count == 0);
    SMS_CHECK(first.reserved_slot_count == 1);
    SMS_CHECK(first.published_slot_count == 0);
    SMS_CHECK(first.pending_removal_count == 1);
    SMS_CHECK(first.active_reservation_count == 1);
    SMS_CHECK(first.active_lease_count == 1);
    SMS_CHECK(first.free_lease_count == 3);
    SMS_CHECK(first.participant_record_count == 4);
    SMS_CHECK(first.active_participant_count == 2);
    SMS_CHECK(first.free_participant_count == 2);
    SMS_CHECK(first.occupied_index_entry_count == 2);
    SMS_CHECK(first.empty_index_entry_count ==
              first.index_entry_count - first.occupied_index_entry_count);
    SMS_CHECK(first.usable_index_capacity == first.empty_index_entry_count);
    SMS_CHECK(first.last_failure_status == SMS_STATUS_SUCCESS);
    SMS_CHECK(first.recovery_attempt_count == 4);
    SMS_CHECK(first.live_owner_classification_count == 1);
    SMS_CHECK(second.last_failure_status == SMS_STATUS_REMOVE_PENDING);
    SMS_CHECK(second.failure_counts[SMS_STATUS_REMOVE_PENDING] == 1);

    SMS_CHECK(reservation.abort() == status::success);
    SMS_CHECK(lease.release() == status::success);
    SMS_CHECK(lease.release() == status::lease_already_released);
    SMS_CHECK(creator.try_get_diagnostics(creator_snapshot) == status::success);
    SMS_CHECK(creator_snapshot.stale_token_count() == 1);
    SMS_CHECK(creator_snapshot.helped_transition_count() >= 1);
    SMS_CHECK(creator_snapshot.recovery_attempt_count() == 4);
    SMS_CHECK(creator_snapshot.failure_count(
                  status::lease_already_released) == 1);
    SMS_CHECK(creator_snapshot.free_slot_count() == 3);
    SMS_CHECK(creator_snapshot.free_lease_count() == 4);

    cancellation_source cancellation;
    SMS_CHECK(cancellation.signal() == status::success);
    diagnostics_snapshot canceled;
    SMS_CHECK(creator.try_get_diagnostics(
                  canceled,
                  wait_options::infinite(cancellation.token())) ==
              status::operation_canceled);

    return 0;
}
