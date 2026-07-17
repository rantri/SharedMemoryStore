#include "directory_test_support.hpp"
#include "test_support.hpp"

#include <array>
#include <atomic>
#include <cstdint>
#include <string>
#include <thread>

namespace {

using sms::detail::DirectoryEntry;
using sms::detail::DirectoryLocation;
using sms::detail::MappedAtomic64;
using sms::detail::OperationBudget;
using sms::detail::SpillSummary;
using sms::test::directory::Fixture;
using sms::test::directory::bytes;

void fill_primary_pair(
    Fixture& fixture,
    std::uint64_t hash,
    std::int32_t first_slot = 0) {
    std::int32_t canonical{};
    std::int32_t alternate{};
    fixture.directory().buckets_for_hash(hash, canonical, alternate);
    for (std::int32_t lane = 0;
         lane < sms::detail::sms2_primary_lanes_per_bucket;
         ++lane) {
        const auto slot_index = first_slot + lane;
        const auto key = "primary-a-" + std::to_string(lane);
        const auto binding = fixture.seed_slot(
            slot_index, key, hash + static_cast<std::uint64_t>(lane) + 1);
        fixture.publish_reference(
            binding,
            fixture.location(
                sms::detail::directory_target_primary,
                canonical * sms::detail::sms2_primary_lanes_per_bucket + lane,
                1));
    }
    for (std::int32_t lane = 0;
         lane < sms::detail::sms2_primary_lanes_per_bucket;
         ++lane) {
        const auto slot_index = first_slot +
            sms::detail::sms2_primary_lanes_per_bucket + lane;
        const auto key = "primary-b-" + std::to_string(lane);
        const auto binding = fixture.seed_slot(
            slot_index, key, hash + static_cast<std::uint64_t>(lane) + 100);
        fixture.publish_reference(
            binding,
            fixture.location(
                sms::detail::directory_target_primary,
                alternate * sms::detail::sms2_primary_lanes_per_bucket + lane,
                1));
    }
}

SpillSummary decode_summary(std::uint64_t raw) {
    SpillSummary summary{};
    if (!SpillSummary::try_decode(raw, summary)) {
        throw std::runtime_error("Malformed spill summary in directory test.");
    }
    return summary;
}

} // namespace

