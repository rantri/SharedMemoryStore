#pragma once

#include "participant_registry.hpp"

#include <cstdint>

namespace sms::detail {

// Captures the process incarnation fields written to an SMS2 participant
// record. Failure deliberately returns the protocol's conservative Unknown
// identity rather than inventing a recoverable owner identity.
[[nodiscard]] ParticipantIdentity capture_participant_identity() noexcept;

// Captures Linux's numeric PID-namespace inode token. Windows and unsupported
// platforms return zero, as required by the SMS2 store header contract.
[[nodiscard]] std::uint64_t capture_pid_namespace_id() noexcept;

} // namespace sms::detail
