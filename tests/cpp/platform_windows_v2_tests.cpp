#include "cold_open.hpp"
#include "internal.hpp"
#include "participant_registry.hpp"
#include "shared_memory_store/store.hpp"

#if defined(_WIN32)

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <future>
#include <iostream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <thread>

using namespace sms::detail;
using namespace shared_memory_store;

namespace {

std::atomic<int> failures{};
std::atomic<std::uint64_t> name_sequence{};

void expect(bool condition, std::string_view message) {
    if (!condition) {
        std::cerr << "FAIL: " << message << '\n';
        failures.fetch_add(1, std::memory_order_relaxed);
    }
}

ResourceName resource_for(std::string_view suffix) {
    const auto public_name =
        std::string("native-windows-v2-") +
        std::to_string(GetCurrentProcessId()) + "-" +
        std::to_string(GetTickCount64()) + "-" +
        std::to_string(name_sequence.fetch_add(1, std::memory_order_relaxed)) +
        "-" + std::string(suffix);
    ResourceName resource{};
    if (!make_resource_name(public_name, resource)) {
        throw std::runtime_error("Could not derive the Windows test resource.");
    }
    return resource;
}

Options options_for(
    const ResourceName& resource,
    sms_open_mode mode = SMS_OPEN_MODE_CREATE_OR_OPEN,
    std::int64_t total_bytes = 1'000'000) {
    Options options{};
    options.name = resource.public_name;
    options.open_mode = mode;
    options.total_bytes = total_bytes;
    options.slot_count = 2;
    options.max_value_bytes = 64;
    options.max_descriptor_bytes = 8;
    options.max_key_bytes = 16;
    options.lease_record_count = 4;
    options.participant_record_count = 2;
    return options;
}

LayoutV2 layout_for(const Options& options) {
    LayoutV2 layout{};
    expect(LayoutV2::calculate(
               options.total_bytes,
               options.slot_count,
               options.max_value_bytes,
               options.max_descriptor_bytes,
               options.max_key_bytes,
               options.lease_record_count,
               options.participant_record_count,
               layout),
           "Windows fixture layout calculation");
    return layout;
}

void close_platform_result(PlatformOpenResult& result) noexcept {
    // A failed or not-yet-attached cold transaction unwinds mapping then gate.
    if (result.region) {
        result.region->close();
        result.region.reset();
    }
    if (result.cold_lock) {
        result.cold_lock->release();
        result.cold_lock.reset();
    }
    if (result.lifecycle_lock) {
        result.lifecycle_lock->release();
        result.lifecycle_lock.reset();
    }
}

void mutex_precedes_mapping_and_waits_are_bounded() {
    const auto resource = resource_for("ordering");
    auto options = options_for(resource);

    std::promise<bool> ownership_promise;
    auto ownership = ownership_promise.get_future();
    std::promise<void> release_promise;
    auto release = release_promise.get_future();
    std::thread holder([&] {
        const auto mutex = CreateMutexW(
            nullptr, TRUE, resource.windows_lock_name.c_str());
        ownership_promise.set_value(mutex != nullptr);
        if (mutex == nullptr) return;
        release.wait();
        (void)ReleaseMutex(mutex);
        (void)CloseHandle(mutex);
    });

    const auto held = ownership.get();
    expect(held, "test owns the named cold gate");
    if (!held) {
        release_promise.set_value();
        holder.join();
        return;
    }

    CancellationFlag canceled;
    canceled.cancel();
    auto canceled_open = platform_open(resource, options, Wait{-1, &canceled});
    expect(canceled_open.status == SMS_OPEN_OPERATION_CANCELED,
           "canceled named-gate wait preserves the public open status");
    close_platform_result(canceled_open);

    const auto started = std::chrono::steady_clock::now();
    auto blocked = platform_open(resource, options, Wait{30, nullptr});
    const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - started);
    expect(blocked.status == SMS_OPEN_STORE_BUSY,
           "contended named-gate wait returns StoreBusy");
    expect(elapsed < std::chrono::seconds(1),
           "contended named-gate wait remains bounded");
    close_platform_result(blocked);

    const auto premature_mapping = OpenFileMappingW(
        FILE_MAP_READ, FALSE, resource.windows_region_name.c_str());
    expect(premature_mapping == nullptr,
           "Windows mapping is not created before named-gate acquisition");
    if (premature_mapping != nullptr) (void)CloseHandle(premature_mapping);

    release_promise.set_value();
    holder.join();

    auto missing_options = options;
    missing_options.open_mode = SMS_OPEN_MODE_OPEN_EXISTING;
    auto missing = platform_open(resource, missing_options, Wait{1000, nullptr});
    expect(missing.status == SMS_OPEN_NOT_FOUND,
           "missing OpenExisting reports NotFound after acquiring the gate");
    close_platform_result(missing);

    auto created = platform_open(resource, options, Wait{1000, nullptr});
    expect(created.status == SMS_OPEN_SUCCESS && created.physical_creator &&
               created.region && created.cold_lock,
           "a failed OpenExisting releases every resource for the next creator");
    close_platform_result(created);
}

