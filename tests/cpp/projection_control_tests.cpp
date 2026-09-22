#include "interop_faults.hpp"
#include "mapped_atomic.hpp"
#include "test_support.hpp"

int main() {
    using namespace shared_memory_store;
    using namespace sms::detail;

    for (const auto control : {
             sms2_store_initializing, sms2_store_corrupt, sms2_store_unsupported,
             std::uint64_t{0xffff}}) {
        auto options = sms_test_options("projection-control", 2, 2);
        memory_store store;
        SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
        SMS_CHECK(store.try_publish(sms_test_bytes("key"), sms_test_bytes("payload"),
                                   sms_test_bytes("descriptor")) == status::success);
        value_lease lease;
        SMS_CHECK(store.try_acquire(sms_test_bytes("key"), lease) == status::success);
        value_reservation reservation;
        SMS_CHECK(store.try_reserve(sms_test_bytes("reserved"), 4, {}, reservation) ==
                  status::success);
        SMS_CHECK(lease.valid() && lease.value().size() == 7 && lease.descriptor().size() == 10);
        SMS_CHECK(reservation.valid() && reservation.buffer().size() == 4);
        SMS_CHECK(reservation.advance(1) == status::success);
        SMS_CHECK(reservation.payload_length() == 4 && reservation.bytes_written() == 1);

        // Change the actual shared control after valid tokens have escaped.
        sms::interop_test::raw_mapping mapping(options);
        auto& header = sms::interop_test::validate_raw_mapping(mapping, options);
        MappedAtomic64::store_release(header.Control, control);
        SMS_CHECK(!lease.valid());
        SMS_CHECK(lease.value().empty());
        SMS_CHECK(lease.descriptor().empty());
        SMS_CHECK(!reservation.valid());
        SMS_CHECK(reservation.buffer().empty());
        SMS_CHECK(reservation.payload_length() == 0 && reservation.bytes_written() == 0);
        SMS_CHECK(MappedAtomic64::load_acquire(header.Control) == control);
    }
    return 0;
}
