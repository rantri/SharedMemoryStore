#include "checkpoint.hpp"
#include "interop_faults.hpp"
#include "mapped_atomic.hpp"
#include "test_support.hpp"

namespace {

class ProjectionObserver final : public sms::test_detail::CheckpointObserver {
public:
    ProjectionObserver(shared_memory_store::memory_store& store,
                       shared_memory_store::value_lease& original,
                       sms::detail::LeaseRecordV2& record, int action)
        : store_(store), original_(original), record_(record), action_(action) {}

    void reach(sms::test_detail::CheckpointId checkpoint) noexcept override {
        if (fired || checkpoint != sms::test_detail::CheckpointId::
                ProjectAfterMetadataReadBeforeControlRevalidation) return;
        fired = true;
        if (action_ == 0) {
            const auto active = sms::detail::MappedAtomic64::load_acquire(record_.Control);
            sms::detail::MappedAtomic64::store_release(
                record_.Control, active & ((1ULL << 36) - 1));
            succeeded = true;
        } else {
            succeeded = original_.release() == shared_memory_store::status::success;
            if (action_ == 2) {
                succeeded = succeeded && store_.try_acquire(
                    sms_test_bytes("other"), replacement) == shared_memory_store::status::success;
            }
        }
    }

    bool fired{};
    bool succeeded{};
    shared_memory_store::value_lease replacement;

private:
    shared_memory_store::memory_store& store_;
    shared_memory_store::value_lease& original_;
    sms::detail::LeaseRecordV2& record_;
    int action_{};
};

} // namespace

int main() {
    using namespace shared_memory_store;
    using namespace sms::detail;

    for (int action = 0; action < 3; ++action) {
        for (int accessor = 0; accessor < 3; ++accessor) {
            auto options = sms_test_options("lease-projection-schedule", 2, 1);
            memory_store store;
            SMS_CHECK(memory_store::try_create_or_open(options, store) == open_status::success);
            SMS_CHECK(store.try_publish(sms_test_bytes("key"), sms_test_bytes("payload"),
                                       sms_test_bytes("descriptor")) == status::success);
            SMS_CHECK(store.try_publish(sms_test_bytes("other"), sms_test_bytes("replacement")) ==
                      status::success);
            value_lease original;
            SMS_CHECK(store.try_acquire(sms_test_bytes("key"), original) == status::success);
            sms::interop_test::raw_mapping mapping(options);
            auto& header = sms::interop_test::validate_raw_mapping(mapping, options);
            auto& record = *reinterpret_cast<LeaseRecordV2*>(mapping.data() + header.LeaseRegistryOffset);
            ProjectionObserver observer(store, original, record, action);
            {
                sms::test_detail::ScopedCheckpointObserver scoped(observer);
                if (accessor == 0) SMS_CHECK(!original.valid());
                if (accessor == 1) SMS_CHECK(original.value().empty());
                if (accessor == 2) SMS_CHECK(original.descriptor().empty());
            }
            SMS_CHECK(observer.fired && observer.succeeded);
            SMS_CHECK(MappedAtomic64::load_acquire(header.Control) ==
                      (action == 0 ? sms2_store_corrupt : sms2_store_ready));
            if (action == 2) {
                SMS_CHECK(observer.replacement.valid());
                SMS_CHECK(observer.replacement.value().size() == 11);
            }
        }
    }
    return 0;
}
