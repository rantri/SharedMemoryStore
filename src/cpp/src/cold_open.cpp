#include "cold_open.hpp"

#include "store_control.hpp"

namespace sms::detail {
namespace {

ColdOpenStatus map_registration(
    ParticipantRegistrationStatus status) noexcept {
    switch (status) {
    case ParticipantRegistrationStatus::success:
        return ColdOpenStatus::success;
    case ParticipantRegistrationStatus::table_full:
        return ColdOpenStatus::participant_table_full;
    case ParticipantRegistrationStatus::store_busy:
        return ColdOpenStatus::store_busy;
    case ParticipantRegistrationStatus::operation_canceled:
        return ColdOpenStatus::operation_canceled;
    case ParticipantRegistrationStatus::corrupt_store:
        return ColdOpenStatus::corrupt_store;
    case ParticipantRegistrationStatus::unsupported_platform:
        return ColdOpenStatus::unsupported_platform;
    default:
        return ColdOpenStatus::incompatible_layout;
    }
}

ColdOpenStatus map_store_control(StoreControlStatus status) noexcept {
    switch (status) {
    case StoreControlStatus::success: return ColdOpenStatus::success;
    case StoreControlStatus::store_busy: return ColdOpenStatus::store_busy;
    case StoreControlStatus::corrupt_store: return ColdOpenStatus::corrupt_store;
    case StoreControlStatus::unsupported_platform:
        return ColdOpenStatus::unsupported_platform;
    default: return ColdOpenStatus::incompatible_layout;
    }
}

} // namespace

ColdOpenV2::ColdOpenV2(
    std::uint8_t* mapping_base,
    std::size_t actual_capacity) noexcept
    : mapping_base_(mapping_base),
      actual_capacity_(actual_capacity) {}

ColdOpenResult ColdOpenV2::attach(
    bool physical_creator,
    ColdOpenMode mode,
    const LayoutV2& requested_layout,
    const ParticipantIdentity& identity,
    std::uint64_t new_store_id,
    std::uint64_t pid_namespace_id,
    const OperationBudget& budget,
    bool architecture_supported) noexcept {
    ColdOpenResult result{};
    if (!architecture_supported) {
        result.status = ColdOpenStatus::unsupported_platform;
        return result;
    }
    if (mapping_base_ == nullptr || !identity.valid() ||
        requested_layout.total_bytes <= 0) {
        result.status = ColdOpenStatus::invalid_options;
        return result;
    }
    if (physical_creator && mode == ColdOpenMode::open_existing) {
        result.status = ColdOpenStatus::not_found;
        return result;
    }
    if (!physical_creator && mode == ColdOpenMode::create_new) {
        result.status = ColdOpenStatus::already_exists;
        return result;
    }
    if (physical_creator &&
        (!requested_layout.fits_within_total_bytes() ||
            static_cast<std::uint64_t>(requested_layout.total_bytes) >
                actual_capacity_)) {
        result.status = ColdOpenStatus::insufficient_capacity;
        return result;
    }

    StoreControlV2 control(
        mapping_base_, actual_capacity_, requested_layout);
    ParticipantRegistry participants(
        mapping_base_, actual_capacity_, requested_layout);
    auto* header = reinterpret_cast<StoreHeaderV2*>(mapping_base_);

    if (physical_creator) {
#if defined(_WIN32)
        const auto namespace_mode = sms2_pid_namespace_recovery_enabled;
#else
        const auto namespace_mode = pid_namespace_id == 0
            ? sms2_pid_namespace_recovery_mixed
            : sms2_pid_namespace_recovery_enabled;
#endif
        if (new_store_id == 0 ||
            !control.initialize_creator(
                new_store_id,
                pid_namespace_id,
                namespace_mode,
                budget)) {
            result.status = budget.check() == SMS_STATUS_OPERATION_CANCELED
                ? ColdOpenStatus::operation_canceled
                : ColdOpenStatus::store_busy;
            return result;
        }
        result.initialized = true;
    } else {
        if (actual_capacity_ < sizeof(StoreHeaderV2)) {
            result.status = ColdOpenStatus::incompatible_layout;
            return result;
        }
        if (header->Magic == 0) {
            result.status = mode == ColdOpenMode::create_or_open
                ? ColdOpenStatus::store_busy
                : ColdOpenStatus::incompatible_layout;
            return result;
        }
        if (header->TotalBytes <= 0 ||
            static_cast<std::uint64_t>(header->TotalBytes) > actual_capacity_) {
            result.status = ColdOpenStatus::incompatible_layout;
            return result;
        }
        const auto validation = control.validate_existing();
        if (validation != StoreControlStatus::success) {
            result.status = map_store_control(validation);
            return result;
        }

#if defined(_WIN32)
        if (header->PidNamespaceId != 0) {
            result.status = ColdOpenStatus::incompatible_layout;
            return result;
        }
#else
        auto namespace_mode = MappedAtomic64::load_acquire(
            header->PidNamespaceMode);
        if (namespace_mode == sms2_pid_namespace_recovery_enabled &&
            (header->PidNamespaceId == 0 || pid_namespace_id == 0 ||
             header->PidNamespaceId != pid_namespace_id)) {
            auto expected = sms2_pid_namespace_recovery_enabled;
            (void)MappedAtomic64::compare_exchange(
                header->PidNamespaceMode,
                expected,
                sms2_pid_namespace_recovery_mixed);
        }
#endif
    }

    ParticipantRegistration registration{};
    const auto registered = participants.try_register(
        *header, identity, budget, registration);
    result.status = map_registration(registered);
    result.registration = registration;
    return result;
}

} // namespace sms::detail
