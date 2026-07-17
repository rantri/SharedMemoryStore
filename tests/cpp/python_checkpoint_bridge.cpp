#include "checkpoint.hpp"

#include <cstdint>

#if defined(_WIN32)
#define SMS_TEST_EXPORT extern "C" __declspec(dllexport)
#else
#define SMS_TEST_EXPORT extern "C" __attribute__((visibility("default")))
#endif

namespace {

using checkpoint_callback = void (*)(std::int32_t checkpoint_id, void* context);

class CallbackObserver final : public sms::test_detail::CheckpointObserver {
public:
    void configure(checkpoint_callback callback, void* context) noexcept {
        callback_ = callback;
        context_ = context;
    }

    void reach(sms::test_detail::CheckpointId checkpoint) noexcept override {
        if (callback_ != nullptr) {
            callback_(static_cast<std::int32_t>(checkpoint), context_);
        }
    }

private:
    checkpoint_callback callback_{};
    void* context_{};
};

thread_local CallbackObserver observer;

} // namespace

SMS_TEST_EXPORT std::uint32_t sms_test_checkpoint_bridge_version() noexcept {
    return 1;
}

SMS_TEST_EXPORT void sms_test_set_thread_checkpoint_callback(
    checkpoint_callback callback,
    void* context) noexcept {
    observer.configure(callback, context);
    (void)sms::test_detail::set_thread_checkpoint_observer(
        callback == nullptr ? nullptr : &observer);
}

#undef SMS_TEST_EXPORT
