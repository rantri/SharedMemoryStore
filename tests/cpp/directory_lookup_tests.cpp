#include "directory_test_support.hpp"
#include "test_support.hpp"

#include <cstdint>

namespace {

using sms::detail::DirectoryCheckpoint;
using sms::detail::DirectoryEntry;
using sms::detail::MappedAtomic64;
using sms::detail::OperationBudget;
using sms::detail::SpillSummary;
using sms::test::directory::Fixture;
using sms::test::directory::bytes;

struct ReplaceMalformedContext {
    std::uint64_t* word{};
    bool reached{};
};

void replace_malformed(
    void* raw_context,
    DirectoryCheckpoint checkpoint,
    std::uint64_t,
    std::uint64_t) noexcept {
    auto& context = *static_cast<ReplaceMalformedContext*>(raw_context);
    if (checkpoint == DirectoryCheckpoint::after_invalid_reference_confirmation &&
        !context.reached && context.word != nullptr) {
        context.reached = true;
        MappedAtomic64::store_release(*context.word, 0);
    }
}

} // namespace

int main() {
    const auto budget = OperationBudget::unbounded_scan();

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 0x1234'5678'9abc'def0ULL;
        std::int32_t first{};
        std::int32_t second{};
        fixture.directory().buckets_for_hash(hash, first, second);

        const auto first_binding = fixture.seed_slot(0, "alpha", hash);
        const auto first_location = fixture.location(
            sms::detail::directory_target_primary,
            first * sms::detail::sms2_primary_lanes_per_bucket,
            1);
        fixture.publish_reference(first_binding, first_location);

        DirectoryEntry entry{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("alpha"), hash, budget, entry) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(entry.binding == first_binding);
        SMS_CHECK(entry.location.value == first_location.value);

        // Hash equality is only a candidate filter. Exact binary key equality
        // decides the result.
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("alphb"), hash, budget, entry) ==
                  SMS_STATUS_NOT_FOUND);

        const auto second_binding = fixture.seed_slot(1, "bravo", hash);
        const auto second_location = fixture.location(
            sms::detail::directory_target_primary,
            second * sms::detail::sms2_primary_lanes_per_bucket + 3,
            1);
        fixture.publish_reference(second_binding, second_location);
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("bravo"), hash, budget, entry) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(entry.binding == second_binding);

        bool exact{};
        SMS_CHECK(fixture.directory().confirm_exact_reference(
                      second_location, second_binding, exact) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(exact);
        MappedAtomic64::store_release(fixture.primary(second_location.index), 0);
        SMS_CHECK(fixture.directory().confirm_exact_reference(
                      second_location, second_binding, exact) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(!exact);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 77;
        std::int32_t first{};
        std::int32_t second{};
        fixture.directory().buckets_for_hash(hash, first, second);
        (void)second;
        const auto current = fixture.seed_slot(0, "stale", hash, 2);
        (void)current;
        std::uint64_t stale{};
        SMS_CHECK(sms::detail::IndexBinding::try_encode(0, 1, stale));
        auto& cell = fixture.primary(
            first * sms::detail::sms2_primary_lanes_per_bucket);
        MappedAtomic64::store_release(cell, stale);
        DirectoryEntry entry{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("stale"), hash, budget, entry) ==
                  SMS_STATUS_NOT_FOUND);
        SMS_CHECK(MappedAtomic64::load_acquire(cell) == 0);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 88;
        std::int32_t first{};
        std::int32_t second{};
        fixture.directory().buckets_for_hash(hash, first, second);
        (void)second;
        auto& cell = fixture.primary(
            first * sms::detail::sms2_primary_lanes_per_bucket);
        MappedAtomic64::store_release(cell, 1); // generation zero: malformed
        DirectoryEntry entry{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("bad"), hash, budget, entry) ==
                  SMS_STATUS_CORRUPT_STORE);
    }

    {
        ReplaceMalformedContext context{};
        Fixture fixture(
            32,
            128,
            sms::detail::DirectoryHooks{&context, &replace_malformed});
        constexpr std::uint64_t hash = 99;
        std::int32_t first{};
        std::int32_t second{};
        fixture.directory().buckets_for_hash(hash, first, second);
        (void)second;
        context.word = &fixture.primary(
            first * sms::detail::sms2_primary_lanes_per_bucket);
        MappedAtomic64::store_release(*context.word, 1);
        DirectoryEntry entry{};
        // The first malformed sample lost its source word before the second
        // validation. It is contention/stale observation, not corruption.
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("moving"), hash, budget, entry) ==
                  SMS_STATUS_NOT_FOUND);
        SMS_CHECK(context.reached);
    }

    {
        Fixture fixture;
        constexpr std::uint64_t hash = 0xf00d;
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        const auto binding = fixture.seed_slot(0, "overflow", hash);
        const auto overflow_index = fixture.directory().overflow_start_for_hash(hash);
        const auto location = fixture.location(
            sms::detail::directory_target_overflow, overflow_index, 1);
        fixture.publish_reference(binding, location);

        DirectoryEntry entry{};
        // Initial/empty summaries are a versioned negative cache and avoid the
        // overflow scan even when a delayed raw cell remains visible.
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("overflow"), hash, budget, entry) ==
                  SMS_STATUS_NOT_FOUND);
        std::uint64_t present{};
        SMS_CHECK(SpillSummary::try_encode_present(binding, present));
        MappedAtomic64::store_release(fixture.spill(canonical), present);
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("overflow"), hash, budget, entry) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(entry.binding == binding);
        SMS_CHECK(entry.location.value == location.value);
    }

    {
        Fixture fixture;
        sms::detail::CancellationFlag cancellation;
        cancellation.cancel();
        const auto canceled = OperationBudget::unbounded_scan(&cancellation);
        DirectoryEntry entry{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("cancel"), 123, canceled, entry) ==
                  SMS_STATUS_OPERATION_CANCELED);
    }

    return 0;
}
