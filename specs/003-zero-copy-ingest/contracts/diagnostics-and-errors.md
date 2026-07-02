# Diagnostics and Error Contract

Expected reservation outcomes are represented as status values. Hot-path
reservation, publish, acquire, remove, recovery, and diagnostic operations must
not throw for expected store pressure, validation, duplicate, incomplete, or
invalid-state outcomes.

## StoreStatus Additions

Existing numeric values remain stable:

- `Success` = 0
- `DuplicateKey` = 1
- `NotFound` = 2
- `KeyTooLarge` = 3
- `ValueTooLarge` = 4
- `DescriptorTooLarge` = 5
- `StoreFull` = 6
- `LeaseTableFull` = 7
- `InvalidLease` = 8
- `LeaseAlreadyReleased` = 9
- `RemovePending` = 10
- `UnsupportedPlatform` = 11
- `StoreDisposed` = 12
- `CorruptStore` = 13
- `AccessDenied` = 14
- `UnknownFailure` = 15

Reservation statuses are appended:

- `InvalidReservation` = 16
- `ReservationIncomplete` = 17
- `ReservationAlreadyCompleted` = 18
- `ReservationWriteOutOfRange` = 19

## Operation Outcomes

### TryReserve

- `Success`: reservation created and pending key inserted.
- `DuplicateKey`: key is published, pending removal, or pending reservation.
- `KeyTooLarge`: key exceeds configured limit.
- `ValueTooLarge`: announced payload length exceeds configured limit.
- `DescriptorTooLarge`: descriptor exceeds configured limit.
- `StoreFull`: no free slot is available.
- `StoreDisposed`: store handle is disposed.
- `UnsupportedPlatform`: store is in unsupported state.
- `CorruptStore`: layout validation found unsafe state.
- `UnknownFailure`: unexpected runtime failure after cleanup.

### Advance

- `Success`: progress advanced by the supplied byte count.
- `InvalidReservation`: token does not match a pending slot generation.
- `ReservationAlreadyCompleted`: reservation has committed, aborted, or been
  reclaimed.
- `ReservationWriteOutOfRange`: byte count is negative or exceeds remaining
  payload bytes.
- `StoreDisposed`: store handle is disposed.

### Commit

- `Success`: value is atomically published.
- `ReservationIncomplete`: advanced byte count is less than payload length.
- `InvalidReservation`: token does not match a pending slot generation.
- `ReservationAlreadyCompleted`: reservation has already completed.
- `StoreDisposed`: store handle is disposed.
- `CorruptStore`: impossible slot or index state detected.
- `UnknownFailure`: unexpected runtime failure after cleanup or safe failure.

### Abort

- `Success`: pending key removed and slot reclaimed.
- `InvalidReservation`: token does not match a pending slot generation.
- `ReservationAlreadyCompleted`: reservation has already committed, aborted, or
  been reclaimed.
- `StoreDisposed`: store handle is disposed.
- `CorruptStore`: impossible slot or index state detected.

### TryPublishSegments

- Returns the first deterministic validation, copy, advance, or commit failure.
- On failure after reservation succeeds, the helper attempts to abort before
  returning.
- `copiedBytes` reports bytes copied into store-owned memory before completion
  or failure.

### TryRecoverReservations

- `Success`: scan completed and report is populated.
- `UnsupportedPlatform`: owner liveness cannot be checked safely and recovery
  cannot proceed for the requested policy.
- `StoreDisposed`: store handle is disposed.
- `CorruptStore`: slot/index state is unsafe.
- `UnknownFailure`: unexpected runtime failure after safe cleanup.

## DiagnosticsSnapshot Additions

`DiagnosticsSnapshot` must expose caller-readable reservation diagnostics:

- `ActiveReservationCount`
- `AbortedReservationCount`
- `FailedCommitCount`
- `RecoveredReservationCount`
- `ActiveReservationRecoveryCount`
- `UnsupportedReservationRecoveryCount`
- `FailedReservationRecoveryCount`
- `InvalidReservationFailures`
- `ReservationIncompleteFailures`
- `ReservationAlreadyCompletedFailures`
- `ReservationWriteOutOfRangeFailures`

Existing diagnostic fields and `GetFailureCount(StoreStatus status)` remain
compatible. Capacity pressure includes `StoreFull` caused by pending
reservations occupying slots.

## Timing Contract

After initialization and warm-up, these expected outcomes must complete without
managed heap allocation and within the same timing budget used by existing
deterministic store failures:

- duplicate reserve.
- oversized payload or descriptor reserve.
- commit before complete.
- advance beyond remaining length.
- commit after abort.
- abort after commit.
- acquire of a pending key.
- explicit recovery scan with no stale reservations in the benchmark
  configuration.

## Exception Contract

Exceptions are reserved for:
- invalid API use where a status cannot be returned safely.
- object lifetime patterns required by .NET conventions.
- unexpected initialization failures before the store can provide a stable
  status.

Expected reservation lifecycle outcomes use `StoreStatus` values and diagnostic
counters, not exceptions.
