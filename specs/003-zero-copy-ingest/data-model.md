# Data Model: Zero-Copy Frame Ingest

## IngestReservation

Represents one pending value claim for a key before the value is visible to
readers.

**Fields**:
- `StoreId`: owning mapped store identity.
- `SlotIndex`: reserved reusable slot.
- `Generation`: slot generation captured at reservation.
- `KeyHash`: stable hash of the reserved key.
- `KeyLength`: key byte length.
- `PayloadLength`: announced payload byte length.
- `DescriptorLength`: fixed descriptor byte length.
- `BytesWritten`: payload bytes advanced by the producer.
- `OwnerProcessId`: process that created the reservation.
- `State`: `Pending`, `Committed`, `Aborted`, `Failed`, `Stale`, or
  `Reclaimed`.

**Relationships**:
- Belongs to one `SharedMemoryStore`.
- Owns one writable payload region while pending.
- Blocks duplicate keys through one shared key index entry.
- Transitions to one committed frame value after successful commit.

**Validation Rules**:
- key must satisfy the configured key limit.
- payload length must be between 0 and `MaxValueBytes`.
- descriptor length must be between 0 and `MaxDescriptorBytes`.
- a duplicate published, pending-removal, or pending-reservation key is
  rejected.
- pending reservations are invisible to `TryAcquire`.
- commit succeeds only when `BytesWritten == PayloadLength`.
- abort succeeds only while the reservation is pending.
- repeated commit, abort, advance, or memory access after completion returns a
  deterministic invalid or already-completed outcome.

**State Transitions**:
- `Free slot` -> `Pending`: `TryReserve` succeeds.
- `Pending` -> `Committed`: exact byte count is advanced and commit succeeds.
- `Pending` -> `Aborted` -> `Reclaimed`: producer aborts or disposes before
  commit.
- `Pending` -> `Failed` -> `Reclaimed`: validation or internal commit failure
  prevents publication.
- `Pending` -> `Stale` -> `Reclaimed`: explicit recovery determines that the
  owning producer can no longer complete the reservation.

## WritablePayloadRegion

Store-owned payload bytes exposed to the producer before commit.

**Fields**:
- `PayloadOffset`: mapped-region offset to the reserved slot payload bytes.
- `PayloadLength`: announced frame payload length.
- `BytesWritten`: current producer progress.
- `RemainingBytes`: `PayloadLength - BytesWritten`.
- `SpanView`: writable span over the remaining bytes.
- `MemoryView`: writable memory over the remaining bytes.

**Relationships**:
- Exists only for one pending ingest reservation.
- Becomes immutable value bytes after commit.

**Validation Rules**:
- writable views are valid only while the reservation is pending and the store
  handle remains open.
- `GetSpan(sizeHint)` returns at least the requested remaining size when
  possible and never beyond `RemainingBytes`.
- `DangerousGetMemory(sizeHint)` returns retained-capable writable memory for
  trusted direct-I/O adapters under the same pending-reservation and remaining
  byte rules.
- `Advance(byteCount)` rejects negative counts and counts greater than
  `RemainingBytes`.
- callers must not mutate the region after commit, abort, dispose, recovery,
  store disposal, or slot reuse.

## FrameDescriptor

Optional metadata known before payload bytes are read.

**Fields**:
- `DescriptorBytes`: caller-defined opaque bytes.
- `DescriptorLength`: byte length.

**Relationships**:
- Copied into descriptor storage during reservation.
- Exposed through `ValueLease.DescriptorSpan` after commit.

**Validation Rules**:
- descriptor bytes are fixed at reservation time.
- descriptor bytes are never interpreted by the core store.
- descriptor bytes remain immutable after commit.
- missing or inconsistent descriptor data is rejected before reservation by
  caller-owned protocol parsing or by configured descriptor length checks.

## CommittedFrameValue

Published value produced by a successful reservation commit.

**Fields**:
- `SlotIndex`
- `Generation`
- `KeyHash`
- `KeyLength`
- `DescriptorLength`
- `ValueLength`
- `CommittedSequence`
- `PublisherProcessId`
- `State`: `Published` or `RemoveRequested`

**Relationships**:
- Is found by key through the shared key index.
- Is protected by zero or more store reader leases.
- Reuses the same immutable value and removal behavior as existing
  byte-oriented publishes.

**Validation Rules**:
- readers can acquire only committed values.
- payload and descriptor bytes are immutable after commit.
- remove while leased transitions to pending removal and storage is not reused
  until the final lease releases.
- slot generation prevents stale reservations or leases from acting on reused
  storage.

## SegmentedFrameSource

Producer-visible frame data that already exists across multiple buffers.

**Fields**:
- `TotalLength`: logical frame payload length.
- `Segments`: ordered byte segments, represented in .NET by
  `ReadOnlySequence<byte>` or equivalent adapter.
- `DescriptorBytes`: fixed descriptor bytes.

**Relationships**:
- Published by reserving one contiguous store slot.
- Copies each source segment into the writable payload region.

**Validation Rules**:
- total length must fit in `int` and must not exceed `MaxValueBytes`.
- copied byte count must equal the announced total length before commit.
- no temporary contiguous full-payload array is allocated.
- source segments remain caller-owned and are not referenced after commit.

## ReservationRecoveryReport

Summary returned by explicit stale reservation recovery.

**Fields**:
- `ScannedReservationCount`: pending reservations inspected.
- `RecoveredReservationCount`: stale reservations reclaimed.
- `ActiveReservationCount`: pending reservations still owned by live producers.
- `UnsupportedReservationCount`: reservations that could not be evaluated on the
  current platform.
- `FailedRecoveryCount`: recovery attempts that found inconsistent state.

**Relationships**:
- Produced by `TryRecoverReservations`.
- Feeds diagnostics and owner-controlled logging or metrics.

**Validation Rules**:
- recovery never exposes pending bytes to readers.
- recovery removes pending index entries before freeing slots.
- recovery checks slot generation before reclaiming.
- unsupported owner-liveness checks return deterministic statuses or report
  counts without unsafe reclamation.

## ReservationDiagnostics

Allocation-conscious counters and state surfaced through diagnostics snapshots.

**Fields**:
- `ActiveReservationCount`
- `AbortedReservationCount`
- `FailedCommitCount`
- `RecoveredReservationCount`
- `ReservationIncompleteFailures`
- `ReservationWriteOutOfRangeFailures`
- `InvalidReservationFailures`
- `ReservationAlreadyCompletedFailures`

**Relationships**:
- Extends existing `DiagnosticsSnapshot` behavior.
- Counts expected operation outcomes without writing to console.

**Validation Rules**:
- snapshot retrieval remains caller-controlled and allocation-conscious.
- non-success reservation statuses increment failure counters.
- capacity pressure includes store-full outcomes caused by active reservations.
- diagnostics do not mutate reservation or value lifecycle state.

## StoreReaderLease

Existing reader protection over a committed value.

**Fields**:
- `SlotIndex`
- `Generation`
- `LeaseRecordId`
- `ValueSpan`
- `DescriptorSpan`
- `State`: `Active`, `Released`, `Abandoned`, or `Invalid`

**Relationships**:
- Can acquire values produced by either simple publish or ingest commit.
- Prevents slot reuse while active.

**Validation Rules**:
- acquire fails with `NotFound` for pending, aborted, failed, stale, or
  reclaimed reservations.
- lease release rules are unchanged by ingest.
- `ValueSpan` and `DescriptorSpan` remain read-only.
