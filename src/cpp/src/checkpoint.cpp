#include "checkpoint.hpp"

#if defined(SMS_ENABLE_TEST_CHECKPOINTS)

namespace sms::test_detail {
namespace {

thread_local CheckpointObserver* current_observer{};

} // namespace

CheckpointObserver* set_thread_checkpoint_observer(
    CheckpointObserver* observer) noexcept {
    return std::exchange(current_observer, observer);
}

void reach_checkpoint(CheckpointId checkpoint) noexcept {
    if (current_observer != nullptr) current_observer->reach(checkpoint);
}

} // namespace sms::test_detail

#endif
