# Phase 0 Research: Zero-Copy Frame Ingest

## Decision: Use fixed-slot reservations as the core ingest primitive

**Rationale**: The current store already owns bounded fixed slots, descriptor
storage, payload storage, duplicate-key detection, lease protection, removal,
and reuse. A reservation can reuse the existing `SlotPublishing` state to claim
one slot before payload bytes are filled, then transition to `Published` only
after the producer commits. This satisfies the direct ingest requirement without
adding a variable-size allocator or changing reader leases.

**Alternatives considered**:
- Build a separate ingest buffer pool: rejected because it duplicates capacity
  management and still requires a publish transfer into the store.
- Add a variable-size allocator: rejected for this feature because the current
  fixed-slot design gives deterministic capacity, reuse, and benchmark
  behavior for the 1.3 MB frame workload.
- Make socket or pipeline readers part of the core store: rejected because the
  core contract must remain language-neutral and protocol-neutral.

## Decision: Expose store-owned memory through reservation span and memory views

**Rationale**: Direct socket-style receive needs writable memory that belongs to
the mapped store, while tests and synchronous writers benefit from spans. The
reservation token should expose `GetSpan` and `GetMemory` over the remaining
payload region. Any backing object needed to represent unmanaged mapped memory
as `Memory<byte>` is allocated per slot during create/open, so steady-state
reservation and receive loops do not allocate per frame.

**Alternatives considered**:
- Expose only `Span<byte>`: rejected because async socket APIs commonly require
  `Memory<byte>`.
- Allocate a new memory manager per reservation: rejected because it violates
  the 0-byte per-frame allocation goal.
- Return a caller-owned array or pooled array: rejected because that moves the
  payload outside shared memory and reintroduces a publish copy.

## Decision: Insert pending keys into the shared index at reservation time

**Rationale**: Duplicate keys must be rejected while another producer has a
pending reservation for the same key. Inserting the key before payload fill lets
all producers observe the pending claim. Readers still call acquire through the
slot state and only succeed when the slot is `Published`, so no partial payload
bytes become visible.

**Alternatives considered**:
- Insert the key only at commit: rejected because two producers could reserve
  the same key and race at commit.
- Expose pending values through a separate reservation index: rejected because
  it adds another shared-memory structure with no current need.
- Lock duplicate keys only in process-local memory: rejected because producers
  run in different processes.

## Decision: Track written byte count and require exact completion before commit

**Rationale**: The spec requires deterministic behavior when a producer writes
too few or too many bytes. A reservation therefore exposes `Advance(byteCount)`
after data is written to the returned memory. Commit succeeds only when the
advanced count exactly equals the announced payload length. Over-advance and
commit-before-complete return deterministic statuses and do not publish bytes.

**Alternatives considered**:
- Trust callers to fill the entire returned span: rejected because incomplete
  writes could be committed undetected.
- Require a final caller-provided byte count at commit only: rejected because it
  cannot safely support chunked receive loops or prevent accidental overrun
  during the reservation lifetime.
- Zero-fill the remainder on short commit: rejected because that would publish
  protocol-corrupt frames as successful values.

## Decision: Implement segmented publish over the reservation path

**Rationale**: A frame already available as multiple read segments cannot avoid
the copy from those buffers into the shared-memory value, but it can avoid a
temporary contiguous full-frame allocation. `ReadOnlySequence<byte>` is a BCL
representation that maps naturally to pipeline buffers and can be copied segment
by segment into a reservation while advancing progress.

**Alternatives considered**:
- Require callers to flatten segments before publish: rejected because it
  violates the no temporary full-payload allocation requirement.
- Store scatter/gather values across multiple slots: rejected for this feature
  because existing reader leases expose one contiguous value span and the spec
  keeps scatter/gather committed values out of scope.
- Add pipeline-specific core APIs: rejected because `System.IO.Pipelines` is an
  adapter layer, not the language-neutral store contract.

## Decision: Keep reservation cleanup explicit and owner-controlled

**Rationale**: A producer can abort a reservation when a frame is malformed,
the socket closes early, or shutdown begins. If a producer exits without abort
or commit, the store owner invokes explicit stale reservation recovery. Recovery
scans pending reservations, validates owner liveness when the platform supports
it, removes pending index entries, frees slots, and reports results through a
small report struct and diagnostics.

**Alternatives considered**:
- Background cleanup timer: rejected because hidden background work conflicts
  with the constitution and complicates process shutdown.
- Automatic recovery on every publish/acquire: rejected because it makes hot
  paths unpredictable and couples unrelated operations to recovery cost.
- Leave stale reservations until process restart: rejected because capacity
  could remain leaked indefinitely.

## Decision: Preserve existing reader leases and simple publish semantics

**Rationale**: Existing consumers rely on immutable `ValueLease` spans,
remove-while-leased behavior, and slot reuse after final release. Values
committed through a reservation become normal published values governed by the
same lease and removal rules. The existing `TryPublish(ReadOnlySpan<byte>...)`
remains available and may internally use the reservation path as long as its
documented statuses and allocation contract remain compatible.

**Alternatives considered**:
- Add a separate frame value type for readers: rejected because the store must
  remain opaque-byte and frame-neutral.
- Change `ValueLease` to expose writable memory: rejected because readers must
  stay protected from mutation after commit.
- Replace existing publish with reservations only: rejected because existing
  byte-oriented callers must remain supported.

## Decision: Use additive API and status changes with a layout minor update

**Rationale**: The feature adds public capabilities and deterministic outcomes
without changing the package identity. While the package remains pre-1.0, this
is a minor package feature. The physical slot metadata can reuse the existing
reserved integer field for pending reservation progress, but the layout contract
must document that meaning and increment the minor layout version. Existing
status numeric values remain stable; reservation statuses are appended.

**Alternatives considered**:
- Reuse unrelated statuses for reservation failures: rejected because producers
  need precise diagnostics for incomplete writes, invalid tokens, and repeated
  completion.
- Change the layout major version: rejected unless implementation requires
  incompatible section sizes or old readers/writers would become unsafe.
- Avoid documenting layout impact: rejected because future C++ and Python
  consumers need the reservation state machine.