int main() {
    const auto budget = OperationBudget::unbounded_scan();

    {
        Fixture fixture;
        constexpr std::uint64_t collision_hash = 0xfeed'face'cafe'beefULL;
        const auto first = fixture.seed_slot(
            0, "collision-one", collision_hash, 1, 1);
        const auto second = fixture.seed_slot(
            1, "collision-two", collision_hash, 1, 1);
        DirectoryLocation first_location{};
        DirectoryLocation second_location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("collision-one"),
                      collision_hash,
                      first,
                      budget,
                      first_location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("collision-two"),
                      collision_hash,
                      second,
                      budget,
                      second_location) == SMS_STATUS_SUCCESS);
        SMS_CHECK(first_location.value != second_location.value);

        DirectoryEntry found{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("collision-one"),
                      collision_hash,
                      budget,
                      found) == SMS_STATUS_SUCCESS);
        SMS_CHECK(found.binding == first);
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("collision-two"),
                      collision_hash,
                      budget,
                      found) == SMS_STATUS_SUCCESS);
        SMS_CHECK(found.binding == second);

        const auto duplicate = fixture.seed_slot(
            2, "collision-one", collision_hash, 1, 1);
        DirectoryLocation rejected{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("collision-one"),
                      collision_hash,
                      duplicate,
                      budget,
                      rejected) == SMS_STATUS_DUPLICATE_KEY);
        SMS_CHECK(rejected.value == 0);
        bool duplicate_visible{};
        SMS_CHECK(fixture.directory().contains_exact_reference(
                      duplicate, budget, duplicate_visible) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(!duplicate_visible);
    }

    {
        Fixture fixture(40);
        constexpr std::uint64_t hash = 0x1020'3040'5060'7080ULL;
        fill_primary_pair(fixture, hash);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;

        const auto first = fixture.seed_slot(16, "spill-one", hash, 1, 1);
        const auto second = fixture.seed_slot(17, "spill-two", hash, 1, 1);
        DirectoryLocation first_location{};
        DirectoryLocation second_location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("spill-one"), hash, first, budget, first_location) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(first_location.kind ==
                  sms::detail::directory_target_overflow);
        auto summary = decode_summary(
            fixture.directory().read_spill_summary(canonical));
        SMS_CHECK(summary.is_present);
        SMS_CHECK(summary.binding() == first);

        SMS_CHECK(fixture.directory().try_insert(
                      bytes("spill-two"), hash, second, budget, second_location) ==
                  SMS_STATUS_SUCCESS);
        SMS_CHECK(second_location.kind ==
                  sms::detail::directory_target_overflow);
        SMS_CHECK(second_location.index != first_location.index);

        fixture.set_slot_state(16, 1, 5);
        SMS_CHECK(fixture.directory().try_unlink(first, budget) ==
                  SMS_STATUS_SUCCESS);
        summary = decode_summary(
            fixture.directory().read_spill_summary(canonical));
        SMS_CHECK(summary.is_present);
        SMS_CHECK(summary.binding() == second); // witness repointed

        const auto third = fixture.seed_slot(18, "spill-three", hash, 1, 1);
        DirectoryLocation third_location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("spill-three"), hash, third, budget, third_location) ==
                  SMS_STATUS_SUCCESS);
        // Overflow capacity is preserved across churn: the first released
        // probe position is reused instead of becoming a tombstone.
        SMS_CHECK(third_location.index == first_location.index);

        fixture.set_slot_state(17, 1, 5);
        SMS_CHECK(fixture.directory().try_unlink(second, budget) ==
                  SMS_STATUS_SUCCESS);
        summary = decode_summary(
            fixture.directory().read_spill_summary(canonical));
        SMS_CHECK(summary.is_present);
        SMS_CHECK(summary.binding() == third);

        const auto stale_present = summary.value;
        fixture.set_slot_state(18, 1, 5);
        SMS_CHECK(fixture.directory().try_unlink(third, budget) ==
                  SMS_STATUS_SUCCESS);
        summary = decode_summary(
            fixture.directory().read_spill_summary(canonical));
        SMS_CHECK(!summary.is_initial());
        SMS_CHECK(!summary.is_present);
        SMS_CHECK(summary.binding() == third);

        // A delayed setter that sampled the prior Present generation cannot
        // ABA through zero or overwrite this versioned Empty witness.
        std::uint64_t delayed_desired{};
        SMS_CHECK(SpillSummary::try_encode_present(second, delayed_desired));
        auto delayed_expected = stale_present;
        SMS_CHECK(!MappedAtomic64::compare_exchange(
            fixture.spill(canonical), delayed_expected, delayed_desired));
        SMS_CHECK(delayed_expected == summary.value);

        DirectoryEntry absent{};
        SMS_CHECK(fixture.directory().try_lookup(
                      bytes("spill-three"), hash, budget, absent) ==
                  SMS_STATUS_NOT_FOUND);
    }

    {
        Fixture fixture(24);
        constexpr std::uint64_t hash = 0xabcdef;
        fill_primary_pair(fixture, hash);
        const auto blocker = fixture.seed_slot(17, "overflow-full", hash + 1);
        for (std::int32_t index = 0; index < fixture.layout().slot_count; ++index) {
            MappedAtomic64::store_release(fixture.overflow(index), blocker);
        }
        const auto candidate = fixture.seed_slot(16, "bounded-full", hash, 1, 1);
        DirectoryLocation location{};
        SMS_CHECK(fixture.directory().try_insert(
                      bytes("bounded-full"),
                      hash,
                      candidate,
                      budget,
                      location) == SMS_STATUS_STORE_FULL);
        SMS_CHECK(location.value == 0);
        std::int32_t canonical{};
        std::int32_t alternate{};
        fixture.directory().buckets_for_hash(hash, canonical, alternate);
        (void)alternate;
        SMS_CHECK(fixture.directory().read_mutation(canonical) == 0);
    }

    {
        // Several same-hash insertions contend on one canonical mutation word.
        // Helpers make progress using mapped CAS only; no process-local or
        // store-wide operation lock is required.
        Fixture fixture(32);
        constexpr std::uint64_t hash = 0x55aa'55aa;
        constexpr std::array<std::string_view, 4> keys{
            "parallel-a", "parallel-b", "parallel-c", "parallel-d"};
        std::array<std::uint64_t, keys.size()> bindings{};
        for (std::size_t index = 0; index < keys.size(); ++index) {
            bindings[index] = fixture.seed_slot(
                static_cast<std::int32_t>(index), keys[index], hash, 1, 1);
        }
        std::array<std::atomic<std::int32_t>, keys.size()> outcomes{};
        std::array<std::thread, keys.size()> workers;
        for (std::size_t index = 0; index < keys.size(); ++index) {
            workers[index] = std::thread([&, index] {
                DirectoryLocation location{};
                outcomes[index].store(
                    fixture.directory().try_insert(
                        bytes(keys[index]),
                        hash,
                        bindings[index],
                        OperationBudget::unbounded_scan(),
                        location),
                    std::memory_order_release);
            });
        }
        for (auto& worker : workers) worker.join();
        for (std::size_t index = 0; index < outcomes.size(); ++index) {
            const auto outcome = outcomes[index].load(std::memory_order_acquire);
            if (outcome != SMS_STATUS_SUCCESS) {
                std::cerr << "parallel insert " << index
                          << " returned status " << outcome << '\n';
            }
            SMS_CHECK(outcome == SMS_STATUS_SUCCESS);
        }
        for (std::size_t index = 0; index < keys.size(); ++index) {
            DirectoryEntry found{};
            SMS_CHECK(fixture.directory().try_lookup(
                          bytes(keys[index]), hash, budget, found) ==
                      SMS_STATUS_SUCCESS);
            SMS_CHECK(found.binding == bindings[index]);
        }
    }

    return 0;
}
