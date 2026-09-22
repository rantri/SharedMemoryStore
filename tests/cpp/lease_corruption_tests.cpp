#include "interop_faults.hpp"
#include "lease_registry.hpp"
#include "mapped_atomic.hpp"
#include "test_support.hpp"

int main() {
    using namespace shared_memory_store;
    using namespace sms::detail;

    // Every public projection must preserve structural corruption, even
    // though its public return type is only a boolean or a borrowed span.
    for (int fault = 0; fault < 5; ++fault) {
        for (int accessor = 0; accessor < 3; ++accessor) {
            auto options = sms_test_options("lease-projection-corruption", 2, 1);
            memory_store store;
            SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
            SMS_CHECK(store.try_publish(sms_test_bytes("key"), sms_test_bytes("payload"),
                                       sms_test_bytes("descriptor")) == status::success);
            value_lease lease;
            SMS_CHECK(store.try_acquire(sms_test_bytes("key"), lease) == status::success);
            SMS_CHECK(lease.valid());

            sms::interop_test::raw_mapping mapping(options);
            auto& header = sms::interop_test::validate_raw_mapping(mapping, options);
            auto& record = *reinterpret_cast<LeaseRecordV2*>(
                mapping.data() + header.LeaseRegistryOffset);
            const auto active = MappedAtomic64::load_acquire(record.Control);
            switch (fault) {
            case 0: // Active without its required participant owner.
                MappedAtomic64::store_release(record.Control, active & ((1ULL << 36) - 1));
                break;
            case 1: // Unknown lifecycle state.
                MappedAtomic64::store_release(record.Control, (active & ~7ULL) | 6ULL);
                break;
            case 2: // Zero incarnation is never valid.
                MappedAtomic64::store_release(record.Control, active & ~(((1ULL << 33) - 1) << 3));
                break;
            case 3:
                MappedAtomic64::store_release(record.SlotBinding, 0);
                break;
            default: { // Validly encoded binding for a different slot.
                IndexBinding original{};
                SMS_CHECK(IndexBinding::try_decode(record.SlotBinding, original));
                std::uint64_t different{};
                SMS_CHECK(IndexBinding::try_encode(1 - original.slot_index, original.generation, different));
                MappedAtomic64::store_release(record.SlotBinding, different);
                break;
            }
            }
            if (accessor == 0) SMS_CHECK(!lease.valid());
            if (accessor == 1) SMS_CHECK(lease.value().empty());
            if (accessor == 2) SMS_CHECK(lease.descriptor().empty());
            SMS_CHECK(MappedAtomic64::load_acquire(header.Control) == sms2_store_corrupt);
            SMS_CHECK(store.try_publish(sms_test_bytes("another"), sms_test_bytes("value")) ==
                      status::corrupt_store);
        }
    }
    return 0;
}