void gate_is_retained_through_registration_and_capacity_is_physical() {
    const auto resource = resource_for("retention");
    auto creator_options = options_for(resource);
    auto creator = platform_open(
        resource, creator_options, Wait{1000, nullptr});
    expect(creator.status == SMS_OPEN_SUCCESS && creator.physical_creator &&
               creator.region && creator.cold_lock,
           "physical Windows creator returns a mapped view and held gate");
    if (creator.status != SMS_OPEN_SUCCESS || !creator.region ||
        !creator.cold_lock) {
        close_platform_result(creator);
        return;
    }

    const auto layout = layout_for(creator_options);
    auto existing_options = creator_options;
    existing_options.open_mode = SMS_OPEN_MODE_OPEN_EXISTING;
    existing_options.total_bytes = 4096;

    std::atomic<bool> contender_started{};
    std::atomic<bool> contender_completed{};
    PlatformOpenResult contender{};
    std::thread waiter([&] {
        contender_started.store(true, std::memory_order_release);
        contender = platform_open(
            resource, existing_options, Wait{2000, nullptr});
        contender_completed.store(true, std::memory_order_release);
    });
    while (!contender_started.load(std::memory_order_acquire)) {
        std::this_thread::yield();
    }

    const ParticipantIdentity identity{
        current_process_id(),
        identity_windows_creation_file_time,
        1,
        0};
    ColdOpenV2 cold(
        creator.region->data(),
        static_cast<std::size_t>(creator.region->size()));
    const auto attached = cold.attach(
        true,
        ColdOpenMode::create_or_open,
        layout,
        identity,
        0x1234'5678ULL,
        0,
        OperationBudget::unbounded_scan());
    expect(attached.status == ColdOpenStatus::success &&
               attached.registration.valid(layout.participant_record_count),
           "header publication and participant registration complete under the gate");

    auto* participant = attached.registration.record_index >= 0
        ? reinterpret_cast<ParticipantRecordV2*>(
              creator.region->data() + layout.participant_offset +
              static_cast<std::int64_t>(attached.registration.record_index) *
                  layout.participant_stride)
        : nullptr;
    expect(participant != nullptr &&
               MappedAtomic64::load_acquire(participant->Control) ==
                   attached.registration.active_control,
           "the retained gate covers exact Active participant publication");
    std::this_thread::sleep_for(std::chrono::milliseconds(50));
    expect(!contender_completed.load(std::memory_order_acquire),
           "a second opener cannot pass the gate before registration finishes");

    creator.cold_lock->release();
    creator.cold_lock.reset();
    waiter.join();
    expect(contender.status == SMS_OPEN_SUCCESS && !contender.physical_creator &&
               contender.region && contender.cold_lock,
           "gate ownership transfers to the waiting existing opener");
    if (contender.region) {
        expect(contender.region->size() >= creator_options.total_bytes &&
                   contender.region->size() != existing_options.total_bytes,
               "existing open projects the physical view extent, not requested capacity");
    }
    if (contender.cold_lock) {
        contender.cold_lock->release();
        contender.cold_lock.reset();
    }

    auto create_new_options = creator_options;
    create_new_options.open_mode = SMS_OPEN_MODE_CREATE_NEW;
    create_new_options.total_bytes *= 2;
    auto create_new = platform_open(
        resource, create_new_options, Wait{1000, nullptr});
    expect(create_new.status == SMS_OPEN_ALREADY_EXISTS &&
               !create_new.physical_creator && !create_new.region,
           "CreateNew uses physical creation disposition, not requested dimensions");
    close_platform_result(create_new);

    if (attached.registration.valid(layout.participant_record_count)) {
        ParticipantRegistry registry(
            creator.region->data(),
            static_cast<std::size_t>(creator.region->size()),
            layout);
        expect(registry.close_and_retire(attached.registration),
               "test retires its exact participant before unmapping");
    }
    close_platform_result(contender);
    close_platform_result(creator);
}

void failed_post_mapping_open_releases_every_resource() {
    const auto resource = resource_for("failed-open");
    auto insufficient = store_options::create(
        resource.public_name,
        2,
        64,
        8,
        16,
        4,
        2,
        open_mode::create_or_open);
    --insufficient.total_bytes;

    memory_store rejected;
    expect(memory_store::try_create_or_open(
               insufficient, rejected, wait_options{1000}) ==
               open_status::insufficient_capacity,
           "post-mapping creator validation reports InsufficientCapacity");

    auto valid = insufficient;
    ++valid.total_bytes;
    memory_store replacement;
    expect(memory_store::try_create_or_open(
               valid, replacement, wait_options{1000}) ==
               open_status::success,
           "failed creator leaves no mapping, gate, or ownership leak");
    replacement.close();
}

} // namespace

int main() {
    mutex_precedes_mapping_and_waits_are_bounded();
    gate_is_retained_through_registration_and_capacity_is_physical();
    failed_post_mapping_open_releases_every_resource();
    if (failures.load(std::memory_order_relaxed) == 0) {
        std::cout << "platform_windows_v2_tests: PASS\n";
        return 0;
    }
    return 1;
}

#endif
